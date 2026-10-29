using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WhisperLocal.Config;

namespace WhisperLocal.Stt;

/// <summary>Common part of the cloud backends: nothing to load or unload, errors with a readable message.</summary>
public abstract class CloudStt : ISpeechToText
{
    protected readonly ConfigManager Config;
    protected readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    protected CloudStt(ConfigManager config) => Config = config;

    public bool IsLoaded => false;
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;
    public void Unload() { }

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Config.Current.Stt.CloudTimeoutSec));

        using var request = BuildRequest(wav);
        Log.Info($"STT request -> {Name} ({ModelName}): {wav.Length / 1024} KB audio, {request.RequestUri?.Host}");
        var sw = Stopwatch.StartNew();
        using var response = await Http.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Log.Info($"STT response <- {Name}: HTTP {(int)response.StatusCode} in {sw.ElapsedMilliseconds} ms, {body.Length} bytes");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{Name} returned {(int)response.StatusCode}: {Truncate(body)}");

        LastUsedUtc = DateTime.UtcNow;
        using var doc = JsonDocument.Parse(body);
        return ParseText(doc.RootElement).Trim();
    }

    /// <summary>Provider name for logs and errors.</summary>
    protected abstract string Name { get; }
    protected abstract string ModelName { get; }

    protected abstract HttpRequestMessage BuildRequest(byte[] wav);
    protected abstract string ParseText(JsonElement root);

    protected static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public void Dispose() => Http.Dispose();
}

/// <summary>Groq: OpenAI-compatible /audio/transcriptions endpoint.</summary>
public sealed class GroqStt(ConfigManager config) : CloudStt(config)
{
    protected override string Name => SttProviders.Groq;
    protected override string ModelName => Config.Current.Stt.Groq.Model;

    protected override HttpRequestMessage BuildRequest(byte[] wav)
    {
        var cfg = Config.Current;
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(cfg.Stt.Groq.Model), "model");
        form.Add(new StringContent(cfg.Whisper.Language), "language");
        form.Add(new StringContent(cfg.Whisper.Temperature.ToString(CultureInfo.InvariantCulture)), "temperature");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(cfg.Whisper.InitialPrompt))
            form.Add(new StringContent(cfg.Whisper.InitialPrompt), "prompt");

        var request = new HttpRequestMessage(HttpMethod.Post, cfg.Stt.Groq.Url) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secrets.Get("GroqApiKey", "GROQ_API_KEY"));
        return request;
    }

    protected override string ParseText(JsonElement root) =>
        root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
}

/// <summary>Cloudflare Workers AI: REST /ai/run/{model} with base64 audio in a JSON body.</summary>
public sealed class CloudflareStt(ConfigManager config) : CloudStt(config)
{
    protected override string Name => SttProviders.Cloudflare;
    protected override string ModelName => Config.Current.Stt.Cloudflare.Model;

    protected override HttpRequestMessage BuildRequest(byte[] wav)
    {
        var cfg = Config.Current;
        var cf = cfg.Stt.Cloudflare;
        if (string.IsNullOrWhiteSpace(cf.AccountId))
            throw new InvalidOperationException("Stt.Cloudflare.AccountId is empty in config.json");

        var body = new Dictionary<string, object>
        {
            ["audio"] = Convert.ToBase64String(wav),
            ["task"] = "transcribe",
            ["language"] = cfg.Whisper.Language,
        };
        if (!string.IsNullOrWhiteSpace(cfg.Whisper.InitialPrompt))
            body["initial_prompt"] = cfg.Whisper.InitialPrompt;

        var url = $"https://api.cloudflare.com/client/v4/accounts/{cf.AccountId.Trim()}/ai/run/{cf.Model}";
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secrets.Get("CloudflareApiToken", "CLOUDFLARE_API_TOKEN"));
        return request;
    }

    protected override string ParseText(JsonElement root)
    {
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException($"Cloudflare error: {root.GetProperty("errors")}");
        return root.TryGetProperty("result", out var result) && result.TryGetProperty("text", out var text)
            ? text.GetString() ?? ""
            : "";
    }
}

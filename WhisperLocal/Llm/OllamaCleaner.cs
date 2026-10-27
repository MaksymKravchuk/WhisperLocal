using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperLocal.Config;

namespace WhisperLocal.Llm;

/// <summary>Transcript cleanup via Ollama /api/chat.</summary>
public sealed class OllamaCleaner : IDisposable
{
    private readonly ConfigManager _config;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private DateTime _lastUseUtc = DateTime.MinValue;
    private Task? _preload;

    public OllamaCleaner(ConfigManager config) => _config = config;

    /// <summary>
    /// Loads the model into VRAM while the user is still speaking.
    /// Does nothing if the model is probably loaded already.
    /// </summary>
    public Task PreloadAsync()
    {
        var c = _config.Current.Llm;
        if (!c.Enabled || IsLikelyWarm(c) || _preload is { IsCompleted: false })
            return Task.CompletedTask;
        return _preload = PreloadCoreAsync(c);
    }

    private async Task PreloadCoreAsync(LlmConfig c)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // An empty message list makes Ollama load the model and return immediately
            var body = new Dictionary<string, object?>
            {
                ["model"] = c.Model,
                ["messages"] = Array.Empty<object>(),
                ["keep_alive"] = KeepAliveValue(c.KeepAlive),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(c.ColdTimeoutMs * 2));
            using var response = await _http.PostAsJsonAsync(c.Url.TrimEnd('/') + "/api/chat", body, cts.Token);
            response.EnsureSuccessStatusCode();
            _lastUseUtc = DateTime.UtcNow;
            Log.Info($"LLM '{c.Model}' preloaded in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Log.Warn($"LLM preload failed: {ex.Message}");
        }
    }

    public async Task<string> CleanAsync(string text, string? processName, CancellationToken ct)
    {
        var c = _config.Current.Llm;
        var cold = !IsLikelyWarm(c) || _preload is { IsCompleted: false };
        var timeout = cold ? c.ColdTimeoutMs : c.TimeoutMs;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var response = await _http.PostAsJsonAsync(c.Url.TrimEnd('/') + "/api/chat", BuildRequest(c, text, processName), cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama returned {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        _lastUseUtc = DateTime.UtcNow;

        return Sanitize(content, text, c);
    }

    /// <summary>Asks Ollama to drop the model from VRAM right now.</summary>
    public async Task UnloadAsync()
    {
        var c = _config.Current.Llm;
        try
        {
            using var cts = new CancellationTokenSource(3000);
            using var response = await _http.PostAsJsonAsync(c.Url.TrimEnd('/') + "/api/generate",
                new Dictionary<string, object> { ["model"] = c.Model, ["keep_alive"] = 0 }, cts.Token);
            response.EnsureSuccessStatusCode();
            Log.Info($"LLM '{c.Model}' unloaded");
        }
        catch (Exception ex)
        {
            Log.Warn($"LLM unload request failed (Ollama not running?): {ex.Message}");
        }
        _lastUseUtc = DateTime.MinValue;
    }

    private static Dictionary<string, object?> BuildRequest(LlmConfig c, string text, string? processName)
    {
        var system = c.SystemPrompt;
        // Lookup is case-insensitive: the deserialized dictionary loses the default comparer
        var style = string.IsNullOrEmpty(processName)
            ? null
            : c.AppStyles.FirstOrDefault(kv => StripExe(kv.Key).Equals(StripExe(processName), StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(style))
            system += "\n\nAdditional style for this application: " + style;

        var messages = new List<object> { new { role = "system", content = system } };
        foreach (var ex in c.Examples)
        {
            messages.Add(new { role = "user", content = ex.Input });
            messages.Add(new { role = "assistant", content = ex.Output });
        }
        messages.Add(new { role = "user", content = text });

        var request = new Dictionary<string, object?>
        {
            ["model"] = c.Model,
            ["messages"] = messages,
            ["stream"] = false,
            ["keep_alive"] = KeepAliveValue(c.KeepAlive),
            ["options"] = new { temperature = c.Temperature },
        };

        var think = c.Think ?? (c.Model.Contains("qwen", StringComparison.OrdinalIgnoreCase) ? false : null);
        if (think != null)
            request["think"] = think;

        return request;
    }

    private static string Sanitize(string output, string input, LlmConfig c)
    {
        output = Regex.Replace(output, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        if (output.Length >= 2 && IsWrappedInQuotes(output) && !IsWrappedInQuotes(input.Trim()))
            output = output[1..^1].Trim();

        if (output.Length == 0)
            throw new InvalidOperationException("LLM returned empty text");

        var ratio = (double)output.Length / Math.Max(1, input.Length);
        if (ratio < c.MinLengthRatio || ratio > c.MaxLengthRatio)
            throw new InvalidOperationException($"LLM output length ratio {ratio:F2} is out of bounds (it probably answered or truncated): {output}");

        return output;
    }

    private static bool IsWrappedInQuotes(string s) =>
        s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '«' && s[^1] == '»') || (s[0] == '“' && s[^1] == '”'));

    private bool IsLikelyWarm(LlmConfig c)
    {
        var keepAlive = ParseKeepAlive(c.KeepAlive);
        return keepAlive == null || DateTime.UtcNow - _lastUseUtc < keepAlive.Value - TimeSpan.FromSeconds(5);
    }

    /// <summary>null = forever.</summary>
    private static TimeSpan? ParseKeepAlive(string value)
    {
        value = value.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds < 0 ? null : TimeSpan.FromSeconds(seconds);

        var m = Regex.Match(value, @"^(\d+(?:\.\d+)?)\s*(ms|s|m|h)$");
        if (!m.Success)
            return TimeSpan.FromMinutes(5); // Ollama default
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            _ => TimeSpan.FromHours(n),
        };
    }

    private static object KeepAliveValue(string value) =>
        int.TryParse(value.Trim(), out var seconds) ? seconds : value.Trim();

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public void Dispose() => _http.Dispose();
}

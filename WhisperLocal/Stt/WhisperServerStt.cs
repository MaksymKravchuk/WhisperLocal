using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WhisperLocal.Config;
using WhisperLocal.Native;

namespace WhisperLocal.Stt;

/// <summary>
/// whisper.cpp whisper-server (Vulkan build) running as a managed child process, used over HTTP.
/// </summary>
public sealed class WhisperServerStt : ISpeechToText
{
    private readonly ConfigManager _config;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private string? _signature;

    public WhisperServerStt(ConfigManager config) => _config = config;

    public bool IsLoaded => _process is { HasExited: false };
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            await EnsureStartedAsync(_config.Current.Whisper, ct);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        var c = _config.Current.Whisper;
        await WarmUpAsync(ct);
        LastUsedUtc = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(c.Temperature.ToString(CultureInfo.InvariantCulture)), "temperature");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent(c.Language), "language");
        if (!string.IsNullOrWhiteSpace(c.InitialPrompt))
            form.Add(new StringContent(c.InitialPrompt), "prompt");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(c.RequestTimeoutSec));

        using var response = await _http.PostAsync(BaseUrl(c) + c.InferencePath, form, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"whisper-server returned {(int)response.StatusCode}: {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"whisper-server error: {error}");

        LastUsedUtc = DateTime.UtcNow;
        return doc.RootElement.TryGetProperty("text", out var text) ? text.GetString()?.Trim() ?? "" : "";
    }

    public void Unload()
    {
        if (_process != null)
            Log.Info("Stopping whisper-server");
        StopProcess();
        _signature = null;
    }

    private async Task EnsureStartedAsync(WhisperConfig c, CancellationToken ct)
    {
        var exe = AppPaths.Resolve(c.ServerExe);
        var model = AppPaths.Resolve(c.ModelPath);
        var args = BuildArgs(c, model);
        var signature = exe + "|" + args;

        if (_process is { HasExited: false } && _signature == signature)
            return;

        if (_process != null)
        {
            Log.Info("whisper-server settings changed or the process exited, restarting");
            StopProcess();
        }

        if (!File.Exists(exe))
            throw new FileNotFoundException($"whisper-server not found: {exe}");
        if (!File.Exists(model))
        {
            // Create the folder so the user can drop the downloaded model straight into it
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            throw new ModelNotFoundException(model);
        }

        // Only the bundled server is used; anything else on the port is a configuration error
        if (await PingAsync(c, ct))
            throw new InvalidOperationException($"Port {c.Port} is already in use by another process, change Whisper.Port in the config");

        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log.Server(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Server(e.Data); };
        process.Exited += (_, _) => Log.Info($"whisper-server exited with code {process.ExitCode}");

        Log.Info($"Starting whisper-server: \"{exe}\" {args}");
        var sw = Stopwatch.StartNew();
        process.Start();
        ChildProcessJob.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        while (true)
        {
            if (process.HasExited)
            {
                StopProcess();
                throw new InvalidOperationException($"whisper-server exited during startup, see {Log.ServerFile}");
            }
            if (await PingAsync(c, ct))
                break;
            if (sw.Elapsed > TimeSpan.FromSeconds(c.StartupTimeoutSec))
            {
                StopProcess();
                throw new TimeoutException($"whisper-server did not become ready within {c.StartupTimeoutSec} s");
            }
            await Task.Delay(200, ct);
        }

        _signature = signature;
        LastUsedUtc = DateTime.UtcNow;
        Log.Info($"whisper-server ready in {sw.ElapsedMilliseconds} ms");
    }

    private async Task<bool> PingAsync(WhisperConfig c, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000);
            using var response = await _http.GetAsync(BaseUrl(c) + "/", cts.Token);
            return true; // any HTTP answer means the server is listening
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void StopProcess()
    {
        var p = _process;
        _process = null;
        if (p == null)
            return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
                p.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to stop whisper-server: {ex.Message}");
        }
        p.Dispose();
    }

    private static string BuildArgs(WhisperConfig c, string model) =>
        $"-m \"{model}\" --host {c.Host} --port {c.Port} -l {c.Language} {c.ExtraArgs}".Trim();

    private static string BaseUrl(WhisperConfig c) => $"http://{c.Host}:{c.Port}";

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public void Dispose()
    {
        StopProcess();
        _http.Dispose();
    }
}

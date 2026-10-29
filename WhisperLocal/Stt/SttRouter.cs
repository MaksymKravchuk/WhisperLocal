using WhisperLocal.Config;

namespace WhisperLocal.Stt;

/// <summary>
/// Picks the backend from Stt.Provider on every call (so a config change applies immediately)
/// and falls back to the local whisper-server when a cloud request fails.
/// </summary>
public sealed class SttRouter : ISpeechToText
{
    private readonly ConfigManager _config;
    private readonly WhisperServerStt _local;
    private readonly GroqStt _groq;
    private readonly CloudflareStt _cloudflare;

    public SttRouter(ConfigManager config, WhisperServerStt local)
    {
        _config = config;
        _local = local;
        _groq = new GroqStt(config);
        _cloudflare = new CloudflareStt(config);
    }

    /// <summary>Backend that produced the last transcription, e.g. "Groq" or "Local (fallback)".</summary>
    public string LastProvider { get; private set; } = SttProviders.Local;

    /// <summary>Why the cloud backend failed in the last transcription; null when no fallback happened.</summary>
    public string? LastFallbackReason { get; private set; }

    public bool UsesLocal => Current() == _local;

    // Only the local backend holds memory; cloud backends have nothing to load
    public bool IsLoaded => _local.IsLoaded;
    public DateTime LastUsedUtc => _local.LastUsedUtc;

    public Task WarmUpAsync(CancellationToken ct) => Current().WarmUpAsync(ct);

    public void Unload() => _local.Unload();

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        var backend = Current();
        var name = _config.Current.Stt.Provider;
        LastFallbackReason = null;
        try
        {
            var text = await backend.TranscribeAsync(wav, ct);
            LastProvider = backend == _local ? SttProviders.Local : name;
            return text;
        }
        catch (Exception ex) when (backend != _local && _config.Current.Stt.FallbackToLocal && !ct.IsCancellationRequested)
        {
            Log.Warn($"{name} STT failed, falling back to the local whisper-server: {ex.Message}");
            LastFallbackReason = $"{name}: {ex.Message}";
            var text = await _local.TranscribeAsync(wav, ct);
            LastProvider = $"{SttProviders.Local} (fallback)";
            return text;
        }
    }

    private ISpeechToText Current()
    {
        var provider = _config.Current.Stt.Provider;
        if (provider.Equals(SttProviders.Groq, StringComparison.OrdinalIgnoreCase))
            return _groq;
        if (provider.Equals(SttProviders.Cloudflare, StringComparison.OrdinalIgnoreCase))
            return _cloudflare;
        if (!provider.Equals(SttProviders.Local, StringComparison.OrdinalIgnoreCase))
            Log.Warn($"Unknown Stt.Provider '{provider}', using Local");
        return _local;
    }

    public void Dispose()
    {
        _groq.Dispose();
        _cloudflare.Dispose();
        _local.Dispose();
    }
}

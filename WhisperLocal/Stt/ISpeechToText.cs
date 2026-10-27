namespace WhisperLocal.Stt;

public interface ISpeechToText : IDisposable
{
    bool IsLoaded { get; }
    DateTime LastUsedUtc { get; }

    /// <summary>Loads the model (starts the server) if needed.</summary>
    Task WarmUpAsync(CancellationToken ct);

    /// <summary>Transcribes a 16 kHz mono 16-bit WAV file (with header).</summary>
    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);

    /// <summary>Releases the model (and its VRAM).</summary>
    void Unload();
}

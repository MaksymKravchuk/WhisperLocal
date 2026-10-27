using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperLocal.Audio;

/// <summary>
/// WASAPI shared-mode capture into memory at the device mix format;
/// converted to 16 kHz mono 16-bit PCM when recording stops.
/// </summary>
internal sealed class AudioRecorder : IDisposable
{
    private readonly object _sync = new();
    private WasapiCapture? _capture;
    private MemoryStream? _buffer;
    private TaskCompletionSource? _stopped;
    private long _maxBytes;
    private int _blockAlign;
    private bool _limitHit;

    /// <summary>Raised on the capture thread when MaxDurationSec is reached.</summary>
    public event Action? MaxDurationReached;

    public bool IsRecording => _capture != null;

    public static IReadOnlyList<string> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => d.FriendlyName)
            .ToList();
    }

    public void Start(string? deviceName, int maxSeconds)
    {
        if (_capture != null)
            throw new InvalidOperationException("Recording is already in progress");

        var device = FindDevice(deviceName);
        var capture = new WasapiCapture(device, true, 50);
        var format = capture.WaveFormat;

        lock (_sync)
        {
            _buffer = new MemoryStream(format.AverageBytesPerSecond * 15);
            _blockAlign = format.BlockAlign;
            _maxBytes = maxSeconds > 0 ? (long)format.AverageBytesPerSecond * maxSeconds : long.MaxValue;
            _limitHit = false;
        }

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, e) => OnData(e);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
                Log.Error("Audio capture stopped with an error", e.Exception);
            stopped.TrySetResult();
        };

        _stopped = stopped;
        _capture = capture;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            _capture = null;
            capture.Dispose();
            throw;
        }
        Log.Debug($"Recording started: '{device.FriendlyName}', {format}");
    }

    private void OnData(WaveInEventArgs e)
    {
        var limitReached = false;
        lock (_sync)
        {
            if (_buffer == null || _limitHit)
                return;
            var room = _maxBytes - _buffer.Length;
            var count = (int)Math.Min(room, e.BytesRecorded);
            count -= count % _blockAlign;
            _buffer.Write(e.Buffer, 0, count);
            if (count < e.BytesRecorded)
                _limitHit = limitReached = true;
        }
        if (limitReached)
            MaxDurationReached?.Invoke();
    }

    /// <summary>Stops capture and returns 16 kHz mono 16-bit PCM (no WAV header).</summary>
    public async Task<byte[]> StopAsync()
    {
        var capture = _capture;
        if (capture == null)
            return [];
        _capture = null;

        var format = capture.WaveFormat;
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Warn($"StopRecording failed: {ex.Message}");
        }
        await Task.WhenAny(_stopped!.Task, Task.Delay(2000));

        byte[] raw;
        lock (_sync)
        {
            raw = _buffer?.ToArray() ?? [];
            _buffer = null;
        }
        capture.Dispose();

        return await Task.Run(() => AudioConvert.To16kMono(raw, format));
    }

    private static MMDevice FindDevice(string? name)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
            Log.Warn($"Capture device '{name}' not found, using the default device");
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
    }
}

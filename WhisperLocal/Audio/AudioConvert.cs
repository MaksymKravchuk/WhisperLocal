using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WhisperLocal.Audio;

internal static class AudioConvert
{
    /// <summary>Format sent to Whisper.</summary>
    public static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Converts raw PCM/float audio in any format to 16 kHz mono 16-bit PCM.</summary>
    public static byte[] To16kMono(byte[] raw, WaveFormat format)
    {
        var mono = DownmixToMono(raw, format);
        if (mono.Length == 0)
            return [];

        ISampleProvider provider = new FloatArraySampleProvider(mono, format.SampleRate);
        if (format.SampleRate != TargetFormat.SampleRate)
            provider = new WdlResamplingSampleProvider(provider, TargetFormat.SampleRate);

        var pcm16 = new SampleToWaveProvider16(provider);
        using var ms = new MemoryStream((int)((long)mono.Length * 2 * TargetFormat.SampleRate / format.SampleRate) + 1024);
        var buffer = new byte[32000];
        int read;
        while ((read = pcm16.Read(buffer, 0, buffer.Length)) > 0)
            ms.Write(buffer, 0, read);
        return ms.ToArray();
    }

    /// <summary>Loads any file Media Foundation can decode (wav, mp3, m4a, flac) as 16 kHz mono PCM.</summary>
    public static byte[] LoadFile(string path)
    {
        using var reader = new MediaFoundationReader(path);
        using var ms = new MemoryStream();
        reader.CopyTo(ms);
        return To16kMono(ms.ToArray(), reader.WaveFormat);
    }

    public static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream(pcm.Length + 64);
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), TargetFormat))
            writer.Write(pcm, 0, pcm.Length);
        return ms.ToArray();
    }

    public static double DurationMs(byte[] pcm) => pcm.Length * 1000.0 / TargetFormat.AverageBytesPerSecond;

    /// <summary>
    /// Speech level in dBFS: mean RMS of the loudest 20% of 30 ms frames,
    /// so long pauses do not drag the level down.
    /// </summary>
    public static double SpeechLevelDbfs(byte[] pcm)
    {
        const int frameSamples = 480;
        var samples = pcm.Length / 2;
        if (samples < frameSamples)
            return double.NegativeInfinity;

        var frames = new List<double>(samples / frameSamples);
        for (var start = 0; start + frameSamples <= samples; start += frameSamples)
        {
            double sum = 0;
            for (var i = start; i < start + frameSamples; i++)
            {
                double s = BitConverter.ToInt16(pcm, i * 2) / 32768.0;
                sum += s * s;
            }
            frames.Add(Math.Sqrt(sum / frameSamples));
        }

        frames.Sort();
        var top = frames.Skip(frames.Count * 4 / 5).ToList();
        var rms = top.Average();
        return rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms);
    }

    private static float[] DownmixToMono(byte[] raw, WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        var channels = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = format.BlockAlign;
        var frames = raw.Length / frameSize;
        var mono = new float[frames];

        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = f * frameSize + c * bytesPerSample;
                sum += (isFloat, bytesPerSample) switch
                {
                    (true, 4) => BitConverter.ToSingle(raw, o),
                    (false, 2) => BitConverter.ToInt16(raw, o) / 32768f,
                    (false, 3) => ((raw[o] | raw[o + 1] << 8 | raw[o + 2] << 16) << 8) / 2147483648f,
                    (false, 4) => BitConverter.ToInt32(raw, o) / 2147483648f,
                    _ => throw new NotSupportedException($"Unsupported capture format: {format}"),
                };
            }
            mono[f] = sum / channels;
        }
        return mono;
    }

    private sealed class FloatArraySampleProvider(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}

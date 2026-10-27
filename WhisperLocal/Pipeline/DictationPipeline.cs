using System.Diagnostics;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Stt;
using WhisperLocal.Text;

namespace WhisperLocal.Pipeline;

public sealed class DictationResult
{
    public double AudioMs { get; set; }
    public double SpeechDbfs { get; set; }
    public long SttMs { get; set; }
    public long LlmMs { get; set; }
    public long TotalMs { get; set; }
    public string RawText { get; set; } = "";
    public string DictionaryText { get; set; } = "";
    public string FinalText { get; set; } = "";
    public bool LlmUsed { get; set; }

    /// <summary>Why the result is empty or why the LLM was skipped / failed.</summary>
    public string Note { get; set; } = "";
}

/// <summary>Audio → STT → hallucination filter → dictionary → optional LLM cleanup.</summary>
public sealed class DictationPipeline
{
    private readonly ConfigManager _config;
    private readonly ISpeechToText _stt;
    private readonly OllamaCleaner _llm;
    private readonly TextFilters _filters = new();

    public DictationPipeline(ConfigManager config, ISpeechToText stt, OllamaCleaner llm)
    {
        _config = config;
        _stt = stt;
        _llm = llm;
    }

    /// <summary>Called on hotkey press: loads models while the user speaks.</summary>
    public async Task PrepareAsync()
    {
        var llmPreload = _llm.PreloadAsync();
        try
        {
            await _stt.WarmUpAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error("STT warm-up failed", ex);
        }
        await llmPreload;
    }

    /// <param name="pcm">16 kHz mono 16-bit PCM without header.</param>
    public async Task<DictationResult> ProcessAsync(byte[] pcm, string? processName, CancellationToken ct)
    {
        var cfg = _config.Current;
        var total = Stopwatch.StartNew();
        var result = new DictationResult
        {
            AudioMs = AudioConvert.DurationMs(pcm),
            SpeechDbfs = AudioConvert.SpeechLevelDbfs(pcm),
        };

        if (result.AudioMs < cfg.Filter.MinDurationMs)
            return Finish(result, total, cfg, $"audio too short ({result.AudioMs:F0} ms)");
        if (result.SpeechDbfs < cfg.Filter.MinSpeechDbfs)
            return Finish(result, total, cfg, $"silence ({result.SpeechDbfs:F1} dBFS)");

        var sw = Stopwatch.StartNew();
        result.RawText = await _stt.TranscribeAsync(AudioConvert.ToWav(pcm), ct);
        result.SttMs = sw.ElapsedMilliseconds;

        var text = _filters.RemoveHallucinations(result.RawText, cfg);
        text = _filters.ApplyDictionary(text, cfg);
        result.DictionaryText = text;
        result.FinalText = text;

        if (text.Length == 0)
            return Finish(result, total, cfg, "empty after hallucination filter");

        var words = TextFilters.CountWords(text);
        if (!cfg.Llm.Enabled)
            return Finish(result, total, cfg, "LLM disabled");
        if (words < cfg.Llm.MinWords)
            return Finish(result, total, cfg, $"LLM skipped ({words} words)");

        sw.Restart();
        try
        {
            result.FinalText = await _llm.CleanAsync(text, processName, ct);
            result.LlmUsed = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            result.Note = ex is OperationCanceledException
                ? $"LLM timeout after {sw.ElapsedMilliseconds} ms, raw text used"
                : $"LLM failed, raw text used: {ex.Message}";
            Log.Warn(result.Note);
        }
        result.LlmMs = sw.ElapsedMilliseconds;

        return Finish(result, total, cfg, result.Note);
    }

    private static DictationResult Finish(DictationResult r, Stopwatch total, AppConfig cfg, string note)
    {
        r.Note = note;
        r.TotalMs = total.ElapsedMilliseconds;
        Log.Info($"Dictation: audio={r.AudioMs / 1000:F1}s level={r.SpeechDbfs:F1}dBFS stt={r.SttMs}ms llm={r.LlmMs}ms total={r.TotalMs}ms llmUsed={r.LlmUsed}"
                 + (string.IsNullOrEmpty(note) ? "" : $" note: {note}"));
        if (cfg.Logging.LogText && r.RawText.Length > 0)
        {
            Log.Info($"  raw:   {r.RawText}");
            if (r.DictionaryText != r.RawText)
                Log.Info($"  dict:  {r.DictionaryText}");
            if (r.FinalText != r.DictionaryText)
                Log.Info($"  final: {r.FinalText}");
        }
        return r;
    }
}

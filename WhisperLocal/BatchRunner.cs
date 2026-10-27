using System.Text;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Llm;
using WhisperLocal.Native;
using WhisperLocal.Pipeline;
using WhisperLocal.Stt;

namespace WhisperLocal;

/// <summary>
/// Evaluation mode: runs a folder of audio files through the pipeline and writes CSV + TXT reports.
/// Usage: WhisperLocal.exe --batch &lt;folder&gt; [--out &lt;file.csv&gt;] [--config &lt;config.json&gt;]
/// </summary>
internal static class BatchRunner
{
    private static readonly string[] Extensions = [".wav", ".mp3", ".m4a", ".flac", ".ogg", ".wma"];

    public static async Task<int> RunAsync(string[] args)
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        Console.OutputEncoding = Encoding.UTF8;
        Log.EchoToConsole = true;

        string? folder = null, output = null, configPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--batch" when i + 1 < args.Length: folder = args[++i]; break;
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
            }
        }

        if (folder == null || !Directory.Exists(folder))
        {
            Console.WriteLine("Usage: WhisperLocal.exe --batch <folder> [--out <file.csv>] [--config <config.json>]");
            return 2;
        }

        output ??= Path.Combine(folder, $"whisperlocal-results-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var files = Directory.GetFiles(folder)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Log.Info($"Batch: {files.Count} files from {folder}");

        ConfigManager config;
        try
        {
            config = new ConfigManager(Path.GetFullPath(configPath ?? AppPaths.ConfigPath), watch: false);
        }
        catch (Exception ex)
        {
            Log.Error($"Batch: failed to load config: {ex.Message}");
            return 1;
        }

        using var configScope = config;
        Log.DebugEnabled = config.Current.Logging.Debug;
        using var stt = new WhisperServerStt(config);
        using var llm = new OllamaCleaner(config);
        var pipeline = new DictationPipeline(config, stt, llm);

        try
        {
            await stt.WarmUpAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"Batch: STT start failed: {ex.Message}");
            return 1;
        }

        var csv = new StringBuilder("file;audio_s;stt_ms;llm_ms;total_ms;llm_used;note;raw;dictionary;final\n");
        var txt = new StringBuilder();
        var failures = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                var pcm = AudioConvert.LoadFile(file);
                var r = await pipeline.ProcessAsync(pcm, null, CancellationToken.None);
                csv.AppendJoin(';', Csv(name), (r.AudioMs / 1000).ToString("F1"), r.SttMs, r.LlmMs, r.TotalMs, r.LlmUsed,
                        Csv(r.Note), Csv(r.RawText), Csv(r.DictionaryText), Csv(r.FinalText))
                    .Append('\n');
                txt.Append($"=== {name} ({r.AudioMs / 1000:F1} s, stt {r.SttMs} ms, llm {r.LlmMs} ms, total {r.TotalMs} ms) ===\n")
                    .Append($"RAW:   {r.RawText}\n")
                    .Append($"FINAL: {r.FinalText}\n");
                if (!string.IsNullOrEmpty(r.Note))
                    txt.Append($"NOTE:  {r.Note}\n");
                txt.Append('\n');
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error($"Batch: {name} failed", ex);
                csv.Append($"{Csv(name)};;;;;;{Csv("ERROR: " + ex.Message)};;;\n");
            }
        }

        // BOM so Excel detects UTF-8
        await File.WriteAllTextAsync(output, csv.ToString(), new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".txt"), txt.ToString(), new UTF8Encoding(true));
        Log.Info($"Batch done: {files.Count - failures} ok, {failures} failed. Results: {output}");

        stt.Unload();
        return failures == 0 ? 0 : 1;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

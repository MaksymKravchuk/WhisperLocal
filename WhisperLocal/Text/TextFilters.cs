using System.Text.RegularExpressions;
using WhisperLocal.Config;

namespace WhisperLocal.Text;

/// <summary>Deterministic post-STT processing: hallucination removal and dictionary replacements.</summary>
internal sealed class TextFilters
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private sealed record HallucinationRegexes(Regex Whole, Regex Trailing);

    private AppConfig? _cachedFor;
    private List<HallucinationRegexes> _hallucinations = [];
    private List<Regex> _noise = [];
    private List<(Regex Regex, string Replacement)> _dictionary = [];

    public string RemoveHallucinations(string text, AppConfig cfg)
    {
        EnsureCompiled(cfg);
        foreach (var noise in _noise)
            text = noise.Replace(text, " ");
        text = CollapseSpaces(text);

        foreach (var h in _hallucinations)
        {
            if (h.Whole.IsMatch(text))
                return "";
            text = h.Trailing.Replace(text, "");
        }
        return text.Trim();
    }

    public string ApplyDictionary(string text, AppConfig cfg)
    {
        EnsureCompiled(cfg);
        foreach (var (regex, replacement) in _dictionary)
            text = regex.Replace(text, replacement);
        return text;
    }

    private void EnsureCompiled(AppConfig cfg)
    {
        // Config objects are replaced on reload, so reference equality detects changes
        if (ReferenceEquals(_cachedFor, cfg))
            return;

        _noise = cfg.Filter.NoiseTags.Select(p => Compile(p)).OfType<Regex>().ToList();

        _hallucinations = [];
        foreach (var p in cfg.Filter.Hallucinations)
        {
            var whole = Compile($@"^[\s\p{{P}}]*(?:{p})[\s\p{{P}}]*$");
            // A hallucinated sentence appended after real speech
            var trailing = Compile($@"(?<=[.!?…])\s*(?:{p})[\s\p{{P}}]*$");
            if (whole != null && trailing != null)
                _hallucinations.Add(new HallucinationRegexes(whole, trailing));
        }

        _dictionary = [];
        foreach (var entry in cfg.Dictionary)
        {
            var pattern = entry.WholeWord ? $@"(?<![\p{{L}}\p{{N}}])(?:{entry.Pattern})(?![\p{{L}}\p{{N}}])" : entry.Pattern;
            var regex = Compile(pattern);
            if (regex != null)
                _dictionary.Add((regex, entry.Replacement));
        }

        _cachedFor = cfg;
    }

    private static Regex? Compile(string pattern)
    {
        try
        {
            return new Regex(pattern, Options, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            Log.Error($"Invalid regex in config, skipped: {pattern} ({ex.Message})");
            return null;
        }
    }

    public static string CollapseSpaces(string text) => Regex.Replace(text, @"[ \t]{2,}", " ").Trim();

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

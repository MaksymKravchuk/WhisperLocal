using System.Text.Json;

namespace WhisperLocal.Config;

/// <summary>
/// API keys, kept out of config.json (which is in git). Looked up in an environment variable first,
/// then in secrets.json next to the exe, e.g. { "GroqApiKey": "gsk_...", "CloudflareApiToken": "..." }.
/// Read on every request, so editing the file needs no restart.
/// </summary>
internal static class Secrets
{
    public const string FileName = "secrets.json";

    public static string FilePath => Path.Combine(AppPaths.DataDir, FileName);

    /// <exception cref="InvalidOperationException">The key is not set anywhere.</exception>
    public static string Get(string name, string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        if (File.Exists(FilePath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty(name, out var prop) && prop.GetString() is { Length: > 0 } fromFile)
                return fromFile.Trim();
        }

        throw new InvalidOperationException($"{name} is not set: add \"{name}\" to {FilePath} or set the {envVar} environment variable");
    }
}

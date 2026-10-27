namespace WhisperLocal.Config;

/// <summary>
/// Portable layout next to the exe: config.json (copied from the project on build), logs\,
/// whisper\ (bundled runtime) and models\ (downloaded separately, not in source control).
/// </summary>
internal static class AppPaths
{
    public const string ConfigFileName = "config.json";

    public static string DataDir { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string ConfigPath => Path.Combine(DataDir, ConfigFileName);
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>Relative config paths are relative to the exe directory; absolute paths are used as is.</summary>
    public static string Resolve(string path) => Path.GetFullPath(path, DataDir);
}

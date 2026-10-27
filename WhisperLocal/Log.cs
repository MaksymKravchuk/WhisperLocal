namespace WhisperLocal;

/// <summary>Minimal thread-safe daily file logger.</summary>
internal static class Log
{
    private static readonly object Sync = new();

    public static string LogDir { get; private set; } = AppContext.BaseDirectory;
    public static bool EchoToConsole { get; set; }
    public static bool DebugEnabled { get; set; }

    public static string CurrentFile => Path.Combine(LogDir, $"whisperlocal-{DateTime.Now:yyyyMMdd}.log");
    public static string ServerFile => Path.Combine(LogDir, "whisper-server.log");

    public static void Init(string logDir)
    {
        LogDir = logDir;
        Directory.CreateDirectory(logDir);
    }

    public static void Debug(string message)
    {
        if (DebugEnabled)
            Write("DEBUG", message);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    /// <summary>Raw output of the whisper-server child process, kept in a separate file.</summary>
    public static void Server(string line) => Append(ServerFile, $"{DateTime.Now:HH:mm:ss.fff} {line}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Append(CurrentFile, line);
        if (EchoToConsole)
            Console.WriteLine(line);
    }

    private static void Append(string file, string line)
    {
        lock (Sync)
        {
            try
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never break dictation
            }
        }
    }
}

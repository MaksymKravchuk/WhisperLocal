using System.Text.Encodings.Web;
using System.Text.Json;

namespace WhisperLocal.Config;

/// <summary>Loads config.json, writes defaults on first run and hot-reloads on change.</summary>
public sealed class ConfigManager : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        // Keep Cyrillic readable in the file
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _debounce;

    public string FilePath { get; }
    public AppConfig Current { get; private set; }

    /// <summary>Raised on a thread-pool thread after a successful reload.</summary>
    public event Action? Changed;

    public ConfigManager(string filePath, bool watch)
    {
        FilePath = filePath;
        Current = LoadOrCreate();
        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);

        if (watch)
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e) => _debounce.Change(500, Timeout.Infinite);

    private AppConfig LoadOrCreate()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = new AppConfig();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, JsonOptions));
            Log.Info($"Default config written to {FilePath}");
            return defaults;
        }
        return Parse(ReadWithRetry());
    }

    private void Reload()
    {
        try
        {
            Current = Parse(ReadWithRetry());
            Log.Info("Config reloaded");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Config reload failed, keeping previous settings", ex);
        }
    }

    private string ReadWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(FilePath);
            }
            catch (IOException) when (attempt < 5)
            {
                // Editors may hold the file briefly while saving
                Thread.Sleep(100);
            }
        }
    }

    private static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? throw new InvalidDataException("Config file is empty");

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}

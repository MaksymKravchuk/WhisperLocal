using System.Diagnostics;
using WhisperLocal.Audio;
using WhisperLocal.Config;
using WhisperLocal.Input;
using WhisperLocal.Llm;
using WhisperLocal.Native;
using WhisperLocal.Output;
using WhisperLocal.Pipeline;
using WhisperLocal.Stt;
using WhisperLocal.Ui;

namespace WhisperLocal;

/// <summary>Tray-only application: hotkey → record → pipeline → paste.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private enum State { Idle, Recording, Processing }

    private readonly ConfigManager _config;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _unloadItem;
    private readonly KeyboardHook _hook;
    private readonly AudioRecorder _recorder = new();
    private readonly WhisperServerStt _stt;
    private readonly OllamaCleaner _llm;
    private readonly DictationPipeline _pipeline;
    private readonly TextInserter _inserter = new();
    private readonly RecordingOverlay _overlay = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };

    private State _state = State.Idle;
    private bool _enabled = true;
    private long _pressedAt;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _modelDialogOpen;
    private ForegroundApp? _target;

    public TrayApp(ConfigManager config)
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;

        _config = config;
        _stt = new WhisperServerStt(config);
        _llm = new OllamaCleaner(config);
        _pipeline = new DictationPipeline(config, _stt, _llm);

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled()) { Checked = true };
        _unloadItem = new ToolStripMenuItem("Unload from memory", null, async (_, _) => await UnloadFromMemoryAsync());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _enabledItem,
            _unloadItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open config", null, (_, _) => OpenFile(_config.FilePath)),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenFile(Log.CurrentFile)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()),
        ]);

        // "Unload from memory" is available only while something is loaded
        menu.Opening += (_, _) => _unloadItem.Enabled = IsAnythingLoaded;

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };

        _hook = new KeyboardHook(_ui);
        _hook.HotkeyDown += OnHotkeyDown;
        _hook.HotkeyUp += OnHotkeyUp;
        _hook.HotkeyCancel += OnHotkeyCancel;
        _recorder.MaxDurationReached += () => _ui.Post(_ => _ = FinishRecordingAsync(), null);
        _config.Changed += () => _ui.Post(_ => ApplyConfig(), null);

        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        ApplyConfig();
        UpdateTrayState();
        Log.Info($"WhisperLocal {Application.ProductVersion} started, config: {_config.FilePath}");

        if (config.Current.Whisper.StartOnLaunch)
            _ = WarmUpWhisperAsync();
    }

    private void ApplyConfig()
    {
        var cfg = _config.Current;
        Log.DebugEnabled = cfg.Logging.Debug;
        try
        {
            _hook.SetHotkey(KeyboardHook.ParseHotkey(cfg.Hotkey.Key));
            Log.Info($"Hotkey: {KeyboardHook.Describe(cfg.Hotkey.Key)}");
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex.Message);
            Notify(ex.Message, ToolTipIcon.Error);
        }
        _hook.SuppressKey = cfg.Hotkey.SuppressKey;

        try
        {
            Autostart.Apply(cfg.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the Windows autostart entry", ex);
        }

        UpdateTrayState();
    }

    private async void OnHotkeyCancel()
    {
        if (_state != State.Recording)
            return;
        Log.Info("Recording cancelled: another key was pressed while holding the hotkey");
        await _recorder.StopAsync();
        SetState(State.Idle);
    }

    private void OnHotkeyDown()
    {
        if (!_enabled || _state != State.Idle)
            return;

        var cfg = _config.Current;

        // Do not let the user dictate for nothing
        var modelPath = AppPaths.Resolve(cfg.Whisper.ModelPath);
        if (!File.Exists(modelPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            ShowModelMissing(new ModelNotFoundException(modelPath));
            return;
        }

        _target = ForegroundApp.Capture();
        try
        {
            _recorder.Start(cfg.Audio.DeviceName, cfg.Audio.MaxDurationSec);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start recording", ex);
            Notify($"Microphone error: {ex.Message}", ToolTipIcon.Error);
            return;
        }

        _pressedAt = Stopwatch.GetTimestamp();
        _lastActivityUtc = DateTime.UtcNow;
        SetState(State.Recording);
        _ = _pipeline.PrepareAsync();
    }

    private void OnHotkeyUp() => _ = FinishRecordingAsync();

    private async Task FinishRecordingAsync()
    {
        if (_state != State.Recording)
            return;

        var cfg = _config.Current;
        var heldMs = Stopwatch.GetElapsedTime(_pressedAt).TotalMilliseconds;
        SetState(State.Processing);
        try
        {
            var pcm = await _recorder.StopAsync();
            if (heldMs < cfg.Hotkey.MinHoldMs)
            {
                Log.Debug($"Hotkey tap ignored ({heldMs:F0} ms)");
                return;
            }

            var result = await _pipeline.ProcessAsync(pcm, _target?.ProcessName, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(result.FinalText))
                return;

            var text = cfg.Insert.AppendTrailingSpace ? result.FinalText + " " : result.FinalText;
            await _inserter.InsertAsync(text, _target, cfg.Insert);
        }
        catch (Exception ex)
        {
            ReportError("Dictation failed", ex);
        }
        finally
        {
            _lastActivityUtc = DateTime.UtcNow;
            SetState(State.Idle);
        }
    }

    private void ToggleEnabled()
    {
        _enabled = !_enabled;
        _enabledItem.Checked = _enabled;
        Log.Info(_enabled ? "Dictation enabled" : "Dictation disabled");
        UpdateTrayState();
    }

    /// <summary>
    /// Stops all servers and unloads all models to free RAM/VRAM.
    /// Dictation stays enabled: the next hotkey press starts everything again.
    /// </summary>
    private async Task UnloadFromMemoryAsync()
    {
        // A running dictation finishes first; the idle timer or the user can unload afterwards
        if (_state == State.Processing)
            return;
        if (_state == State.Recording)
        {
            await _recorder.StopAsync();
            SetState(State.Idle);
        }

        _stt.Unload();
        if (_config.Current.Llm.Enabled)
            await _llm.UnloadAsync();
        Log.Info("Unloaded from memory: all servers stopped, models released");
    }

    private bool IsAnythingLoaded => _stt.IsLoaded;

    private async void CheckIdle()
    {
        var minutes = _config.Current.Memory.IdleUnloadMinutes;
        if (minutes <= 0 || _state != State.Idle || !IsAnythingLoaded)
            return;
        if (DateTime.UtcNow - _lastActivityUtc < TimeSpan.FromMinutes(minutes))
            return;

        Log.Info($"No dictation for {minutes} min, unloading from memory");
        await UnloadFromMemoryAsync();
    }

    private async Task WarmUpWhisperAsync()
    {
        try
        {
            await _stt.WarmUpAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ReportError("whisper-server start failed", ex);
        }
    }

    /// <summary>A missing model gets a dialog with download instructions; other errors a tray balloon.</summary>
    private void ReportError(string context, Exception ex)
    {
        if (ex is ModelNotFoundException missing)
        {
            ShowModelMissing(missing);
            return;
        }
        Log.Error(context, ex);
        Notify($"{context}: {ex.Message}", ToolTipIcon.Error);
    }

    private void ShowModelMissing(ModelNotFoundException ex)
    {
        Log.Error(ex.Message.ReplaceLineEndings(" "));
        // Startup warm-up and a hotkey press may both hit this; one dialog at a time
        if (_modelDialogOpen)
            return;
        _modelDialogOpen = true;
        try
        {
            MessageBox.Show(ex.Message, "WhisperLocal: model not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _modelDialogOpen = false;
        }
    }

    private void SetState(State state)
    {
        _state = state;
        if (_config.Current.Ui.ShowOverlay)
        {
            switch (state)
            {
                case State.Recording:
                    _overlay.ShowState("● Recording", Color.FromArgb(255, 90, 90));
                    break;
                case State.Processing:
                    _overlay.ShowState("… Processing", Color.FromArgb(255, 180, 60));
                    break;
                default:
                    _overlay.Hide();
                    break;
            }
        }
        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        if (!_enabled)
        {
            _tray.Icon = TrayIcons.Off;
            _tray.Text = "WhisperLocal: disabled";
            return;
        }
        (_tray.Icon, _tray.Text) = _state switch
        {
            State.Recording => (TrayIcons.Recording, "WhisperLocal: recording"),
            State.Processing => (TrayIcons.Processing, "WhisperLocal: processing"),
            _ => (TrayIcons.Idle, $"WhisperLocal: hold {KeyboardHook.Describe(_config.Current.Hotkey.Key)} to dictate"),
        };
    }

    private void Notify(string message, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(4000, "WhisperLocal", message, icon);

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Cannot open {path}", ex);
        }
    }

    private void ExitApp()
    {
        Log.Info("WhisperLocal exiting");
        _idleTimer.Stop();
        _hook.Dispose();
        _recorder.Dispose();
        _stt.Dispose();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hook.Dispose();
            _recorder.Dispose();
            _stt.Dispose();
            _llm.Dispose();
            _tray.Dispose();
            _overlay.Dispose();
            _idleTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

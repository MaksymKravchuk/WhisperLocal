using WhisperLocal.Config;

namespace WhisperLocal;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Log.Init(AppPaths.LogDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        if (args.Length > 0 && args[0].Equals("--batch", StringComparison.OrdinalIgnoreCase))
            return BatchRunner.RunAsync(args).GetAwaiter().GetResult();

        using var mutex = new Mutex(true, @"Local\WhisperLocal.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("WhisperLocal is already running.", "WhisperLocal", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);

        ConfigManager config;
        try
        {
            config = new ConfigManager(AppPaths.ConfigPath, watch: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load config", ex);
            MessageBox.Show($"Failed to load config:\n{AppPaths.ConfigPath}\n\n{ex.Message}", "WhisperLocal",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using (config)
        using (var app = new TrayApp(config))
            Application.Run(app);
        return 0;
    }
}

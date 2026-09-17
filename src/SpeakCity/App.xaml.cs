using System.IO;
using System.Windows;

namespace SpeakCity;

public partial class App : Application
{
    private Mutex? _singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string mode = e.Args.Length >= 2 ? e.Args[0] : "";
        if (mode == "--self-test")
        {
            int code = await SelfTests.RunAsync(e.Args[1]);
            Shutdown(code); return;
        }
        if (mode == "--diagnose")
        {
            // Writes one small local JSON file (no conversation content, no keys) so an
            // install that "does not open" can be told apart from one that cannot speak.
            int code = await StartupDiagnostics.RunAsync(e.Args[1]);
            Shutdown(code); return;
        }
        bool smoke = mode == "--ui-smoke";
        // An automated run has nobody to click a dialog: a modal message there is not a
        // prompt, it is a hang. Such runs report through their exit code and JSON file.
        bool silent = smoke || AppStartup.Automated;
        _singleInstance = new Mutex(true, "Local\\SPEAKCITY.Desktop", out bool first);
        if (!first)
        {
            if (silent)
            {
                if (smoke)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!);
                        File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(new
                        {
                            status = "skipped",
                            passed = true,
                            skipped = true,
                            skipped_reason = "another-instance-running",
                            error = "Another SPEAKCITY window already holds this Windows session."
                        }));
                    }
                    catch { }
                }
                Shutdown(0); return;
            }
            MessageBox.Show("SPEAKCITY is already open. Look for its window on your taskbar.", "SPEAKCITY", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            // Never show raw exception messages that might contain request data.
            AppStartup.Note("unhandled-exception", args.Exception.GetType().Name);
            args.Handled = true;
            if (silent) { Shutdown(1); return; }
            MessageBox.Show("SPEAKCITY encountered an unexpected problem. Please restart the app. Your saved vocabulary is kept.", "SPEAKCITY", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        string? smokeReport = smoke ? e.Args[1] : null;
        var window = new MainWindow(smokeReport);
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

using System.IO;
using System.Windows;

namespace SpeakCity;

public partial class App : Application
{
    private Mutex? _singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A mode may take a value argument (--self-test <path>) or none (--native).
        string mode = e.Args.Length >= 1 ? e.Args[0] : "";
        if (mode == "--self-test" && e.Args.Length >= 2)
        {
            int code = await SelfTests.RunAsync(e.Args[1]);
            Shutdown(code); return;
        }
        if (mode == "--diagnose" && e.Args.Length >= 2)
        {
            // Writes one small local JSON file (no conversation content, no keys) so an
            // install that "does not open" can be told apart from one that cannot speak.
            int code = await StartupDiagnostics.RunAsync(e.Args[1]);
            Shutdown(code); return;
        }
        bool smoke = mode == "--ui-smoke" && e.Args.Length >= 2;
        // The native WPF window is the default interface. It needs no browser
        // component at all. --webview still opens the old WebView2 window so the
        // two can be compared while the browser dependency is being removed.
        bool webview = mode == "--webview";
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
        // The smoke check still drives the WebView2 window for now, so CI keeps
        // producing the same evidence until the browser dependency is removed.
        if (!webview && !smoke)
        {
            var nativeWindow = new NativeMainWindow();
            MainWindow = nativeWindow;
            nativeWindow.Show();
            return;
        }
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

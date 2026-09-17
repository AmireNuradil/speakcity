using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace SpeakCity;

public sealed class MainWindow : Window
{
    private const string AppOrigin = "https://speakcity.local";
    private const string WebView2Page = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private readonly WebView2 _web = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ApiConfig _config = ApiConfigStore.Load();
    private AppRouter? _router;
    private bool _closing;
    private bool? _microphonePermission;
    private readonly string? _smokeReport;
    private bool _smokeStarted;

    public MainWindow(string? smokeReport = null)
    {
        _smokeReport = smokeReport;
        Title = "SPEAKCITY AI"; Width = 1320; Height = 900; MinWidth = 850; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = System.Windows.Media.Brushes.White;
        Content = new TextBlock { Text = "Opening SPEAKCITY…", Margin = new Thickness(35), FontSize = 23 };
        Loaded += async (_, _) => await InitializeAsync();
        Closing += (_, _) =>
        {
            _closing = true; _shutdown.Cancel(); _router?.Dispose(); _web.Dispose();
        };
    }

    private async Task InitializeAsync()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "ui");
        if (!File.Exists(Path.Combine(folder, "index.html")))
        {
            ShowStartupError("SPEAKCITY's interface files are missing from the installed folder. Reinstall the app using the official installer.");
            return;
        }
        AppStartup.Note("startup", "begin");
        var runtime = await AppStartup.AcquireRuntimeAsync();
        if (!runtime.Ready)
        {
            AppStartup.Note("startup", runtime.Status, runtime.Detail);
            // A build agent has no desktop to paint on. Say so and exit cleanly instead
            // of reporting a failure that describes the machine, not the app.
            if (runtime.Status == "no-desktop" && _smokeReport is not null)
            {
                WriteSmokeReport(new { status = "skipped", passed = true, skipped = true, skipped_reason = "non-interactive-session", error = runtime.Advice });
                Application.Current.Shutdown(0);
                return;
            }
            ShowStartupError(runtime.Advice, runtime);
            return;
        }
        AppStartup.Note("startup", "browser-component-ready", $"{runtime.ElapsedMs}ms via {runtime.Status}");
        try
        {
            var bridge = _web.EnsureCoreWebView2Async(runtime.Runtime);
            if (await Task.WhenAny(bridge, Task.Delay(AppStartup.BridgeDeadline)) != bridge)
            {
                _ = bridge.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                AppStartup.Note("startup", "bridge-timeout");
                ShowStartupError("The interface process started but never connected to the window. Reinstall SPEAKCITY, and if it continues allow SPEAKCITY and msedgewebview2.exe in your security software.");
                return;
            }
            await bridge;
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("speakcity.local", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => { if (!IsAppUri(args.Uri)) args.Cancel = true; };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) =>
            {
                if (!IsAppUri(args.Uri) || args.PermissionKind != CoreWebView2PermissionKind.Microphone)
                { args.State = CoreWebView2PermissionState.Deny; return; }
                if (AppStartup.Automated) { args.State = CoreWebView2PermissionState.Deny; return; }
                _microphonePermission ??= MessageBox.Show(this,
                    "Allow SPEAKCITY to use your microphone for English practice? Speech is transcribed locally; transcript text is sent to your configured AI provider. You can always type instead.",
                    "SPEAKCITY microphone", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                args.State = _microphonePermission.Value ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };
            core.DownloadStarting += (_, args) =>
            {
                if (!args.DownloadOperation.Uri.StartsWith("blob:" + AppOrigin + "/", StringComparison.Ordinal))
                { args.Cancel = true; return; }
                var save = new SaveFileDialog { FileName = "speakcity-vocabulary.json", Filter = "Vocabulary JSON (*.json)|*.json", AddExtension = true, DefaultExt = ".json" };
                if (save.ShowDialog(this) == true) { args.ResultFilePath = save.FileName; args.Handled = true; }
                else args.Cancel = true;
            };
            var api = new ApiClient(() => _config);
            _router = new AppRouter(new SpeechWorkerClient(), ConfigureAsync, () => _config, api.CompleteJsonAsync);
            core.AddWebResourceRequestedFilter(AppOrigin + "/api/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnAppRequest;
            if (_smokeReport is not null)
                core.NavigationCompleted += async (_, args) => { if (!_smokeStarted) { _smokeStarted = true; await RunUiSmokeAsync(args.IsSuccess); } };
            Content = _web;
            core.Navigate(AppOrigin + "/index.html");
            AppStartup.Note("startup", "navigating");
        }
        catch (Exception exc)
        {
            AppStartup.Note("startup", "failed", exc.GetType().Name);
            ShowStartupError("SPEAKCITY could not start its packaged interface. Repair or reinstall the app. Do not install Python or run developer commands.");
        }
    }

    private void WriteSmokeReport(object value)
    {
        if (_smokeReport is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_smokeReport))!);
            File.WriteAllText(_smokeReport, System.Text.Json.JsonSerializer.Serialize(value));
        }
        catch { }
    }

    private void ShowStartupError(string message) => ShowStartupError(message, null);

    /// <summary>
    /// A message the learner can act on, inside the window, with a retry. No modal
    /// dialog: a modal that nobody can click is how a slow start becomes a frozen app.
    /// </summary>
    private void ShowStartupError(string message, RuntimeResult? runtime)
    {
        var panel = new StackPanel { Margin = new Thickness(48), MaxWidth = 820, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "SPEAKCITY could not open", FontSize = 30, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock
        {
            Text = runtime is null
                ? "A technical note was saved in the startup file listed below."
                : $"Check performed in {runtime.ElapsedMs} ms (status: {runtime.Status}). Folder: {runtime.ProfilePath}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock { Text = $"Details: {AppStartup.TraceFile}  \u2014  ask for that file if you need help; it never contains your conversations.", TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = System.Windows.Media.Brushes.Gray });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        var retry = new Button { Content = "Try again", Padding = new Thickness(18, 9, 18, 9), FontSize = 16, Margin = new Thickness(0, 0, 12, 0) };
        retry.Click += async (_, _) =>
        {
            Content = new TextBlock { Text = "Opening SPEAKCITY\u2026", Margin = new Thickness(35), FontSize = 23 };
            await InitializeAsync();
        };
        var getRuntime = new Button { Content = "Get the browser component from Microsoft", Padding = new Thickness(18, 9, 18, 9), FontSize = 16 };
        getRuntime.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(WebView2Page) { UseShellExecute = true }); } catch { }
        };
        buttons.Children.Add(retry);
        buttons.Children.Add(getRuntime);
        panel.Children.Add(buttons);
        Content = panel;
        if (_smokeReport is not null)
        {
            WriteSmokeReport(new { status = "failed", passed = false, error = message, stage = runtime?.Status });
            Application.Current.Shutdown(1);
        }
    }

    private async Task RunUiSmokeAsync(bool navigated)
    {
        int exit = 1;
        try
        {
            if (!navigated) throw new InvalidOperationException("Native page navigation failed.");
            await _web.CoreWebView2.ExecuteScriptAsync("window.__nativeSmoke = null; fetch('/api/bootstrap',{headers:{'X-Speakcity':'1'}}).then(r=>r.json()).then(d=>{window.__nativeSmoke={desktop:d.desktop,scenarios:d.scenarios.length,tts:d.tts_installed,stt:d.stt_installed};}).catch(()=>{window.__nativeSmoke={error:true};});");
            string result = "null";
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(250);
                result = await _web.CoreWebView2.ExecuteScriptAsync("window.__nativeSmoke");
                if (result != "null") break;
            }
            var data = System.Text.Json.Nodes.JsonNode.Parse(result)?.AsObject();
            if (data is null || data["desktop"]?.GetValue<bool>() != true || data["scenarios"]?.GetValue<int>() != 8)
                throw new InvalidOperationException("Native API bridge or eight-scenario bootstrap failed.");
            string markup = await _web.CoreWebView2.ExecuteScriptAsync("({pinCount:document.querySelectorAll('.pin[data-start]').length,hasSettings:!!document.querySelector('[data-nav=\"settings\"]'),hasPage:!!document.querySelector('.page')})");
            var ui = System.Text.Json.Nodes.JsonNode.Parse(markup)?.AsObject();
            bool passed = ui?["pinCount"]?.GetValue<int>() == 8 && ui?["hasSettings"]?.GetValue<bool>() == true;
            string imagePath = Path.ChangeExtension(_smokeReport!, ".png");
            await using (var output = File.Create(imagePath)) await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            await File.WriteAllTextAsync(_smokeReport!, System.Text.Json.JsonSerializer.Serialize(new { status = passed ? "passed" : "failed", passed, bootstrap = data, ui, screenshot = Path.GetFileName(imagePath), scope = "Real WebView2 Windows UI; no live API credential or physical microphone used." }));
            exit = passed ? 0 : 1;
        }
        catch
        {
            WriteSmokeReport(new { status = "failed", passed = false, error = "Native UI smoke test failed." });
        }
        Application.Current.Shutdown(exit);
    }

    private static bool IsAppUri(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "speakcity.local" && uri.IsDefaultPort;

    private Task<bool> ConfigureAsync()
    {
        var dialog = new ApiSettingsWindow(_config) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Saved) _config = ApiConfigStore.Load();
        return Task.FromResult(ApiConfigStore.IsConfigured(_config));
    }

    private async void OnAppRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (_closing || _router is null) return;
            var request = args.Request;
            if (!IsAppUri(request.Uri) || !request.Headers.Contains("X-Speakcity") || request.Headers.GetHeader("X-Speakcity") != "1")
            { SetResponse(args, AppResponse.Error("Request rejected.", 403)); return; }
            var path = new Uri(request.Uri).AbsolutePath;
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            { SetResponse(args, AppResponse.Error("Unknown command.", 404)); return; }
            if (request.Headers.Contains("Origin") && request.Headers.GetHeader("Origin") != AppOrigin)
            { SetResponse(args, AppResponse.Error("Origin rejected.", 403)); return; }
            int max = path == "/api/stt" ? 3 * 1024 * 1024 : 8192;
            using var data = new MemoryStream();
            if (request.Content is not null)
            {
                byte[] buffer = new byte[16384];
                int read;
                while ((read = await request.Content.ReadAsync(buffer, _shutdown.Token)) > 0)
                {
                    if (data.Length + read > max) { SetResponse(args, AppResponse.Error("body_too_large", 413)); return; }
                    data.Write(buffer, 0, read);
                }
            }
            var result = await _router.HandleAsync(request.Method.ToUpperInvariant(), path, data.ToArray(), _shutdown.Token);
            if (!_closing) SetResponse(args, result);
        }
        catch (OperationCanceledException) { if (!_closing) SetResponse(args, AppResponse.Error("Request canceled.", 499)); }
        catch { if (!_closing) SetResponse(args, AppResponse.Error("The app request could not finish. Please retry.", 500)); }
        finally { try { deferral.Complete(); } catch when (_closing) { } }
    }

    private void SetResponse(CoreWebView2WebResourceRequestedEventArgs args, AppResponse response)
    {
        var stream = new MemoryStream(response.Body, writable: false);
        args.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, response.Status,
            response.Status == 200 ? "OK" : "Request failed",
            $"Content-Type: {response.ContentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
    }
}

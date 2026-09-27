using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpeakCity;

/// <summary>
/// Smoke test of the native path the learner uses, in two parts. First the real
/// router, the real bundled speech worker (ping and Kokoro TTS) and the real scenario
/// catalog, with a scripted AI completion standing in for the provider. Then the
/// window itself: it is opened, driven from the city map through a conversation to the
/// end-of-run review, and rendered to PNG files beside the report. The JSON report keeps
/// the shape the build's UI gate reads (status/passed/skipped/error).
/// </summary>
public static class NativeSmoke
{
    private const string Scope = "Native WPF path: AppRouter, bundled speech worker, Kokoro voice and the native window. AI provider is a test double; no live API credentials used.";

    public static async Task<int> RunAsync(string reportPath)
    {
        var checks = new List<string>();
        var timings = new JsonObject();
        var screenshots = new JsonArray();
        void Check(string name, bool ok) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            string catalogPath = Path.Combine(AppContext.BaseDirectory, "content", "scenarios.json");
            var trace = new JsonArray();
            foreach (var pair in AppStartup.Snapshot()) trace.Add($"{pair.Key}={pair.Value}");
            var report = new JsonObject
            {
                ["scope"] = Scope,
                ["catalog_present"] = File.Exists(catalogPath),
                ["startup_trace"] = trace
            };
            if (!report["catalog_present"]!.GetValue<bool>())
                throw new InvalidOperationException("The scenario package is missing from the build output.");

            // Part 1 is scoped so its worker process is gone before the window starts its own.
            {
                using var worker = new SpeechWorkerClient();
                report["worker_path_source"] = worker.ResolvedFrom;
                var ping = await worker.RequestAsync("ping");
                Check("Bundled speech worker starts and answers ping", ping["models_ready"]?.GetValue<bool>() == true);
                var spoken = await worker.RequestAsync("tts", new JsonObject
                {
                    ["text"] = "Welcome to the airport. How can I help you today?",
                    ["voice"] = "american", ["speed"] = 0.95
                });
                byte[] audio = Convert.FromBase64String(spoken["audio_base64"]!.GetValue<string>());
                Check("Kokoro returns WAV audio", audio.Length > 44 && Encoding.ASCII.GetString(audio, 0, 4) == "RIFF");

                using var router = new AppRouter(worker, () => Task.FromResult(true), () => SmokeConfig, Mock, catalogPath);
                async Task<JsonObject> Call(string method, string path, byte[] body)
                {
                    var response = await router.HandleAsync(method, path, body);
                    if (response.Status != 200)
                        throw new InvalidOperationException($"{method} {path} returned HTTP {response.Status}: {(response.Body.Length > 300 ? response.Body[..300] : response.Body)}");
                    var payload = JsonNode.Parse(response.Body)?.AsObject()
                        ?? throw new InvalidOperationException($"{method} {path} returned a non-object body.");
                    checks.Add($"{method} {path.Split('/').Last()} succeeds");
                    return payload;
                }
                var bootstrap = await Call("GET", "/api/bootstrap", []);
                Check("Bootstrap reports voice ready", bootstrap["tts_installed"]?.GetValue<bool>() == true && bootstrap["stt_installed"]?.GetValue<bool>() == true);
                Check("Bootstrap exposes eight scenarios", bootstrap["scenarios"]!.AsArray().Count == 8);
                var start = await Call("POST", "/api/sessions", Encoding.UTF8.GetBytes(new JsonObject { ["scenario"] = "airport", ["level"] = "A2" }.ToJsonString()));
                string sessionId = start["session_id"]!.GetValue<string>();
                Check("Start conversation returns Lucy's opening", start["reply"]?.GetValue<string>().Contains("airport", StringComparison.OrdinalIgnoreCase) == true);
                var turn = await Call("POST", $"/api/sessions/{sessionId}/turn", Encoding.UTF8.GetBytes(new JsonObject { ["text"] = "I want book a room.", ["request_id"] = Guid.NewGuid().ToString("N") }.ToJsonString()));
                Check("Turn returns Lucy reply without end-of-run corrections", turn["reply"] is not null && !turn.ContainsKey("corrections"));
                var finish = await Call("POST", $"/api/sessions/{sessionId}/finish", Encoding.UTF8.GetBytes(new JsonObject { ["language"] = "en" }.ToJsonString()));
                Check("Finish returns end-only vocabulary", finish["vocabulary"]!.AsArray().Count == 6);
            }

            await WindowChecksAsync(reportPath, Check, timings, screenshots);

            report["checks"] = JsonSerializer.SerializeToNode(checks);
            report["timings_ms"] = timings;
            report["screenshots"] = screenshots;
            report["passed"] = true;
            report["status"] = "passed";
            Write(reportPath, report);
            return 0;
        }
        catch (Exception error)
        {
            var trace = new JsonArray();
            foreach (var pair in AppStartup.Snapshot()) trace.Add($"{pair.Key}={pair.Value}");
            var report = new JsonObject
            {
                ["scope"] = Scope,
                ["passed"] = false,
                ["status"] = "failed",
                ["error"] = error is InvalidOperationException ? error.Message : error.GetType().Name,
                ["checks_passed_before_failure"] = JsonSerializer.SerializeToNode(checks),
                ["timings_ms"] = timings,
                ["screenshots"] = screenshots,
                ["startup_trace"] = trace
            };
            Write(reportPath, report);
            return 1;
        }
    }

    private static ApiConfig SmokeConfig => new() { BaseUrl = "https://example.invalid/v1", Model = "smoke", ApiKey = "unused" };

    /// <summary>Scripted provider: one known mistake gets a correction, anything else gets none.</summary>
    private static Task<JsonObject> Mock(string prompt, IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct)
    {
        if (prompt.Contains("grammar teacher", StringComparison.Ordinal))
        {
            var corrections = new JsonArray();
            if (messages.Any(message => message.Content.Contains("I want book a room.", StringComparison.Ordinal)))
                corrections.Add(new JsonObject
                {
                    ["original"] = "I want book a room.", ["corrected"] = "I want to book a room.",
                    ["explanation"] = new JsonObject { ["en"] = "Use want + to + verb.", ["kk"] = "Want сөзінен кейін to + етістік қолданылады.", ["ru"] = "После want используйте to + глагол." }
                });
            return Task.FromResult(new JsonObject { ["corrections"] = corrections });
        }
        return Task.FromResult(new JsonObject { ["reply"] = "Thanks. What would you like to do next?" });
    }

    /// <summary>Opens the real window with the scripted provider and walks the learner's path.</summary>
    private static async Task WindowChecksAsync(string reportPath, Action<string, bool> check, JsonObject timings, JsonArray screenshots)
    {
        var app = Application.Current;
        var previousMode = app?.ShutdownMode ?? ShutdownMode.OnLastWindowClose;
        // Closing the test window must not end the process before the report is written.
        if (app is not null) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new NativeMainWindow(SmokeConfig, Mock)
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };
        try
        {
            var clock = Stopwatch.StartNew();
            window.Show();
            await WithTimeout(window.Ready, 60, "The native window did not finish loading");
            check("Native window opens with eight pins on the city map", window.Pins.Count(pin => pin.Visibility == Visibility.Visible) == 8);
            check("City map picture is drawn", window.MapPicture is { PixelWidth: > 0 });
            check("Lucy's portrait is drawn on the city screen", window.LucyPicture is { PixelWidth: > 0 });
            check("Every map pin is a named, focusable button", window.Pins.All(pin =>
                pin.Focusable && pin.IsTabStop && pin.IsEnabled && AutomationProperties.GetName(pin).Length > 0));
            // The same routed event that Enter, Space or a click raise on a focused pin.
            window.Pins.First(pin => (string)pin.Tag == "cafe").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            check("Choosing a pin selects that place", window.SelectedPlaceId == "cafe");
            screenshots.Add(await SnapshotAsync(window, reportPath, "native-ui-city.png"));

            if (window.VoiceWarmup is { } warmup) await Task.WhenAny(warmup, Task.Delay(TimeSpan.FromSeconds(150)));
            timings["window_open_to_voice_warm_ms"] = clock.ElapsedMilliseconds;
            var ping = await window.PingVoiceForTestAsync();
            check("Voice warm-up loads Kokoro and Whisper before the first turn",
                ping?["tts"]?["loaded"]?.GetValue<bool>() == true && ping?["stt"]?["loaded"]?.GetValue<bool>() == true);

            await window.StartForTestAsync();
            check("Start opens the practice screen with the scene portrait",
                window.InPractice && window.PortraitPicture is { PixelWidth: > 0 } && window.PortraitPainted);
            if (window.CurrentSpeech is { } greeting) await Task.WhenAny(greeting, Task.Delay(TimeSpan.FromSeconds(60)));
            if (window.FirstVoiceDelay is { } greetingDelay) timings["greeting_first_voice_ms"] = (long)greetingDelay.TotalMilliseconds;
            check("Lucy's greeting is voiced sentence by sentence", window.VoicedSentences >= 2);

            await window.SendForTestAsync("I want book a room.");
            check("Lucy's replies carry her avatar", window.LucyAvatars >= 2);
            window.ShowThinkingForTest(true);
            window.ShowRecordingForTest(true);
            window.ShowToast("Transcribed. Edit it if needed, then press Send.");
            screenshots.Add(await SnapshotAsync(window, reportPath, "native-ui-practice.png"));
            window.ShowThinkingForTest(false);
            window.ShowRecordingForTest(false);
            if (window.CurrentSpeech is { } reply) await Task.WhenAny(reply, Task.Delay(TimeSpan.FromSeconds(60)));
            if (window.FirstVoiceDelay is { } replyDelay) timings["reply_first_voice_ms"] = (long)replyDelay.TotalMilliseconds;

            await window.FinishForTestAsync();
            check("Finish shows the correction in the window",
                window.FeedbackShown && window.ChatLines.Any(line => line.Contains("I want to book a room.", StringComparison.Ordinal)));
            screenshots.Add(await SnapshotAsync(window, reportPath, "native-ui-feedback.png"));

            // A second round where the provider returns no corrections must not be told it was perfect.
            await window.StartForTestAsync();
            await window.SendForTestAsync("A coffee, please.");
            await window.FinishForTestAsync();
            var lines = window.ChatLines.ToList();
            check("An empty review is reported honestly, not as perfect English",
                window.FeedbackShown && lines.Contains("No clear grammar corrections were returned.")
                && !lines.Any(line => line.Contains("Well done", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            window.Close();
            if (app is not null) app.ShutdownMode = previousMode;
        }
    }

    private static async Task WithTimeout(Task task, int seconds, string message)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task)
            throw new InvalidOperationException(message);
        await task;
    }

    /// <summary>Renders the window's content (not a screen grab) to a PNG beside the report.</summary>
    private static async Task<string> SnapshotAsync(Window window, string reportPath, string name)
    {
        window.UpdateLayout();
        await Task.Delay(600);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(root.ActualWidth)), Math.Max(1, (int)Math.Ceiling(root.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, name);
        using (var file = File.Create(path)) encoder.Save(file);
        return name;
    }

    private static void Write(string reportPath, JsonObject report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

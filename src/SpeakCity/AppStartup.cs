using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace SpeakCity;

/// <summary>
/// Result of one bounded attempt to start the browser component.
/// Statuses: ready, ready-after-fallback, no-desktop, timeout, runtime-missing,
/// profile-unwritable, failed.
/// </summary>
public sealed record RuntimeResult(CoreWebView2Environment? Runtime, string ProfilePath, string Status, int ElapsedMs, string? Detail)
{
    public bool Ready => Runtime is not null;

    /// <summary>What the learner sees. Never a stack trace, never a path they cannot use.</summary>
    public string Advice => Status switch
    {
        "no-desktop" => "SPEAKCITY needs a normal signed-in Windows desktop. It cannot start from a background or automated session.",
        "runtime-missing" => "The Microsoft Edge WebView2 Runtime is not installed. Run the SPEAKCITY installer again and let it install the official Microsoft component, or install that runtime from Microsoft.",
        "profile-unwritable" => "SPEAKCITY cannot write its own interface folder. An antivirus or folder permission is blocking it: allow the SPEAKCITY folder in your user profile, then start again.",
        "timeout" => "SPEAKCITY waited for the browser component and it did not answer. This is usually antivirus holding the browser process, or an out-of-date Edge WebView2 Runtime. Try again below, or repair the runtime from Microsoft.",
        _ => "SPEAKCITY could not start its interface. Reinstall the app from the official installer. You do not need Python or any developer tool."
    };
}

/// <summary>
/// Bounded, self-healing startup for the packaged interface.
///
/// Creating a WebView2 environment never times itself out. A locked browser profile,
/// security software holding the browser process, or a session with no desktop all
/// present exactly the same way to a learner: a window reading "Opening SPEAKCITY…"
/// forever, with no error, because nothing ever failed. Every step here therefore has
/// a deadline, one automatic retry with a different profile folder, a plain-words
/// message, and a small local trace file so the problem can be identified at all.
/// </summary>
public static class AppStartup
{
    public static readonly TimeSpan RuntimeDeadline = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan BridgeDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Set by the build agent. An automated run must never be blocked by a modal dialog.</summary>
    public static bool Automated { get; } = Environment.GetEnvironmentVariable("SPEAKCITY_AUTOMATION") == "1";

    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeakCity");
    public static string PrimaryProfile => Path.Combine(DataRoot, "WebViewProfile");
    public static string FallbackProfile => Path.Combine(Path.GetTempPath(), "SPEAKCITY-WebViewProfile");
    public static string TraceFile => Path.Combine(DataRoot, "startup.json");

    /// <summary>
    /// A window can only be painted in an interactive desktop session. In session 0 the
    /// browser process starts and then waits for a surface that will never exist, which
    /// is indistinguishable from a slow start unless it is checked.
    /// </summary>
    public static bool HasDesktop { get; } = DetectDesktop();

    private static bool DetectDesktop()
    {
        try { return Environment.UserInteractive && Process.GetCurrentProcess().SessionId != 0; }
        catch { return true; }   // A failed check must never be the reason the app refuses to start.
    }

    public static string? BrowserVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return null; }
    }

    public static bool LoaderBesideApp => File.Exists(Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll"));

    /// <summary>onnxruntime and CTranslate2 need this Windows component; its absence is a silent worker failure.</summary>
    public static bool VisualCppRuntimePresent => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vcruntime140.dll"));

    public static bool PrepareProfile(string directory, out string reason)
    {
        reason = "";
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception exc) { reason = exc.GetType().Name; return false; }
    }

    public static async Task<RuntimeResult> AcquireRuntimeAsync()
    {
        if (!HasDesktop) return new RuntimeResult(null, PrimaryProfile, "no-desktop", 0, null);

        var profiles = new List<string>();
        string detail = "";
        if (PrepareProfile(PrimaryProfile, out string primaryReason)) profiles.Add(PrimaryProfile);
        else detail = $"primary profile refused ({primaryReason})";
        if (PrepareProfile(FallbackProfile, out _) && !profiles.Contains(FallbackProfile)) profiles.Add(FallbackProfile);
        if (profiles.Count == 0) return new RuntimeResult(null, PrimaryProfile, "profile-unwritable", 0, detail);

        for (int index = 0; index < profiles.Count; index++)
        {
            string profile = profiles[index];
            CoreWebView2EnvironmentOptions? options = null;
            if (Automated)
            {
                // A virtual build machine has no GPU; without this the browser can stall
                // waiting for a compositor that does not exist.
                options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-gpu --disable-gpu-compositing" };
            }
            var watch = Stopwatch.StartNew();
            Task<CoreWebView2Environment> creation;
            try { creation = CoreWebView2Environment.CreateAsync(null, profile, options); }
            catch (Exception exc) { return new RuntimeResult(null, profile, "failed", (int)watch.ElapsedMilliseconds, exc.GetType().Name); }

            if (await Task.WhenAny(creation, Task.Delay(RuntimeDeadline)) != creation)
            {
                // Keep the abandoned task observed so a late fault cannot surface on the
                // finalizer thread, then try the alternate profile once before reporting.
                _ = creation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                Note("runtime-attempt", "timeout", profile);
                continue;
            }
            try
            {
                var runtime = await creation;
                Note("runtime-attempt", index == 0 ? "ready" : "ready-after-fallback", $"{profile} in {watch.ElapsedMilliseconds}ms");
                return new RuntimeResult(runtime, profile, index == 0 ? "ready" : "ready-after-fallback", (int)watch.ElapsedMilliseconds, detail.Length == 0 ? null : detail);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return new RuntimeResult(null, profile, "runtime-missing", (int)watch.ElapsedMilliseconds, detail);
            }
            catch (Exception exc)
            {
                return new RuntimeResult(null, profile, "failed", (int)watch.ElapsedMilliseconds, exc.GetType().Name);
            }
        }
        return new RuntimeResult(null, profiles[^1], "timeout", (int)RuntimeDeadline.TotalMilliseconds * profiles.Count, detail);
    }

    public static JsonObject Snapshot()
    {
        bool primary = PrepareProfile(PrimaryProfile, out string primaryReason);
        bool fallback = PrepareProfile(FallbackProfile, out string fallbackReason);
        return new JsonObject
        {
            ["app_version"] = typeof(AppStartup).Assembly.GetName().Version?.ToString() ?? "0.2.0",
            ["created_utc"] = DateTime.UtcNow.ToString("O"),
            ["desktop_session"] = HasDesktop,
            ["automated_session"] = Automated,
            ["webview2_runtime_version"] = BrowserVersion(),
            ["webview2_loader_beside_app"] = LoaderBesideApp,
            ["visual_cpp_runtime_present"] = VisualCppRuntimePresent,
            ["interface_folder"] = Path.Combine(AppContext.BaseDirectory, "ui"),
            ["primary_profile"] = new JsonObject { ["path"] = PrimaryProfile, ["writable"] = primary, ["reason"] = primary ? null : primaryReason },
            ["fallback_profile"] = new JsonObject { ["path"] = FallbackProfile, ["writable"] = fallback, ["reason"] = fallback ? null : fallbackReason }
        };
    }

    /// <summary>
    /// One small file describing the last start: stage names, timings, exception type
    /// names and folders. It never contains conversation text, recordings, provider URLs
    /// or keys, so it is safe for a learner to send to support.
    /// </summary>
    public static void Note(string stage, string status, string? detail = null)
    {
        lock (_trace)
        {
            _trace.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {stage}={status}{(string.IsNullOrEmpty(detail) ? "" : " " + detail)}");
            if (_trace.Count > 40) _trace.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(DataRoot);
                File.WriteAllText(TraceFile, JsonSerializer.Serialize(new
                {
                    app = "SPEAKCITY AI",
                    desktop_session = HasDesktop,
                    webview2 = BrowserVersion(),
                    trace = _trace.ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* a failing app must never be made worse by its own diagnostics */ }
        }
    }

    private static readonly List<string> _trace = new();
}

/// <summary>
/// "SpeakCity.exe --diagnose out.json" - everything needed to tell a broken install,
/// a blocked browser, a missing Windows component and a missing model apart, without
/// the learner running any developer command beyond this one.
/// </summary>
public static class StartupDiagnostics
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var report = AppStartup.Snapshot();
        var speech = new JsonObject { ["path"] = new SpeechWorkerClient().WorkerPath };
        report["speech_worker"] = speech;
        report["ai_provider"] = new JsonObject { ["configured"] = ApiConfigStore.IsConfigured(ApiConfigStore.Load()) };
        try
        {
            using var worker = new SpeechWorkerClient();
            speech["executable_present"] = File.Exists(worker.WorkerPath);
            var watch = Stopwatch.StartNew();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ping = await worker.RequestAsync("ping", null, limit.Token);
            speech["started"] = true;
            speech["elapsed_ms"] = watch.ElapsedMilliseconds;
            speech["models_ready"] = ping["models_ready"]?.DeepClone();
            speech["frozen"] = ping["frozen"]?.DeepClone();
        }
        catch (Exception exc)
        {
            speech["started"] = false;
            speech["error_type"] = exc.GetType().Name;
            speech["error"] = exc.Message.Length > 240 ? exc.Message[..240] : exc.Message;
        }
        var runtime = await AppStartup.AcquireRuntimeAsync();
        report["browser_component"] = new JsonObject
        {
            ["status"] = runtime.Status,
            ["profile_used"] = runtime.ProfilePath,
            ["elapsed_ms"] = runtime.ElapsedMs,
            ["detail"] = runtime.Detail,
            ["what_this_means"] = runtime.Ready ? "The interface can start on this PC." : runtime.Advice
        };
        report["likely_causes"] = LikelyCauses(report, runtime);
        try
        {
            string full = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AppStartup.Note("diagnose", "written", full);
            return 0;
        }
        catch (Exception exc)
        {
            AppStartup.Note("diagnose", "unwritable", exc.GetType().Name);
            return 1;
        }
    }

    private static JsonArray LikelyCauses(JsonObject report, RuntimeResult runtime)
    {
        bool profileWritable = report["primary_profile"]?["writable"]?.GetValue<bool>() ?? true;
        bool workerStarted = report["speech_worker"]?["started"]?.GetValue<bool>() ?? false;
        bool loaderPresent = report["webview2_loader_beside_app"]?.GetValue<bool>() ?? true;
        var causes = new JsonArray();
        if (runtime.Status == "runtime-missing") causes.Add("The WebView2 Runtime is not installed: install it from Microsoft, or run the SPEAKCITY installer again and let it install that component.");
        if (runtime.Status == "timeout") causes.Add($"The browser component did not answer within {AppStartup.RuntimeDeadline.TotalSeconds:0} seconds. Security software holding msedgewebview2.exe is the usual cause.");
        if (runtime.Status == "profile-unwritable") causes.Add("Neither SPEAKCITY interface folder can be written: allow the SPEAKCITY folder in your user profile in your security software.");
        if (runtime.Status == "no-desktop") causes.Add("No interactive Windows desktop session is attached to this process.");
        if (!profileWritable) causes.Add("The SPEAKCITY folder in your user profile is not writable.");
        if (!workerStarted) causes.Add("The bundled speech component did not answer. A missing Visual C++ runtime, or a quarantined file, is the usual cause.");
        if (!loaderPresent) causes.Add("WebView2Loader.dll is missing from the installed folder, so the installation is incomplete: reinstall it.");
        if (runtime.Ready && workerStarted && profileWritable && loaderPresent)
            causes.Add("Nothing here looks broken. If the window still does not open, run Repair from the installer and send this file.");
        return causes;
    }
}

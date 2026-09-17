using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeakCity;

public static class SelfTests
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var checks = new List<string>();
        var report = new JsonObject
        {
            ["scope"] = "Windows packaged application core and actual bundled speech. Conversation provider is a test double; no live API credentials used.",
            ["os"] = Environment.OSVersion.VersionString,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
        };
        void Check(string name, bool ok) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Check("All packaged interface assets exist", new[] { "index.html", "app.js", "app.css", "i18n.js", "recorder.js", "recorder-worklet.js", "scenario-catalog.js" }.All(f => File.Exists(Path.Combine(AppContext.BaseDirectory, "ui", f))));
            // Recorded, not asserted: these describe the machine, and a build agent
            // legitimately differs from a learner's desktop. They exist so a failed or
            // skipped window test can be read as an environment fact, not a guess.
            report["startup_checks"] = new JsonObject
            {
                ["webview2_runtime_version"] = AppStartup.BrowserVersion(),
                ["webview2_loader_beside_app"] = AppStartup.LoaderBesideApp,
                ["interactive_desktop_session"] = AppStartup.HasDesktop,
                ["visual_cpp_runtime_present"] = AppStartup.VisualCppRuntimePresent,
                ["interface_profile_writable"] = AppStartup.PrepareProfile(AppStartup.PrimaryProfile, out _) || AppStartup.PrepareProfile(AppStartup.FallbackProfile, out _)
            };
            byte[] probe = RandomNumberGenerator.GetBytes(32);
            byte[] encrypted = ProtectedData.Protect(probe, null, DataProtectionScope.CurrentUser);
            Check("Windows protected storage round trip", ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser).SequenceEqual(probe));
            CryptographicOperations.ZeroMemory(probe);
            Check("Unconfigured API is recognized", !ApiConfigStore.IsConfigured(new ApiConfig()));
            Check("Unencrypted API URL rejected", !ApiConfigStore.IsConfigured(new ApiConfig { BaseUrl = "http://example.invalid/v1", Model = "test", ApiKey = "not-a-real-key" }));
            var config = new ApiConfig { BaseUrl = "https://example.invalid/v1", Model = "test-fixture-only", ApiKey = "not-a-real-key" };
            using var worker = new SpeechWorkerClient();
            var ping = await worker.RequestAsync("ping");
            Check("Bundled speech models ready", ping["models_ready"]?.GetValue<bool>() == true);
            bool frozen = ping["frozen"]?.GetValue<bool>() ?? ping["is_frozen"]?.GetValue<bool>() ?? false;
            report["worker_ping"] = ping.DeepClone();
            Check("Speech runs as frozen Windows executable", frozen);
            var spoken = await worker.RequestAsync("tts", new JsonObject { ["text"] = "I am flying to London and I have one suitcase.", ["voice"] = "american", ["speed"] = .95 });
            byte[] audio = Convert.FromBase64String(spoken["audio_base64"]!.GetValue<string>());
            Check("Actual Kokoro returns WAV audio", audio.Length > 44 && Encoding.ASCII.GetString(audio, 0, 4) == "RIFF");
            var heard = await worker.RequestAsync("stt", new JsonObject { ["audio_base64"] = Convert.ToBase64String(audio) });
            string text = heard["text"]!.GetValue<string>();
            Check("Actual Whisper recognizes synthetic speech", text.Contains("London", StringComparison.OrdinalIgnoreCase) && text.Contains("suitcase", StringComparison.OrdinalIgnoreCase));
            report["synthetic_speech_transcript"] = text;
            int calls = 0;
            Task<JsonObject> Mock(string prompt, IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct)
            {
                calls++;
                Check("Only user/assistant history reaches API interface", messages.All(m => m.Role is "user" or "assistant"));
                if (prompt.Contains("grammar teacher", StringComparison.Ordinal))
                    return Task.FromResult(new JsonObject { ["corrections"] = new JsonArray(new JsonObject
                    {
                        ["original"] = "I want book a room.", ["corrected"] = "I want to book a room.",
                        ["explanation"] = new JsonObject { ["en"] = "Use want + to + verb.", ["kk"] = "Want сөзінен кейін to + етістік қолданылады.", ["ru"] = "После want используйте to + глагол." }
                    }) });
                return Task.FromResult(new JsonObject { ["reply"] = "Thank you. What would you like to do next?" });
            }
            using var router = new AppRouter(worker, () => Task.FromResult(false), () => config, Mock);
            async Task<JsonObject> Command(string method, string path, JsonObject? body = null, int expected = 200)
            {
                var r = await router.HandleAsync(method, path, body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString()));
                Check($"{method} {path.Split('/').Last()} status {expected}", r.Status == expected);
                return JsonNode.Parse(r.Body)!.AsObject();
            }
            var bootstrap = await Command("GET", "/api/bootstrap");
            Check("Eight active scenarios exposed", bootstrap["scenarios"]!.AsArray().Count == 8);
            foreach (string id in new[] { "airport", "hotel", "cafe", "shop", "hospital", "directions", "school", "interview" })
            {
                var start = await Command("POST", "/api/sessions", new JsonObject { ["scenario"] = id, ["level"] = "A2", ["language"] = "kk" });
                string sid = start["session_id"]!.GetValue<string>();
                string requestId = Guid.NewGuid().ToString();
                var input = new JsonObject { ["text"] = "I want book a room.", ["request_id"] = requestId };
                int before = calls;
                var turn = await Command("POST", $"/api/sessions/{sid}/turn", input);
                Check($"{id}: actual API abstraction invoked", calls == before + 1);
                Check($"{id}: no feedback during dialogue", !turn.ContainsKey("corrections"));
                await Command("POST", $"/api/sessions/{sid}/turn", input);
                Check($"{id}: retry is idempotent", calls == before + 1);
                var end = await Command("POST", $"/api/sessions/{sid}/finish", new JsonObject { ["language"] = "ru" });
                Check($"{id}: end-only correction included", end["corrections"]!.AsArray().Count == 1);
                Check($"{id}: scenario-specific vocabulary", end["vocabulary"]!.AsArray().Count == 6 && end["vocabulary"]!.AsArray().All(w => w!["scenario"]!.GetValue<string>() == id));
                Check($"{id}: three vocabulary languages", end["vocabulary"]!.AsArray().All(w => new[] { "en", "kk", "ru" }.All(l => w!["meaning"]![l] is not null)));
                await Command("POST", $"/api/sessions/{sid}/turn", new JsonObject { ["text"] = "Another answer.", ["request_id"] = Guid.NewGuid().ToString() }, 409);
                await Command("DELETE", $"/api/sessions/{sid}");
                await Command("GET", $"/api/sessions/{sid}", expected: 404);
            }
            report["passed"] = true;
            report["checks"] = JsonSerializer.SerializeToNode(checks);
            report["remaining"] = "Real API-provider calls, Windows 11 physical microphone, interactive WebView2 UI and installer-on-clean-PC verification remain required.";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            report["passed"] = false;
            report["checks"] = JsonSerializer.SerializeToNode(checks);
            report["failed_check_or_error"] = error is InvalidOperationException ? error.Message : error.GetType().Name;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}

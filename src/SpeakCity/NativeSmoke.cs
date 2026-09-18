using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeakCity;

/// <summary>
/// Headless smoke test of the native path the learner uses: the real router, the
/// real bundled speech worker (ping and Kokoro TTS) and the real scenario catalog,
/// with a scripted AI completion standing in for the provider. It exercises
/// Start → typed turn → Lucy reply → finish feedback and writes the same JSON
/// report shape the build's UI gate reads (status/passed/skipped/error), so no
/// browser component is involved anywhere.
/// </summary>
public static class NativeSmoke
{
    public static async Task<int> RunAsync(string reportPath)
    {
        try
        {
            string catalogPath = Path.Combine(AppContext.BaseDirectory, "content", "scenarios.json");
            var trace = new JsonArray();
            foreach (var pair in AppStartup.Snapshot()) trace.Add($"{pair.Key}={pair.Value}");
            var report = new JsonObject
            {
                ["scope"] = "Native WPF path: AppRouter, bundled speech worker and Kokoro voice. AI provider is a test double; no live API credentials used.",
                ["catalog_present"] = File.Exists(catalogPath),
                ["startup_trace"] = trace
            };
            if (!report["catalog_present"]!.GetValue<bool>())
                throw new InvalidOperationException("The scenario package is missing from the build output.");
            var checks = new List<string>();
            void Check(string name, bool ok) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }

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

            static Task<JsonObject> Mock(string prompt, IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct)
            {
                if (prompt.Contains("grammar teacher", StringComparison.Ordinal))
                    return Task.FromResult(new JsonObject { ["corrections"] = new JsonArray(new JsonObject
                    {
                        ["original"] = "I want book a room.", ["corrected"] = "I want to book a room.",
                        ["explanation"] = new JsonObject { ["en"] = "Use want + to + verb.", ["kk"] = "Want сөзінен кейін to + етістік қолданылады.", ["ru"] = "После want используйте to + глагол." }
                    }) });
                return Task.FromResult(new JsonObject { ["reply"] = "Thanks. What would you like to do next?" });
            }
            using var router = new AppRouter(worker, () => Task.FromResult(true), () => new ApiConfig { BaseUrl = "https://example.invalid/v1", Model = "smoke", ApiKey = "unused" }, Mock, catalogPath);
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

            report["checks"] = JsonSerializer.SerializeToNode(checks);
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
                ["scope"] = "Native WPF path: AppRouter, bundled speech worker and Kokoro voice. AI provider is a test double; no live API credentials used.",
                ["passed"] = false,
                ["status"] = "failed",
                ["error"] = error is InvalidOperationException ? error.Message : error.GetType().Name,
                ["startup_trace"] = trace
            };
            Write(reportPath, report);
            return 1;
        }
    }

    private static void Write(string reportPath, JsonObject report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

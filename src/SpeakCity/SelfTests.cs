using System.Diagnostics;
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
            // The native window's pictures: every scenario's picture must decode, and a missing
            // one must fail here instead of showing up as an empty grey card on a learner's PC.
            var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "scenarios.json")))!.AsObject();
            foreach (var pair in catalog)
            {
                var picture = NativeAssets.Load(NativeAssets.ForScenario(pair.Value?["image"]?.GetValue<string>()));
                Check($"{pair.Key}: native scene picture decodes", picture.PixelWidth > 0 && new System.Windows.Controls.Image { Source = picture }.Source is not null);
            }
            Check("Native city map and Lucy portrait decode", NativeAssets.Load(NativeAssets.CityPicture).PixelWidth > 0 && NativeAssets.Load(NativeAssets.LucyPicture).PixelWidth > 0);
            bool missingReported;
            try { NativeAssets.Load("not-packaged.jpg"); missingReported = false; }
            catch (InvalidOperationException) { missingReported = true; }
            Check("A missing native picture fails loudly", missingReported);
            Check("Lucy's lines split into sentences without losing words", ChunksKeepWords());
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
            var clock = Stopwatch.StartNew();
            var spoken = await worker.RequestAsync("tts", new JsonObject { ["text"] = "I am flying to London and I have one suitcase.", ["voice"] = "american", ["speed"] = .95 });
            var timings = new JsonObject { ["tts_first_call_ms"] = clock.ElapsedMilliseconds };
            byte[] audio = Convert.FromBase64String(spoken["audio_base64"]!.GetValue<string>());
            Check("Actual Kokoro returns WAV audio", audio.Length > 44 && Encoding.ASCII.GetString(audio, 0, 4) == "RIFF");
            clock.Restart();
            var heard = await worker.RequestAsync("stt", new JsonObject { ["audio_base64"] = Convert.ToBase64String(audio) });
            timings["stt_first_call_ms"] = clock.ElapsedMilliseconds;
            string text = heard["text"]!.GetValue<string>();
            Check("Actual Whisper recognizes synthetic speech", text.Contains("London", StringComparison.OrdinalIgnoreCase) && text.Contains("suitcase", StringComparison.OrdinalIgnoreCase));
            report["synthetic_speech_transcript"] = text;
            // Recorded, not asserted (machine-dependent): what sentence-by-sentence voicing saves
            // on this CPU, and what a warm transcription costs once the models are loaded.
            const string reply = "That sounds lovely, thank you. We have a quiet table by the window. Would you like to start with a drink while you look at the menu?";
            clock.Restart();
            await worker.RequestAsync("tts", new JsonObject { ["text"] = reply, ["voice"] = "american", ["speed"] = .95 });
            timings["tts_warm_whole_reply_ms"] = clock.ElapsedMilliseconds;
            clock.Restart();
            await worker.RequestAsync("tts", new JsonObject { ["text"] = SpeechChunks.Split(reply)[0], ["voice"] = "american", ["speed"] = .95 });
            timings["tts_warm_first_sentence_ms"] = clock.ElapsedMilliseconds;
            clock.Restart();
            await worker.RequestAsync("stt", new JsonObject { ["audio_base64"] = Convert.ToBase64String(audio) });
            timings["stt_warm_ms"] = clock.ElapsedMilliseconds;
            report["timings_ms"] = timings;
            // The learner speaks quietly on purpose; the capture has to lift that without turning an
            // empty room into invented speech.
            static byte[] Tone(int amplitude, int frames)
            {
                var bytes = new byte[frames * 2];
                for (int index = 0; index < frames; index++)
                    BitConverter.GetBytes((short)(amplitude * Math.Sin(index / 8.0))).CopyTo(bytes, index * 2);
                return bytes;
            }
            byte[] quiet = WavRecorder.Normalize(Tone(2000, 8000), 2000);
            int lifted = 0;
            for (int index = 0; index + 1 < quiet.Length; index += 2) lifted = Math.Max(lifted, Math.Abs(BitConverter.ToInt16(quiet, index)));
            Check("Quiet speech is lifted to a recognisable level", lifted > 8000 && lifted <= short.MaxValue);
            byte[] hiss = Tone(300, 8000);
            Check("Room hiss below the floor is left alone", ReferenceEquals(WavRecorder.Normalize(hiss, 300), hiss));
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
            // A provider reply mangled around the corrections key is measured in the field.
            // It has to cost one retry, not the learner's whole end-of-run report.
            var usable = new JsonObject { ["corrections"] = new JsonArray(new JsonObject {
                ["original"] = "I want book a room.", ["corrected"] = "I want to book a room.",
                ["explanation"] = new JsonObject { ["en"] = "e", ["kk"] = "k", ["ru"] = "r" } }) };
            var mangled = new JsonObject { ["corrections:[{"] = "" };

            async Task<(int status, int calls, int shown)> FinishRuns(bool alwaysBroken)
            {
                int calls = 0;
                Task<JsonObject> Complete(string prompt, IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct)
                {
                    if (!prompt.Contains("grammar teacher", StringComparison.Ordinal))
                        return Task.FromResult(new JsonObject { ["reply"] = "Noted. What else?" });
                    calls++;
                    return Task.FromResult((alwaysBroken || calls == 1 ? mangled : usable).DeepClone().AsObject());
                }
                using var target = new AppRouter(worker, () => Task.FromResult(false), () => config, Complete);
                var opened = JsonNode.Parse((await target.HandleAsync("POST", "/api/sessions",
                    Encoding.UTF8.GetBytes(new JsonObject { ["scenario"] = "hotel", ["level"] = "A2" }.ToJsonString()))).Body)!;
                string id = opened["session_id"]!.GetValue<string>();
                await target.HandleAsync("POST", $"/api/sessions/{id}/turn", Encoding.UTF8.GetBytes(
                    new JsonObject { ["text"] = "I want book a room.", ["request_id"] = Guid.NewGuid().ToString() }.ToJsonString()));
                var finish = await target.HandleAsync("POST", $"/api/sessions/{id}/finish",
                    Encoding.UTF8.GetBytes(new JsonObject { ["language"] = "en" }.ToJsonString()));
                return (finish.Status, calls, finish.Status == 200
                    ? JsonNode.Parse(finish.Body)!["corrections"]!.AsArray().Count : -1);
            }

            var recovered = await FinishRuns(false);
            Check("Mangled feedback recovers after one retry", recovered.status == 200 && recovered.calls == 2 && recovered.shown == 1);
            var lost = await FinishRuns(true);
            Check("Feedback that stays unusable says so instead of passing silently", lost.status == 503 && lost.calls == 2);

            // What the model actually sends: one sentence quoted out of a longer answer, a bad item
            // ahead of good ones, a reply cut off mid-JSON. None of these may cost the whole review.
            static JsonObject Fix(string original, string corrected) => new()
            {
                ["original"] = original, ["corrected"] = corrected,
                ["explanation"] = new JsonObject { ["en"] = "e", ["kk"] = "k", ["ru"] = "r" }
            };
            async Task<(int status, int calls, int shown, int after)> Review(string said, Func<int, JsonObject> reply, string depth = "focused")
            {
                int calls = 0;
                Task<JsonObject> Complete(string prompt, IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct)
                {
                    if (!prompt.Contains("grammar teacher", StringComparison.Ordinal))
                        return Task.FromResult(new JsonObject { ["reply"] = "Noted. What else?" });
                    calls++;
                    return Task.FromResult(reply(calls));
                }
                using var target = new AppRouter(worker, () => Task.FromResult(false), () => config, Complete);
                var opened = JsonNode.Parse((await target.HandleAsync("POST", "/api/sessions",
                    Encoding.UTF8.GetBytes(new JsonObject { ["scenario"] = "airport", ["level"] = "A2" }.ToJsonString()))).Body)!;
                string id = opened["session_id"]!.GetValue<string>();
                await target.HandleAsync("POST", $"/api/sessions/{id}/turn", Encoding.UTF8.GetBytes(
                    new JsonObject { ["text"] = said, ["request_id"] = Guid.NewGuid().ToString() }.ToJsonString()));
                var finish = await target.HandleAsync("POST", $"/api/sessions/{id}/finish",
                    Encoding.UTF8.GetBytes(new JsonObject { ["language"] = "en", ["depth"] = depth }.ToJsonString()));
                var next = await target.HandleAsync("POST", $"/api/sessions/{id}/turn", Encoding.UTF8.GetBytes(
                    new JsonObject { ["text"] = "One more answer.", ["request_id"] = Guid.NewGuid().ToString() }.ToJsonString()));
                return (finish.Status, calls, finish.Status == 200 ? JsonNode.Parse(finish.Body)!["corrections"]!.AsArray().Count : -1, next.Status);
            }
            var partial = await Review("I go to airport yesterday. I want buy ticket to London.",
                _ => new JsonObject { ["corrections"] = new JsonArray(Fix("I go to airport yesterday", "I went to the airport yesterday.")) });
            Check("A correction quoting one sentence of a longer answer is kept", partial.status == 200 && partial.calls == 1 && partial.shown == 1);
            var fourth = await Review("I has a bag. She like tea. They is late.",
                _ => new JsonObject { ["corrections"] = new JsonArray(Fix("I never said this.", "x"), Fix("I has a bag.", "I have a bag."), Fix("She like tea.", "She likes tea."), Fix("They is late.", "They are late.")) });
            Check("A valid correction behind an invalid one is not lost", fourth.status == 200 && fourth.shown == 3);
            var cut = await Review("I want book a room.",
                call => call == 1 ? throw new ProviderReplyException("cut off") : new JsonObject { ["corrections"] = new JsonArray(Fix("I want book a room.", "I want to book a room.")) });
            Check("A cut-off review reply is retried once", cut.status == 200 && cut.calls == 2 && cut.shown == 1 && cut.after == 409);
            var failed = await Review("I want book a room.", _ => mangled.DeepClone().AsObject());
            Check("A failed review leaves the conversation open", failed.status == 503 && failed.after == 200);

            // What the learner chooses in Settings: every CEFR level shapes Lucy's instructions, and a
            // thorough review keeps more corrections than a focused one.
            string? levelPrompt = null;
            Task<JsonObject> LevelComplete(string prompt, IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct)
            {
                if (!prompt.Contains("grammar teacher", StringComparison.Ordinal)) levelPrompt = prompt;
                return Task.FromResult(new JsonObject { ["reply"] = "Noted. What else?" });
            }
            using (var levels = new AppRouter(worker, () => Task.FromResult(false), () => config, LevelComplete))
            {
                bool allLevels = true;
                foreach (string level in AppPreferences.Levels)
                {
                    var opened = await levels.HandleAsync("POST", "/api/sessions", Encoding.UTF8.GetBytes(new JsonObject { ["scenario"] = "cafe", ["level"] = level }.ToJsonString()));
                    string sid = JsonNode.Parse(opened.Body)?["session_id"]?.GetValue<string>() ?? "";
                    await levels.HandleAsync("POST", $"/api/sessions/{sid}/turn", Encoding.UTF8.GetBytes(new JsonObject { ["text"] = "A tea, please.", ["request_id"] = Guid.NewGuid().ToString() }.ToJsonString()));
                    allLevels &= opened.Status == 200 && levelPrompt?.Contains(AppRouter.LevelGuidance[level], StringComparison.Ordinal) == true;
                    await levels.HandleAsync("DELETE", $"/api/sessions/{sid}", []);
                }
                Check("Every CEFR level from A1 to C2 shapes Lucy's instructions", allLevels && AppRouter.LevelGuidance.Count == AppPreferences.Levels.Count);
                var unknown = await levels.HandleAsync("POST", "/api/sessions", Encoding.UTF8.GetBytes(new JsonObject { ["scenario"] = "cafe", ["level"] = "B3" }.ToJsonString()));
                Check("An unknown level is refused", unknown.Status == 422);
            }
            const string sixMistakes = "I has a bag. She like tea. They is late. He go home. We was happy. It are cold.";
            JsonObject SixFixes(int _) => new()
            {
                ["corrections"] = new JsonArray(Fix("I has a bag.", "I have a bag."), Fix("She like tea.", "She likes tea."), Fix("They is late.", "They are late."),
                    Fix("He go home.", "He goes home."), Fix("We was happy.", "We were happy."), Fix("It are cold.", "It is cold."))
            };
            var focused = await Review(sixMistakes, SixFixes);
            var thorough = await Review(sixMistakes, SixFixes, "thorough");
            Check("A focused review keeps three corrections, a thorough one five", focused.shown == 3 && thorough.shown == 5);
            var unknownDepth = await Review("I want book a room.", _ => new JsonObject { ["corrections"] = new JsonArray() }, "deep");
            Check("An unknown review type is refused", unknownDepth.status == 422);

            // The Feedback page's word list: what the learner said, apart from what only Lucy said.
            Task<JsonObject> Quiet(string prompt, IReadOnlyList<(string Role, string Content)> messages, int maxTokens, CancellationToken ct) =>
                Task.FromResult(prompt.Contains("grammar teacher", StringComparison.Ordinal)
                    ? new JsonObject { ["corrections"] = new JsonArray() }
                    : new JsonObject { ["reply"] = "Lovely. Anything else?" });
            using (var words = new AppRouter(worker, () => Task.FromResult(false), () => config, Quiet))
            {
                var opened = JsonNode.Parse((await words.HandleAsync("POST", "/api/sessions", Encoding.UTF8.GetBytes(new JsonObject { ["scenario"] = "hotel", ["level"] = "A2" }.ToJsonString()))).Body)!;
                string sid = opened["session_id"]!.GetValue<string>();
                await words.HandleAsync("POST", $"/api/sessions/{sid}/turn", Encoding.UTF8.GetBytes(new JsonObject { ["text"] = "I need a room for one night.", ["request_id"] = Guid.NewGuid().ToString() }.ToJsonString()));
                var review = JsonNode.Parse((await words.HandleAsync("POST", $"/api/sessions/{sid}/finish", Encoding.UTF8.GetBytes(new JsonObject { ["language"] = "en" }.ToJsonString()))).Body)!;
                var vocabulary = review["vocabulary"]!.AsArray().OfType<JsonObject>().ToDictionary(word => word["word"]!.GetValue<string>());
                Check("Words the learner said are told apart from words only Lucy said",
                    vocabulary["room"]["used_by_learner"]!.GetValue<bool>() && vocabulary["night"]["used_by_learner"]!.GetValue<bool>()
                    && vocabulary["reservation"]["encountered"]!.GetValue<bool>() && !vocabulary["reservation"]["used_by_learner"]!.GetValue<bool>());
            }

            // Preferences and the review history live in a scratch folder here, never the learner's own.
            string scratch = Path.Combine(Path.GetTempPath(), "speakcity-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string prefsPath = Path.Combine(scratch, "preferences.json");
                new AppPreferences { Level = "C1", FeedbackLanguage = "kk", FeedbackDepth = "thorough" }.Save(prefsPath);
                var loaded = AppPreferences.Load(prefsPath);
                Check("Preferences survive a restart", loaded.Level == "C1" && loaded.FeedbackLanguage == "kk" && loaded.FeedbackDepth == "thorough");
                File.WriteAllText(prefsPath, "{\"level\":\"C9\",\"feedback_language\":7}");
                var odd = AppPreferences.Load(prefsPath);
                File.WriteAllText(prefsPath, "{not json");
                var broken = AppPreferences.Load(prefsPath);
                Check("Unknown or unreadable preferences fall back to the defaults", odd.Level == "A2" && odd.FeedbackLanguage == "en" && broken.Level == "A2");

                string historyPath = Path.Combine(scratch, "feedback-history.json");
                var history = new FeedbackHistory(historyPath);
                for (int index = 0; index < FeedbackHistory.MaxEntries + 2; index++)
                    history.Add(new JsonObject
                    {
                        ["scenario"] = "cafe", ["corrections"] = new JsonArray(), ["vocabulary"] = new JsonArray(),
                        ["messages"] = new JsonArray(JsonValue.Create("conversation text is not kept"))
                    }, $"Café {index}", "A2", DateTime.UtcNow);
                var reloaded = new FeedbackHistory(historyPath);
                Check("Feedback history keeps the newest reviews, capped, across a restart",
                    reloaded.Entries.Count == FeedbackHistory.MaxEntries && reloaded.Entries[0]["title"]!.GetValue<string>() == $"Café {FeedbackHistory.MaxEntries + 1}");
                Check("Feedback history stores the review, not the conversation", !File.ReadAllText(historyPath).Contains("conversation text is not kept", StringComparison.Ordinal));
                reloaded.Clear();
                Check("Clearing feedback history empties it for good", new FeedbackHistory(historyPath).Entries.Count == 0);
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            // Lucy's voice is one continuous stream: Kokoro's WAV must read as PCM, and the pause
            // between sentences is exactly the configured silence.
            Check("Kokoro's WAV is read as 24 kHz mono 16-bit PCM",
                WavAudio.TryRead(audio, out var voiceFormat, out var voicePcm) && voiceFormat == new WavFormat(24000, 1, 16) && voicePcm.Length == audio.Length - 44);
            byte[] pauseBytes = voiceFormat.Silence(NativeMainWindow.SentencePause);
            Check("The pause between sentences is silence of the configured length",
                (voiceFormat.Duration(pauseBytes.Length) - NativeMainWindow.SentencePause).Duration() < TimeSpan.FromMilliseconds(1) && Array.TrueForAll(pauseBytes, value => value == 0));
            TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);
            var p = NativeMainWindow.SentencePause;
            var onTime = SentencePacing.Next(Ms(1000), Ms(1500), first: false, p);
            var late = SentencePacing.Next(Ms(1600), Ms(1500), first: false, p);
            var veryLate = SentencePacing.Next(Ms(3000), Ms(1500), first: false, p);
            Check("A sentence that is ready in time follows after exactly the pause",
                onTime.Silence == p && onTime.Start == Ms(1500) + p && onTime.AlreadySilent == TimeSpan.Zero);
            Check("A sentence that was not ready in time adds no pause on top of the wait",
                late.Silence == p - Ms(100) && late.Start == Ms(1500) + p && veryLate.Silence == TimeSpan.Zero && veryLate.Start == Ms(3000));
            bool device;
            using (var output = new WaveOutVoice()) device = output.Open(voiceFormat);
            // Recorded, not asserted: a build agent has no speakers, a learner's PC does.
            report["voice_output_device"] = device;
            checks.Add("The voice output opens, or reports that this PC has none, without failing");

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

    private static bool ChunksKeepWords()
    {
        const string reply = "That sounds lovely, thank you. We have a quiet table by the window. Would you like a drink first?";
        var parts = SpeechChunks.Split(reply);
        return parts.Count == 3 && string.Join(" ", parts) == reply
            && SpeechChunks.Split("OK. Great! What is your name, please?").Count == 1
            && SpeechChunks.Split("No punctuation at all here").Count == 1
            && SpeechChunks.Split("   ").Count == 0;
    }
}

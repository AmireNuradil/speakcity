# AGENT_STATE.md — SPEAKCITY AI takeover checkpoint

## 0. Checkpoint 2026-09-27 — PR #1 (branch `hoplite/megale-polis-dbc1a735`)

Done from a Linux sandbox. Here WPF compiles (`-p:EnableWindowsTargeting=true`) but cannot run.
Windows proof therefore comes only from the PR's CI run. **That run needs the owner to press
"Approve and run workflows" on the PR**, because GitHub holds workflows opened by the bot and
manual dispatch is refused to the bot token (HTTP 403). Expected counts once it runs:
self-test **149** (was 134), UI smoke **23** (was 11). Treat them as unproven until the run shows them.

**2026-09-28, the owner's own laptop build** (after PR #1 was merged): the installer compiled and the
installed copy passed the self-test. The UI smoke then failed at "Voice warm-up loads Kokoro and
Whisper". The cause was a race in the smoke, not in the app: `_warmup` only exists once the bootstrap
ping returns, and on a laptop that ping (worker cold start plus hashing about 465 MB) is still running
when the smoke looks for it, so the smoke did not wait. Fixed in the follow-up PR: the smoke now waits
on `VoiceSettled`, and the one CPU-speed assertion (sentence gap under 400 ms) became an overlap check.
A failed run now also uninstalls its `%TEMP%\SpeakCity Test` copy.

**Native window, now like the web version.** The city view has `city` as a map with 8 pins at the
web's percentages (keyboard-focusable named buttons; the selected pin is teal with a white ring,
the café's is brick red), plus the Lucy card, the onboarding card and "how it works". The practice
view has the scene portrait from the catalog `image`, the mission and phrase pills, chat with a 28 px
Lucy avatar and the web bubble shapes, "Hear again" on each line, a round mic that pulses red
while recording, "Lucy is thinking…" dots and a toast. "Back to city" keeps an unfinished
conversation resumable.
- **WebP decision: (b′).** JPEG twins of `ui/assets/*.webp` are compiled into `SpeakCity.dll` as
  WPF resources. Generate them with `build/native_images.py` (needs Pillow, a developer tool only).
  They are pinned in `assets-source/manifest.json` and hash-checked by `windows.ps1`. There is no
  copy stage, so a re-run cannot collide. The web and its CSP are untouched.
- **Honest limit:** only airport, café, city and Lucy art exists. Six scenarios show Lucy's portrait,
  as the web does.
- First Windows run (36333569149, before the fixes below): build, installer and **self-test
  145/145** passed. TTS for a whole 3-sentence reply took 1659 ms; its first sentence took
  488 ms. The UI smoke passed 18 checks, then stopped on voicing: the runner has no audio
  output, so `MediaPlayer` fails. The smoke now uses stand-in speakers, as it already did
  for the AI, and additionally checks that the next sentence is ready when the previous one ends.
- Screenshots: the UI smoke renders `native-ui-city.png`, `native-ui-practice.png` and
  `native-ui-feedback.png`. The workflow uploads them as the run artifact `speakcity-reports-<run>`.
  Nobody has looked at them yet.

**Speed (offline-verifiable part only; no API key was available).**
- The warm-up used to ping only. The ping hashes the ~465 MB of models but loads nothing
  (`loaded: false`), so Lucy's first line and the first recording paid the model load. The warm-up
  now synthesises "Hello there." and transcribes it. The UI smoke asserts that both models report
  `loaded`.
- Lucy is voiced sentence by sentence (`SpeechChunks`). The next sentence is synthesised while the
  current one plays, and the audio is cached for replay. Worker requests are never cancelled,
  because cancelling kills the worker and forces a model reload.
- The self-test records `timings_ms`: TTS/STT cold vs warm, and a whole reply vs its first sentence.
  The UI smoke records `greeting_first_voice_ms` and `reply_first_voice_ms`.
- **Not done:** LLM latency (reasoning off/low, model choice). It needs a key and the `sc-quality`
  harness. The ≤10 s goal is owner-stated; the AI's share of it is still unmeasured.

**Fixes from the audit (`docs/CODE_AUDIT.md`, written this session):**
- F-01: the `WAVEHDR[]` array is pinned while the driver owns it, and headers are unprepared on
  close. The code is right by the WinMM contract, but a real microphone is still needed to see it.
- F-02: the failure path no longer publishes the unsigned installer from a public repository. The
  three such public pre-releases were deleted on 2026-09-28 at the owner's request.
- F-03, F-05, F-06, F-07 in the review:
  - a quote of one sentence (or without the full stop) is accepted;
  - every item is validated before the three-item cap;
  - a cut-off or malformed reply (`ProviderReplyException`) is retried once;
  - a failed review leaves the conversation open;
  - the prompt schema is real quoted JSON.
- F-04: an empty review now shows `noErrors` + `noErrorsNote` instead of "Well done".
- F-08: the warm-up (above).
- F-09: the recording cap is 29 s in the device's own format (the worker refuses more than 30 s).
  The window stops the take itself and says so. **This replaces the old "60 s" rule, which the
  worker never honoured.**
- F-10: a failed Send returns the words to the box.
- F-11: recording silences Lucy.
- F-12: queue time no longer counts toward the worker's 120 s limit.
- F-13: temporary TTS files are tracked until they are deleted.
- F-14: sessions are deleted when a review finishes or another place starts.

**CI:** the workflow now runs for pull requests and uploads the reports and screenshots. Build
evidence goes to the branch that was built (a PR's head branch), not always `main`. The UI smoke
guard is raised from 240 s to 420 s.

**Owner decisions still open:**
1. Approve the PR's workflow run.
2. ~~Delete the three public `-incomplete` pre-releases?~~ Done 2026-09-28 at the owner's request (releases and tags deleted).
3. Provide a key (as a secret, never in chat or the repo) so the `sc-quality` and latency harness
   can measure the AI turn.
4. Decide on two models: a fast one for dialogue, a stronger one for the review.
5. Keep the three-correction cap per review?

---

Checkpoint written: **2026-09-25 (this session)**. Previous checkpoint: 2026-09-21/22.
Working repo: `C:\Users\Acer\Desktop\english` — this **is** the `AmireNuradil/speakcity` clone
(the folder name is stale; the git remote proves it). Never work in `C:\Users\Acer` (stray home repo).

---

## 1. The headline: the model was never the problem, and it breaks on its own

The 2026-09-21 checkpoint declared `nex-agi/nex-n2.5-pro:free` a proven working model.
**On 2026-09-25 it returns HTTP 404 — OpenRouter has delisted it.** The model is simply gone from
the catalogue (458 models listed, no `nex-*`). The app therefore fails every AI turn again, with
no code change on our side. That is why the project keeps feeling "stalled".

Live probe of the app's own `ApiClient` + saved settings on 2026-09-25:

| Model | Result |
| --- | --- |
| `nex-agi/nex-n2.5-pro:free` (the previously chosen one) | **404 — no longer exists** |
| `qwen/qwen3.8-27b:free`, `z-ai/glm-5.2:free` | 429 quota |
| `thinkingmachines/inkling:free` | 403 key not permitted |
| `deepseek/deepseek-v4.1-flash` | **works** — see measurements below |
| `nvidia/nemotron-3-ultra-550b-a55b:free` | **works** — see measurements below |

Conclusion: **a free-tier model ID is a countdown timer, not a configuration.** Any model chosen
because it is `:free` will rot in days. A paid-but-stable concrete ID is the only durable choice.

## 2. Measured comparison of the two models that work (app's exact prompts, 2026-09-25)

Requests were sent through the app's own `ApiClient`, with the app's real `Lucy` turn prompt and
the real `Finish and feedback` teacher prompt, scored with `AppRouter`'s own acceptance rules.

| Test | `deepseek/deepseek-v4.1-flash` | `nvidia/nemotron-3-ultra-550b-a55b:free` |
| --- | --- | --- |
| Connection test (`TestAsync`) | 8/8 pass | 1/1 pass |
| Turn @220 tokens (old budget) | **2/3 — one truncated `finish_reason=length`** | 3/3 |
| Turn @700 tokens (new budget) | 3/3 | 3/3 |
| Grammar feedback, corrections a learner actually sees | **6 of 9** (one call of three kept nothing) | **9 of 9** |
| Turn latency | 1.4–8 s | 3–12 s |
| Feedback latency | 3.9–8 s | **13.6–27.6 s** |
| Cost per request (from the provider's own `usage.cost`) | ~$0.000007–0.000046 | $0 (free tier) |

Read this carefully:
- **DeepSeek V4.1 Flash is enough for the conversation** and is essentially free money
  (~2 000 full sessions per US$1). Its 220-token turn budget was the real defect, not the model.
- **DeepSeek's weak spot is the grammar-feedback reply shape.** Root cause found by dumping the
  raw replies: about a third of feedback calls answer with a JSON object whose only key is the
  literal string `"corrections:[{"` instead of the requested shape — structurally valid JSON, so
  `ApiClient` accepts it, but `corrections` is missing. The old code threw there and the learner
  lost the whole report. Measured delivery over six runs: **6 of 18 corrections survived**.
  Note the UI was never dishonest about this: `noErrors` reads "No clear grammar corrections were
  returned." and adds an explicit "this does not mean every sentence was perfect". The defect was
  the lost report and the 503, not a false claim of perfect English.
- The free `nemotron` is more reliable on that JSON but 3x slower on feedback and, per section 1,
  will disappear from the catalogue eventually.

## 3. The re-run trap: why no installer existed (this was never the network)

`AGENT_STATE.md` (21.09) blamed Cloudflare WARP / DNS timeouts. The real chain, now proven:

1. The eSpeak download **did** succeed on 23.09 (`out/native-espeak/manifest.json`,
   `"status": "built-and-validated"`, source commit `4870adfa`, ~1 500 hashed files).
2. The run then died at `build/windows.ps1:148` — `Copy-Item out/worker/... out/app/speech
   -Recurse` fails with *"A file with the specified name already exists"* because `out/app/speech`
   still held the **previous** build's payload. So the 20-minute build reached the end and lost.
3. Because that run left a stamped `espeakng-loader` inside `out/venv`
   (`out/venv/Lib/site-packages/espeakng_loader/speakcity-native-build.json`), every later re-run
   dies early at `native_espeak.ps1:38` ("Use a new/empty OutputRoot") and then at
   `native_driver.py:173` ("never restamp a previous build").

So `build/windows.ps1` could only ever be run **once** from a clean `out/`. That alone explains a
week of "the project does not move".

## 4. Changes made in this session (verified, NOT pushed)

| File | Change | Why |
| --- | --- | --- |
| `build/windows.ps1` | new `Copy-Fresh` helper, used for the worker, model, and docs payload copies | fixes the 2. above; a re-run no longer collides with the previous payload |
| `build/windows.ps1` | recreate `out/venv` when it already carries a controlled eSpeak build | fixes the 3. above; enforces the driver's freshness rule instead of tripping over it |
| `src/SpeakCity/AppRouter.cs` | conversation turn `maxTokens: 220 -> 700` | section 2: 220 truncated ~1 turn in 3 with a reasoning-capable model |
| `src/SpeakCity/AppRouter.cs` | one retry when the feedback reply is unusable (`AcceptedCorrections` returns null for a missing `corrections` **or** for entries that all fail validation); still no retry when the provider genuinely returned an empty list | turns 6/18 delivered corrections into 15/18 |
| `src/SpeakCity/AppRouter.cs` | read `voice`, `speed`, `level`, required strings and correction fields by type | a wrongly typed value threw out of `GetValue<T>()` and came back as a 503 provider-fault instead of a 422 bad request; one odd field inside one correction discarded the entire report |
| `src/SpeakCity/SelfTests.cs` | two new checks (132 total): a mangled feedback reply recovers on retry, and a reply that stays unusable returns 503 rather than passing silently | keeps the above from regressing |
| `src/SpeakCity/SpeakCity.csproj` | dropped the `System.Security.Cryptography.ProtectedData` reference | it ships with the SDK; the reference only produced two NU1510 warnings. Build is now 0 warnings / 0 errors and the DPAPI round trip still passes |

Validation after the edits: `tests/speech_worker_test.py` Ran 35 OK · `tests/notice_collection_test.py`
Ran 8 OK · `node --test ui/tests/` 39 pass 0 fail · `dotnet build -c Release` 0 errors 0 warnings ·
`SpeakCity.exe --self-test` passed 132/132 · `SpeakCity.exe --ui-smoke` status=passed 11/11 ·
`windows.ps1` parses with 0 errors.

Also: `out/native-espeak-20260923-evidence/` — the 23.09 manifest moved aside (not deleted) so a
fresh build had an empty output root. `%LOCALAPPDATA%\SpeakCity\api-settings.bin` now selects
`deepseek/deepseek-v4.1-flash` (no key is stored in this repo; none is printed here).

## 4b. Second pass: an audit of the code no test covers

A read-only audit of `WavRecorder.cs`, `SpeechWorkerClient.cs`, `ApiClient.cs`, the two windows and
`ui/app.js` produced four candidates. Three were real and are fixed; one was measured and cleared.

| Finding | Verdict | What was done |
| --- | --- | --- |
| `WavRecorder` set `bytespersec`/`alignment` as **mandatory** MCI params | real — a driver that rejects derived params reports "microphone could not be used" on working hardware | only the format (`bitspersample`/`channels`/`samplespersec`) is required now; derived params are best-effort |
| No cap on recording length, and the window read the whole WAV into memory before the router's 3 MB limit refused it | real — a forgotten microphone grows in memory, then the read loads it all | driver-side cap at 60 s when it accepts millisecond timing (and an uncapped retry if it rejects `length`), plus a size check **before** reading, sharing `AppRouter.MaxBodyBytes` |
| `payload["text"]?.GetValue<string>()` in `TranscriptAsync` | real — a wrongly typed worker field threw *inside* the catch-all and was reported to the learner as a microphone failure | read by type |
| A hung speech worker could hold the serial gate forever (`ReadLineAsync` may ignore cancellation) | **not a defect, measured**: with a stub worker that never answers, both consecutive requests returned `TimeoutException` at 120.1 s and 120.0 s, so the token is honoured and the gate is released | left alone deliberately — no speculative change to concurrency code |
| TTS temp files never deleted (one orphan per spoken line) | real, slow | `MediaEnded`/`MediaFailed` retire the file, and the next line retries if it is still open |

## 4c. The full re-run collision list

Five collisions had to be fixed before `.\build\windows.ps1` could run twice; all are now handled
by the script itself, and each is the same class of bug (a stage that refuses a directory an earlier
run left behind):

1. `Copy-Item` of the worker payload onto the previous one → `Copy-Fresh`.
2. `Copy-Item` of the models onto the previous ones → `Copy-Fresh`.
3. `Copy-Item` of `docs` → `Copy-Fresh`.
4. `native_espeak.ps1` refusing a non-empty OutputRoot → the orchestrator clears it.
5. `native_espeak.ps1` refusing an already-stamped venv, and `collect_notices.py` refusing stale
   notice/source directories → recreated up front.

## 4d. The learner speaks quietly — recorded as a first-class requirement

The owner practises in a shared room and deliberately keeps their voice low. That is not a corner
case to be told to stop doing: quiet speech was being captured at raw microphone level and the
local recogniser missed words, which the app reported as "no speech was recognised". Two changes:

- `WavRecorder.Normalize` lifts a quiet take towards a spoken level before it is handed to the
  worker. Peak below **1200** → left completely alone (amplifying an empty room would invent
  speech that was never said, which is worse than missing a word); peak above **24000** → already
  loud; otherwise gain up to **10x** targeting a 16000 peak. Covered by two self-test checks
  (134 total now), and verified live on the owner's webcam microphone: a silent room reported
  peak 81 and was correctly not amplified.
- When a take really is too quiet the window now says *"The microphone picked up almost nothing.
  Speak a little louder, or type the answer."* instead of blaming recognition.

**Still open and worth measuring properly:** whether the gain is enough for a real whispered-voice
answer, and whether a noise gate or feeding 48 kHz to the worker (it resamples internally, with
different filters than ours) recognises quiet speech better. Nobody has measured a spoken-but-quiet
sample yet.

## 4e. What "the other SPEAKCITY that works better" actually is

The owner had Edge open on **"SPEAKCITY AI · Explore city"** and a console titled "SPEAKCITY server".
That is not a second product: it is the **web UI** (`ui/app.js` sets `document.title` to
`SPEAKCITY AI · <page>`, and the city page is titled "Explore city") served by
**`out\server\SpeakCityServer.exe`, a binary from 15.09 whose source was deleted from the repo in
`2f0152d`**, started by the stale Desktop shortcut. Two consequences:

- Comparing it against the native window is comparing a dead architecture to the current one. Its
  AI path predates every fix in this file.
- Its microphone goes through the **browser**, which applies its own processing and gain — the most
  likely reason speech recognition felt better there. Section 4d is the native equivalent.

## 4f. A build stage was hijacking real installs

Found while installing for the owner: the `installer-test` stage installs the freshly built
installer into `%TEMP%\SpeakCity Test`, and **Inno registers that as the machine's install location
for the app's `AppId`**. Any later genuine install then silently defaults into a temp folder that
Windows is free to delete — which is a strong candidate for past "I installed it and it is not
there" confusion. The stage now uninstalls the test copy after its checks. (The owner's current
install is correct: `%LOCALAPPDATA%\Programs\SPEAKCITY AI`, and its `SpeakCity.dll` does contain the
waveIn microphone path.)

## 5. Installer build status

`out\release\SPEAKCITY-AI-Setup-x64.exe` — **26.09 16:56, 691 MB** — is current: it contains the AI
provider fixes, the new native UI, the raised turn budget, the feedback retry and the microphone
hardening. It was produced by a run that installed into a *freshly deleted* directory and then
passed 132/132 self-test checks and 11/11 native UI checks from that installed copy, so
re-runnability is now proven by two consecutive complete builds rather than asserted.

Nothing publishes itself: the last log line is "GitHub publishing skipped (no
GH_TOKEN/GITHUB_REPOSITORY)". If a later run dies, the error stream and the `$stage` value in the
output name the stage; every failure this session was a leftover directory, never the network.
`out\release\SPEAKCITY-AI-Setup-x64-browser-mode.exe` (16.09) and the 83 MB
`SpeakCityAI-Portable-fixed*.exe` files in Documents are all stale — do not distribute them.

## 6. What is NOT done / still unverified

- **Push.** `main` is ahead of `origin/main` by 17 commits and nothing was pushed in this session.
  The repo is public, so publishing a release is auto-skipped; pushing is the owner's call.
- **Microphone: fixed on real hardware, one setting left to check.** Correction — the earlier claim
  in this file that "this PC has no capture device" was wrong (MCI error 287 was read as "no
  devices", and device names were filtered in English on a Russian Windows). The machine has
  `Микрофон (HP 320 FHD Webcam)`, enabled, with microphone privacy set to Allow. MCI's `waveaudio`
  driver rejects every `set` here (error 261), so recordings came out 8-bit/11 kHz and the worker
  could not transcribe them — that is why `Speak` failed in every version ever built. `WavRecorder`
  is rewritten on winmm **waveIn** (no new dependency): verified on that microphone producing
  16 kHz mono 16-bit PCM that the bundled worker accepts. The one open item is that the test take
  peaked at 1/32767, i.e. the Windows input level looks muted or zero — worth checking before
  blaming the app again.
- **Feedback retry is measured, not proven independent.** The 15/18 figure comes from six runs
  against one provider with the app's own client; the app's shipped rule is slightly narrower than
  the harness (it does not retry a genuinely empty `corrections` list, which is correct behaviour
  and saves a call). A stricter contract, e.g. requiring `{"corrections":[...],"checked":true}`,
  would remove the ambiguity entirely but changes the provider contract for every model and was
  left for the owner.
- **Two-model routing**: a fast model for live turns and a stronger one for end-of-run feedback
  (where 27 s is irrelevant). Section 2 is exactly the data that justifies it. Needs a second model
  field in `ApiConfig` + the settings window.
- Kokoro int8 multi-lang model (`model.int8.onnx` 114 MB + `voices.bin`, in
  `C:\Users\Acer\Desktop\claw onyx reader\.tooling\`) versus the pinned fp32 325 MB
  `kokoro-v1.0.onnx`. A potential size + voice-count win, but it contradicts the standing
  "do not replace Kokoro" rule and needs an owner decision.
- Old-version audit: **nothing outside the repo is worth merging back.** All copies under
  `Desktop\SPEAKCITY-old-and-zips\` are strictly older; `content/scenarios.json` is md5-identical
  across all of them; the removed `SpeakCityServer` is recoverable from git history (`2f0152d^`).

## 7. Hard rules (unchanged, still in force)

- Do NOT replace Kokoro, faster-whisper, the frozen speech worker, or `speech/worker.py`.
- Do NOT remove WebView2 (needed by `--webview`). Do NOT touch the stray `C:\Users\Acer\.git`.
- Do NOT weaken: DPAPI settings storage, HTTPS enforcement, redirect blocking, the fail-closed
  licence/notice gate (`build/collect_notices.py`, no `--force`), offline speech, bounded API reads,
  or the eSpeak "fresh venv / empty output root" guard — that guard is a safety property, this
  session only made the script satisfy it automatically.
- Do NOT hard-code personal paths (`C:\Users\Acer\...`) in build scripts.
- No credentials in the repo; it is PUBLIC.
- Model: a CONCRETE ID, never an auto-router — and prefer a paid/stable one over `:free`.
- Speech stays local; only conversation text reaches the configured provider.

## 8. Commands

```powershell
cd C:\Users\Acer\Desktop\english
python -B tests/speech_worker_test.py          # Ran 35, OK
python -B tests/notice_collection_test.py      # Ran 8, OK
node --test ui/tests/ui.test.cjs ui/tests/recorder.test.cjs   # 39 pass
& 'C:\Users\Acer\dotnet10\dotnet.exe' build src\SpeakCity\SpeakCity.csproj -c Release
& ".\src\SpeakCity\bin\Release\net10.0-windows\win-x64\SpeakCity.exe" --ui-smoke "$env:TEMP\smoke.json"
.\build\windows.ps1                            # full installer, 20-40 min
```

Model re-probe harnesses written this session (temporary, in `%TEMP%`, safe to delete):
`sc-modelprobe` (candidate sweep), `sc-dsprobe` (raw HTTP vs app client), `sc-quality`
(section 2's measurement with the app's real prompts). `sc-apicheck` from the previous session
still works. Re-run `sc-quality` whenever a model ID is suspected rotten — it is the only test that
reveals silent feedback loss.

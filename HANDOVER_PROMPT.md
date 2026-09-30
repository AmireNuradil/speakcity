# HANDOVER PROMPT — SPEAKCITY AI

> Copy everything below into a fresh session with your strongest model. It is self-contained: the
> agent needs no memory of previous conversations. `AGENT_STATE.md` holds the same ground truth in
> checkpoint form; this file is the instruction version.

---

You are taking over **SPEAKCITY AI**, a Windows 11 desktop app that practices *spoken* English with
learners whose English is weak (Kazakh/Russian native speakers). Work autonomously: measure before
you decide, do not ask which option you would prefer, and leave a written trail of what you proved.

## Latest state (2026-09-29): read `AGENT_STATE.md` sections 0a and 0 first

- PR #3 (stacked on #2) adds the Settings page: English level A1–C2, Feedback (history,
  words, explanation language, review type) and AI configuration as a page.
- Home now only starts practice.
- Lucy's voice goes through one waveOut stream with a 220 ms pause between sentences.
- Learner preferences live in `%LOCALAPPDATA%\SpeakCity\preferences.json` and reviews in
  `feedback-history.json`. The smoke and self-tests use scratch folders and never these files.

## Latest state (2026-09-27): read `AGENT_STATE.md` section 0 first

PR #1 gives the native window the web version's look: a city map with 8 pins, Lucy portraits,
chat avatars, a round mic, thinking dots and a toast. It also makes the voice warm-up really load
Kokoro and Whisper, voices Lucy sentence by sentence, and fixes the audit's review-loss and
microphone bugs (`docs/CODE_AUDIT.md`: F-01 to F-14, except F-07, which is unmeasured, and the
open items listed there). Native pictures are JPEG twins compiled into the DLL, because WPF cannot
decode WebP. The recording cap is now 29 s in the device's format; the worker refuses more than
30 s, so the old "60 s" never worked. Windows proof for the PR comes from its CI run, which the
owner must approve. Then check the `speakcity-reports-<run>` artifact: 149 self-test checks,
22 UI-smoke checks and the `native-ui-*.png` screenshots.

## What the product is

- **C# / .NET 10 WPF** native window (`--webview` opens an older WebView2 window; both exist).
- **Speech runs 100% locally**: Kokoro TTS and faster-whisper STT inside a frozen PyInstaller
  worker (`speakcity-speech-worker.exe`) that the app talks to over stdin/stdout JSON lines.
- **Only conversation text leaves the machine**, to any OpenRouter-compatible
  `/chat/completions` endpoint configured in the app's settings (DPAPI-encrypted at rest).
- 8 scenario role-plays (`content/scenarios.json`), UI in EN/KK/RU, max 8 turns per session, then
  an end-of-run grammar report and vocabulary review.
- Distribution is one Inno Setup installer (~691 MB, models included).

## Where to work — read this first

- Repo: `C:\Users\Acer\Desktop\english`. **The folder name lies**: it is the clone of
  `github.com/AmireNuradil/speakcity` (public). Verify with `git remote -v`.
- `C:\Users\Acer` itself is a **stray git repo** with unrelated/broken remotes. Never run git there,
  never touch `C:\Users\Acer\.git`.
- `github.com/AmireNuradil/english` returns **404** — it does not exist. Do not go looking for it.
- Old copies under `Desktop\SPEAKCITY-old-and-zips\` and the 83 MB
  `Documents\SpeakCityAI-Portable-fixed*.exe` files are all strictly older. Audited 2026-09-25:
  **nothing outside the repo is worth merging back.** Do not repeat that sweep.

## Environment quirks on this machine

- `dotnet` is **not on PATH**: use `C:\Users\Acer\dotnet10\dotnet.exe`.
- Python 3.11 x64 is **not on PATH**: `%LOCALAPPDATA%\Programs\Python\Python311\python.exe`.
- Build script accepts `SPEAKCITY_DOTNET` / `SPEAKCITY_PYTHON` overrides.
- PowerShell 7 at `C:\Program Files\PowerShell\7\pwsh.exe`.
- **There is no capture device on this machine** (verified by enumeration). The microphone path can
  never be tested here; it needs a real laptop.
- Long builds must be launched detached (`Start-Process pwsh -File build\windows.ps1 ...`) or a
  10-minute tool timeout kills them mid-flight.

## Prove the baseline before changing anything

```powershell
cd C:\Users\Acer\Desktop\english
python -B tests/speech_worker_test.py                       # Ran 35, OK
python -B tests/notice_collection_test.py                   # Ran 8, OK
node --test ui/tests/ui.test.cjs ui/tests/recorder.test.cjs # 39 pass, 0 fail
& 'C:\Users\Acer\dotnet10\dotnet.exe' build src\SpeakCity\SpeakCity.csproj -c Release   # 0 err, 0 warn
& ".\src\SpeakCity\bin\Release\net10.0-windows\win-x64\SpeakCity.exe" --self-test "$env:TEMP\st.json"  # 164 checks after PR #3 (149 after PR #1, 134 before)
& ".\src\SpeakCity\bin\Release\net10.0-windows\win-x64\SpeakCity.exe" --ui-smoke "$env:TEMP\smoke.json" # 37 checks after PR #3 (23 after PR #1, 11 before); also writes native-ui-*.png beside the report
```

As of 2026-09-26 all of the above are green, and `out\release\SPEAKCITY-AI-Setup-x64.exe`
(26.09 16:56) was installed into a freshly deleted directory and passed **132/132** self-test
checks and **11/11** native UI checks *from the installed copy*. App flags: `--ui-smoke <file>`,
`--diagnose <file>`, `--webview`; no flag = native window.

## The three things that actually broke this project (not the ones it was blamed for)

### 1. Free-tier model IDs rot. The model was never "too weak".

The owner kept believing DeepSeek was not good enough. Reality: the previously "proven" model
`nex-agi/nex-n2.5-pro:free` was **delisted by OpenRouter** (HTTP 404) four days after it was chosen,
so every AI turn failed again with no code change. A `:free` model ID is a countdown timer.

**Rule: always a concrete model ID. Never `openrouter/free` or `openrouter/auto`** — those are
auto-routers that land on random models; one live request was routed to a *content-safety
classifier* that answered `User Safety: safe` instead of JSON.

Measured live on 2026-09-25/26 through the app's own `ApiClient`, using the app's real prompts and
`AppRouter`'s real acceptance rules:

| | `deepseek/deepseek-v4.1-flash` | `nvidia/nemotron-3-ultra-550b-a55b:free` |
| --- | --- | --- |
| connection test | 8/8 pass | pass |
| conversation turn @220 tokens | 2/3, then **0/3** on a later run | 3/3 |
| conversation turn @700 tokens | 3/3 | 3/3 |
| corrections the learner actually sees | 6 of 9 (before the retry fix) | 9 of 9 |
| turn latency | 1.4–8 s | 3–12 s |
| feedback latency | 3.9–8 s | **13.6–27.6 s** |
| cost per request | ~$0.000007–0.000046 | $0, but it will vanish |

Also rejected by live probe: `qwen/qwen3.8-27b:free` and `z-ai/glm-5.2:free` (429 quota),
`thinkingmachines/inkling:free` (403 key not permitted), `nvidia/nemotron-3-super-120b-a55b:free`
(returns `{}`), `google/gemma-4-*:free` (429), `liquid/lfm-2.5-2.6b:free` (leaks
`<|tool_call_start|>` into the reply). Currently configured: **`deepseek/deepseek-v4.1-flash`**
(shared settings file `%LOCALAPPDATA%\SpeakCity\api-settings.bin`, so the installed app picks it up).

### 2. The build script could only run ONCE from a clean `out/`

That is why the installer silently went stale for a week while everyone assumed the network was
broken. Six separate re-run collisions, all now fixed in `build/windows.ps1`:

1. `Copy-Item` of the worker payload onto the previous run's copy → new `Copy-Fresh` helper.
2. Same for `out/models` → `Copy-Fresh`.
3. Same for `docs` → `Copy-Fresh`.
4. `native_espeak.ps1` refuses a non-empty OutputRoot → the orchestrator now clears it.
5. The eSpeak driver refuses to re-stamp an already-stamped venv → the venv is recreated when
   `out/venv/Lib/site-packages/espeakng_loader/speakcity-native-build.json` exists.
   `collect_notices.py` likewise refuses stale notice/source dirs → recreated.
6. Inno Setup's own installer, and the install-test directory in `%TEMP%`, misbehaved over the
   previous run's leftovers → both cleared. **This also made the install test a genuine clean
   install** instead of an upgrade-in-place.

Every failure was a leftover directory. The historical "network timeout" diagnosis is obsolete: the
SHA-pinned eSpeak download has succeeded on every attempt since transport retries were added.

### 3. A provider reply mangling cost the learner the whole grammar report

Measured by dumping raw replies: DeepSeek intermittently answers with a JSON object whose *only key
is the literal string* `"corrections:[{"`. That is structurally valid JSON, so the strict parser
accepts it, and the feedback code then threw. **Delivery was 6 of 18 corrections; with one retry it
is 15 of 18.** A genuinely empty `corrections` list is still accepted without a retry, because that
honestly means "nothing to fix". (The UI wording was always cautious — "No clear grammar corrections
were returned" plus an explicit note that this does not mean the English was perfect. The defect was
the lost report, not a false claim.)

Same class of bug, also fixed: reading `voice`, `speed`, `level`, required strings and correction
fields with `GetValue<T>()` **throws on a wrongly typed JSON value**, which surfaced a bad request as
a 503 "provider fault" and let one oddly typed field inside one correction discard the entire
report. There is now a type-safe `Text()` reader, and `--self-test` has two checks that fail if the
retry behaviour regresses.

## One audit finding that was WRONG — do not "fix" it

A code audit claimed a hung speech worker could hold the client's serial gate forever because
`ReadLineAsync` might ignore cancellation. **Tested with a stub worker that reads a request line and
never answers: both consecutive requests returned `TimeoutException` at 120.1 s and 120.0 s**, so
cancellation is honoured and the gate is released. `SpeechWorkerClient`'s concurrency is fine. Do
not rewrite it on the strength of that claim.

Also verified as *not* defects: `ApiClient` retry is bounded and only for unprocessed 400/422 or an
ambiguous parsed reply; the response body is read with a hard cap plus one extra byte; the API key
never reaches messages or logs; `worker.py` drains stderr and redirects noise to NUL so the stdout
JSON channel cannot be corrupted; `Kill(entireProcessTree: true)` reaps the PyInstaller tree;
`ui/app.js` normalises every field it consumes.

## Known-open, in priority order

1. **The microphone never worked in any version, and the cause was MCI, not the hardware.**
   This PC does have a capture device — `Микрофон (HP 320 FHD Webcam)`, status OK, Windows
   microphone privacy set to Allow. An earlier note in this file claimed the machine had no input
   device at all; that was **wrong** (it came from reading MCI error 287 as "no devices" and from
   filtering device names in English on a Russian Windows). The real fault: MCI's `waveaudio` driver
   refuses every `set` command here (error 261, "command not supported by the driver"), so a
   recording came back as **8-bit 11 kHz**, which the bundled Whisper cannot transcribe.
   `WavRecorder` is therefore rewritten on the winmm **waveIn** API — same system library, still no
   package dependency, real 16 kHz mono 16-bit PCM, with fallbacks to 44.1/48 kHz and managed
   downmix + resampling for devices that cannot do 16 kHz.
   Verified on the owner's own microphone: capture → WAV header `format=1 channels=1 rate=16000
   bits=16` → bundled worker answered `{"text":"","needs_review":true}` in 3.05 s, i.e. the worker
   accepts the format (empty text because the 3-second test take was silence).
   **Still to check on the owner's side:** that test take had a peak amplitude of 1/32767, which
   means the Windows input level is effectively muted or at zero. If `Speak` returns "no speech was
   recognised" while the pipeline is healthy, look at Settings → System → Sound → Input → HP 320
   FHD Webcam volume first.
   **Done since:** the capture normalises quiet speech (peak window 1200-24000, max 10x gain,
   silence left completely untouched) and the window now separates "the microphone picked up almost
   nothing" from "no speech was recognised"; two self-test checks cover the curve, 134 checks total.
   Measured live: an empty room reported peak 81 and was correctly not amplified.
   **Not done:** no genuinely quiet *spoken* sentence has been measured, so the gain target is still
   theory. The owner's report that the old browser UI understood them better is most likely the
   browser's own microphone processing, which this normaliser imitates crudely. A noise gate, or
   handing 48 kHz to the worker and letting it resample, may do more.
   **Do not mistake for a second product:** what the owner sometimes has open as "SPEAKCITY AI -
   Explore city" is the web UI served by the leftover `out\server\SpeakCityServer.exe` from 15.09,
   whose source was deleted from the repo; its AI path predates every fix in this file.
2. **The Desktop shortcut is a relic of a deleted architecture.** `SPEAKCITY AI.lnk` points at
   `Desktop\english\out\server\Start SPEAKCITY.bat`, which starts the removed `SpeakCityServer`
   minimized on port 8899 and **never shows a window** — a second reason the app "did not launch".
   The installer's own shortcut is correct (`{app}\SpeakCity.exe`) but its desktop-icon task is
   `Flags: unchecked`, so it is off by default. Launch from the Start Menu, or tick the box, or
   delete the stale `.lnk`.
3. **Install on a clean PC** (a second machine).
4. **Two-model routing** — a fast model for live turns, a stronger one for the end-of-run report
   where 28 s is irrelevant. The table above is the justification. Needs a second model field in
   `ApiConfig` + the settings window + DPAPI storage validation.
5. **Stricter feedback contract** — `{"corrections":[...],"checked":true}` and reject a reply
   without the marker. Removes the empty-vs-failed ambiguity outright, at the cost of changing the
   contract for every model. Owner decision, not yet taken.
6. **Kokoro int8 multi-lang** (`model.int8.onnx` 114 MB + `voices.bin` 53 MB, at
   `C:\Users\Acer\Desktop\claw onyx reader\.tooling\kokoro-int8-multi-lang-v1_1`) versus the pinned
   fp32 325 MB `kokoro-v1.0.onnx` with two voices. Potential size and voice-count win, but it
   collides with the standing rule below. Owner decision required.

## Hard rules — people have broken these before

- **Do NOT replace** Kokoro, faster-whisper, `speech/worker.py`, or the frozen speech worker. The
  engine works; user-visible speech problems are integration bugs, not engine bugs.
- **Do NOT remove WebView2** (the `--webview` window needs it). The full Edge dependency and
  `SpeakCityServer` were removed deliberately; `SpeakCityServer` is recoverable from git at
  `2f0152d^`.
- **Do NOT weaken**: DPAPI settings storage, HTTPS-only, redirect blocking, bounded request/response
  sizes, offline speech, the fail-closed licence/notice gate in `build/collect_notices.py`
  (there is deliberately **no `--force` flag**), or the eSpeak "fresh venv / empty output root"
  guard. That guard is a safety property — the fix was to *satisfy* it, never to bypass it.
- **Do NOT hard-code `C:\Users\...` paths in build scripts.** Tool discovery order is
  `SPEAKCITY_*` env → PATH → `DOTNET_ROOT`/registry → portable scratch install → TEMP → `out/tmp`.
- **No credentials in the repo. It is PUBLIC.** Never print an API key, not even a prefix.
- Keep the UI dependency-free: HTML/CSS/JS plus native WPF. No framework, no CDN.
- Speech stays local; only conversation text reaches the configured provider.
- Every transcript intentionally carries `needs_review: true`. It is not a pronunciation score.
- Do not invent pronunciation scores, do not correct grammar *during* a conversation, and do not
  let the app claim a learner was perfect when the provider simply failed.

## Push / publishing behaviour

`main` is pushed to `origin` (public). A push to `main` triggers `.github/workflows/windows-desktop.yml`
(`windows-latest`, 45-minute cap), which runs the same `build/windows.ps1` on a clean runner —
independent verification is free there. Docs-only paths (`.build/**`, `README.md`, `docs/**`) are
`paths-ignore`d, so documentation commits do not spin a build. `permissions: contents: write` means
the run may add `.build/` "Record desktop build evidence" commits. **Release publishing is gated on
the repository being private** (`repo.private -eq $true`), so on this public repo the build verifies
but never publishes — that is intentional: the installer is unsigned and the recorded download
pointers are temporary signed URLs.

## Reusable measurement harnesses (in `%TEMP%`, safe to recreate)

- `sc-quality` — the important one: drives the app's real prompts and applies `AppRouter`'s real
  acceptance rules, so it reveals *silent* feedback loss that a connection test cannot.
- `sc-dsprobe` — raw HTTP vs the app's client, side by side; use it to tell "the provider sent
  garbage" from "our parser rejected a fine reply".
- `sc-modelprobe` — sweeps candidate model IDs with N attempts each. Run it before ever trusting a
  "this model works" claim, and again whenever the AI mysteriously stops answering.
- `sc-timeout` + `sc-hang` — hung-worker stub that measures the serial gate.
- Model availability check, no key needed: `GET https://openrouter.ai/api/v1/models`.

## Working style this project needs

The owner works alone, is cost-sensitive, and hands over the repo while going away. Decide, do the
work, and write the reasoning down; they review afterwards. But never push, never buy, never delete
their source or old copies without being asked, and never report something as fixed without the
command output that proves it. When a symptom is "the AI is bad", first check whether the model ID
still exists — that has been the answer twice.

**Open loop to close with the owner:** they said they will hand this project to a stronger model for
the hard parts and asked to be reminded to request the prompt for it. Ask them what they want that
prompt to cover before writing it — they explicitly said they would forget, and that the instructions
will come once things are tested.

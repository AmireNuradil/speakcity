# AGENT_STATE.md — SPEAKCITY AI takeover checkpoint

Checkpoint written: **2026-09-21 ~22:15 (Windows 11, local machine)**
Working repo: `C:\Users\Acer\Desktop\english` (this folder **is** the `AmireNuradil/speakcity`
clone — the folder name "english" is stale, git remote proves it).
Reference repo `AmireNuradil/english` does **not** exist (HTTP 404). Do not go looking for it.

---

## 1. What was done in this session

### 1a. Repository truth established
- Only one real repo exists: `AmireNuradil/speakcity` (public). The `fork`/`new`/`origin` remotes
  in the home folder point at non-existent or unrelated repos; the home folder `C:\Users\Acer` is
  itself a stray git repo — **never work there**, only in `Desktop\english`.
- All Phase 3/4 audit items were already committed before this session:
  `4066dab Portable-build`, `0a92d04 Fix-dotnet-arch-check`, `2f0152d Remove-orphaned-SpeakCityServer`.

### 1b. Commits made this session (local, NOT pushed)
| Commit | Subject |
| --- | --- |
| `1e079b7` | Fix stale repository links and document the native default window |
| `b489305` | Fix local .NET install false failure and stop publishing on a public repository |
| `15ff396` | Make the AI conversation work with a real provider |
| `aa8ef44` | Make the native window look like the rest of the product |

### 1c. THE MAIN FIX — AI did not work, and it was the model ID, not the app
Root cause proven with live OpenRouter requests using the app's **exact** payload shape:
`openrouter/free` is an **auto-router**, not a model. One live request was routed to
`nvidia/nemotron-3.5-content-safety:free` — a **content-safety classifier** that answered
`User Safety: safe` instead of JSON. The client correctly rejects that, so every turn failed.

Model reliability probe (same payload as `ApiClient.BuildRequest`, `temperature 0.2`,
`stream false`, `response_format json_object`):

| Model | Result |
| --- | --- |
| `nex-agi/nex-n2.5-pro:free` | **3/3 turns, 8/8 feedback — CHOSEN** |
| `openrouter/free`, `openrouter/auto` | auto-routers, land on random/unsuitable models |
| `nvidia/nemotron-3-super-120b-a12b:free` | 0/3 (returned `{}`, `{"": ""}`) |
| `google/gemma-4-31b-it:free`, `google/gemma-4-26b-a4b-it:free` | HTTP 429 |
| `liquid/lfm-2.5-2.6b:free` | leaks `<|tool_call_start|>[google(query=...)]` into the reply |
| `dots-studio/dots-3-note-preview:free` | 1/3 (empty content, `finish_reason=length`) |

Code changes in `15ff396`:
- `ApiClient.TestAsync`: probe budget `maxTokens: 32 → 128` (reasoning models burned the whole
  budget before the visible answer and looked broken).
- `ApiClient`: malformed-JSON and incomplete-reply messages now name auto-router model IDs.
- `ApiClient`: `AmbiguousReplyMessage` constant + `IsRetryableReply()`; **one automatic retry**
  when a provider returns duplicate JSON property names (measured: 1 feedback reply in ~14).
- `AppRouter` turn: a reply of `{}` now raises a 503 "AI did not return a usable reply"
  instead of a 422 that blamed the learner's answer.
- `README.md`: verified/rejected model list + live-provider call moved out of "unproven".

### 1d. UI beautification (commit `aa8ef44`)
`src/SpeakCity/NativeTheme.cs` (new, 240 lines): ResourceDictionary with the product palette
(teal `#196D69`, mint `#E8F4EF`, ink `#162F3C`, coral, hover, line, white) and styles —
`Heading`, `Label`, `Body`, `Card`, `Pill`, `PrimaryButton`, `OutlineButton`, `MicButton`,
`PlainList`, `ScenarioItem`, `ScenarioList`, `InputBox`, `ScrollHost`.
Hard-won constraint: **XamlReader cannot resolve `clr-namespace` types from its own assembly**, so
no custom-type references and no converter references may appear in that XAML. Per-item styling
(chat bubbles, scenario cards) is therefore built in C#.

`src/SpeakCity/NativeMainWindow.cs`: header (mic mark, SPEAKCITY wordmark, AI badge, tagline),
scenario list as cards with emoji medallion + title + role, mission card with quoted example
pills, conversation as bubbles (mint = Lucy, teal = learner) inside a ScrollViewer, footer status
bar. Emoji are text in `Segoe UI Emoji` (no image file, codec, or packaged resource to lose).

### 1e. Earlier build-script fixes in this session (commit `b489305`)
- `build/windows.ps1`: removed `Assert-Exit` after `dotnet-install.ps1` (a PowerShell script, so
  `$LASTEXITCODE` was stale plumbing and the SDK install was reported as failed on a machine with
  no dotnet on PATH). Judged by the `Test-Path`/version/arch checks that follow instead.
- Publishing now **requires a private repo**: an unsigned installer plus the temporary signed
  download links in `.build/downloads.json` are not published to a public repository.
---

## 2. What WORKS right now (verified live on this machine)

| Thing | Evidence |
| --- | --- |
| AI conversation via the app's own ApiClient + saved DPAPI settings | connection test PASS; turn returned a JSON object with a reply; feedback returned corrections with en/kk/ru |
| Model reliability | nex-agi/nex-n2.5-pro:free 3/3 turns, 8/8 feedback (6/6 on a focused duplicate-key probe) |
| Saved settings | Model nex-agi/nex-n2.5-pro:free, BaseUrl https://openrouter.ai/api/v1, key present (DPAPI-protected, never printed or committed); fresh backup at %LOCALAPPDATA%\SpeakCity\api-settings.bin.backup |
| Speech suites | python -B tests/speech_worker_test.py -> Ran 35, OK |
| Notice gate | python -B tests/notice_collection_test.py -> Ran 4, OK |
| UI + recorder suites | node --test ui/tests/ui.test.cjs ui/tests/recorder.test.cjs -> 39 pass, 0 fail |
| C# build | dotnet build src/SpeakCity/SpeakCity.csproj -c Release -> 0 errors (2x NU1510 warnings, pre-existing) |
| Native smoke regression | SpeakCity.exe --ui-smoke <report> -> status=passed, 11/11 checks |
| New UI renders | window captured at 1220x880; automation tree exposes every control under the same names (Configure AI, Start conversation, Speak, Replay Lucy, Read Lucy aloud, Send, Finish and feedback) |
| Installed app config | reads the same %LOCALAPPDATA%\SpeakCity\api-settings.bin, so the AI fix applies without reinstalling |

---

## 3. What does NOT work / is blocked

### 3a. Installer rebuild is BLOCKED by network timeouts (environment, not code)
build/windows.ps1 has now failed TWICE in a row at the controlled eSpeak stage:

native_espeak.ps1:434  Controlled eSpeak source build failed (1).
build/collect_notices.py:261  response.read(...) -> TimeoutError: The read operation timed out

That is build/collect_notices.py fetch() line 251 (opener.open(req, timeout=45)) timing out while
streaming the pinned eSpeak source tarball from codeload.github.com. A HEAD probe to that same URL
returned 200 in 2.9 s, so the host is fine - the connection is being dropped mid-body.
Cloudflare WARP is running on this machine and is the prime suspect.
Nothing was left half-written: out/native-espeak was cleaned before the second attempt and is
still absent, so there is no stale state to clean before the next try.

### 3b. Consequently: the installer and out/app payload are stale
- out/release/SPEAKCITY-AI-Setup-x64.exe  2026-09-19 21:39  old (no AI fix, no new UI)
- out/app/SpeakCity.exe                   2026-09-20 20:55  old
- src/SpeakCity/bin/Release/.../SpeakCity.exe  2026-09-21 18:55  CURRENT, has both fixes
- out/worker/speakcity-speech-worker.exe  2026-09-19 21:34  current enough (worker unchanged)
- out/native-espeak/manifest.json         ABSENT  the build never got that far

So: the AI fix is live for the installed app (shared settings file), but a NEW installer that
contains the duplicate-key retry plus the new UI does not exist yet.

### 3c. Still unverified (unchanged from the audit)
- A real microphone on a physical Windows 11 machine (the build agent has none).
- Installing on a clean PC.
- Interactive UI on a machine with no desktop session (reports skipped).
- needs_review: true on every transcript is intentional; it is not a pronunciation score.

---

## 4. Exact next steps (in order)

1. Retry the installer build. Nothing needs cleaning first - see the command in section 6.
   If it fails at the same native_espeak.ps1:434 / TimeoutError again, treat it as a network
   problem: pause/disable Cloudflare WARP or switch network, then retry.
2. Push or hold. main is 6 commits ahead of origin/main and unpushed. Pushing triggers the GitHub
   Actions build; publishing now auto-skips on a public repo (see section 5), so CI will build and
   verify but not publish a release.
3. Mic test on the real machine - press Start conversation, then Speak, and confirm the transcript
   appears in the box.
4. Optional, explicitly deferred by the user (UI polish). A network-independent hardening item,
   agreed as optional and NOT yet done: bounded retries with backoff inside
   build/collect_notices.py fetch() so one dropped connection cannot lose a ~20-minute build.

---

## 5. Decisions and agreements already made (do not relitigate)

- Do NOT replace Kokoro, faster-whisper, the frozen speech worker, or speech/worker.py. The
  engine-level pipeline was proven working; a user-facing Whisper problem would be an integration
  bug, not an engine bug.
- Do NOT remove WebView2. It is genuinely required by the optional --webview window; only the full
  Edge browser dependency was removed (together with SpeakCityServer).
- Do NOT weaken: DPAPI settings storage, HTTPS enforcement, the fail-closed licence/notice gate
  (build/collect_notices.py, no --force flag), offline speech, bounded API responses.
- Do NOT hard-code personal paths (C:\Users\Acer\...) in build scripts. Discovery order is
  SPEAKCITY_DOTNET / SPEAKCITY_PYTHON env override -> PATH -> DOTNET_ROOT/registry -> portable
  scratch install under RUNNER_TEMP -> TEMP -> out/tmp.
- Do NOT modify the english repo, and do not touch the stray C:\Users\Acer\.git.
- No credentials in the repo. This file is committed to a PUBLIC repo, so it names no key.
- Keep the UI a dependency-free HTML/CSS/JS + native WPF pair. No frontend framework, no CDN.
- Speech stays local; only conversation text reaches the configured provider.
- Model choice: use a CONCRETE model ID, never an auto-router (openrouter/free or openrouter/auto).

---

## 6. Commands to continue

Work only in C:\Users\Acer\Desktop\english.

Full installer build in the background, logs to %TEMP%:
  $out="$env:TEMP\sc_build_out.txt"; $err="$env:TEMP\sc_build_err.txt"
  $p = Start-Process -FilePath 'C:\Program Files\PowerShell\7\pwsh.exe' `
       -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','build\windows.ps1' `
       -WorkingDirectory 'C:\Users\Acer\Desktop\english' `
       -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
  "pid=$($p.Id)"
  Get-Content $err -Tail 20 -ErrorAction SilentlyContinue

Tests that need no build:
  python -B tests/speech_worker_test.py          # 35 tests
  python -B tests/notice_collection_test.py      # 4 tests
  node --test ui/tests/ui.test.cjs ui/tests/recorder.test.cjs   # 39 tests

Compile plus native smoke without the full pipeline:
  & 'C:\Users\Acer\dotnet10\dotnet.exe' build src\SpeakCity\SpeakCity.csproj -c Release
  & "$PWD\src\SpeakCity\bin\Release\net10.0-windows\win-x64\SpeakCity.exe" --ui-smoke "$env:TEMP\smoke.json"
  & "$PWD\src\SpeakCity\bin\Release\net10.0-windows\win-x64\SpeakCity.exe" --diagnose "$env:TEMP\diag.json"

Run the app (native window is the default; --webview opens the old WebView2 window):
  & "$PWD\src\SpeakCity\bin\Release\net10.0-windows\win-x64\SpeakCity.exe"

Live AI re-check through the app's own client (uses the saved DPAPI settings):
  cd "$env:TEMP\sc-apicheck"; & 'C:\Users\Acer\dotnet10\dotnet.exe' run --project . -c Release

---

## 7. Files changed now and why

Committed in this session:
- README.md, docs/README.md, ui/README.md  (1e079b7)
- build/windows.ps1, .github/workflows/windows-desktop.yml  (b489305)
- src/SpeakCity/ApiClient.cs, src/SpeakCity/AppRouter.cs, README.md  (15ff396)
- src/SpeakCity/NativeMainWindow.cs, src/SpeakCity/NativeTheme.cs (new)  (aa8ef44)

Working tree at checkpoint: only speech/smoke-test-summary.json (the seconds field moved
0.7658 -> 0.719 because the suite was re-run; 35 tests, 0 failures). Kept, not reverted.

Not in the repo (temporary, in %TEMP%, safe to delete): the sc-apicheck harness that proves the
live AI path, sc_probe.py / sc_dup.py model probes, sc_modeltest.ps1, sc_shot*.ps1 screenshot
helpers, and the build logs. The harness is worth keeping until the next live AI check.

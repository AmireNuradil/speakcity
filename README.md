# SPEAKCITY AI — Windows desktop

A Windows 11 app for practising **spoken English** in eight everyday places. Lucy asks, you answer out loud, and the app keeps up with whatever you actually said. Recording and voices happen on your own PC; only the words of the conversation go to the AI provider you configure.

## The short version

| Question | Answer |
| --- | --- |
| What is this? | Desktop English conversation trainer: Airport, Hotel, Café, Shop, Hospital, City Map, School, Job Interview |
| Can I download it yet? | Not from a release page, no. See [Status](#status) and [Getting the installer](#getting-the-installer) |
| Do learners need Python? | No. Python, CMake and .NET are only for the machine that *builds* the installer |
| What does it cost? | Free for learners. You bring your own AI API key |
| Interface languages | English, Kazakh, Russian — speaking practice is always English |
| What's on your PC afterwards | Installer in `%LOCALAPPDATA%\Programs\SPEAKCITY AI`; settings, vocabulary and the browser profile in `%LOCALAPPDATA%\SpeakCity` |
| The real specification | The original research paper. This repo rebuilds it as a native Windows app |

## Where everything lives

```
english/
├── src/SpeakCity/          the Windows app itself (C#, WPF + WebView2)
│   ├── MainWindow.cs           window, WebView2 host, microphone permission, --ui-smoke
│   ├── AppRouter.cs            the /api/* routes the UI talks to (in-memory sessions)
│   ├── ApiClient.cs            talks to your configured AI provider
│   ├── ApiConfig.cs            validates + encrypts that provider setting (per Windows user)
│   ├── ApiSettingsWindow.cs    the native "Configure AI" dialog (never the browser)
│   ├── AppStartup.cs           bounded browser-component start-up, startup.json trace, --diagnose
│   ├── SpeechWorkerClient.cs   starts the bundled speech worker over stdin/stdout pipes
│   └── SelfTests.cs            the packaged --self-test that CI runs before packaging
├── ui/                     the screens: HTML/CSS/JS, no framework, no CDN
│   ├── app.js                  scenes, conversation, recording, feedback, vocabulary
│   ├── i18n.js                 every EN/KK/RU string
│   ├── recorder.js + -worklet  microphone capture, hands-free segmentation, WAV export
│   ├── scenario-catalog.js     display-only fallback copy of the dataset (keep aligned)
│   └── tests/                  dependency-free Node tests for the above
├── content/scenarios.json  the eight scenarios: roles, missions, hints, vocabulary seeds
├── speech/                 the bundled offline speech engine (Python, frozen into an .exe)
│   ├── worker.py                 Kokoro TTS + faster-whisper STT, JSON-lines protocol
│   ├── download_assets.py        SHA-pinned model downloader (offline afterwards)
│   ├── requirements.txt          the exact pinned build environment
│   └── speech_worker.spec        PyInstaller recipe: ship the whole folder, not just the .exe
├── build/                  how the installer is made
│   ├── windows.ps1               the whole pipeline, stage by stage
│   ├── native_espeak.ps1         controlled eSpeak NG build from pinned source
│   ├── collect_notices.py        third-party notices + corresponding source gate
│   └── installer.iss             Inno Setup script (per-user, no admin rights)
├── assets-source/          illustrations + icon as base64, decoded by the build (see manifest)
├── tests/                  speech worker + notice-collection test suites (standard library only)
├── docs/                   the two things worth reading before shipping
└── .build/                 machine-written build evidence, pushed by CI - do not hand-edit
```

## I want to know X — where do I read it?

| Question | Read this first | And then |
| --- | --- | --- |
| What the finished app should feel like | [docs/README.md](docs/README.md) | [ui/README.md](ui/README.md) |
| The exact `/api/*` request and response shapes | [ui/README.md](ui/README.md) | `src/SpeakCity/AppRouter.cs` |
| The speech worker protocol, limits, error codes | [speech/README.md](speech/README.md) | `speech/worker.py` |
| Licences, GPL obligations, the source companion | [docs/THIRD_PARTY.md](docs/THIRD_PARTY.md) | `build/collect_notices.py` |
| Why the last build passed or failed | [Actions runs](https://github.com/AmireNuradil/english/actions) | `.build/desktop-build.json` (stage + logs tail) |
| What the packaged self-test actually proved | `.build/windows-self-test.json` | `src/SpeakCity/SelfTests.cs` |
| Which model files are downloaded, and their hashes | `speech/assets-manifest.json` | `speech/download_assets.py` |
| Where a user's API key is stored | `src/SpeakCity/ApiConfig.cs` | `%LOCALAPPDATA%\SpeakCity\api-settings.bin`, DPAPI, current Windows user |
| The window is stuck on "Opening SPEAKCITY..." | `src/SpeakCity/AppStartup.cs` | run `SpeakCity.exe --diagnose check.json` and read `docs/README.md` |

## Status

The app is **built but not yet released**. Concretely:

- ✅ Everything up to and including the packaged self-test passes on the Windows build runner: the C# app compiles, eSpeak NG is built from pinned source, the speech worker freezes, Kokoro speaks, Whisper transcribes, and all eight scenarios pass the conversation/feedback/vocabulary checks.
- ✅ The blocker that stopped packaging for a while (a false "licence file missing" report for `setuptools`) is fixed in `build/collect_notices.py`, with `tests/notice_collection_test.py` pinning both the false positive and the fail-closed case.
- ⚠️ No installer has been published from a fully green run yet, so the [Releases page](https://github.com/AmireNuradil/english/releases) is still empty. If it stays empty, that means "not built yet" - not "lost".
- ⏳ Still genuinely unproven, and nobody should claim otherwise: a real AI provider call, a real microphone on a real Windows 11 machine, and installing on a clean PC. The build runs on Windows *Server*, which has no microphone and no desktop to paint a window on - so the packaged UI check reports `skipped (non-interactive-session)` rather than pretending.
- ✅ A start that cannot finish no longer hangs: the interface start-up is bounded, retries a locked browser profile once in a temporary folder, and shows what to do instead of sitting on "Opening SPEAKCITY...". `%LOCALAPPDATA%\SpeakCity\startup.json` and `SpeakCity.exe --diagnose <file>` exist so "it does not open" can be answered with facts.

## Getting the installer

### Route A — let GitHub build it (the normal way)

The workflow runs on every push to `main` (`.github/workflows/windows-desktop.yml`). One run is roughly 10–20 minutes and, at the end, attaches `SPEAKCITY-AI-Setup-x64.exe`, the source archive and the third-party source archive to a private release.

```bash
gh run list --repo AmireNuradil/english --workflow windows-desktop.yml --limit 5   # watch it
gh release download --repo AmireNuradil/english --pattern "*.exe"                  # fetch it
```

If a late *verification* step fails after the installer was already compiled, that installer is still published as a prerelease titled `…AUTOMATED VERIFICATION INCOMPLETE`, together with the build reports - so a near miss never throws a usable build away.

### Route B — build it yourself on a Windows 11 PC

```powershell
git clone https://github.com/AmireNuradil/english.git
cd english
$env:RUNNER_TEMP = "$PWD\out\tmp"; New-Item -ItemType Directory -Force $env:RUNNER_TEMP | Out-Null
.\build\windows.ps1
```

Prerequisites the script expects to find: Windows x64, `py -3.11` (Python 3.11 64-bit), Visual Studio C++ tools with CMake (for the eSpeak build) and .NET 10 (installed automatically if absent). It downloads about 500 MB of models. Your file ends up at `out\release\SPEAKCITY-AI-Setup-x64.exe` - on your own machine the final GitHub-publishing step is recognised as unavailable and skipped, so the run finishes cleanly.

Either way: the build is **unsigned**, so SmartScreen will warn the first time - and the first launch asks for an API base URL, model ID and key in *Configure AI*. There is no built-in key and no scripted fallback conversation.

## Checks you can run without building anything

```bash
python -B tests/speech_worker_test.py       # speech worker protocol, limits, downloader guards
python -B tests/notice_collection_test.py   # the licence gate: no false positives, no false passes
node --test ui/tests/ui.test.cjs ui/tests/recorder.test.cjs   # screens, API contract, all 8 scenes
```

## Ground rules

- **No credentials in the repo.** Ever. Not in tests, not in `.build/`, not "temporarily".
- **Learner data stays local.** Audio is transcribed on the PC; only conversation text reaches the configured provider. Provider eligibility and child-data consent are the owner's responsibility.
- **Fail-closed packaging.** `build/collect_notices.py` returning non-zero is a stop signal, not a warning to switch off. There is no `--force`, and the release is not "compliant" because a build succeeded.
- **`.build/` is written by CI.** Read it, don't edit it - the next run overwrites it anyway.
- **Not medical, not navigation.** Hospital is English practice; City Map uses the fictional 3×3 grid shown in the app.
- **Model weights, native binaries and build output are never committed** - `speech/.gitignore` keeps them out of the repo.

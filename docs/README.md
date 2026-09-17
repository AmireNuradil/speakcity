# SPEAKCITY AI desktop candidate

Windows 11 x64 desktop application with all eight research-paper scenarios. Local Kokoro TTS and Whisper STT are bundled. Adaptive conversation and grammar feedback use the AI API that the owner configures inside the native settings dialog.

## Starting

Install using the Windows installer, then open SPEAKCITY from the Start menu. No Python installation, command prompt, Railway, PWA or local web server is required. The native window displays packaged app screens using Microsoft's WebView2 runtime. Windows 11 normally includes it; the installer offers the official Microsoft prerequisite if missing.

If the window stays on "Opening SPEAKCITY...", it is no longer silent about why. Interface start-up is bounded: 20 seconds for the browser component and 30 for the page bridge. A browser profile folder that security software has locked is retried once in a temporary location, and anything that still fails is written in the window as an instruction you can act on, with a Try again button - never as a modal dialog that no one can see. The last start is also recorded in `%LOCALAPPDATA%\SpeakCity\startup.json` (stage names, timings and folder paths only: no conversations, no recordings, no keys). To check an install completely:

```powershell
& "$env:LOCALAPPDATA\Programs\SPEAKCITY AI\SpeakCity.exe" --diagnose "$env:USERPROFILE\Desktop\speakcity-check.json"
```

That reports the WebView2 runtime version, whether the app's own folders are writable, whether the packaged speech component starts and whether its models are ready.

Open Configure AI and enter an eligible provider's HTTPS API base URL, exact model ID, and API key. Use Test connection, then Save. No provider or key is silently selected or bundled. The key is protected for the current Windows user and is not passed to the UI pages. Do not use one embedded master key for public distribution. API eligibility and child-data consent requirements apply; do not send sensitive learner data.

Choose Airport, Hotel, Café, Shop, Hospital, City Map, School or Job Interview. Type, click to record and review your transcript, or start hands-free mode. Lucy's follow-up must depend on your answer. End the dialogue to request grammar feedback and save vocabulary. All support screens offer English, Kazakh and Russian; speaking practice remains English. American voice is the default; British voice and speed are adjustable.

Speech processing stays on the PC; only conversation text is sent to the configured AI provider. An internet connection is needed for API replies and feedback. Microphone permissions are requested explicitly. Saved vocabulary/preferences persist in the Windows user's application profile. API settings use Windows-protected encrypted storage. Active conversations are temporary app memory and are not guaranteed to survive an app restart.

## Release status

This is an unsigned candidate until signing is configured. Do not treat it as government-approved or a certified production build. Build tests and any remaining limitations are supplied with the release. Do not disable Windows security if an installer is blocked; stop and review the signature/source with the project owner.

Windows Server build-runner tests do not replace a physical Windows 11 microphone test. Nor do they prove the visible window: a build agent has no interactive desktop to paint on, so the packaged UI check reports `skipped (non-interactive-session)` in `.build/native-ui.json` and in the release notes. A skipped check is recorded as skipped and is never counted as a pass. Automated API-interface tests use explicit test doubles unless a real API connection is separately configured; they are not evidence of a live provider test. The runtime application never falls back to scripted AI replies.

The Hospital activity is language practice, not medical advice or diagnosis. The City Map activity uses the explicit fictional 3×3 street grid shown in the app, not real-world navigation. The original research paper's core functions guide the design; its experimental results do not establish performance of this new build.

See THIRD_PARTY.md and bundled notices/source for dependency and redistribution terms. Model/runtime binaries and source retain their own licences. The app is free to learners; no experimental group sizes or operating-price forecasts are part of the product specification.

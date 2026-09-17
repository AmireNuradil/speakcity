# SPEAKCITY desktop UI

Standalone, dependency-free UI for the Windows shell's secure virtual origin, `https://speakcity.local/`. The shell maps this directory directly. No server, external script, provider SDK, browser installation flow, or Node integration is required at runtime.

## Files

- `index.html`: root-relative entrypoint and restrictive same-origin CSP. Blob media is allowed.
- `app.js`: all-eight city/scenario rendering, native configuration onboarding, adaptive API conversation, recording modes, feedback, vocabulary and storage guards.
- `app.css`, `i18n.js`: reused appearance and EN/KK/RU strings, adapted to Windows, local speech, and a configured AI provider.
- `scenario-catalog.js`: display-only fallback metadata copied from `../content/scenarios.json` (id, title, role, mission, hint, image). It enables browsing before bootstrap and during API errors. Bootstrap metadata overrides it. It contains no openings, dialogue responses, or model context. Keep this display snapshot aligned if the dataset changes.
- `recorder.js`, `recorder-worklet.js`: real mono microphone capture, hands-free segmentation, bounded PCM WAV export, and cleanup on stop/cancel, hidden window, navigation and exit.
- `assets/{city,lucy,airport,cafe}.webp`: unchanged copies of the existing illustrations. The six other scenarios use `lucy.webp` as the dataset specifies.
- `tests/*.test.cjs`: dependency-free development tests, not loaded by the entrypoint.

## Exact native API expectations

Every API request is same-origin and includes `X-Speakcity: 1`. JSON request bodies use `Content-Type: application/json`. No request includes provider credentials or a provider URL. Requests and responses must not contain API keys; native error details must not echo secrets.

| Route | Request | Successful response used by the UI |
| --- | --- | --- |
| `GET /api/bootstrap` | No body; cache disabled | `{conversation_installed:boolean, tts_installed:boolean, stt_installed:boolean, engine:string, tts:"Kokoro", stt:"Whisper", max_turns:8, desktop:true, scenarios:[full scenario objects]}` |
| `POST /api/configure` | **No body**; open the native settings dialog | `{configured:boolean}` after the dialog is dismissed. The UI then refetches bootstrap. It does not automatically start a conversation. No UI timeout is imposed while the dialog is open. |
| `POST /api/sessions` | `{scenario, language:"en"\|"kk"\|"ru", level:"A1"\|"A2"}` | `{session_id:string, reply:string}`. Only this reply is used as the opening; the bundled catalog never supplies dialogue. |
| `GET /api/sessions/{id}` | No body; cache disabled | `{scenario, turn_count:number, messages:[{role:"user"\|"assistant",content:string}], ended:boolean, busy?:boolean, feedback?:object\|null}`. Busy sessions are polled every 2 seconds, up to 90 follow-ups. |
| `DELETE /api/sessions/{id}` | No body | JSON success or HTTP 204. A 404/`session_expired` means the old session is already gone. |
| `POST /api/sessions/{id}/turn` | `{text:string, request_id:string}` | `{reply:string, turn_count:number, at_limit?:boolean}`. Native turns must be idempotent for `request_id`; retries preserve it. |
| `POST /api/sessions/{id}/finish` | `{language:"en"\|"kk"\|"ru"}` | `{scenario,turn_count,corrections:[{original,corrected,explanation:{en,kk,ru}}],vocabulary:[{word,meaning:{en,kk,ru},example,scenario?,encountered?}],messages?:[...]}`. Vocabulary without `scenario` inherits the finished scenario. Missing feedback history falls back to the actual session history. |
| `POST /api/tts` | `{text,voice:"american"\|"british",speed:number}` | WAV bytes (`audio/wav`). Played from a local blob URL, revoked on completion/cancel. |
| `POST /api/stt` | Raw WAV bytes, `Content-Type: audio/wav` | `{text:string}`. Recorder output: PCM signed 16-bit, mono, 16 kHz, at most 29 seconds. |

`conversation_installed` is interpreted as **AI provider configured**, not “a local conversation model is installed.” Missing Kokoro/Whisper disables their respective speech action, not typed conversation. Scenario IDs are `airport`, `hotel`, `school`, `hospital`, `cafe`, `shop`, `directions`, `interview`. Session IDs must be 1–160 ASCII letters, digits, `_` or `-` (UUIDs are supported). Counts must be integers from 0 to 8. Turn input is limited to 400 characters. Default accent is American and speed is 0.95; saved valid preferences remain unchanged.

For errors, return a non-2xx status with `{detail:"known_code_or_useful_English_message"}`. Existing codes and common provider-configuration/auth/rate-limit/local-speech codes are localized. Unknown detail text is displayed as escaped text. Temporary failures preserve the draft; expired sessions do not clear saved vocabulary. HTTP 404 is treated as expired for session GET/DELETE.

## Directions schematic

The practice view, including narrow windows, shows this exact dataset geometry independently of the illustrated city:

```
Airport  — Hotel — School
   |         |       |
Hospital — Café  — Shop
   |         |       |
City Map — Park  — Job Interview
```

Only adjacent horizontal/vertical cells are joined. English place labels are always present for the English dialogue, with additional Kazakh/Russian labels when chosen. City Map is marked as the default start, not a dynamically inferred learner position. The client does not invent routes or interpret learner movement; the native AI uses the dataset context.

## Persistence and microphone lifecycle

Preserved keys: `sc_preferences_v1` and `sc_vocabulary_v1` in localStorage; `sc_active` in sessionStorage. New `sc_draft_v1` in sessionStorage is a versioned temporary record of `{version:1, session_id, draft, pending}`. Pending includes `text`, `request_id`, and a client-only `base_count`; only `text` and `request_id` are transmitted. Preferences are whitelisted, corrupt vocabulary is filtered, and unknown schema fields are not copied into saved records. There is no credential storage in this UI.

Refresh retrieves real session history from the native API; it does not reopen the mic or autoplay restored speech. Native session lifetime is authoritative. Closing the window is not promised to preserve active conversations/drafts. Vocabulary/settings persist as long as the shell retains its WebView2 user-data directory.

Microphone permission is explicitly requested. Manual mode produces an editable draft. Hands-free sends after a speech pause; pause during transcription keeps the resulting draft without automatically sending it. Cancel releases media tracks before awaiting audio-context shutdown. Late permission, worklet load, audio-context resume and TTS responses cannot restart capture/playback after cancellation.

Vocabulary export uses an `a[download]` link to a same-origin blob (`speakcity-vocabulary.json`), not external navigation. The shell must allow/handle the WebView2 download event for it. Do not treat a local blob download as a request to open another browser origin.

## Validation and integration limits

Run from any directory with Node 18+:

```
node --check /path/to/ui/app.js
node --check /path/to/ui/i18n.js
node --check /path/to/ui/scenario-catalog.js
node --check /path/to/ui/recorder.js
node --check /path/to/ui/recorder-worklet.js
node --test /path/to/ui/tests/ui.test.cjs /path/to/ui/tests/recorder.test.cjs
```

The harness tests generated UI markup, API contracts/state transitions, three languages, all eight scenes, configuration gating, escaped untrusted text, vocabulary operations, refresh/idempotency, blob audio, and real recorder/worklet code with simulated media devices. Test responses are isolated test doubles, not a runtime demo or fixtures.

Native implementation was still in progress during this port. Windows/WebView2 visual layout, real provider adaptivity, actual Kokoro/Whisper output, device permission behavior, native-dialog timing and download handling still require integration testing in the completed shell. Serve `.js` with an executable JavaScript MIME type, allow `getUserMedia` for the trusted origin, preserve the WebView2 user-data directory, and keep all provider credentials inside native code.

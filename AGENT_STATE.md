# AGENT_STATE.md — SPEAKCITY AI takeover checkpoint

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
- **Real microphone.** Still never tested on physical hardware — and it cannot be on this machine:
  enumerating capture devices returns none. The smoke test exercises the bundled audio paths, not a
  live microphone, so driver behaviour remains unverified even though section 4b removed the three
  ways a compliant-but-different driver could have been rejected outright. This stays the single
  most important manual check: Start conversation -> Speak -> transcript must appear in the box.
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

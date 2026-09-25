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
- **DeepSeek's weak spot is the grammar-feedback JSON**: about one call in three produced output
  that `AppRouter`'s filters discarded — which the learner sees as *"no mistakes found"*,
  a silent wrong answer on the app's core feature.
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

Validation after the edits: `tests/speech_worker_test.py` Ran 35 OK · `tests/notice_collection_test.py`
Ran 8 OK · `node --test ui/tests/` 39 pass 0 fail · `dotnet build -c Release` 0 errors ·
`SpeakCity.exe --ui-smoke` status=passed 11/11 · `windows.ps1` parses with 0 errors.

Also: `out/native-espeak-20260923-evidence/` — the 23.09 manifest moved aside (not deleted) so the
fresh build had an empty output root. `%LOCALAPPDATA%\SpeakCity\api-settings.bin` now selects
`deepseek/deepseek-v4.1-flash` (no key is stored in this repo; none is printed here).

## 5. Installer build status

A full `.\build\windows.ps1` run was started in this session **after** the fixes, detached, with
logs in `%TEMP%\sc_build10_out.txt` / `sc_build10_err.txt`. Check those plus
`out\release\SPEAKCITY-AI-Setup-x64.exe` timestamp before assuming it finished — the build takes
20-40 minutes and the previous attempts all died quietly. If it succeeds, the installer is the
first one containing both the AI fix and the new native UI.

## 6. What is NOT done / still unverified

- **Push.** `main` is ahead of `origin/main` by 9+ commits and nothing was pushed in this session.
  The repo is public, so publishing a release is auto-skipped; pushing is the owner's call.
- **Real microphone.** Still never tested on physical hardware by an agent — the smoke test uses
  bundled audio paths, not a live mic. This is the single most important manual check left:
  Start conversation -> Speak -> transcript must appear.
- **Silent feedback failure** (section 2): `AppRouter` cannot tell "the model returned no
  corrections because the English was clean" apart from "the model returned junk we discarded".
  A minimal fix is to require an explicit marker in the contract, e.g. ask for
  `{"corrections":[...],"checked":true}` and reject a reply without `checked:true`. Not done here
  because it changes the provider contract and the UI copy without the owner in the loop.
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

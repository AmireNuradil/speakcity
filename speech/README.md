# SPEAKCITY bundled speech worker

Persistent stdin/stdout JSON-lines worker, adapted from the previously tested `speakcity-app/speech.py`, `download_models.py`, and `download_stt.py`. No HTTP listener, microphone access, shell command endpoint, GPU, Torch, spaCy, or inference API. The desktop owns recording/playback and launches exactly one worker with redirected pipes, keeping it alive between turns.

## Runtime and assets

- TTS: real `kokoro-onnx==0.6.1`, Kokoro v1.0, ONNX Runtime CPU. Default American `af_heart` / `en-us`; British `bf_emma` / `en-gb`. Speed range **0.75–1.2 inclusive**, default **0.95**.
- STT: real `faster-whisper==1.2.1`, `base.en`, CTranslate2 **CPU int8**. Conversion repository `Systran/faster-whisper-base.en`, revision `3d3d5dee26484f91867d81cb899cfcf72b96be6c`. Every transcript has `needs_review: true`; this is not an assessment of a learner's pronunciation or correctness.
- Sequential requests, lazy native imports/model loading, one cached model session each. Two selected Kokoro voice styles are cached in memory rather than reread every turn. The first request for each model includes its load cost; later turns reuse it. Default two CPU inference threads, operator-selectable `--threads 1` through `4`; numerical/OpenMP pools are bounded to the same limit, ORT inter-op and Whisper workers to one, Silero VAD to one. This is a per-pool compute bound, not a promise that the OS will report at most four total process threads.
- `ping` verifies lengths and SHA-256 without loading numerical libraries. Hash results are cached against file fingerprints; missing/changed files are rechecked. Both Kokoro publisher digests **must pass before model/NPZ loading**. The four Whisper runtime files are also hash-verified. Models should be distributed read-only to learners; this is not a defence against an administrator concurrently modifying files during inference.
- Developer default: `speech/models/`. Frozen default: `Path(sys.executable).parent / "models"`, **not** `_MEIPASS`/`_internal`, CWD, the user's home or a Hugging Face cache. `--models PATH` is an **operator-only CLI override**, never accepted over the protocol.
- All inference inputs stay in memory. The worker does not persist learner text/audio. Unknown WAV metadata is ignored; only validated PCM is passed into the PyAV resampler, never into a media-format decoder. PyAV's native resampling needs no separate FFmpeg executable.

The separate operator downloader accepts only fixed HTTPS publisher URLs and a fixed Whisper commit. It rejects checksum/length mismatches, unverified existing files and existing partial files; it never downloads executable Python or extracts archives. `assets-manifest.json` documents the pins, but **the worker trusts the compiled-in manifest in `download_assets.py`, not a supplied JSON manifest**. Whisper digests are recorded hashes of the tested pinned snapshot, not publisher cryptographic signatures.

From the desktop root on a developer machine:

```sh
python -B speech/download_assets.py
python -B speech/download_assets.py --models /operator/model/path --verify-only
python -B speech/worker.py --models /operator/model/path --threads 2
```

`download_assets.py` can require an Internet connection during operator setup. **`worker.py` never calls it to download anything**: local-files-only Whisper, offline/telemetry environment flags, explicit CPU providers and a Python audit hook rejecting network/DNS/bind and subprocess/shell execution. This hook is defence in depth, not an OS sandbox for native libraries; apply outbound-deny firewall rules to the installed worker if an OS-enforced network guarantee is required. No user-provided model name/path/URL is passed into a library.

## Wire contract

UTF-8 bytes, one JSON object followed by LF per request. CRLF also works. No unsolicited startup messages; write a `ping` and read its matching reply. Flush stdin after each request, continue draining stdout, and impose a desktop-side deadline/cancellation policy. The worker processes one request at a time; cancellation of an active native inference requires terminating/restarting that process. `quit` acknowledges and exits after earlier requests finish; closing stdin is also graceful.

```json
{"id":"health-1","op":"ping"}
{"id":"reply-1","op":"tts","text":"A glass of water, please."}
{"id":"reply-2","op":"tts","text":"Where is the train station?","voice":"british","speed":1.2}
{"id":"listen-1","op":"stt","audio_base64":"<base64 PCM WAV bytes>"}
{"id":"stop-1","op":"quit"}
```

Successful envelopes:

```json
{"id":"reply-1","ok":true,"result":{"audio_base64":"<base64 RIFF WAV>","sample_rate":24000,"duration_seconds":1.78}}
{"id":"listen-1","ok":true,"result":{"text":"A glass of water, please.","needs_review":true}}
{"id":"stop-1","ok":true,"result":{"quitting":true}}
```

`ping.result` includes `protocol_version: 1`, aggregate `models_ready`, `frozen` (`bool(sys.frozen)`), `offline`, `threads`, and `tts`/`stt` identity objects. Each identity reports `models_ready`, `loaded` and `load_count` separately; model integrity is **not** a promise that the native package/DLL stack has already been loaded successfully. The TTS identity lists both voices, its default and speed bounds; STT includes repository, pinned revision, CPU and int8.

Errors use `{"id":"...","ok":false,"error":{"code":"INVALID_AUDIO","message":"..."}}`. Codes include `INVALID_JSON`, `INVALID_REQUEST`, `UNKNOWN_OPERATION`, `LINE_TOO_LARGE`, `INVALID_TEXT`, `TEXT_TOO_LONG`, `INVALID_VOICE`, `INVALID_SPEED`, `INVALID_AUDIO`, `AUDIO_TOO_LARGE`, `INVALID_SAMPLE_RATE`, `INVALID_DURATION`, `MODEL_MISSING`, `MODEL_INTEGRITY`, `TTS_UNAVAILABLE`, `STT_UNAVAILABLE`, `TTS_FAILED`, `STT_FAILED`, `OUTPUT_TOO_LARGE`, and `INTERNAL_ERROR`. If the JSON/id cannot safely be parsed or a line is oversized/unframed, `id` is the empty string; otherwise it is preserved. Errors do not terminate the worker. Do not put learner text or secrets into correlation IDs.

Limits:

- Request/response JSON line: **4 MiB**, excluding final LF. Overlong input is drained in bounded chunks through LF and emits one error; the next request still works. EOF without the required LF returns a framing error, never executes that partial request.
- `id`: nonempty string, at most 128 characters, no controls/unpaired surrogates.
- Text: at most **1,200 characters before trimming**, nonempty and containing words; controls other than tab/CR/LF are rejected. Voice and speed are strictly validated (booleans are not numbers).
- STT audio: strict base64 (no `data:` prefix), decoded **at most 3 MiB**, RIFF/WAVE PCM tag 1, **mono, 16-bit**, **0.1–30 seconds**, sample rate one of **16000, 22050, 24000, 44100, 48000 Hz**. Chunk lengths, RIFF length, block alignment, byte rate, fmt/data multiplicity/order, padding and actual frame count must agree. Up to 64 bounded chunks; no RF64, RIFX, compressed/extensible WAV, truncated data, or trailing undeclared bytes.
- TTS output is PCM WAV at 24 kHz. The generated result must fit the output bounds; overly long speech returns `OUTPUT_TOO_LARGE` instead of silent truncation. The desktop should send brief replies; maximum-length text can produce more than 30 seconds of TTS and therefore must not be directly submitted as one STT recording.
- Duplicate JSON keys, nonfinite JSON constants, unknown fields and unknown operations are rejected. No paths, raw model voice identifiers, languages or decoder options are accepted from requests.

The protocol has a dedicated duplicated stdout descriptor. Third-party Python/native stdout and stderr are redirected to the null device permanently, including Windows native standard handles, so shutdown/debug messages cannot corrupt JSON or expose speech. Worker diagnostics go only to a saved stderr descriptor as **fixed literal messages**, never exception strings, stack traces, IDs, text, audio or URLs. This deliberately trades verbose runtime diagnostics for privacy; use the asset verifier and controlled synthetic release tests to investigate installation failures. CLI `--help` is sent to stderr only.

## Windows onedir packaging — still requires Windows validation

Build on **Windows x64 with Python 3.11**, not by copying Linux wheels or cross-freezing. Python is needed only on the build machine. A correctly built onedir distribution carries the interpreter, standard library, extensions, native DLLs and data; **learners must not be asked to install Python, eSpeak or FFmpeg**. Ship the entire directory, not just its executable.

`requirements.txt` pins the existing tested Linux runtime dependency closure and excludes unrelated app/server/LLM packages. The HF/HTTP entries are mandatory transitive dependencies, not evidence of network inference. Windows-only transitives such as `colorama`/`pyreadline3`, the build-only PyInstaller/hook versions, and availability of these exact native wheels must be resolved/pinned/audited on the Windows builder before release; they are not invented as “tested” pins here.

Example builder commands from the desktop root (not yet executed on Windows):

```powershell
py -3.11 -m venv speech\.venv
speech\.venv\Scripts\python -m pip install -r speech\requirements.txt
# Install your reviewed/pinned PyInstaller 6.x + hooks in this build environment.
speech\.venv\Scripts\python -m PyInstaller --clean --noconfirm --distpath speech\dist --workpath speech\build speech\speech_worker.spec
speech\.venv\Scripts\python speech\download_assets.py --models speech\dist\speakcity-speech-worker\models
```

The spec uses `console=True` to preserve pipes; the desktop launcher should use `CREATE_NO_WINDOW`/its platform equivalent, not a windowed PyInstaller build. It collects native libraries, wheel-adjacent DLL folders, all relevant package data, package metadata, **the complete `espeakng_loader/espeak-ng-data` tree and eSpeak DLL**, Kokoro vocabulary JSON, and faster-whisper's bundled Silero VAD ONNX. Models are never copied into the executable or collected by the spec:

```text
speakcity-speech-worker/
  speakcity-speech-worker.exe
  _internal/                  # bundled CPython, native libraries and package data
  models/
    kokoro-v1.0.onnx
    voices-v1.0.bin
    assets-manifest.json
    whisper-base.en/
      config.json
      model.bin
      tokenizer.json
      vocabulary.txt
      README.md
```

**Known compatibility addressed in source:** CTranslate2 4.6.0's Windows loader calls `pkg_resources.resource_filename` while the tested `setuptools==84.0.0` no longer includes `pkg_resources`. `windows_compat.py` supplies only the exact `resource_filename("ctranslate2", "")` operation when needed; it does not emulate a general `pkg_resources` module or allow arbitrary paths. This narrow adapter and DLL search/output-handle setup must be tested on Windows. Linux smoke success is not a packaging certification.

Release gates: clean Windows VM with no Python/eSpeak/FFmpeg and network denied; launch from a different CWD and a non-ASCII/spaced install path; `ping.frozen == true`; missing/corrupt assets; cold and repeated American/British TTS; real microphone WAV STT and silence at supported rates; bounded/invalid protocol input and privacy checks; graceful shutdown, CPU/memory review, unsigned/signed installation policy and architecture/VC runtime compatibility. Include final third-party notices and satisfy licensing/source obligations: **Phonemizer and bundled eSpeak NG have GPL-family obligations**. The runtime is not wholly MIT/Apache, and metadata collection is not a legal compliance review.

## Tests and evidence

`tests/speech_worker_test.py` provides the standard-library validation/protocol/asset/downloader/guard tests. Optional smoke uses real assets **in place**, creates synthetic speech in memory, transcribes it with real Whisper, tests both voices and speed extremes, checks all supported resampling rates on silence, confirms lazy initial state and exactly one load per model, and requires a clean JSON-only stdout/empty stderr.

```sh
python -B tests/speech_worker_test.py --smoke-models /agent/workspace/speakcity-speech/models
```

See `smoke-test-summary.json` for the actual run result, timings and synthetic-only transcripts. No model weights, WAV recordings, native binaries or build outputs are committed here. Synthetic round-trip accuracy is **not** a child/learner speech evaluation; **Windows freezing/packaging has not been tested**.

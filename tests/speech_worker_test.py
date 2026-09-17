"""Speech worker validation tests; optional real, synthetic-only protocol smoke.

From the desktop root:
  python -B tests/speech_worker_test.py
  python -B tests/speech_worker_test.py --smoke-models /path/to/models

No weights/audio files are copied. Temporary test files and summary are confined
inside speech/. No model/network inference is needed for the default unit suite.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import queue
import re
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock
import wave

SPEECH = Path(__file__).resolve().parents[1] / "speech"
sys.path.insert(0, str(SPEECH))
sys.dont_write_bytecode = True
import download_assets as assets
import worker
import windows_compat


def wav_bytes(rate=16000, frames=1600, channels=1, width=2):
    output = io.BytesIO()
    with wave.open(output, "wb") as sink:
        sink.setnchannels(channels)
        sink.setsampwidth(width)
        sink.setframerate(rate)
        sink.writeframes(b"\0" * frames * channels * width)
    return output.getvalue()


def chunk(kind, data):
    return kind + struct.pack("<I", len(data)) + data + (b"\0" if len(data) % 2 else b"")


def riff(chunks):
    body = b"WAVE" + b"".join(chunks)
    return b"RIFF" + struct.pack("<I", len(body)) + body


def fmt(rate=16000, tag=1, channels=1, alignment=2, bits=16, byte_rate=None):
    return chunk(b"fmt ", struct.pack("<HHIIHH", tag, channels, rate, rate * 2 if byte_rate is None else byte_rate, alignment, bits))


def env_for_child():
    env = os.environ.copy()
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    env["PYTHONUNBUFFERED"] = "1"
    return env


class SpeechWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="speech-test-", dir=SPEECH)
        self.addCleanup(self.temporary.cleanup)
        self.models = Path(self.temporary.name)

    def assert_error(self, code, fn, *args):
        with self.assertRaises(worker.WorkerError) as caught:
            fn(*args)
        self.assertEqual(caught.exception.code, code)

    def protocol(self, data, runtime=None):
        out, diagnostics = io.BytesIO(), io.BytesIO()
        runtime = runtime or worker.SpeechRuntime(self.models)
        code = worker.serve(io.BytesIO(data), out, runtime, diagnostics)
        self.assertEqual(code, 0)
        return [json.loads(line) for line in out.getvalue().splitlines()], diagnostics.getvalue()

    def test_default_voice_and_speed(self):
        self.assertEqual(worker.validate_tts({"text": " Hello. "}), ("Hello.", "american", 0.95))
        self.assertEqual(worker.VOICE_CONFIGS["american"], {"id": "af_heart", "language": "en-us"})
        self.assertEqual(worker.VOICE_CONFIGS["british"], {"id": "bf_emma", "language": "en-gb"})

    def test_text_limits_and_control_characters(self):
        worker.validate_tts({"text": "a" * 1200})
        for text, code in [("a" * 1201, "TEXT_TOO_LONG"), (" " * 1200 + "a", "TEXT_TOO_LONG"), ("", "INVALID_TEXT"), ("  ", "INVALID_TEXT"), (None, "INVALID_TEXT"), (42, "INVALID_TEXT"), ("!?", "INVALID_TEXT"), ("Hello\x00", "INVALID_TEXT"), ("Hello\x7f", "INVALID_TEXT"), ("Hello\ud800", "INVALID_TEXT")]:
            with self.subTest(text_type=type(text).__name__, code=code):
                self.assert_error(code, worker.validate_tts, {"text": text})
        worker.validate_tts({"text": "Hello\nthere\tfriend."})

    def test_speed_bounds_types_and_finiteness(self):
        for speed in [0.75, 1, 1.2]:
            self.assertEqual(worker.validate_tts({"text": "Hello", "speed": speed})[2], speed)
        for speed in [True, False, None, "1", [], {}, 0.749, 1.201, float("inf"), float("nan"), 10 ** 200]:
            with self.subTest(speed_type=type(speed).__name__):
                self.assert_error("INVALID_SPEED", worker.validate_tts, {"text": "Hello", "speed": speed})

    def test_voice_is_closed_allowlist(self):
        for voice in ["af_heart", "../voice", "AMERICAN", "", 1, [], {}, None]:
            self.assert_error("INVALID_VOICE", worker.validate_tts, {"text": "Hello", "voice": voice})
        self.assertEqual(worker.validate_tts({"text": "Hello", "voice": "british"})[1], "british")

    def test_no_request_paths_or_extra_options(self):
        for op, extra in [("ping", {"models": "/tmp"}), ("tts", {"path": "x"}), ("stt", {"audio_path": "x"}), ("tts", {"device": "cuda"}), ("quit", {"text": "x"})]:
            self.assert_error("INVALID_REQUEST", worker.validate_request, {"id": "x", "op": op, **extra})
        for op in [[], {}, 1, "download", "shell", None]:
            self.assert_error("UNKNOWN_OPERATION", worker.validate_request, {"id": "x", "op": op})

    def test_id_validation(self):
        self.assertEqual(worker.request_id({"id": "a" * 128}), "a" * 128)
        for identifier in [None, 1, [], "", "a" * 129, "new\nline", "bad\udc00"]:
            self.assert_error("INVALID_REQUEST", worker.request_id, {"id": identifier})

    def test_strict_json(self):
        for raw in [b"{", b"\xff", b'{"id":"a","id":"b"}', b'{"speed":NaN}', b'{"speed":Infinity}', b"[" * 1500 + b"]" * 1500]:
            self.assert_error("INVALID_JSON", worker.parse_request, raw)
        for raw in [b"[]", b"42", b"null", b'"text"']:
            self.assert_error("INVALID_REQUEST", worker.parse_request, raw)

    def test_pcm_duration_and_rate_bounds(self):
        for rate in worker.SAMPLE_RATES:
            for frames in [rate // 10, rate * 30]:
                recording = worker.validate_wav(wav_bytes(rate, frames))
                self.assertEqual(recording.frames, frames)
                self.assertEqual(recording.sample_rate, rate)
            for frames in [rate // 10 - 1, rate * 30 + 1]:
                self.assert_error("INVALID_DURATION", worker.validate_wav, wav_bytes(rate, frames))
        self.assert_error("INVALID_SAMPLE_RATE", worker.validate_wav, wav_bytes(8000, 800))

    def test_rejects_stereo_wrong_depth_non_pcm(self):
        for channels, width in [(2, 2), (1, 1), (1, 3), (1, 4)]:
            self.assert_error("INVALID_AUDIO", worker.validate_wav, wav_bytes(channels=channels, width=width))
        for tag in [3, 6, 7, 65534]:
            self.assert_error("INVALID_AUDIO", worker.validate_wav, riff([fmt(tag=tag), chunk(b"data", b"\0" * 3200)]))
        for header in [b"RIFX", b"RF64", b"OggS"]:
            self.assert_error("INVALID_AUDIO", worker.validate_wav, header + wav_bytes()[4:])

    def test_rejects_truncated_and_inconsistent_lengths(self):
        valid = wav_bytes()
        for data in [b"", valid[:-1], valid + b"junk", valid[:4] + struct.pack("<I", 0xFFFFFFFF) + valid[8:], valid[:40] + struct.pack("<I", 0xFFFFFFFF) + valid[44:]]:
            self.assert_error("INVALID_AUDIO", worker.validate_wav, data)
        for settings in [{"alignment": 4}, {"byte_rate": 1234}, {"channels": 0}, {"bits": 0}]:
            self.assert_error("INVALID_AUDIO", worker.validate_wav, riff([fmt(**settings), chunk(b"data", b"\0" * 3200)]))

    def test_chunk_validation_and_safe_metadata(self):
        pcm = chunk(b"data", b"\0" * 3200)
        normal_fmt = fmt()
        self.assertEqual(worker.validate_wav(riff([chunk(b"JUNK", b"abc"), normal_fmt, pcm, chunk(b"LIST", b"ignored metadata")])).frames, 1600)
        for chunks in [[pcm, normal_fmt], [normal_fmt, normal_fmt, pcm], [normal_fmt, pcm, pcm], [normal_fmt, chunk(b"data", b"\0" * 3201)], [normal_fmt, pcm] + [chunk(b"JUNK", b"")] * 64, [normal_fmt, pcm, b"JUNK\x01\0\0\0x"], [normal_fmt, pcm, b"x"], [normal_fmt, chunk(b"data", b"")]]:
            self.assert_error("INVALID_AUDIO", worker.validate_wav, riff(chunks))

    def test_fmt18_requires_zero_extension(self):
        basic = fmt()[8:]
        pcm = chunk(b"data", b"\0" * 3200)
        self.assertEqual(worker.validate_wav(riff([chunk(b"fmt ", basic + b"\0\0"), pcm])).frames, 1600)
        self.assert_error("INVALID_AUDIO", worker.validate_wav, riff([chunk(b"fmt ", basic + b"\1\0"), pcm]))

    def test_base64_and_audio_byte_limits(self):
        for value in [None, 1, b"bytes", "", "a", "!!!!", "data:audio/wav;base64,AAAA", "\u2603", "YQ==\n"]:
            self.assert_error("INVALID_AUDIO", worker.decode_recording, value)
        valid = wav_bytes()
        self.assertEqual(worker.decode_recording(base64.b64encode(valid).decode()).frames, 1600)
        self.assert_error("AUDIO_TOO_LARGE", worker.validate_wav, b"x" * (worker.MAX_AUDIO_BYTES + 1))
        self.assert_error("AUDIO_TOO_LARGE", worker.decode_recording, "A" * (worker.MAX_BASE64_CHARS + 1))

    def test_invalid_requests_do_not_load_models(self):
        runtime = worker.SpeechRuntime(self.models)
        for request in [{"id": "a", "op": "tts", "text": ""}, {"id": "a", "op": "stt", "audio_base64": "bad"}]:
            with self.assertRaises(worker.WorkerError):
                runtime.handle(request)
        self.assertIsNone(runtime._tts)
        self.assertIsNone(runtime._stt)

    def test_ping_missing_models_is_lazy(self):
        response = worker.SpeechRuntime(self.models).ping()
        self.assertFalse(response["models_ready"])
        self.assertFalse(response["tts"]["loaded"])
        self.assertFalse(response["stt"]["loaded"])
        self.assertEqual(response["tts"]["load_count"], 0)
        self.assertEqual(response["stt"]["compute_type"], "int8")
        self.assertEqual(response["stt"]["revision"], "3d3d5dee26484f91867d81cb899cfcf72b96be6c")

    def test_missing_and_tampered_assets(self):
        runtime = worker.SpeechRuntime(self.models)
        self.assert_error("MODEL_MISSING", runtime._verify_assets, assets.TTS_ASSETS)
        for name in assets.TTS_ASSETS:
            (self.models / name).write_bytes(b"bad")
        self.assert_error("MODEL_INTEGRITY", runtime._verify_assets, assets.TTS_ASSETS)
        self.assertIsNone(runtime._tts)

    def test_integrity_hashes_and_cache_invalidation(self):
        data = b"verified test fixture"
        name = "kokoro-v1.0.onnx"
        metadata = {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), "url": "https://example.invalid/not-used"}
        path = self.models / name
        path.write_bytes(data)
        runtime = worker.SpeechRuntime(self.models)
        with mock.patch.dict(assets.ASSETS, {name: metadata}), mock.patch.object(worker, "verify", wraps=assets.verify) as check:
            runtime._verify_assets((name,))
            runtime._verify_assets((name,))
            self.assertEqual(check.call_count, 1)
            path.write_bytes(b"x" * len(data))
            self.assert_error("MODEL_INTEGRITY", runtime._verify_assets, (name,))
            self.assertEqual(check.call_count, 2)
        self.assertFalse(assets.verify(path, metadata))

    def test_persistent_instances_not_reloaded(self):
        runtime = worker.SpeechRuntime(self.models)
        runtime._tts = object()
        runtime._stt = object()
        with mock.patch.object(runtime, "_verify_assets", side_effect=AssertionError("Unexpected reload")):
            self.assertIs(runtime._load_tts(), runtime._tts)
            self.assertIs(runtime._load_stt(), runtime._stt)

    def test_frozen_and_developer_model_locations(self):
        with mock.patch.object(sys, "frozen", False, create=True):
            self.assertEqual(assets.default_models_directory(), SPEECH / "models")
        executable = self.models / "speakcity-speech-worker.exe"
        with mock.patch.object(sys, "frozen", True, create=True), mock.patch.object(sys, "executable", str(executable)):
            self.assertEqual(assets.default_models_directory(), self.models / "models")
            override = self.models / "operator-models"
            self.assertEqual(worker.SpeechRuntime(override).models, override)

    def test_runtime_overrides_unsafe_thread_and_online_env(self):
        with mock.patch.dict(os.environ, {"OMP_NUM_THREADS": "99", "HF_HUB_OFFLINE": "0", "LOG_LEVEL": "DEBUG", "PHONEMIZER_ESPEAK_LIBRARY": "untrusted.dll"}):
            self.assertEqual(worker.configure_runtime(99), 4)
            for name in ["OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "RAYON_NUM_THREADS"]:
                self.assertEqual(os.environ[name], "4")
            self.assertEqual(os.environ["HF_HUB_OFFLINE"], "1")
            self.assertEqual(os.environ["TOKENIZERS_PARALLELISM"], "false")
            self.assertNotIn("PHONEMIZER_ESPEAK_LIBRARY", os.environ)
            self.assertEqual(worker.configure_runtime(0), 1)

    def test_protocol_recovers_and_quit_stops(self):
        responses, logs = self.protocol(b'bad\n{"id":"p","op":"ping"}\n{"id":"q","op":"quit"}\n{"id":"never","op":"ping"}\n')
        self.assertEqual([r["id"] for r in responses], ["", "p", "q"])
        self.assertFalse(responses[0]["ok"])
        self.assertTrue(responses[1]["ok"])
        self.assertEqual(responses[2]["result"], {"quitting": True})
        self.assertEqual(logs, b"")

    def test_oversized_line_is_drained_and_next_request_works(self):
        with mock.patch.object(worker, "MAX_LINE_BYTES", 128):
            responses, _ = self.protocol(b"x" * 300000 + b'\n{"id":"q","op":"quit"}\n')
        self.assertEqual(responses[0]["error"]["code"], "LINE_TOO_LARGE")
        self.assertEqual(responses[1]["id"], "q")
        self.assertTrue(responses[1]["ok"])

    def test_exact_line_boundary(self):
        base = b'{"id":"q","op":"quit"}'
        with mock.patch.object(worker, "MAX_LINE_BYTES", 128):
            responses, _ = self.protocol(base + b" " * (128 - len(base)) + b"\n")
            self.assertTrue(responses[0]["ok"])
            responses, _ = self.protocol(base + b" " * (129 - len(base)) + b"\n")
            self.assertEqual(responses[0]["error"]["code"], "LINE_TOO_LARGE")

    def test_eof_and_partial_line(self):
        self.assertEqual(self.protocol(b"")[0], [])
        responses, _ = self.protocol(b'{"id":"q","op":"quit"}')
        self.assertEqual(responses[0]["error"]["code"], "INVALID_JSON")
        self.assertEqual(responses[0]["id"], "")

    def test_error_does_not_leak_exception_or_request(self):
        secret = "SYNTHETIC_PRIVATE_SENTINEL"
        runtime = mock.Mock()
        runtime.handle.side_effect = RuntimeError(secret)
        responses, logs = self.protocol(json.dumps({"id": "test", "op": "tts", "text": secret}).encode() + b"\n", runtime)
        self.assertEqual(responses[0]["error"]["code"], "INTERNAL_ERROR")
        self.assertNotIn(secret, json.dumps(responses))
        self.assertNotIn(secret.encode(), logs)

    def test_partial_pipe_writes_are_completed(self):
        class Partial(io.BytesIO):
            def write(self, value):
                return super().write(value[:7])
        output = Partial()
        worker.emit(output, {"id": "q", "ok": True, "result": {"quitting": True}})
        self.assertTrue(json.loads(output.getvalue())["ok"])
        self.assertTrue(output.getvalue().endswith(b"\n"))

    def test_output_size_is_bounded(self):
        output = io.BytesIO()
        with mock.patch.object(worker, "MAX_LINE_BYTES", 256):
            worker.emit(output, {"id": "x", "ok": True, "result": {"audio_base64": "A" * 512}})
        response = json.loads(output.getvalue())
        self.assertEqual(response["error"]["code"], "OUTPUT_TOO_LARGE")
        self.assertLess(len(output.getvalue()), 256)

    def test_worker_process_is_lazy_without_runtime_imports(self):
        command = (
            "import sys,runpy; "
            f"sys.path.insert(0,{str(SPEECH)!r}); "
            "import worker; "
            "assert not any(n in sys.modules for n in ['numpy','kokoro_onnx','onnxruntime','faster_whisper','torch','spacy']); "
            f"raise SystemExit(worker.main(['--models',{str(self.models)!r}]))"
        )
        process = subprocess.run([sys.executable, "-B", "-c", command], input=b'{"id":"p","op":"ping"}\n{"id":"q","op":"quit"}\n', capture_output=True, timeout=20, env=env_for_child())
        self.assertEqual(process.returncode, 0)
        self.assertEqual(process.stderr, b"")
        results = [json.loads(line) for line in process.stdout.splitlines()]
        self.assertFalse(results[0]["result"]["models_ready"])
        self.assertEqual(len(results), 2)

    def test_python_network_and_shell_guard(self):
        command = f"""import sys, socket, subprocess
sys.path.insert(0, {str(SPEECH)!r})
import worker
worker.install_offline_guard()
operations = [lambda: socket.getaddrinfo('example.invalid', 80), lambda: socket.socket().connect(('127.0.0.1', 9)), lambda: socket.socket().bind(('127.0.0.1', 0)), lambda: subprocess.Popen([sys.executable, '-c', 'pass'])]
for operation in operations:
    try:
        operation()
    except PermissionError:
        continue
    raise AssertionError('Guard failed')
for module in ('torch', 'spacy', 'tensorflow', 'transformers'):
    try:
        __import__(module)
    except ImportError as error:
        assert 'disabled' in str(error)
        continue
    raise AssertionError('Optional conversion import was allowed')
print('guard-pass')
"""
        process = subprocess.run([sys.executable, "-B", "-c", command], capture_output=True, timeout=20, env=env_for_child())
        self.assertEqual(process.returncode, 0)
        self.assertEqual(process.stdout.strip(), b"guard-pass")

    def test_operator_help_never_pollutes_stdout(self):
        process = subprocess.run([sys.executable, "-B", str(SPEECH / "worker.py"), "--help"], capture_output=True, timeout=20, env=env_for_child())
        self.assertEqual(process.returncode, 0)
        self.assertEqual(process.stdout, b"")
        self.assertIn(b"--models", process.stderr)

    def test_operator_invalid_options_are_safe(self):
        process = subprocess.run([sys.executable, "-B", str(SPEECH / "worker.py"), "--threads", "99"], capture_output=True, timeout=20, env=env_for_child())
        self.assertEqual(process.returncode, 2)
        self.assertEqual(process.stdout, b"")
        self.assertNotIn(b"99", process.stderr)

    def test_manifest_matches_pinned_assets(self):
        manifest = json.loads((SPEECH / "assets-manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest, assets.integrity_manifest())
        self.assertEqual(len(manifest["files"]), 7)
        self.assertEqual(assets.ASSETS["kokoro-v1.0.onnx"]["sha256"], "beb0d1848dee9a49da392cc3df26958d46cfa35d321edf434f52949153f0df3a")
        for name, metadata in assets.ASSETS.items():
            self.assertTrue(metadata["url"].startswith("https://"))
            self.assertNotIn("..", name)
            if name.startswith("whisper-base.en/"):
                self.assertIn(assets.WHISPER_REVISION, metadata["url"])

    def test_legacy_windows_adapter_is_narrow(self):
        for package, resource in [("other", ""), ("ctranslate2", "../x"), ("ctranslate2", "model.bin")]:
            with self.assertRaises(RuntimeError):
                windows_compat.ctranslate2_resource_filename(package, resource)
        fake_spec = mock.Mock(origin=str(self.models / "ctranslate2" / "__init__.py"))
        with mock.patch.object(windows_compat.importlib.util, "find_spec", return_value=fake_spec):
            self.assertEqual(windows_compat.ctranslate2_resource_filename("ctranslate2", ""), str(self.models / "ctranslate2"))

    def test_downloader_failure_does_not_install_or_keep_partial(self):
        name = "tiny-test-asset"
        metadata = {"url": "https://example.invalid/fixed", "bytes": 4, "sha256": hashlib.sha256(b"good").hexdigest()}
        response = io.BytesIO(b"evil")
        response.status = 200
        response.headers = {"Content-Length": "4"}
        opener = mock.Mock()
        opener.open.return_value = response
        with mock.patch.object(assets, "ASSETS", {name: metadata}), mock.patch.object(assets.urllib.request, "build_opener", return_value=opener):
            with self.assertRaises(RuntimeError):
                assets.download(self.models)
        self.assertFalse((self.models / name).exists())
        self.assertFalse((self.models / (name + ".partial")).exists())

    def test_downloader_refuses_existing_partial_and_bad_asset(self):
        name = "tiny-test-asset"
        metadata = {"url": "https://example.invalid/fixed", "bytes": 4, "sha256": hashlib.sha256(b"good").hexdigest()}
        with mock.patch.object(assets, "ASSETS", {name: metadata}), mock.patch.object(assets.urllib.request, "build_opener") as opener:
            partial = self.models / (name + ".partial")
            partial.write_bytes(b"existing")
            with self.assertRaises(RuntimeError):
                assets.download(self.models)
            self.assertEqual(partial.read_bytes(), b"existing")
            partial.unlink()
            destination = self.models / name
            destination.write_bytes(b"bad!")
            with self.assertRaises(RuntimeError):
                assets.download(self.models)
            self.assertEqual(destination.read_bytes(), b"bad!")
            opener.return_value.open.assert_not_called()


def normalize(text):
    return re.findall(r"[a-z0-9']+", text.lower())


def word_error_rate(reference, hypothesis):
    ref, hyp = normalize(reference), normalize(hypothesis)
    row = list(range(len(hyp) + 1))
    for index, word in enumerate(ref, 1):
        following = [index]
        for position, other in enumerate(hyp, 1):
            following.append(min(following[-1] + 1, row[position] + 1, row[position - 1] + (word != other)))
        row = following
    return round(row[-1] / max(1, len(ref)), 4)


def smoke(models: Path) -> dict:
    """Real models in a subprocess over actual stdin/stdout; no user recordings."""
    environment = env_for_child()
    environment["LOG_LEVEL"] = "DEBUG"  # Must be overridden, not leak text.
    process = subprocess.Popen(
        [sys.executable, "-B", str(SPEECH / "worker.py"), "--models", str(models), "--threads", "2"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment,
    )
    responses = queue.Queue()
    stderr_chunks = []
    def read_lines():
        for line in iter(process.stdout.readline, b""):
            responses.put(line)
        responses.put(None)
    def read_errors():
        stderr_chunks.append(process.stderr.read())
    reader = threading.Thread(target=read_lines, daemon=True)
    errors = threading.Thread(target=read_errors, daemon=True)
    reader.start()
    errors.start()
    requests_sent = 0
    def exchange(request):
        nonlocal requests_sent
        requests_sent += 1
        before = time.perf_counter()
        encoded = json.dumps(request, separators=(",", ":")).encode() + b"\n"
        assert len(encoded) <= worker.MAX_LINE_BYTES + 1
        process.stdin.write(encoded)
        process.stdin.flush()
        line = responses.get(timeout=180)
        assert line is not None, "Worker exited before replying"
        assert line.endswith(b"\n") and len(line) <= worker.MAX_LINE_BYTES + 1
        result = json.loads(line)
        assert result["id"] == request["id"], "Unexpected protocol output"
        return result, round(time.perf_counter() - before, 4)
    cases = [
        {"id": "american-default", "text": "The blue train leaves at nine in the morning.", "keywords": ["train", "nine", "morning"]},
        {"id": "british-fast", "voice": "british", "speed": 1.2, "text": "Could you help me find the train station?", "keywords": ["train", "station"]},
        {"id": "american-slow", "voice": "american", "speed": 0.75, "text": "A glass of water, please.", "keywords": ["glass", "water"]},
    ]
    results = []
    started = time.perf_counter()
    try:
        cold, cold_seconds = exchange({"id": "cold", "op": "ping"})
        assert cold["ok"] and cold["result"]["models_ready"], "Pinned assets did not verify"
        assert not cold["result"]["frozen"] and not cold["result"]["tts"]["loaded"] and not cold["result"]["stt"]["loaded"]
        bad, _ = exchange({"id": "reject-text", "op": "tts", "text": "x" * 1201})
        assert not bad["ok"] and bad["error"]["code"] == "TEXT_TOO_LONG"
        bad, _ = exchange({"id": "reject-audio", "op": "stt", "audio_base64": "not-a-wave"})
        assert not bad["ok"] and bad["error"]["code"] == "INVALID_AUDIO"
        for case in cases:
            request = {key: value for key, value in case.items() if key != "keywords"}
            request["op"] = "tts"
            tts, tts_seconds = exchange(request)
            assert tts["ok"], str(tts.get("error"))
            audio = base64.b64decode(tts["result"]["audio_base64"], validate=True)
            recording = worker.validate_wav(audio)
            assert recording.sample_rate == 24000
            assert abs(recording.duration_seconds - tts["result"]["duration_seconds"]) < 0.00001
            peak = max(abs(value[0]) for value in struct.iter_unpack("<h", recording.pcm))
            assert peak > 100, "TTS output is silent"
            stt, stt_seconds = exchange({"id": case["id"] + "-stt", "op": "stt", "audio_base64": tts["result"]["audio_base64"]})
            assert stt["ok"], str(stt.get("error"))
            transcript = stt["result"]["text"]
            assert stt["result"]["needs_review"] is True
            observed = set(normalize(transcript))
            # Whisper legitimately formats spoken "nine" as "9". Accept that
            # semantic equivalent here; leave reported lexical WER unchanged.
            if "9" in observed:
                observed.add("nine")
            assert set(case["keywords"]) <= observed, "Synthetic keyword check failed"
            results.append({
                "case": case["id"], "synthetic_text": case["text"], "transcript": transcript,
                "voice": case.get("voice", "american"), "speed": case.get("speed", worker.DEFAULT_SPEED),
                "sample_rate": recording.sample_rate, "duration_seconds": round(recording.duration_seconds, 4),
                "wav_bytes": len(audio), "wav_sha256": hashlib.sha256(audio).hexdigest(),
                "peak_pcm16": peak, "tts_request_seconds": tts_seconds, "stt_request_seconds": stt_seconds,
                "word_error_rate": word_error_rate(case["text"], transcript), "needs_review": True,
            })
        # Exercise validated PCM resampling at all accepted microphone rates.
        silence_rates = []
        for rate in worker.SAMPLE_RATES:
            silence = base64.b64encode(wav_bytes(rate, rate // 5)).decode()
            stt, _ = exchange({"id": f"silence-{rate}", "op": "stt", "audio_base64": silence})
            assert stt["ok"] and stt["result"] == {"text": "", "needs_review": True}
            silence_rates.append(rate)
        warm, _ = exchange({"id": "warm", "op": "ping"})
        assert warm["ok"]
        assert warm["result"]["tts"]["load_count"] == warm["result"]["stt"]["load_count"] == 1
        assert warm["result"]["threads"] <= 4
        bye, _ = exchange({"id": "bye", "op": "quit"})
        assert bye["ok"] and bye["result"]["quitting"]
        process.stdin.close()
        process.wait(timeout=20)
        reader.join(timeout=5)
        errors.join(timeout=5)
        stderr = b"".join(stderr_chunks)
        assert process.returncode == 0
        assert stderr == b"", "Non-protocol diagnostics unexpectedly emitted"
        assert responses.get(timeout=5) is None, "Unsolicited stdout after quit"
        return {
            "passed": True, "platform": sys.platform, "python": sys.version.split()[0],
            "frozen": False, "windows_packaging_tested": False,
            "scope": "Linux source worker; real local synthetic TTS-to-STT only, not learner-speech accuracy evaluation",
            "models_source": str(models.resolve()), "weights_copied": False,
            "python_network_and_process_audit_guard_enabled": True,
            "stderr_bytes": len(stderr), "process_exit_code": process.returncode,
            "requests_replied": requests_sent, "total_seconds": round(time.perf_counter() - started, 4),
            "cold_ping_seconds": cold_seconds, "cold_ping": cold["result"], "warm_ping": warm["result"],
            "cases": results, "silence_resampling_rates_passed": silence_rates,
        }
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)
        for pipe in [process.stdin, process.stdout, process.stderr]:
            if pipe and not pipe.closed:
                pipe.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smoke-models", type=Path)
    parser.add_argument("--summary", type=Path, default=SPEECH / "smoke-test-summary.json")
    arguments = parser.parse_args()
    destination = arguments.summary.resolve()
    if SPEECH.resolve() not in destination.parents:
        parser.error("Summary must remain inside speech/.")
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SpeechWorkerTests)
    started = time.perf_counter()
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    summary = {
        "unit_tests": {"passed": result.wasSuccessful(), "tests_run": result.testsRun,
                       "failures": len(result.failures), "errors": len(result.errors),
                       "seconds": round(time.perf_counter() - started, 4)},
        "windows_packaging_tested": False,
    }
    if result.wasSuccessful() and arguments.smoke_models:
        try:
            summary["real_protocol_smoke"] = smoke(arguments.smoke_models)
        except Exception as error:
            summary["real_protocol_smoke"] = {"passed": False, "failure_type": type(error).__name__, "message": str(error)}
    destination.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"summary": str(destination), "unit_tests_passed": result.wasSuccessful(), "smoke_passed": summary.get("real_protocol_smoke", {}).get("passed")}, separators=(",", ":")))
    return 0 if result.wasSuccessful() and summary.get("real_protocol_smoke", {"passed": True})["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

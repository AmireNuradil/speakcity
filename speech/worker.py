"""Persistent, local-only SPEAKCITY speech worker (one JSON request per line).

Based on the tested LucyTTS/LearnerSTT implementation in speakcity-app/speech.py.
Run directly for development, or freeze speech_worker.spec on Windows x64.
Only the operator's --models option can select a directory; requests never can.
"""
from __future__ import annotations

import argparse
import base64
import binascii
from dataclasses import dataclass
import io
import json
import logging
import os
from pathlib import Path
import struct
import sys
import warnings
import wave

from download_assets import (
    ASSETS, STT_ASSETS, TTS_ASSETS, WHISPER_REPOSITORY, WHISPER_REVISION,
    default_models_directory, verify,
)

MAX_LINE_BYTES = 4 * 1024 * 1024  # JSON bytes, excluding the LF delimiter.
MAX_AUDIO_BYTES = 3 * 1024 * 1024
MAX_BASE64_CHARS = 4 * ((MAX_AUDIO_BYTES + 2) // 3)
MAX_TEXT_CHARS = 1200
MAX_ID_CHARS = 128
MAX_WAV_CHUNKS = 64
SAMPLE_RATES = (16000, 22050, 24000, 44100, 48000)
DEFAULT_SPEED = 0.95
VOICE_CONFIGS = {
    "american": {"id": "af_heart", "language": "en-us"},
    "british": {"id": "bf_emma", "language": "en-gb"},
}


class WorkerError(Exception):
    """Only fixed, privacy-safe messages belong in errors sent to the client."""
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


def configure_runtime(threads: int) -> int:
    """Run before importing numerical/native libraries; never trust host limits."""
    threads = max(1, min(int(threads), 4))
    for name in (
        "OMP_NUM_THREADS", "OMP_THREAD_LIMIT", "OPENBLAS_NUM_THREADS",
        "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS", "VECLIB_MAXIMUM_THREADS",
        "BLIS_NUM_THREADS", "RAYON_NUM_THREADS",
    ):
        os.environ[name] = str(threads)
    for name in (
        "HF_HUB_OFFLINE", "HF_HUB_DISABLE_TELEMETRY", "HF_HUB_DISABLE_XET",
        "TRANSFORMERS_OFFLINE", "DO_NOT_TRACK",
    ):
        os.environ[name] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    os.environ["CUDA_VISIBLE_DEVICES"] = ""
    os.environ["LOG_LEVEL"] = "CRITICAL"
    # Inherited environment paths must not select system/custom phonemizers.
    for name in ("PHONEMIZER_ESPEAK_LIBRARY", "PHONEMIZER_ESPEAK_PATH", "ESPEAK_DATA_PATH"):
        os.environ.pop(name, None)
    logging.disable(logging.CRITICAL)
    warnings.filterwarnings("ignore")
    return threads


def install_offline_guard() -> None:
    """Defence in depth, not an OS sandbox: reject Python network/process APIs.

    Trusted native libraries use explicit local paths/CPU providers. For an OS-
    enforced guarantee, additionally deny worker network access in the installer.
    """
    blocked = {
        "socket.connect", "socket.connect_ex", "socket.bind", "socket.sendto",
        "socket.getaddrinfo", "socket.gethostbyname", "socket.gethostbyaddr",
        "subprocess.Popen", "os.system", "os.posix_spawn", "os.fork", "os.exec", "os.spawn",
    }

    blocked_imports = {"torch", "torchaudio", "torchvision", "spacy", "tensorflow", "transformers"}

    def audit(event, args):
        if event in blocked:
            raise PermissionError("Speech worker network and process execution are disabled.")
        # CTranslate2 imports optional converter modules even for inference. Keep
        # an unrelated developer machine's installed Torch stack out of this PID.
        if event == "import" and args and str(args[0]).split(".", 1)[0] in blocked_imports:
            raise ImportError("Optional model-conversion imports are disabled in the speech worker.")

    sys.addaudithook(audit)


def _reject_constant(_value):
    raise ValueError("Non-finite JSON number.")


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key.")
        result[key] = value
    return result


def parse_request(line: bytes) -> dict:
    try:
        request = json.loads(
            line.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object, parse_constant=_reject_constant,
        )
    except (UnicodeError, ValueError, RecursionError):
        raise WorkerError("INVALID_JSON", "Send one valid UTF-8 JSON object per line.") from None
    if not isinstance(request, dict):
        raise WorkerError("INVALID_REQUEST", "The request must be a JSON object.")
    return request


def request_id(request: dict) -> str:
    value = request.get("id")
    if (
        not isinstance(value, str) or not value or len(value) > MAX_ID_CHARS
        or any(ord(ch) < 32 or 0x7F <= ord(ch) <= 0x9F or 0xD800 <= ord(ch) <= 0xDFFF for ch in value)
    ):
        raise WorkerError("INVALID_REQUEST", "A nonempty id of at most 128 characters is required.")
    return value


def validate_request(request: dict) -> str:
    request_id(request)
    op = request.get("op")
    fields = {
        "ping": {"id", "op"}, "quit": {"id", "op"},
        "tts": {"id", "op", "text", "voice", "speed"},
        "stt": {"id", "op", "audio_base64"},
    }
    if not isinstance(op, str) or op not in fields:
        raise WorkerError("UNKNOWN_OPERATION", "Choose ping, tts, stt, or quit.")
    if set(request) - fields[op]:
        raise WorkerError("INVALID_REQUEST", "This operation contains unsupported fields.")
    return op


def validate_tts(request: dict) -> tuple[str, str, float]:
    text = request.get("text")
    if not isinstance(text, str) or not text.strip():
        raise WorkerError("INVALID_TEXT", "Text must be a nonempty string.")
    # Count BEFORE stripping: whitespace must not bypass the request limit.
    if len(text) > MAX_TEXT_CHARS:
        raise WorkerError("TEXT_TOO_LONG", "Text must not exceed 1200 characters.")
    if not any(ch.isalpha() for ch in text):
        raise WorkerError("INVALID_TEXT", "Text must contain words, not only punctuation.")
    if any((ord(ch) < 32 and ch not in "\n\t\r") or 0x7F <= ord(ch) <= 0x9F or 0xD800 <= ord(ch) <= 0xDFFF for ch in text):
        raise WorkerError("INVALID_TEXT", "Text contains unsupported control characters.")
    voice = request.get("voice", "american")
    if not isinstance(voice, str) or voice not in VOICE_CONFIGS:
        raise WorkerError("INVALID_VOICE", "Choose american or british.")
    speed = request.get("speed", DEFAULT_SPEED)
    if isinstance(speed, bool) or not isinstance(speed, (int, float)) or not 0.75 <= speed <= 1.2:
        raise WorkerError("INVALID_SPEED", "Speed must be between 0.75 and 1.2.")
    return text.strip(), voice, float(speed)


@dataclass(frozen=True)
class PCMRecording:
    pcm: bytes
    sample_rate: int
    frames: int

    @property
    def duration_seconds(self) -> float:
        return self.frames / self.sample_rate


def validate_wav(data: bytes) -> PCMRecording:
    """Parse bounded RIFF ourselves before anything reaches a native decoder.

    Accept little-endian PCM tag 1, mono, 16-bit, 0.1–30 seconds. Require exact
    RIFF/chunk sizes, padding, ordering, block alignment and byte rate. Unknown
    metadata chunks are skipped, never decoded, with at most 64 chunks total.
    No RF64, RIFX, compressed/extensible WAV, duplicate fmt/data or trailing data.
    """
    if not isinstance(data, bytes) or len(data) > MAX_AUDIO_BYTES:
        raise WorkerError("AUDIO_TOO_LARGE", "PCM WAV must not exceed 3 MiB.")
    error = WorkerError("INVALID_AUDIO", "Expected a complete mono 16-bit PCM RIFF WAV.")
    if len(data) < 44 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise error
    if struct.unpack_from("<I", data, 4)[0] != len(data) - 8:
        raise error
    position, chunks = 12, 0
    sample_rate = None
    pcm = None
    while position < len(data):
        chunks += 1
        if chunks > MAX_WAV_CHUNKS or len(data) - position < 8:
            raise error
        kind = data[position:position + 4]
        size = struct.unpack_from("<I", data, position + 4)[0]
        start = position + 8
        end = start + size
        next_chunk = end + (size & 1)
        if end > len(data) or next_chunk > len(data):
            raise error
        if kind == b"fmt ":
            if sample_rate is not None or pcm is not None or size not in (16, 18):
                raise error
            tag, channels, rate, byte_rate, alignment, bits = struct.unpack_from("<HHIIHH", data, start)
            if (tag, channels, alignment, bits) != (1, 1, 2, 16) or byte_rate != rate * 2:
                raise error
            if size == 18 and struct.unpack_from("<H", data, start + 16)[0] != 0:
                raise error
            if rate not in SAMPLE_RATES:
                raise WorkerError("INVALID_SAMPLE_RATE", "Supported sample rates: 16000, 22050, 24000, 44100, 48000 Hz.")
            sample_rate = rate
        elif kind == b"data":
            if sample_rate is None or pcm is not None or size == 0 or size % 2:
                raise error
            frames = size // 2
            if frames * 10 < sample_rate or frames > sample_rate * 30:
                raise WorkerError("INVALID_DURATION", "Audio must last between 0.1 and 30 seconds.")
            pcm = data[start:end]
        position = next_chunk
    if position != len(data) or sample_rate is None or pcm is None:
        raise error
    return PCMRecording(pcm, sample_rate, len(pcm) // 2)


def decode_recording(value) -> PCMRecording:
    if not isinstance(value, str) or not value:
        raise WorkerError("INVALID_AUDIO", "Provide PCM WAV in audio_base64.")
    if len(value) > MAX_BASE64_CHARS:
        raise WorkerError("AUDIO_TOO_LARGE", "PCM WAV must not exceed 3 MiB.")
    try:
        data = base64.b64decode(value, validate=True)
    except (ValueError, binascii.Error):
        raise WorkerError("INVALID_AUDIO", "audio_base64 must be valid base64 without a data URL prefix.") from None
    return validate_wav(data)


def pcm_wav(audio, sample_rate: int) -> bytes:
    # Adapted from tested speech.py; NumPy remains a lazy inference dependency.
    import numpy as np
    audio = np.asarray(audio)
    if audio.ndim != 1 or audio.size == 0 or not np.isfinite(audio).all() or sample_rate != 24000:
        raise WorkerError("TTS_FAILED", "Speech synthesis returned invalid audio.")
    # Keep response lines bounded too. Never truncate generated speech silently.
    if audio.size * 2 + 44 > MAX_AUDIO_BYTES:
        raise WorkerError("OUTPUT_TOO_LARGE", "Generated speech is too long; send a shorter reply.")
    samples = (np.clip(audio, -1, 1) * 32767).astype("<i2")
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(samples.tobytes())
    return buffer.getvalue()


def recording_to_array(recording: PCMRecording):
    """Only trusted, already-validated PCM enters PyAV's resampler, not a decoder."""
    import numpy as np
    samples = np.frombuffer(recording.pcm, dtype="<i2")
    if recording.sample_rate == 16000:
        return samples.astype(np.float32) / 32768.0
    import av
    frame = av.AudioFrame.from_ndarray(samples.reshape(1, -1), format="s16", layout="mono")
    frame.sample_rate = recording.sample_rate
    resampler = av.AudioResampler(format="s16", layout="mono", rate=16000)
    frames = [*resampler.resample(frame), *resampler.resample(None)]
    converted = np.concatenate([part.to_ndarray().reshape(-1) for part in frames])
    return converted.astype(np.float32) / 32768.0


class SpeechRuntime:
    """Sequential request processing; one cached instance of each model per PID."""
    def __init__(self, models: Path | None = None, threads: int = 2):
        self.models = (models if models is not None else default_models_directory()).resolve()
        self.threads = configure_runtime(threads)
        self._tts = None
        self._stt = None
        self._tts_loads = 0
        self._stt_loads = 0
        self._integrity_cache = {}
        self._espeak_handle = None
        self._dll_handles = []

    def _verify_assets(self, names: tuple[str, ...]) -> None:
        for name in names:
            path = self.models / name
            try:
                stat = path.stat()
            except OSError:
                raise WorkerError("MODEL_MISSING", "Required local model files are missing; run operator asset setup.") from None
            if not path.is_file() or path.is_symlink() or (name.startswith("whisper-base.en/") and path.parent.is_symlink()):
                raise WorkerError("MODEL_INTEGRITY", "A local model asset failed integrity verification.")
            # Check fingerprints on ping and before load; avoid rehashing hundreds
            # of MB every utterance. Loaded native sessions are reused unchanged.
            fingerprint = (stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns, stat.st_ino, stat.st_dev)
            if self._integrity_cache.get(name) == fingerprint:
                continue
            self._integrity_cache.pop(name, None)
            if not verify(path, ASSETS[name]):
                raise WorkerError("MODEL_INTEGRITY", "A local model asset failed integrity verification.")
            self._integrity_cache[name] = fingerprint
        # The pinned Whisper revision has no preprocessor_config. Prevent an
        # unverified extra file from overriding the tested feature extraction.
        if names == STT_ASSETS and (self.models / "whisper-base.en" / "preprocessor_config.json").exists():
            raise WorkerError("MODEL_INTEGRITY", "Unexpected Whisper feature configuration is not accepted.")

    def _ready(self, names: tuple[str, ...]) -> bool:
        try:
            self._verify_assets(names)
            return True
        except WorkerError:
            return False

    def ping(self) -> dict:
        tts_ready = self._ready(TTS_ASSETS)
        stt_ready = self._ready(STT_ASSETS)
        return {
            "protocol_version": 1,
            "models_ready": tts_ready and stt_ready,
            "frozen": bool(getattr(sys, "frozen", False)),
            "offline": True,
            "threads": self.threads,
            "tts": {
                "engine": "kokoro-onnx", "model": "kokoro-v1.0.onnx",
                "device": "cpu", "default_voice": "af_heart",
                "voices": {key: value["id"] for key, value in VOICE_CONFIGS.items()},
                "default_speed": DEFAULT_SPEED, "speed_range": [0.75, 1.2],
                "models_ready": tts_ready, "loaded": self._tts is not None,
                "load_count": self._tts_loads,
            },
            "stt": {
                "engine": "faster-whisper", "model": "base.en",
                "repository": WHISPER_REPOSITORY, "revision": WHISPER_REVISION,
                "device": "cpu", "compute_type": "int8",
                "models_ready": stt_ready, "loaded": self._stt is not None,
                "load_count": self._stt_loads,
            },
        }

    def _load_tts(self):
        if self._tts is not None:
            return self._tts
        self._verify_assets(TTS_ASSETS)
        try:
            import ctypes
            import espeakng_loader
            import onnxruntime as ort
            from kokoro_onnx import Kokoro
            from kokoro_onnx.config import EspeakConfig

            # Use bundled library/data explicitly; fail rather than falling back
            # to system eSpeak or a PATH lookup (which can invoke helper shells).
            library = espeakng_loader.get_library_path()
            data_path = espeakng_loader.get_data_path()
            if sys.platform == "win32":
                self._dll_handles.append(os.add_dll_directory(str(Path(library).parent)))
            self._espeak_handle = ctypes.CDLL(library)
            os.environ["PHONEMIZER_ESPEAK_LIBRARY"] = library
            ort.disable_telemetry_events()
            options = ort.SessionOptions()
            options.intra_op_num_threads = self.threads
            options.inter_op_num_threads = 1
            options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
            options.log_severity_level = 4
            options.add_session_config_entry("session.intra_op.allow_spinning", "0")
            options.add_session_config_entry("session.inter_op.allow_spinning", "0")
            session = ort.InferenceSession(
                str(self.models / "kokoro-v1.0.onnx"), sess_options=options,
                providers=["CPUExecutionProvider"],
            )
            engine = Kokoro.from_session(
                session, str(self.models / "voices-v1.0.bin"),
                espeak_config=EspeakConfig(lib_path=library, data_path=data_path),
            )
            archive = engine.voices
            try:
                if not {voice["id"] for voice in VOICE_CONFIGS.values()} <= set(engine.get_voices()):
                    raise RuntimeError("Expected voice missing.")
                # Cache the two verified styles too: Kokoro otherwise lazily
                # rereads/decompresses the NPZ entry on every utterance.
                engine.voices = {voice["id"]: archive[voice["id"]] for voice in VOICE_CONFIGS.values()}
            finally:
                archive.close()
            self._tts = engine
            self._tts_loads += 1
        except Exception:
            raise WorkerError("TTS_UNAVAILABLE", "The bundled CPU speech runtime could not load; check the installation.") from None
        return self._tts

    def _load_stt(self):
        if self._stt is not None:
            return self._stt
        self._verify_assets(STT_ASSETS)
        try:
            from windows_compat import prepare_ctranslate2_windows
            prepare_ctranslate2_windows()
            import onnxruntime as ort
            from faster_whisper import WhisperModel
            import ctranslate2

            ort.disable_telemetry_events()
            ctranslate2.set_log_level(logging.ERROR)
            self._stt = WhisperModel(
                str(self.models / "whisper-base.en"), device="cpu", compute_type="int8",
                cpu_threads=self.threads, num_workers=1, local_files_only=True,
                revision=WHISPER_REVISION, use_auth_token=False,
            )
            self._stt_loads += 1
        except Exception:
            raise WorkerError("STT_UNAVAILABLE", "The bundled CPU transcription runtime could not load; check the installation.") from None
        return self._stt

    def tts(self, request: dict) -> dict:
        text, voice, speed = validate_tts(request)
        engine = self._load_tts()
        config = VOICE_CONFIGS[voice]
        try:
            audio, rate = engine.create(text, voice=config["id"], speed=speed, lang=config["language"], trim=True)
            audio_bytes = pcm_wav(audio, rate)
            return {
                "audio_base64": base64.b64encode(audio_bytes).decode("ascii"),
                "sample_rate": int(rate), "duration_seconds": round(len(audio) / rate, 6),
            }
        except WorkerError:
            raise
        except Exception:
            raise WorkerError("TTS_FAILED", "Speech synthesis failed; try a shorter English reply.") from None

    def stt(self, request: dict) -> dict:
        recording = decode_recording(request.get("audio_base64"))
        engine = self._load_stt()
        try:
            samples = recording_to_array(recording)
            segments, _ = engine.transcribe(
                samples, language="en", task="transcribe", beam_size=3,
                condition_on_previous_text=False, vad_filter=True,
                vad_parameters={"min_silence_duration_ms": 500}, log_progress=False,
            )
            text = " ".join(segment.text.strip() for segment in segments).strip()
            return {"text": text, "needs_review": True}
        except Exception:
            raise WorkerError("STT_FAILED", "Transcription failed; record a new short turn.") from None

    def handle(self, request: dict) -> dict:
        op = validate_request(request)
        if op == "ping":
            return self.ping()
        if op == "quit":
            return {"quitting": True}
        if op == "tts":
            return self.tts(request)
        return self.stt(request)


def _error(identifier: str, code: str, message: str) -> dict:
    return {"id": identifier, "ok": False, "error": {"code": code, "message": message}}


def emit(output, response: dict) -> None:
    encoded = json.dumps(response, ensure_ascii=True, allow_nan=False, separators=(",", ":")).encode("ascii")
    if len(encoded) > MAX_LINE_BYTES:
        encoded = json.dumps(_error(response["id"], "OUTPUT_TOO_LARGE", "Generated speech is too long; send a shorter reply."), separators=(",", ":")).encode("ascii")
    pending = memoryview(encoded + b"\n")
    while pending:
        sent = output.write(pending)
        if not sent:
            raise OSError("Protocol pipe closed.")
        pending = pending[sent:]
    output.flush()


def serve(source, output, runtime: SpeechRuntime, diagnostics=None) -> int:
    """Bounded incremental reads; one reply per line, including malformed lines.

    Unparseable/unframed/oversized input uses id="" because a safe correlation id
    cannot be recovered. An overlong line is drained without accumulating it.
    EOF is graceful; quit acknowledges before stopping. No unsolicited stdout.
    """
    while True:
        raw = source.readline(MAX_LINE_BYTES + 2)
        if not raw:
            return 0
        identifier = ""
        request = None
        try:
            if len(raw) > MAX_LINE_BYTES + 1 or (not raw.endswith(b"\n") and len(raw) > MAX_LINE_BYTES):
                while raw and not raw.endswith(b"\n"):
                    raw = source.readline(64 * 1024)
                raise WorkerError("LINE_TOO_LARGE", "A JSON line must not exceed 4 MiB.")
            if not raw.endswith(b"\n"):
                raise WorkerError("INVALID_JSON", "Each request must end with a newline.")
            request = parse_request(raw[:-1])
            identifier = request_id(request)
            result = runtime.handle(request)
            response = {"id": identifier, "ok": True, "result": result}
        except WorkerError as error:
            response = _error(identifier, error.code, error.message)
        except Exception:
            response = _error(identifier, "INTERNAL_ERROR", "The speech request could not be completed.")
            if diagnostics is not None:
                # No exception repr/traceback, request IDs, text, or audio in logs.
                diagnostics.write(b"speech_worker: internal request error\n")
                diagnostics.flush()
        try:
            emit(output, response)
        except (BrokenPipeError, OSError):
            return 0
        if request is not None and request.get("op") == "quit" and response["ok"]:
            return 0


class OperatorParser(argparse.ArgumentParser):
    def error(self, message):
        raise WorkerError("INVALID_OPTIONS", "Invalid operator options; use --help for usage.")

    def print_help(self, file=None):
        super().print_help(file=sys.stderr)


def main(argv: list[str] | None = None) -> int:
    parser = OperatorParser(description=__doc__)
    parser.add_argument("--models", type=Path, default=default_models_directory())
    parser.add_argument("--threads", type=int, choices=(1, 2, 3, 4), default=2)
    try:
        arguments = parser.parse_args(argv)
    except WorkerError:
        sys.stderr.write("speech_worker: invalid operator options; use --help\n")
        return 2
    # Preserve a private descriptor for the protocol. Redirect BOTH native file
    # descriptors permanently so package prints, native logging and shutdown
    # messages cannot corrupt stdout or leak speech to stderr. Safe diagnostics
    # use a separate saved descriptor and only fixed literal messages.
    if sys.stdin is None or sys.stdout is None or sys.stderr is None:
        return 2  # Build as console=True; the desktop spawns it with no window.
    source = sys.stdin.buffer
    if sys.platform == "win32":
        import msvcrt
        for stream in (source, sys.stdout, sys.stderr):
            msvcrt.setmode(stream.fileno(), os.O_BINARY)
    sys.stdout.flush()
    sys.stderr.flush()
    with os.fdopen(os.dup(sys.stdout.fileno()), "wb", buffering=0) as output, os.fdopen(os.dup(sys.stderr.fileno()), "wb", buffering=0) as diagnostics:
        null = os.open(os.devnull, os.O_WRONLY)
        try:
            os.dup2(null, sys.stdout.fileno())
            os.dup2(null, sys.stderr.fileno())
        finally:
            os.close(null)
        try:
            if sys.platform == "win32":
                # CRT dup2 alone does not cover native code using GetStdHandle.
                import ctypes
                from ctypes import wintypes
                kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
                kernel32.SetStdHandle.argtypes = (wintypes.DWORD, wintypes.HANDLE)
                kernel32.SetStdHandle.restype = wintypes.BOOL
                for handle_id, stream in ((-11, sys.stdout), (-12, sys.stderr)):
                    handle = msvcrt.get_osfhandle(stream.fileno())
                    if not kernel32.SetStdHandle(handle_id & 0xFFFFFFFF, handle):
                        raise OSError("Cannot isolate native output handles.")
            runtime = SpeechRuntime(arguments.models, arguments.threads)
            install_offline_guard()
            return serve(source, output, runtime, diagnostics)
        except KeyboardInterrupt:
            return 0
        except Exception:
            diagnostics.write(b"speech_worker: startup or input failure\n")
            return 1


if __name__ == "__main__":
    raise SystemExit(main())

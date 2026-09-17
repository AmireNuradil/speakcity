"""Operator-only downloader for fixed, integrity-pinned SPEAKCITY speech assets.

Adapted from speakcity-app/download_models.py and download_stt.py. No remote
Python, pickle, archive extraction, model aliases, credentials, or custom URLs.
The worker imports the manifest/verifier below, but never calls download().
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import urllib.request

KOKORO_RELEASE = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1"
WHISPER_REPOSITORY = "Systran/faster-whisper-base.en"
WHISPER_REVISION = "3d3d5dee26484f91867d81cb899cfcf72b96be6c"
WHISPER_RELEASE = f"https://huggingface.co/{WHISPER_REPOSITORY}/resolve/{WHISPER_REVISION}"

# Kokoro SHA-256/lengths are the publisher release digests from the tested
# download_models.py. Whisper hashes/lengths are copied from the tested pinned
# snapshot's stt-model-manifest.json (not claimed to be publisher signatures).
ASSETS = {
    "kokoro-v1.0.onnx": {
        "url": f"{KOKORO_RELEASE}/kokoro-v1.0.onnx",
        "sha256": "beb0d1848dee9a49da392cc3df26958d46cfa35d321edf434f52949153f0df3a",
        "bytes": 325505369,
    },
    "voices-v1.0.bin": {
        "url": f"{KOKORO_RELEASE}/voices-v1.0.bin",
        "sha256": "bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d",
        "bytes": 28214398,
    },
    "whisper-base.en/config.json": {
        "url": f"{WHISPER_RELEASE}/config.json",
        "sha256": "f3bc3821e9fc76a27bae538e11ae5b677dcdd352b4600429ce7951d398569aeb",
        "bytes": 2227,
    },
    "whisper-base.en/model.bin": {
        "url": f"{WHISPER_RELEASE}/model.bin",
        "sha256": "2a166925539a16005f14ff328359f9b9adb9dc4fb631bb3b227526862e93e2ef",
        "bytes": 145216508,
    },
    "whisper-base.en/tokenizer.json": {
        "url": f"{WHISPER_RELEASE}/tokenizer.json",
        "sha256": "929c5252409436dce1b38a75d1abbcb5e132d170d8e324e4e04ed915fa2d22df",
        "bytes": 2128466,
    },
    "whisper-base.en/vocabulary.txt": {
        "url": f"{WHISPER_RELEASE}/vocabulary.txt",
        "sha256": "ff77588746d3a2595d32ab5b69ffd7b95ce2441ac57533cb66fc3eb575a115cf",
        "bytes": 422309,
    },
    "whisper-base.en/README.md": {
        "url": f"{WHISPER_RELEASE}/README.md",
        "sha256": "0360e1518671daf4064d44121ad1c4bbb0446dd5b0f8c724c8d3132fd33a1540",
        "bytes": 1323,
    },
}
TTS_ASSETS = ("kokoro-v1.0.onnx", "voices-v1.0.bin")
STT_ASSETS = tuple(name for name in ASSETS if name.startswith("whisper-base.en/") and not name.endswith("README.md"))


def base_directory() -> Path:
    """Weights belong beside the executable, never in PyInstaller's _internal."""
    return Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve().parent


def default_models_directory() -> Path:
    return base_directory() / "models"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify(path: Path, metadata: dict) -> bool:
    """Check length AND SHA-256; never accept a symlink as an asset file."""
    try:
        return (
            not path.is_symlink()
            and path.is_file()
            and path.stat().st_size == metadata["bytes"]
            and sha256(path) == metadata["sha256"]
        )
    except OSError:
        return False


def integrity_manifest() -> dict:
    return {
        "format_version": 1,
        "kokoro_digest_source": "Publisher release digests in tested speakcity-app/download_models.py",
        "whisper_repository": WHISPER_REPOSITORY,
        "whisper_revision": WHISPER_REVISION,
        "whisper_digest_source": "Tested pinned snapshot stt-model-manifest.json; local SHA-256 records",
        "files": ASSETS,
    }


class HTTPSOnlyRedirect(urllib.request.HTTPRedirectHandler):
    """Publisher CDNs may redirect, but never downgrade transport to HTTP."""
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if not newurl.lower().startswith("https://"):
            raise RuntimeError("Non-HTTPS model redirect refused.")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(target: Path) -> None:
    target.mkdir(parents=True, exist_ok=True)
    # No cookies, auth tokens, or Hugging Face code execution/download helpers.
    opener = urllib.request.build_opener(HTTPSOnlyRedirect())
    for name, metadata in ASSETS.items():
        destination = target / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists() or destination.is_symlink():
            if not verify(destination, metadata):
                raise RuntimeError("An existing model asset failed integrity verification; refusing overwrite.")
            continue
        temporary = destination.with_name(destination.name + ".partial")
        # Never append/reuse an unverified partial or follow a partial-file symlink.
        if temporary.exists() or temporary.is_symlink():
            raise RuntimeError("A partial model download exists; operator inspection is required.")
        request = urllib.request.Request(metadata["url"], headers={"User-Agent": "SPEAKCITY-desktop-assets/1.0"})
        installed = False
        created = False
        try:
            out_file = temporary.open("xb")
            created = True
            with out_file as out, opener.open(request, timeout=60) as response:
                if response.status != 200:
                    raise RuntimeError("Model publisher returned an unexpected HTTP status.")
                expected = metadata["bytes"]
                declared = response.headers.get("Content-Length")
                if declared is not None and int(declared) != expected:
                    raise RuntimeError("Model length does not match the pinned manifest.")
                received = 0
                digest = hashlib.sha256()
                while True:
                    block = response.read(min(1024 * 1024, expected - received + 1))
                    if not block:
                        break
                    received += len(block)
                    if received > expected:
                        raise RuntimeError("Model download exceeds the pinned length.")
                    digest.update(block)
                    out.write(block)
                out.flush()
                os.fsync(out.fileno())
            if received != expected or digest.hexdigest() != metadata["sha256"]:
                raise RuntimeError("Model checksum does not match the pinned manifest.")
            # Hard-link installation is atomic and fails if another installer
            # already created the target. Both files are on the same filesystem.
            os.link(temporary, destination)
            installed = True
        finally:
            # Delete only the temporary file created by this invocation, not an
            # existing partial (those are rejected before entering the try).
            if created and temporary.exists() and not temporary.is_symlink():
                temporary.unlink()
        if not installed:
            raise RuntimeError("Model asset was not installed.")
    (target / "assets-manifest.json").write_text(
        json.dumps(integrity_manifest(), indent=2) + "\n", encoding="utf-8"
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", type=Path, default=default_models_directory(), help="Operator-selected output directory")
    parser.add_argument("--verify-only", action="store_true", help="Verify all seven fixed assets without any network requests")
    arguments = parser.parse_args(argv)
    try:
        target = arguments.models.resolve()
        if not arguments.verify_only:
            download(target)
        statuses = {name: verify(target / name, meta) for name, meta in ASSETS.items()}
        print(json.dumps({"ok": all(statuses.values()), "assets": statuses}, separators=(",", ":")))
        return 0 if all(statuses.values()) else 1
    except Exception:
        # CDN errors may contain signed URLs. Never serialize underlying errors.
        print("Asset setup failed; check connectivity, disk space, and pinned asset integrity.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

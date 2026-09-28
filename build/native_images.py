"""Developer tool: regenerate the native window's JPEG pictures from ui/assets/*.webp.

WPF has no WebP decoder (neither has GDI+ without the optional Store codec), so the
native window uses JPEG twins compiled into SpeakCity.dll as WPF resources. The web
interface keeps its WebP files untouched. Outputs follow the repository convention:
the decoded file, a base64 copy under assets-source/, and a pinned SHA-256 in
assets-source/manifest.json that build/windows.ps1 verifies on every build.

Not part of the build pipeline (it needs Pillow): run it only when an illustration
changes, then commit the outputs.  python build/native_images.py
"""
from __future__ import annotations

import base64
import hashlib
import io
import json
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
PICTURES = ("airport", "cafe", "city", "lucy")
LONG_SIDE = 1600  # the largest on-screen use is the city map at ~1200 px on a 150 % display
QUALITY = 88


def main() -> int:
    manifest_path = ROOT / "assets-source" / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest = [entry for entry in manifest if not entry["output"].startswith("src/SpeakCity/Assets/")]
    for name in PICTURES:
        with Image.open(ROOT / "ui" / "assets" / f"{name}.webp") as source:
            picture = source.convert("RGB")
        scale = min(1.0, LONG_SIDE / max(picture.size))
        if scale < 1.0:
            picture = picture.resize((round(picture.width * scale), round(picture.height * scale)), Image.LANCZOS)
        buffer = io.BytesIO()
        picture.save(buffer, format="JPEG", quality=QUALITY, optimize=True)
        data = buffer.getvalue()
        output = f"src/SpeakCity/Assets/{name}.jpg"
        encoded = f"assets-source/{name}.jpg.b64"
        (ROOT / output).parent.mkdir(parents=True, exist_ok=True)
        (ROOT / output).write_bytes(data)
        (ROOT / encoded).write_text(base64.b64encode(data).decode("ascii"), encoding="ascii")
        manifest.append({"encoded": encoded, "output": output, "sha256": hashlib.sha256(data).hexdigest()})
        print(f"{output}: {picture.width}x{picture.height}, {len(data)} bytes")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

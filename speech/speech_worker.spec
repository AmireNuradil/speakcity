# SPEAKCITY worker, Windows x64 PyInstaller 6.x onedir. Build on Windows, not Linux.
# Model weights are deliberately NOT collected. Ship models/ next to the EXE.
from pathlib import Path
import importlib.util
import sys
from PyInstaller.utils.hooks import (
    collect_data_files, collect_dynamic_libs, collect_submodules,
    copy_metadata, get_package_paths,
)

if sys.platform != "win32":
    raise RuntimeError("Build this Windows speech worker on a Windows x64 builder.")

ROOT = Path(SPECPATH)
# Validate before discovery/importing the replacement. An old wheel, a copied
# DLL, or a hand-set manifest success flag is not an acceptable native input.
policy_spec = importlib.util.spec_from_file_location("speakcity_native_policy", ROOT.parent / "build/collect_notices.py")
policy = importlib.util.module_from_spec(policy_spec)
policy_spec.loader.exec_module(policy)
controlled = policy.validate_controlled_build()
espeak_path = controlled["package"]
datas = [(str(ROOT / "assets-manifest.json"), ".")]
# Explicit data selection keeps runner-specific provenance markers out of the
# runtime payload. The collector ships complete build evidence in the companion.
for relative in controlled["manifest"]["installed_files"]:
    if relative.startswith("espeak-ng-data/"):
        file = espeak_path / relative
        datas.append((str(file), "espeakng_loader/" + str(Path(relative).parent).replace("\\", "/")))
binaries = [(str(espeak_path / "espeak-ng.dll"), "espeakng_loader")]
hiddenimports = ["windows_compat", "espeakng_loader", "ctranslate2._ext", "ctranslate2.models", "tokenizers.tokenizers"]

# Retain every package's runtime data, including Kokoro config.json, the entire
# eSpeak phoneme/voice/dictionary tree, and faster-whisper's bundled Silero VAD.
PACKAGES = (
    "kokoro_onnx", "phonemizer", "onnxruntime", "faster_whisper",
    "ctranslate2", "tokenizers", "av", "numpy", "huggingface_hub", "certifi",
    "joblib", "cloudpickle", "yaml", "coloredlogs", "humanfriendly", "sympy",
    "mpmath", "packaging", "tqdm", "filelock", "fsspec", "flatbuffers",
    "google.protobuf", "anyio", "httpx", "httpcore", "idna", "click", "attrs", "dlinfo",
)
for package in PACKAGES:
    datas += collect_data_files(package, include_py_files=False)
    binaries += collect_dynamic_libs(package)

# Import-string/native extension discovery. Do not collect model-conversion
# extras: they drag Torch/transformers/GPU stacks into an inference-only bundle.
for package in ("kokoro_onnx", "phonemizer", "faster_whisper", "av", "tokenizers"):
    hiddenimports += collect_submodules(package)

# Wheel vendored DLL folders may live beside the Python package (delvewheel),
# not inside it. Preserve the relative layout including .load-order files.
for package in ("numpy", "av", "onnxruntime", "ctranslate2", "tokenizers"):
    package_base, _ = get_package_paths(package)
    for suffix in (".libs", ".dylibs"):
        sibling = Path(package_base) / (package + suffix)
        if sibling.is_dir():
            for file in sibling.rglob("*"):
                if not file.is_file():
                    continue
                destination = str(file.parent.relative_to(package_base))
                if file.suffix.lower() in (".dll", ".pyd", ".so", ".dylib"):
                    binaries.append((str(file), destination))
                else:
                    datas.append((str(file), destination))

# Keep dist-info for importlib.metadata calls and available upstream notices.
# This is NOT a substitute for a redistribution/GPL source-compliance review.
for distribution in ("kokoro-onnx", "faster-whisper"):
    datas += copy_metadata(distribution, recursive=True)

# Use the local adapter's truthful distribution metadata, never the wheel's
# stale RECORD/version after replacement. All native/source gates ran above.
datas += copy_metadata("espeakng-loader")

# Duplicate hook entries are harmless but needlessly increase analysis work.
datas = list(dict.fromkeys(datas))
binaries = list(dict.fromkeys(binaries))
hiddenimports = list(dict.fromkeys(hiddenimports))

a = Analysis(
    [str(ROOT / "worker.py")],
    pathex=[str(ROOT)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    runtime_hooks=[],
    excludes=["torch", "torchaudio", "torchvision", "spacy", "tensorflow", "transformers", "onnxruntime_gpu", "pytest", "IPython", "matplotlib"],
    noarchive=False,
)
# A third-party hook must not leak the build-runner marker or introduce another
# eSpeak binary through automatic discovery. The one approved native file wins
# only by exact identity; extra native files are errors, not silently dropped.
a.datas = [row for row in a.datas if row[0].replace("\\", "/") != "espeakng_loader/" + policy.CONTROLLED_MARKER]
espeak_binaries = [row for row in a.binaries if "espeak" in Path(row[0]).name.lower() and Path(row[0]).suffix.lower() == ".dll"]
if len(espeak_binaries) != 1 or espeak_binaries[0][0].replace("\\", "/") != "espeakng_loader/espeak-ng.dll":
    raise RuntimeError("PyInstaller discovered an extra or misplaced eSpeak native library.")
if Path(espeak_binaries[0][1]).resolve() != (espeak_path / "espeak-ng.dll").resolve():
    raise RuntimeError("PyInstaller selected a different eSpeak native library.")
if not any(row[0] == "espeakng_loader" and Path(row[1]).resolve() == (espeak_path / "__init__.py").resolve() for row in a.pure):
    raise RuntimeError("PyInstaller did not select the locally written eSpeak path adapter.")
policy.validate_controlled_build()  # Detect mutation during hook/analysis work.
pyz = PYZ(a.pure)
exe = EXE(
    pyz, a.scripts, [],
    exclude_binaries=True,
    name="speakcity-speech-worker",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,  # Preserve stdin/stdout pipes. Desktop uses CREATE_NO_WINDOW.
    disable_windowed_traceback=True,
    contents_directory="_internal",
)
coll = COLLECT(
    exe, a.binaries, a.datas,
    strip=False, upx=False,
    name="speakcity-speech-worker",
)
# Verify the actual onedir native payload, not only inputs to Analysis.
frozen_native = policy.controlled_files(Path(coll.name) / "_internal/espeakng_loader")
if frozen_native != controlled["manifest"]["produced_files"]:
    raise RuntimeError("Frozen eSpeak DLL/data bytes differ from the controlled build manifest.")

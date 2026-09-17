#requires -Version 7.2
<#
Controlled Windows x64 eSpeak source build, for phonemization only.
Run AFTER pip installs speech/requirements.txt and BEFORE PyInstaller.
No publisher native DLL/data or upstream loader Python wrapper is retained.
Fresh OutputRoot required; no downloads are executed on non-Windows hosts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PythonPath,
    [string]$OutputRoot = 'out/native-espeak'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'native_espeak.ps1 must run on Windows x64, never on the Linux editing workspace.' }
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$python = (Resolve-Path -LiteralPath $PythonPath).Path
$output = [IO.Path]::GetFullPath($OutputRoot, $root)
$separator = [IO.Path]::DirectorySeparatorChar
function Is-Within([string]$Child, [string]$Parent) {
    return $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or $Child.StartsWith($Parent.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)
}
if ((Is-Within $root $output)) { throw 'OutputRoot must not contain or replace the repository.' }
foreach ($name in @('build', 'speech', 'docs', 'src', 'tests', '.git')) {
    if (Is-Within $output (Join-Path $root $name)) { throw 'OutputRoot must be outside checked-in source directories.' }
}
$current = [IO.DirectoryInfo]::new($output)
while ($null -ne $current) {
    if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'OutputRoot must not use symlinks or junctions.' }
    $current = $current.Parent
}
$preflight = 'import json,platform,struct,sys; assert sys.platform == "win32" and struct.calcsize("P")==8 and platform.machine().lower() in ("amd64","x86_64"); assert sys.version_info[:2] == (3,11) and sys.prefix != sys.base_prefix; print(json.dumps({"prefix":sys.prefix}))' | & $python -I -B -
if ($LASTEXITCODE -ne 0) { throw 'PythonPath must be the actual Python 3.11 x64 Windows build venv.' }
$venv = ($preflight | ConvertFrom-Json).prefix
if ((Is-Within $output $venv) -or (Is-Within $venv $output)) { throw 'OutputRoot and build venv must not overlap.' }
if (Test-Path -LiteralPath $output) {
    if (-not (Test-Path -LiteralPath $output -PathType Container) -or @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
        throw 'Use a new/empty OutputRoot. Stale builds are not reused or recursively deleted.'
    }
}

# This driver is local build code, not downloaded source. Its exact bytes and
# this script/collector/spec are preserved and compared by the release gate.
$nativeDriver = @'
"""Windows-only controlled build driver embedded in build/native_espeak.ps1."""
from __future__ import annotations
import base64
import csv
from datetime import datetime, timezone
import difflib
import importlib
from importlib import metadata
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import platform
import re
import shutil
import struct
import subprocess
import sys
import sysconfig

if sys.platform != "win32" or struct.calcsize("P") != 8 or platform.machine().lower() not in ("amd64", "x86_64"):
    raise RuntimeError("Native build/source execution is permitted only on Windows x64")
if sys.version_info[:2] != (3, 11) or sys.prefix == sys.base_prefix:
    raise RuntimeError("Use the Python 3.11 speech-build venv")
REPO = Path(sys.argv[2]).resolve()
sys.path.insert(0, str(REPO / "build"))
import collect_notices as policy

BASE = Path(__file__).resolve().parents[2]
INPUTS = BASE / "inputs"
EVIDENCE = BASE / "evidence"
WORK = BASE / "work"
SOURCE = WORK / "source"
BUILD = WORK / "cmake-build"
INSTALL = WORK / "install"
SITE = Path(sysconfig.get_path("purelib"))
PACKAGE = SITE / "espeakng_loader"
COMMANDS = []
# No environment dump: record only names removed, not credentials or arbitrary values.
CLEAN_ENV_NAMES = (
    "CC", "CXX", "CFLAGS", "CXXFLAGS", "CPPFLAGS", "LDFLAGS", "CL", "_CL_", "LINK", "_LINK_",
    "CMAKE_TOOLCHAIN_FILE", "CMAKE_PREFIX_PATH", "CMAKE_GENERATOR", "CMAKE_GENERATOR_PLATFORM",
    "CMAKE_GENERATOR_TOOLSET", "CMAKE_GENERATOR_INSTANCE", "CMAKE_INSTALL_MODE",
    "INCLUDE", "LIB", "LIBPATH", "VALGRIND", "ESPEAK_DATA_PATH", "PHONEMIZER_ESPEAK_LIBRARY",
    "PYTHONHOME", "PYTHONPATH",
)

def write(path, data):
    policy.plain_path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data.encode("utf-8") if isinstance(data, str) else data)


def json_write(path, value):
    write(path, json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n")


def run(args, log, *, system_path=False, timeout=1800):
    args = [str(arg) for arg in args]
    env = os.environ.copy()
    for name in CLEAN_ENV_NAMES:
        env.pop(name, None)
    if system_path:
        system_root = os.environ.get("SystemRoot") or os.environ.get("SYSTEMROOT")
        if not system_root:
            raise RuntimeError("Windows system directory is unavailable")
        env["PATH"] = os.pathsep.join((str(Path(system_root) / "System32"), system_root))
    print("[native-espeak] " + log, flush=True)
    result = subprocess.run(args, cwd=WORK, env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, timeout=timeout, check=False)
    write(EVIDENCE / log, result.stdout)
    COMMANDS.append({"argv": args, "cwd": str(WORK), "log": log,
                     "exit_code": result.returncode, "system_only_path": system_path})
    json_write(EVIDENCE / "commands.json", COMMANDS)
    if result.returncode:
        print(result.stdout.decode("utf-8", errors="replace")[-16000:], file=sys.stderr)
        raise RuntimeError(f"{log}: exit {result.returncode}; see preserved build log")
    return result.stdout.decode("utf-8", errors="replace").strip()


def cmake_value(text, name):
    match = re.search(r"^set\(" + re.escape(name) + r' "([^"\r\n]*)"\)', text, re.M)
    if not match:
        raise RuntimeError(f"CMake did not record {name}")
    return match[1]


def smoke(package, destination):
    # An isolated copy with a space-containing path proves we do not rely on
    # the CMake install directory/PATH/ambient eSpeak. No model downloads/audio.
    import ctypes
    sys.path.insert(0, str(package.parent))
    import espeakng_loader
    if Path(espeakng_loader.__file__).resolve() != (package / "__init__.py").resolve():
        raise RuntimeError("Smoke imported the wrong loader adapter")
    library = Path(espeakng_loader.get_library_path())
    data_path = Path(espeakng_loader.get_data_path())
    if library != package / "espeak-ng.dll" or data_path != package / "espeak-ng-data":
        raise RuntimeError("Local adapter returned unexpected asset locations")
    native = ctypes.CDLL(str(library))
    for name in policy.REQUIRED_ESPEAK_EXPORTS:
        getattr(native, name)
    native.espeak_Initialize.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_char_p, ctypes.c_int]
    native.espeak_Initialize.restype = ctypes.c_int
    if native.espeak_Initialize(2, 0, str(data_path).encode("utf-8"), 0x8000) <= 0:
        raise RuntimeError("Native synchronous phonemization initialization failed")
    native.espeak_Info.argtypes = [ctypes.POINTER(ctypes.c_char_p)]
    native.espeak_Info.restype = ctypes.c_char_p
    actual_path = ctypes.c_char_p()
    version = native.espeak_Info(ctypes.byref(actual_path)).decode("ascii")
    path_matches = bool(actual_path.value) and Path(actual_path.value.decode("utf-8")).resolve() == data_path.resolve()
    if version != policy.ESPEAK_VERSION or not path_matches:
        raise RuntimeError("Native API version or actually loaded data directory differs")
    native.espeak_SetVoiceByName.argtypes = [ctypes.c_char_p]
    native.espeak_SetVoiceByName.restype = ctypes.c_int
    native.espeak_TextToPhonemes.argtypes = [ctypes.POINTER(ctypes.c_char_p), ctypes.c_int, ctypes.c_int]
    native.espeak_TextToPhonemes.restype = ctypes.c_char_p
    sample = b"The quick brown fox speaks clearly."
    direct = {}
    # SetVoiceByName accepts eSpeak voice identifiers, not BCP-47 language codes.
    # British English is the native voice 'en'; phonemizer below still uses en-gb.
    native_names = {"en-us": "en-us", "en-gb": "en"}
    for voice, native_name in native_names.items():
        if native.espeak_SetVoiceByName(native_name.encode("ascii")) != 0:
            raise RuntimeError(f"Native voice unavailable: {voice} ({native_name})")
        cursor = ctypes.c_char_p(sample)
        result = []
        for _ in range(32):
            value = native.espeak_TextToPhonemes(ctypes.byref(cursor), 1, 2)
            if value:
                result.append(value.decode("utf-8"))
            if not cursor.value:
                break
        else:
            raise RuntimeError("Native phonemization did not terminate")
        direct[voice] = " ".join(result).strip()
        if not direct[voice] or direct[voice].isascii():
            raise RuntimeError("Native API did not produce IPA phonemes")
    native.espeak_Terminate()
    # Exercise phonemizer's actual DLL-copy lifecycle, voice selection, IPA, and
    # explicit data-path API used by Kokoro, with no OS eSpeak discovery.
    from phonemizer.backend.espeak.wrapper import EspeakWrapper
    from phonemizer.backend import EspeakBackend
    EspeakWrapper.set_library(str(library))
    EspeakWrapper.set_data_path(str(data_path))
    phonemes = {}
    for voice in ("en-us", "en-gb"):
        backend = EspeakBackend(voice, with_stress=True)
        result = backend.phonemize([sample.decode("ascii")], strip=True)
        if len(result) != 1 or not result[0].strip() or result[0].isascii():
            raise RuntimeError("phonemizer/Kokoro native interface smoke failed")
        phonemes[voice] = result[0]
    files = policy.controlled_files(package, skip_marker=True)
    json_write(destination, {"status": "passed", "version": version,
        "loaded_data_path_matches": path_matches, "isolated_copy_test": True,
        "dll_sha256": files["espeak-ng.dll"]["sha256"],
        "adapter_sha256": files["__init__.py"]["sha256"],
        "data_tree_sha256": policy.tree_digest({k: v["sha256"] for k, v in files.items() if k.startswith("espeak-ng-data/")}),
        "direct_api": direct, "phonemizer": phonemes,
        "scope": "Native ABI/data and phonemizer only; not the full frozen Kokoro/model/installer test"})


def build(powershell_version):
    for path in (BASE, SITE, PACKAGE):
        policy.plain_path(path)
    if not PACKAGE.resolve().is_relative_to(Path(sys.prefix).resolve()):
        raise RuntimeError("Native replacement target is outside the build venv")
    data_locations = (PACKAGE / "espeak-ng-data", BUILD / "espeak-ng-data",
                      WORK / "isolated runtime layout with spaces/espeakng_loader/espeak-ng-data")
    if any(len(str(path)) >= 220 for path in data_locations):
        raise RuntimeError("eSpeak 1.52 has a bounded data-path buffer: use a shorter venv/OutputRoot path (spaces are supported)")
    originals = [d for d in metadata.distributions() if policy.canonical(d.metadata.get("Name", "")) == "espeakng-loader"]
    if len(originals) != 1 or originals[0].version != "0.2.4" or (PACKAGE / policy.CONTROLLED_MARKER).exists():
        raise RuntimeError("Start from a fresh venv with espeakng-loader==0.2.4; never restamp a previous build")
    old_info = Path(originals[0]._path)
    policy.plain_path(old_info)
    if old_info.parent.resolve() != SITE.resolve() or not old_info.name.endswith(".dist-info"):
        raise RuntimeError("Unexpected loader metadata layout")
    for sibling in (SITE / "espeakng_loader.libs", SITE / "espeakng_loader.dylibs", SITE / "espeakng_loader.py"):
        if sibling.exists():
            raise RuntimeError("Unexpected unreviewed loader sibling; use a clean venv")
    before = policy.controlled_files(PACKAGE)
    if "espeak-ng.dll" not in before or not any(p.startswith("espeak-ng-data/") for p in before):
        raise RuntimeError("Expected Windows loader DLL/data to replace")
    WORK.mkdir()
    EVIDENCE.mkdir()
    created = datetime.now(timezone.utc).isoformat()
    for name in policy.CONTROLLED_RECIPE_FILES + ("docs/THIRD_PARTY.md",):
        write(INPUTS / "recipes" / name, policy.read_regular(REPO / name))
    write(INPUTS / "local-adapter/espeakng_loader/__init__.py", policy.LOCAL_LOADER_SOURCE)
    write(INPUTS / "local-adapter/METADATA", policy.LOCAL_LOADER_METADATA)
    write(INPUTS / "local-adapter/WHEEL", policy.LOCAL_LOADER_WHEEL)
    url, archive_name, sha = policy.SOURCE_ARCHIVES["espeak-ng"]
    print("[native-espeak] Downloading SHA-pinned official eSpeak source, not native binaries", flush=True)
    raw = policy.fetch(url, sha)
    write(INPUTS / "archives" / archive_name, raw)
    with policy.Archive(raw, strip_root=True) as archive:
        # Extract regular files ourselves. Never trust tar paths, device nodes,
        # or Windows symlink privileges. Materialize the one known header alias.
        links = {}
        for member in archive.handle.getmembers():
            if member.issym():
                relative = "/".join(PurePosixPath(member.name).parts[1:])
                links[relative] = member.linkname
            elif not member.isdir() and not member.isfile():
                raise RuntimeError("Unexpected non-regular source archive member")
        if links != {"src/include/espeak/speak_lib.h": "../espeak-ng/speak_lib.h"}:
            raise RuntimeError("Pinned source symlink set differs")
        for name in sorted(archive.files):
            if any(PureWindowsPath(p).is_reserved() or p.endswith((".", " ")) for p in PurePosixPath(name).parts):
                raise RuntimeError("Source archive contains a Windows-special path")
            write(SOURCE / name, archive.read(name))
        write(SOURCE / "src/include/espeak/speak_lib.h", archive.read("src/include/espeak-ng/speak_lib.h"))
        changes, patch_records = policy.controlled_source_changes(archive)
        patches = []
        for name, content in changes.items():
            patches += list(difflib.unified_diff(archive.read(name).decode().splitlines(True), content.decode().splitlines(True),
                                               fromfile="a/" + name, tofile="b/" + name))
            write(INPUTS / "patched" / name, content)
            write(SOURCE / name, content)
        write(INPUTS / "patches/speakcity-controlled-build.patch", "".join(patches))
        json_write(INPUTS / "patches/changes.json", {"modified_at_utc": created, "patches": patch_records,
            "materialized_symlinks": links, "reason": "Regular-file header alias for Windows extraction; identical upstream header bytes"})
        expected_data = policy.expected_controlled_data(archive)
    cmake = shutil.which("cmake.exe")
    if not cmake:
        raise RuntimeError("CMake 3.21+ must be installed on the Windows runner")
    vswhere = Path(os.environ["ProgramFiles(x86)"]) / "Microsoft Visual Studio/Installer/vswhere.exe"
    vs = json.loads(run([vswhere, "-latest", "-products", "*", "-version", "[17.0,19.0)", "-requires",
                         "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "Microsoft.Component.MSBuild", "-format", "json", "-utf8"], "vswhere.json", timeout=90))
    if len(vs) != 1:
        raise RuntimeError("A supported Visual Studio 2022/2026 C++ and MSBuild toolchain was not found")
    major = int(vs[0]["installationVersion"].split(".")[0])
    generator, toolset = {17: ("Visual Studio 17 2022", "v143,host=x64"), 18: ("Visual Studio 18 2026", "v145,host=x64")}[major]
    cmake_version = run([cmake, "--version"], "cmake-version.txt", timeout=90)
    flags = [f"-D{k}={v}" for k, v in sorted(policy.CONTROLLED_FLAGS.items())]
    configure = [cmake, "-S", SOURCE, "-B", BUILD, "-G", generator, "-A", "x64", "-T", toolset,
                 "-DCMAKE_GENERATOR_INSTANCE=" + Path(vs[0]["installationPath"]).as_posix(),
                 "-DCMAKE_INSTALL_PREFIX=" + INSTALL.as_posix(), *flags]
    run(configure, "configure.log", timeout=600)
    compiler_files = list((BUILD / "CMakeFiles").glob("*/CMakeCCompiler.cmake"))
    if len(compiler_files) != 1:
        raise RuntimeError("Missing/ambiguous actual CMake compiler record")
    compiler_text = compiler_files[0].read_text(encoding="utf-8-sig")
    if cmake_value(compiler_text, "CMAKE_C_COMPILER_ID") != "MSVC":
        raise RuntimeError("Refusing a non-MSVC native compiler")
    compiler = Path(cmake_value(compiler_text, "CMAKE_C_COMPILER"))
    dumpbin = compiler.with_name("dumpbin.exe")
    toolchain = dict(line.split("=", 1) for line in (BUILD / "speakcity-toolchain.txt").read_text(encoding="utf-8-sig").splitlines())
    if not toolchain.get("CMAKE_VS_MSBUILD_COMMAND"):
        raise RuntimeError("Actual Visual Studio MSBuild program not recorded")
    msbuild = Path(toolchain["CMAKE_VS_MSBUILD_COMMAND"])
    msbuild_version = run([msbuild, "-version", "-nologo"], "msbuild-version.txt", timeout=90)
    run([cmake, "--build", BUILD, "--config", "Release", "--target", "data", "--parallel", "2"], "build.log")
    run([cmake, "--install", BUILD, "--config", "Release"], "install.log", timeout=600)
    # Exact expected CMake output name; no speculative DLL rename is allowed.
    native = INSTALL / "bin/espeak-ng.dll"
    pe = policy.pe_identity(policy.read_regular(native, policy.MAX_DOWNLOAD))
    if not pe["dll"] or not policy.REQUIRED_ESPEAK_EXPORTS.issubset(pe["exports"]):
        raise RuntimeError("CMake output does not export the required native eSpeak API")
    if policy.file_digest(native) == policy.DLL_SHA:
        raise RuntimeError("Old unproven DLL cannot become a controlled-build output")
    if {p.name for p in (INSTALL / "bin").glob("*.dll")} != {"espeak-ng.dll"}:
        raise RuntimeError("Unexpected dependent DLL in the install tree")
    run([dumpbin, "/nologo", "/exports", native], "dumpbin-exports.txt", timeout=90)
    run([dumpbin, "/nologo", "/dependents", native], "dumpbin-dependents.txt", timeout=90)
    for origin, destination in ((BUILD / "CMakeCache.txt", "CMakeCache.txt"),
        (BUILD / "speakcity-toolchain.txt", "toolchain.txt"),
        (compiler_files[0], "CMakeCCompiler.cmake"),
        (compiler_files[0].with_name("CMakeCXXCompiler.cmake"), "CMakeCXXCompiler.cmake"),
        (compiler_files[0].with_name("CMakeSystem.cmake"), "CMakeSystem.cmake"),
        (BUILD / "src/libespeak-ng/include/config.h", "config.h"),
        (BUILD / "src/libespeak-ng/espeak-ng.vcxproj", "espeak-ng.vcxproj"),
        (BUILD / "src/espeak-ng-bin.vcxproj", "espeak-ng-bin.vcxproj"),
        (BUILD / "src/ucd-tools/ucd.vcxproj", "ucd.vcxproj"),
        (BUILD / "install_manifest.txt", "install_manifest.txt")):
        write(EVIDENCE / destination, policy.read_regular(origin, policy.MAX_DOWNLOAD))
    project = (EVIDENCE / "espeak-ng.vcxproj").read_text(encoding="utf-8-sig")
    sdk = re.findall(r"<WindowsTargetPlatformVersion>([^<]+)</WindowsTargetPlatformVersion>", project)
    if len(sdk) != 1:
        raise RuntimeError("Actual Windows SDK version not recorded by MSBuild project")
    layout = WORK / "isolated runtime layout with spaces/espeakng_loader"
    shutil.copytree(INSTALL / "share/espeak-ng-data", layout / "espeak-ng-data")
    write(layout / "espeak-ng.dll", policy.read_regular(native, policy.MAX_DOWNLOAD))
    write(layout / "__init__.py", policy.LOCAL_LOADER_SOURCE)
    produced = policy.controlled_files(layout)
    if set(produced) != expected_data | {"espeak-ng.dll", "__init__.py"}:
        raise RuntimeError("Generated native data file set differs from pinned full CMake data recipe")
    if any(produced[n]["bytes"] == 0 for n in ("espeak-ng.dll", "espeak-ng-data/phondata", "espeak-ng-data/phontab", "espeak-ng-data/phonindex", "espeak-ng-data/intonations", "espeak-ng-data/en_dict")):
        raise RuntimeError("Required generated native data is empty")
    run([sys.executable, "-I", "-B", __file__, "smoke", REPO, layout, EVIDENCE / "smoke-isolated.json"],
        "smoke-isolated.log", system_path=True, timeout=120)
    # Stage alongside site-packages so renames remain atomic on this volume even
    # when OutputRoot is on another drive. No old wrapper/DLL/data is merged.
    staging = SITE / "_speakcity_espeak_stage"
    info_stage = SITE / "_speakcity_espeak_info_stage"
    backup = SITE / "_speakcity_espeak_backup"
    info_backup = SITE / "_speakcity_espeak_info_backup"
    new_info = SITE / policy.CONTROLLED_DIST_INFO
    for path in (staging, info_stage, backup, info_backup, new_info):
        policy.plain_path(path)
        if path.exists():
            raise RuntimeError("Previous/partial loader replacement exists; restore a clean venv")
    shutil.copytree(layout, staging)
    info_stage.mkdir()
    write(info_stage / "METADATA", policy.LOCAL_LOADER_METADATA)
    write(info_stage / "WHEEL", policy.LOCAL_LOADER_WHEEL)
    write(info_stage / "top_level.txt", "espeakng_loader\n")
    write(info_stage / "INSTALLER", "speakcity-controlled-native-build\n")
    moved_package = moved_info = new_package = new_metadata = False
    try:
        PACKAGE.rename(backup)
        moved_package = True
        old_info.rename(info_backup)
        moved_info = True
        staging.rename(PACKAGE)
        new_package = True
        info_stage.rename(new_info)
        new_metadata = True
        run([sys.executable, "-I", "-B", __file__, "smoke", REPO, PACKAGE, EVIDENCE / "smoke.json"],
            "smoke-installed.log", system_path=True, timeout=120)
        installed = policy.controlled_files(PACKAGE)
        if installed != produced:
            raise RuntimeError("Installed replacement differs from the built-and-tested assets")
        tool_paths = {"cmake": cmake, "cl": compiler, "dumpbin": dumpbin, "msbuild": msbuild}
        manifest = {"schema": policy.CONTROLLED_SCHEMA, "status": "built-and-validated", "created_at_utc": created,
            "source": {"url": url, "archive": "inputs/archives/" + archive_name, "sha256": sha, "commit": policy.ESPEAK_COMMIT},
            "patches": patch_records, "materialized_symlinks": links,
            "environment": {"platform": "win32", "machine": "AMD64", "python": sys.executable,
                "python_version": sys.version, "removed_environment_names": list(CLEAN_ENV_NAMES),
                "github": {name: os.environ.get(name) for name in ("GITHUB_REPOSITORY", "GITHUB_SHA", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "ImageOS", "ImageVersion")}},
            "tools": {"cmake": cmake_version, "msvc": cmake_value(compiler_text, "CMAKE_C_COMPILER_VERSION"),
                "msbuild": msbuild_version, "windows_sdk": sdk[0], "powershell": powershell_version, "python": sys.version},
            "tool_files": {name: {"path": str(path), "sha256": policy.file_digest(Path(path))} for name, path in tool_paths.items()},
            "build": {"flags": policy.CONTROLLED_FLAGS, "generator": generator, "architecture": "x64",
                "toolset": toolset, "configuration": "Release", "target": "data", "optional_dependencies": []},
            "input_files": policy.controlled_files(INPUTS), "evidence_files": policy.controlled_files(EVIDENCE),
            "produced_files": {k: v for k, v in produced.items() if k != "__init__.py"},
            "installed_files": installed, "installed_package": str(PACKAGE), "pe": pe,
            "data_tree_sha256": policy.tree_digest({k: v["sha256"] for k, v in installed.items() if k.startswith("espeak-ng-data/")}),
            "replaced": {"dll": True, "data": True, "wrapper": True, "distribution_metadata": True},
            "previous_files": before, "previous_distribution_version": "0.2.4",
            "legal_clearance": False, "reproducible_build_claim": False}
        json_write(BASE / "manifest.json", manifest)
        json_write(PACKAGE / policy.CONTROLLED_MARKER, {"schema": policy.CONTROLLED_SCHEMA,
                   "manifest_path": str(BASE / "manifest.json"), "manifest_sha256": policy.file_digest(BASE / "manifest.json")})
        # Truthful local dist-info/RECORD: no stale upstream wheel RECORD claims.
        records = []
        for folder in (PACKAGE, new_info):
            for path in sorted(folder.rglob("*")):
                if path.is_file() and "__pycache__" not in path.parts and path.suffix != ".pyc":
                    hashed = base64.urlsafe_b64encode(bytes.fromhex(policy.file_digest(path))).decode("ascii").rstrip("=")
                    records.append([path.relative_to(SITE).as_posix(), "sha256=" + hashed, str(path.stat().st_size)])
        records.append([policy.CONTROLLED_DIST_INFO + "/RECORD", "", ""])
        with (new_info / "RECORD").open("w", encoding="utf-8", newline="") as stream:
            csv.writer(stream, lineterminator="\n").writerows(records)
        importlib.invalidate_caches()
        policy.validate_controlled_build(BASE / "manifest.json")
    except BaseException as exc:
        # Restore prebuild venv on any replacement/smoke/provenance failure.
        if new_package:
            shutil.rmtree(PACKAGE)
        if new_metadata:
            shutil.rmtree(new_info)
        if moved_package:
            backup.rename(PACKAGE)
        if moved_info:
            info_backup.rename(old_info)
        if (BASE / "manifest.json").exists():
            (BASE / "manifest.json").unlink()
        json_write(BASE / "failure.json", {"status": "failed", "error": str(exc)[:2000], "rollback_attempted": True})
        raise
    shutil.rmtree(backup)
    shutil.rmtree(info_backup)
    print("[native-espeak] Controlled DLL + complete generated data + local adapter validated; manifest: " + str(BASE / "manifest.json"))


if sys.argv[1] == "smoke":
    smoke(Path(sys.argv[3]), Path(sys.argv[4]))
elif sys.argv[1] == "build":
    build(sys.argv[3])
else:
    raise RuntimeError("Unknown native-driver phase")
'@
$recipes = Join-Path $output 'inputs/recipes'
New-Item -ItemType Directory -Path $recipes -Force | Out-Null
$driver = Join-Path $recipes 'native_driver.py'
# Explicit LF/UTF-8 without BOM makes the embedded recipe compare byte-exactly.
[IO.File]::WriteAllText($driver, $nativeDriver.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
& $python -I -B $driver build $root ($PSVersionTable.PSVersion.ToString())
if ($LASTEXITCODE -ne 0) { throw "Controlled eSpeak source build failed ($LASTEXITCODE). Do not run PyInstaller or publish this attempt." }

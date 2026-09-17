"""Collect SPEAKCITY build notices and a fail-closed source-companion directory.

Run with the Windows x64 Python 3.11 build venv, before installer compilation.
Only the standard library is used. Nothing downloaded is imported, installed,
loaded as a DLL, or executed. See docs/THIRD_PARTY.md for the known release gates.
An exit status of zero would mean collection checks passed, NOT legal clearance.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import difflib
import hashlib
from importlib import metadata
import io
import json
import sysconfig
from pathlib import Path, PurePosixPath, PureWindowsPath
import platform
import re
import shutil
import stat
import struct
import sys
import tarfile
import urllib.parse
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MIB = 1024 * 1024
MAX_DOWNLOAD = 64 * MIB
MAX_NOTICE = 4 * MIB
MAX_EXPANDED = 256 * MIB
PINS = {"phonemizer": "3.4.0", "espeakng-loader": "0.2.4"}
PHONEMIZER_SDIST_SHA = "e13231980c50bc671ec0466379ba027260ad9d61929952d8ae9665b3d0f251eb"
LOADER_COMMIT = "146599e29be31bf17d99f0bcb7dbb2f92aef3d95"
LOADER_LICENSE_COMMIT = "0ddc87adf77e5850d7eeb542ac8a87d421b64daa"
ESPEAK_COMMIT = "4870adfa25b1a32b4361592f1be8a40337c58d6c"
SONIC_COMMIT = "fbf75c3d6d846bad3bb3d456cbc5d07d9fd8c104"
ESPEAK_VERSION = "1.52.0"
LOADER_WHEEL = "espeakng_loader-0.2.4-py3-none-win_amd64.whl"
LOADER_WHEEL_SHA = "41f1e08ac9deda2efd1ea9de0b81dab9f5ae3c4b24284f76533d0a7b1dd7abd7"
DLL_SHA = "646d387acbc7ac2aa45e3625aa00a6835ae5d446ff8b0748298c3900b4dde258"
DATA_SHA = "1e227c92894093749c72d99169a0ab30749015ec33c5f9826672d8f3113e8dd0"
RELEASE = "https://github.com/thewh1teagle/espeakng-loader/releases/download/v0.1.0"
KOKORO_DOC_REV = "f3ff3571791e39611d31c381e3a41a3af07b4987"
WHISPER_REV = "3d3d5dee26484f91867d81cb899cfcf72b96be6c"

# These are audit-pinned archive bytes at immutable source commits, not claimed
# publisher signatures. PyPI artifacts are additionally checked against PyPI's
# published digest. Keep these pins under review, never silently refresh them.
SOURCE_ARCHIVES = {
    "espeakng-loader": (
        f"https://codeload.github.com/thewh1teagle/espeakng-loader/tar.gz/{LOADER_COMMIT}",
        f"espeakng-loader-{LOADER_COMMIT}.tar.gz",
        "d6496114cd0608988f5291f54d2d15b50cb37b7f007af00bb62cf93be1b3ceca",
    ),
    "espeak-ng": (
        f"https://codeload.github.com/espeak-ng/espeak-ng/tar.gz/{ESPEAK_COMMIT}",
        f"espeak-ng-{ESPEAK_COMMIT}.tar.gz",
        "cd83f84c4e495f281ac14e919aecf2834306ec1ea1b498de8ef6000f3a0f90de",
    ),
    "sonic": (
        f"https://codeload.github.com/waywardgeek/sonic/tar.gz/{SONIC_COMMIT}",
        f"sonic-{SONIC_COMMIT}.tar.gz",
        "715827b5a39b79e56e44397d7b845910df996d4cca74777b3b61629b1ddc98c1",
    ),
}
RELEASE_FILES = {
    "checksum.txt": "acf4364a8fd9ee48085462c5a4c5d4ba1d28f080a5174281f5bafc9ff49b5a96",
    "espeak-ng-libs-windows-x86_64.tar.gz": "94a66fd30ec94dbe573e3f65491e32b8aa5b890108f890d2b68b42343474f2b6",
    "espeak-ng-data.tar.gz": "38896a06fde172b57828e6877a2dbf6d278590f7f6ef6007a155b4049891f650",
}
# Only license texts/model cards, never weights, samples, or executable code.
NOTICE_DOWNLOADS = {
    "models/kokoro/MODEL_CARD.md": (
        f"https://huggingface.co/hexgrad/Kokoro-82M/resolve/{KOKORO_DOC_REV}/README.md",
        "91dcabced89db6f109b8786642f50402d3ee87450e8189589b6f85520e7f4d78",
    ),
    "models/kokoro/VOICES.md": (
        f"https://huggingface.co/hexgrad/Kokoro-82M/resolve/{KOKORO_DOC_REV}/VOICES.md",
        "ec7e4941ad7e194af61e3455928528a9ff5360c7c505e412efab27d6a69ea106",
    ),
    "models/kokoro/Apache-2.0.txt": (
        "https://www.apache.org/licenses/LICENSE-2.0.txt",
        "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30",
    ),
    "models/whisper/CONVERSION_MODEL_CARD.md": (
        f"https://huggingface.co/Systran/faster-whisper-base.en/resolve/{WHISPER_REV}/README.md",
        "0360e1518671daf4064d44121ad1c4bbb0446dd5b0f8c724c8d3132fd33a1540",
    ),
    "models/whisper/LICENSE": (
        "https://raw.githubusercontent.com/openai/whisper/6dea21fd7f7253bfe450f1e2512a0fe47ee2d258/LICENSE",
        "b5d65a59060e68c4ff940e1eddfa6f94b2d68fdf58ed7f4dd57721c997e35e9d",
    ),
    "models/silero-vad/LICENSE": (
        "https://raw.githubusercontent.com/snakers4/silero-vad/fba061dc5559f696e62171e9a0741782b0fdc23c/LICENSE",
        "2e63e9a38b6e8fc0c7bc37ce174caca1862870856c6daf5697cfb785e925520b",
    ),
    "models/silero-vad/FASTER_WHISPER_README.md": (
        "https://raw.githubusercontent.com/SYSTRAN/faster-whisper/65882eee9f5cdbeeb2d877f1131d48cf241b327d/README.md",
        "5ae59e0781834e6887bbd51bda2d8bd5dfe08cd345e0c6f7f3aca455d129cc69",
    ),
    "upstream/espeakng-loader/LATER_UPSTREAM_LICENSE.txt": (
        f"https://raw.githubusercontent.com/thewh1teagle/espeakng-loader/{LOADER_LICENSE_COMMIT}/LICENSE",
        "b05c73bb1335b4320ec97edff106b3991c12d0acdc6e899c96f061a15039119f",
    ),
}
FIXED_URLS = {v[0] for v in SOURCE_ARCHIVES.values()} | {v[0] for v in NOTICE_DOWNLOADS.values()}
FIXED_URLS |= {f"{RELEASE}/{name}" for name in RELEASE_FILES}
FIXED_URLS |= {f"https://pypi.org/pypi/{name}/{version}/json" for name, version in PINS.items()}
HF_CACHE_PATHS = set()
for _url, _digest in NOTICE_DOWNLOADS.values():
    if _url.startswith("https://huggingface.co/"):
        _repo, _rest = urllib.parse.urlsplit(_url).path[1:].split("/resolve/", 1)
        HF_CACHE_PATHS.add(f"/api/resolve-cache/models/{_repo}/{_rest}")

# This is intentionally NOT a waiver flag. Removing this gate requires a new
# reviewed provenance mapping or a controlled rebuild, not changing CLI options.
PROVENANCE_BLOCKER = (
    "EXACT_ESPEAK_PROVENANCE_UNRESOLVED: the audited Windows wheel identifies eSpeak NG 1.52.0 "
    "and matches the v0.1.0 release DLL/data, and the loader build recipe/gitlink point to "
    f"{ESPEAK_COMMIT}. However the publisher packaged releases/latest and overwrote release "
    "assets with --clobber; an immutable build-to-source/configuration record for this DLL "
    "AND data was not established. Source snapshots here are candidates, not a certified "
    "corresponding-source mapping. Obtain publisher build provenance (including optional "
    "native dependencies/configuration), or replace the wheel with a reviewed pinned-source "
    "rebuild and update this collector's mapping before distributing."
)


class CollectionError(RuntimeError):
    """A collection/release gate failed."""


def canonical(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def own_metadata_directories(directories: set[Path], base: Path, key: str, info_dir: object) -> set[Path]:
    """Keep only the metadata directories that belong to this distribution itself.

    A wheel RECORD can list nested ``*.dist-info`` directories belonging to
    vendored projects (``setuptools`` does exactly that for its ``_vendor``
    copies). Those directories still carry third-party notices that must be
    collected, but they are not this distribution's metadata directory: a
    declared ``License-File`` is relative to the distribution's own
    ``<name>-<version>.dist-info``, so it must never be demanded inside a
    vendor directory that legitimately lacks it.
    """
    own = set()
    for directory in directories:
        if isinstance(info_dir, Path) and directory == info_dir:
            own.add(directory)
            continue
        if directory.resolve().parent != base:
            continue
        stem = directory.name
        for suffix in (".dist-info", ".egg-info", ".egg"):
            if stem.endswith(suffix):
                stem = stem[:-len(suffix)]
                break
        else:
            continue
        canonical_stem = canonical(stem)
        if canonical_stem == key or canonical_stem.startswith(key + "-"):
            own.add(directory)
    return own


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(MIB), b""):
            h.update(block)
    return h.hexdigest()


def plain_path(path: Path) -> None:
    """Reject links/junctions, including parent components, before file I/O."""
    for part in (path, *path.parents):
        if part.is_symlink():
            raise CollectionError(f"Symlink not accepted: {part.name}")
        if part.exists() and getattr(part.stat(), "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 1024):
            raise CollectionError(f"Reparse point not accepted: {part.name}")


def read_regular(path: Path, limit: int = MAX_NOTICE) -> bytes:
    plain_path(path)
    if not path.is_file() or path.stat().st_size > limit:
        raise CollectionError(f"Missing, non-regular, or oversized file: {path.name}")
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise CollectionError(f"Oversized file: {path.name}")
    return data


def put(path: Path, data: bytes) -> None:
    plain_path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    # Fresh output directories and exclusive writes prevent stale mixed releases.
    with path.open("xb") as stream:
        stream.write(data)


def put_json(path: Path, value: object) -> None:
    put(path, (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n").encode("utf-8"))


def validate_url(url: str, *, redirect: bool = False) -> None:
    if any(ord(c) <= 32 or ord(c) == 127 for c in url):
        raise CollectionError("Control characters/whitespace in download URL")
    u = urllib.parse.urlsplit(url)
    if u.scheme != "https" or u.username or u.password or u.port not in (None, 443) or u.fragment:
        raise CollectionError("Downloads require plain HTTPS URLs with no credentials/fragment")
    if url in FIXED_URLS:
        return
    if u.hostname == "files.pythonhosted.org" and not u.query:
        # Only these pinned package releases, under PyPI's actual file host.
        pattern = r"/packages/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{56,64}/(phonemizer-3\.4\.0|espeakng[_-]loader-0\.2\.4)(\.tar\.gz|\.zip|-py3-none-win_amd64\.whl)"
        if re.fullmatch(pattern, u.path):
            return
    if redirect and u.hostname == "huggingface.co" and u.path in HF_CACHE_PATHS:
        return
    if redirect and u.hostname == "release-assets.githubusercontent.com":
        # Official GitHub release CDN, scoped to the loader repository's numeric
        # ID, not arbitrary GitHub users/assets. Signed query fields are not logged.
        if re.fullmatch(r"/github-production-release-asset/912175986/[0-9a-f-]{36}", u.path):
            return
    raise CollectionError(f"Download URL outside the official allowlist: {u.hostname}{u.path}")


class CheckedRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_url(newurl, redirect=True)  # Check BEFORE following, including HTTPS downgrade.
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def fetch(url: str, expected: str | None = None, *, limit: int = MAX_DOWNLOAD, size: int | None = None) -> bytes:
    validate_url(url)
    if expected is not None and not re.fullmatch(r"[0-9a-f]{64}", expected):
        raise CollectionError("Missing/invalid expected SHA-256")
    opener = urllib.request.build_opener(CheckedRedirect())
    req = urllib.request.Request(url, headers={"User-Agent": "SPEAKCITY-source-collector/1", "Accept-Encoding": "identity"})
    with opener.open(req, timeout=45) as response:
        validate_url(response.geturl(), redirect=True)
        if response.status != 200:
            raise CollectionError(f"Unexpected HTTP status {response.status}")
        content_length = response.headers.get("Content-Length")
        if content_length and int(content_length) > limit:
            raise CollectionError("Download exceeds size limit")
        chunks = []
        length = 0
        while True:
            block = response.read(min(MIB, limit + 1 - length))
            if not block:
                break
            length += len(block)
            if length > limit:
                raise CollectionError("Download exceeds size limit")
            chunks.append(block)
    data = b"".join(chunks)
    if size is not None and (type(size) is not int or size < 0 or len(data) != size):
        raise CollectionError("Published artifact size does not match")
    if expected is not None and digest(data) != expected:
        raise CollectionError("SHA-256 mismatch; refusing changed/unverified artifact")
    return data


def member_path(name: str) -> PurePosixPath:
    p = PurePosixPath(name)
    if (not name or "\\" in name or ":" in name or p.is_absolute()
            or any(part in ("", ".", "..") for part in name.rstrip("/").split("/"))
            or any(ord(c) < 32 for c in name)):
        raise CollectionError("Unsafe archive/file-record path")
    return p


class Archive:
    """Inspect ZIP/tar members; never extract an archive or follow its links."""
    def __init__(self, data: bytes, *, strip_root: bool = False):
        stream = io.BytesIO(data)
        self.zip = zipfile.is_zipfile(stream)
        stream.seek(0)
        self.handle = zipfile.ZipFile(stream) if self.zip else tarfile.open(fileobj=stream, mode="r:*")
        self.files = {}
        records = self.handle.infolist() if self.zip else self.handle.getmembers()
        if len(records) > 20000:
            raise CollectionError("Archive member count exceeds limit")
        total = 0
        roots = set()
        seen = set()
        for record in records:
            name = record.filename if self.zip else record.name
            p = member_path(name)
            roots.add(p.parts[0])
            if str(p) in seen:
                raise CollectionError("Duplicate archive member")
            seen.add(str(p))
            size = record.file_size if self.zip else record.size
            total += size
            if size > MAX_DOWNLOAD or total > MAX_EXPANDED:
                raise CollectionError("Expanded archive size exceeds limit")
            if record.is_dir() if self.zip else record.isdir():
                continue
            regular = (stat.S_IFMT(record.external_attr >> 16) in (0, stat.S_IFREG)) if self.zip else record.isfile()
            # Original source archives can contain symlinks; retain their bytes
            # in the archive but never read/copy any link target as a notice.
            if not regular:
                continue
            key = "/".join(p.parts[1:]) if strip_root else str(p)
            if not key:
                raise CollectionError("Unexpected archive layout")
            self.files[key] = record
        if strip_root and len(roots) != 1:
            raise CollectionError("Source archive must have exactly one top-level directory")

    def read(self, name: str, limit: int = MAX_DOWNLOAD) -> bytes:
        if name not in self.files:
            raise CollectionError(f"Required regular archive member missing: {name}")
        record = self.files[name]
        if (record.file_size if self.zip else record.size) > limit:
            raise CollectionError(f"Oversized archive member: {name}")
        stream = self.handle.open(record) if self.zip else self.handle.extractfile(record)
        with stream:
            data = stream.read(limit + 1)
        if len(data) > limit:
            raise CollectionError(f"Oversized archive member: {name}")
        return data

    def close(self) -> None:
        self.handle.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


def is_notice(name: str) -> bool:
    p = PurePosixPath(name)
    base = re.sub(r"[-_. ]", "", p.name.lower())
    return (any(re.match(r"^(licenses?|licences?|notices?)$", part.lower()) for part in p.parts[:-1])
            or any(base.startswith(prefix) for prefix in (
                "license", "licence", "copying", "copyright", "notice", "authors",
                "thirdpartynotice", "thirdpartylicense", "thirdpartylicence", "thirdpartycopyright", "thirdpartysoftware",
            )))


def safe_record_destination(name: str) -> Path:
    try:
        p = member_path(name)
        if any(PureWindowsPath(part).is_reserved() or part.endswith((".", " ")) for part in p.parts):
            raise CollectionError("Windows-special path")
        return Path(*p.parts)
    except CollectionError:
        # Some RECORD entries are relative to site-packages, e.g. ../../../share.
        # Their source must still be within the venv. Never use ../ in output.
        base = re.sub(r"[^A-Za-z0-9._-]", "_", PurePosixPath(name).name) or "notice.txt"
        return Path("external-records", digest(name.encode())[:16], "file-" + base)


def payload_hashes(archive: Archive, prefix: str) -> dict[str, str]:
    return {name: digest(archive.read(name)) for name in sorted(archive.files) if name.startswith(prefix)}


def tree_digest(files: dict[str, str]) -> str:
    return digest("\n".join(f"{files[name]}  {name}" for name in sorted(files)).encode("utf-8"))


# Reviewed controlled-build policy, shared with native_espeak.ps1. No CLI waiver.
CONTROLLED_SCHEMA = "speakcity.native-espeak/1"
CONTROLLED_MARKER = "speakcity-native-build.json"
CONTROLLED_VERSION = "0.2.4+speakcity.1"
CONTROLLED_DIST_INFO = f"espeakng_loader-{CONTROLLED_VERSION}.dist-info"
CONTROLLED_RECIPE_FILES = ("build/native_espeak.ps1", "build/collect_notices.py", "speech/speech_worker.spec")
CONTROLLED_FLAGS = {
    "BUILD_SHARED_LIBS": "ON", "BUILD_TESTING": "OFF", "USE_MBROLA": "OFF",
    "USE_LIBSONIC": "OFF", "USE_LIBPCAUDIO": "OFF", "USE_ASYNC": "OFF",
    "USE_KLATT": "OFF", "USE_SPEECHPLAYER": "OFF", "ESPEAK_COMPAT": "OFF",
    "ESPEAK_BUILD_MANPAGES": "OFF", "CMAKE_MSVC_RUNTIME_LIBRARY": "MultiThreaded",
    "CMAKE_CONFIGURATION_TYPES": "Release", "CMAKE_BUILD_TYPE": "Release",
    "CMAKE_C_STANDARD": "11", "CMAKE_C_STANDARD_REQUIRED": "ON", "CMAKE_C_EXTENSIONS": "OFF",
    "FETCHCONTENT_FULLY_DISCONNECTED": "ON", "FETCHCONTENT_UPDATES_DISCONNECTED": "ON",
    "CMAKE_FIND_USE_PACKAGE_REGISTRY": "OFF", "CMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY": "OFF",
    "CMAKE_C_FLAGS": "/DWIN32 /D_WINDOWS /utf-8 /D_CRT_SECURE_NO_WARNINGS /D_CRT_NONSTDC_NO_DEPRECATE",
    "CMAKE_CXX_FLAGS": "/DWIN32 /D_WINDOWS /utf-8 /D_CRT_SECURE_NO_WARNINGS /D_CRT_NONSTDC_NO_DEPRECATE",
    "CMAKE_C_FLAGS_RELEASE": "/O2 /Ob2 /DNDEBUG",
    "CMAKE_CXX_FLAGS_RELEASE": "/O2 /Ob2 /DNDEBUG",
    "CMAKE_EXE_LINKER_FLAGS": "/machine:x64", "CMAKE_SHARED_LINKER_FLAGS": "/machine:x64",
    "CMAKE_MODULE_LINKER_FLAGS": "/machine:x64", "CMAKE_STATIC_LINKER_FLAGS": "/machine:x64",
    "CMAKE_EXE_LINKER_FLAGS_RELEASE": "/INCREMENTAL:NO",
    "CMAKE_SHARED_LINKER_FLAGS_RELEASE": "/INCREMENTAL:NO",
}
REQUIRED_ESPEAK_EXPORTS = {
    "espeak_Initialize", "espeak_Info", "espeak_ListVoices", "espeak_SetVoiceByName",
    "espeak_GetCurrentVoice", "espeak_TextToPhonemes", "espeak_SetPhonemeTrace",
    "espeak_Synth", "espeak_Terminate", "espeak_ng_InitializePath",
    "espeak_ng_CompileDictionary", "espeak_ng_CompileIntonation", "espeak_ng_CompilePhonemeData",
}
# /MT, disabled audio/async, bundled static ucd: no ambient or vendored runtime
# DLLs may satisfy an unknown dependency. Widen only after explicit review.
SYSTEM_ESPEAK_IMPORTS = {"kernel32.dll", "advapi32.dll", "user32.dll", "winmm.dll"}
LOCAL_LOADER_SOURCE = '''"""SPEAKCITY's locally written path adapter for its controlled Windows eSpeak build.

Not the upstream espeakng-loader wrapper. Native GPL/component terms remain in
third-party-notices and the matching source companion. No system-library fallback.
"""
from pathlib import Path


def get_library_path():
    candidate = Path(__file__).resolve().parent / "espeak-ng.dll"
    if not candidate.is_file():
        raise RuntimeError("The controlled eSpeak NG library is missing")
    return str(candidate)


def get_data_path():
    candidate = Path(__file__).resolve().parent / "espeak-ng-data"
    if not (candidate / "phondata").is_file():
        raise RuntimeError("The controlled eSpeak NG data is missing")
    return str(candidate)
'''
LOCAL_LOADER_METADATA = f'''Metadata-Version: 2.1
Name: espeakng-loader
Version: {CONTROLLED_VERSION}
Summary: SPEAKCITY local path adapter and controlled native build, NOT the PyPI wheel

Locally written adapter. Upstream espeakng-loader Python code is not included.
eSpeak NG is GPL-3.0-or-later with additional component notices; see the source companion.
No license is invented here for the SPEAKCITY adaptation or combined work.
'''
LOCAL_LOADER_WHEEL = "Wheel-Version: 1.0\nGenerator: speakcity-controlled-native-build\nRoot-Is-Purelib: false\nTag: py3-none-win_amd64\n"


def controlled_source_changes(archive: Archive) -> tuple[dict[str, bytes], list[dict]]:
    """The entire, exact reviewed patch set. Never execute archive content here."""
    changed = {}
    reasons = {}

    def replace(name: str, old: str, new: str, reason: str) -> None:
        data = changed.get(name, archive.read(name))
        if data.count(old.encode()) != 1:
            raise CollectionError(f"Pinned source patch preimage differs: {name}")
        changed[name] = data.replace(old.encode(), new.encode(), 1)
        reasons.setdefault(name, []).append(reason)

    replace("CMakeLists.txt", "cmake_minimum_required(VERSION 3.8)",
            "cmake_minimum_required(VERSION 3.21)",
            "Require CMake 3.21 and CMP0091 NEW so the recorded /MT runtime choice is effective; support CMake 4.")
    replace("CMakeLists.txt", "include(CTest)\nadd_subdirectory(tests)",
            "if(BUILD_TESTING)\n  include(CTest)\n  add_subdirectory(tests)\nendif()",
            "Upstream unconditionally adds compiled tests; actually honor BUILD_TESTING=OFF.")
    replace("CMakeLists.txt", "include(CTest)\n\ninclude(cmake/deps.cmake)",
            'file(WRITE "${CMAKE_BINARY_DIR}/speakcity-toolchain.txt"\n'
            '  "CMAKE_VS_MSBUILD_COMMAND=${CMAKE_VS_MSBUILD_COMMAND}\\n"\n'
            '  "CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION=${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}\\n"\n'
            ')\n\ninclude(CTest)\n\ninclude(cmake/deps.cmake)',
            "Record actual VS MSBuild/SDK selections: VS generators need not cache CMAKE_MAKE_PROGRAM.")
    changed["cmake/deps.cmake"] = b'''# SPEAKCITY controlled phonemization build: no dependency search or FetchContent.
# ucd is built statically from this same source archive. No Sonic, PCAudio,
# MBROLA, pthread, or other downloaded/ambient optional library is linked.
set(HAVE_PTHREAD OFF)
set(HAVE_MBROLA OFF)
set(HAVE_LIBSONIC OFF)
set(HAVE_LIBPCAUDIO OFF)
'''
    reasons["cmake/deps.cmake"] = ["Remove unconditional Sonic download and ambient optional-library discovery, even when USE_LIBSONIC=OFF."]
    replace("src/CMakeLists.txt", "add_subdirectory(speechPlayer)",
            "if(USE_SPEECHPLAYER)\n  add_subdirectory(speechPlayer)\nendif()",
            "Do not build unused speechPlayer synthesis code.")
    # MSVC has __has_include, but not GCC/Clang's __has_include_next. This guarded
    # fallback leaves other compilers' builtins alone and keeps upstream shims.
    for name, header in (("src/include/compat/endian.h", "endian.h"),
                         ("src/include/compat/getopt.h", "getopt.h"),
                         ("src/include/compat/unistd.h", "unistd.h")):
        replace(name, f"#if __has_include_next(<{header}>)",
                "#if defined(_MSC_VER) && !defined(__clang__)\n"
                "#ifndef __has_include_next\n#define __has_include_next(x) 0\n#endif\n#endif\n"
                + f"#if __has_include_next(<{header}>)",
                "Guard unavailable __has_include_next when compiling with MSVC cl.exe.")
    recipe = archive.read("cmake/data.cmake")
    if recipe.count(b"add_custom_command(\n") != 4:
        raise CollectionError("Pinned data generation recipe changed")
    changed["cmake/data.cmake"] = recipe.replace(b"add_custom_command(\n", b"add_custom_command(\n  VERBATIM\n")
    reasons["cmake/data.cmake"] = ["Use CMake VERBATIM escaping for all four generated-data commands, including paths with spaces."]
    records = [{"path": name, "before_sha256": digest(archive.read(name)),
                "after_sha256": digest(data), "reasons": reasons[name]} for name, data in sorted(changed.items())]
    return changed, records


def expected_controlled_data(archive: Archive) -> set[str]:
    """Expected complete native data set from the pinned CMake recipe, not a count guess."""
    recipe = archive.read("cmake/data.cmake").decode("utf-8")
    match = re.search(r"list\(APPEND _dict_compile_list\s+(.*?)\)", recipe, re.S)
    if not match or not re.fullmatch(r"[a-z0-9_\s]+", match[1]):
        raise CollectionError("Pinned CMake dictionary list cannot be established")
    files = {"espeak-ng-data/" + n for n in ("phondata", "phondata-manifest", "phonindex", "phontab", "intonations")}
    files |= {f"espeak-ng-data/{name}_dict" for name in match[1].split()}
    files |= {name for name in archive.files if name.startswith(("espeak-ng-data/lang/", "espeak-ng-data/voices/!v/"))}
    return files


def pe_identity(data: bytes) -> dict:
    """Read x64 PE imports/exports without loading executable code (also in collector)."""
    try:
        if data[:2] != b"MZ":
            raise ValueError("Not a PE file")
        pe = struct.unpack_from("<I", data, 0x3c)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("Missing PE signature")
        machine, sections = struct.unpack_from("<HH", data, pe + 4)
        opt_size = struct.unpack_from("<H", data, pe + 20)[0]
        characteristics = struct.unpack_from("<H", data, pe + 22)[0]
        opt = pe + 24
        if machine != 0x8664 or struct.unpack_from("<H", data, opt)[0] != 0x20b or not 0 < sections < 100:
            raise ValueError("Not Windows AMD64 PE32+")
        if opt_size < 240 or struct.unpack_from("<I", data, opt + 108)[0] < 16:
            raise ValueError("Truncated PE directories")
        spans = []
        for i in range(sections):
            at = opt + opt_size + i * 40
            vsize, va, rawsize, raw = struct.unpack_from("<IIII", data, at + 8)
            spans.append((va, rawsize, raw))

        def offset(rva: int, length: int = 1) -> int:
            for va, size, raw in spans:
                if va <= rva and rva + length <= va + size and raw + rva - va + length <= len(data):
                    return raw + rva - va
            raise ValueError("PE RVA outside a section")

        def string(rva: int) -> str:
            at = offset(rva)
            end = data.index(b"\0", at, min(at + 512, len(data)))
            return data[at:end].decode("ascii")

        def directory(index: int) -> tuple[int, int]:
            return struct.unpack_from("<II", data, opt + 112 + index * 8)

        imports = set()
        rva, size = directory(1)
        if rva:
            at = offset(rva, size)
            for pos in range(at, at + size, 20):
                fields = struct.unpack_from("<IIIII", data, pos)
                if not any(fields):
                    break
                imports.add(string(fields[3]).lower())
            else:
                raise ValueError("Unterminated PE import table")
        # Nothing in this build needs delay-loading. Do not miss dependencies
        # hidden there by only reading the normal import directory.
        if any(directory(13)):
            raise ValueError("Unexpected delay-loaded imports")
        exports = []
        rva, size = directory(0)
        if rva:
            at = offset(rva, 40)
            count, names_rva = struct.unpack_from("<II", data, at + 24)[0], struct.unpack_from("<I", data, at + 32)[0]
            functions, funcs_rva = struct.unpack_from("<II", data, at + 20)[0], struct.unpack_from("<I", data, at + 28)[0]
            if count > 5000 or functions > 5000:
                raise ValueError("Excessive PE export count")
            for i in range(functions):
                address = struct.unpack_from("<I", data, offset(funcs_rva + i * 4, 4))[0]
                if rva <= address < rva + size:
                    raise ValueError("Forwarded PE exports are not accepted")
            exports = sorted(string(struct.unpack_from("<I", data, offset(names_rva + i * 4, 4))[0]) for i in range(count))
        if imports - SYSTEM_ESPEAK_IMPORTS:
            raise ValueError(f"Unreviewed DLL imports: {sorted(imports - SYSTEM_ESPEAK_IMPORTS)}")
        return {"machine": "AMD64", "dll": bool(characteristics & 0x2000),
                "exports": exports, "imports": sorted(imports), "delay_imports": []}
    except (IndexError, struct.error, UnicodeError, ValueError) as exc:
        raise CollectionError(f"Invalid/unreviewed native PE: {exc}") from exc


def controlled_files(directory: Path, *, skip_marker: bool = False) -> dict[str, dict]:
    plain_path(directory)
    if not directory.is_dir():
        raise CollectionError(f"Missing controlled-build directory: {directory.name}")
    files, total = {}, 0
    for p in sorted(directory.rglob("*")):
        plain_path(p)
        rel = p.relative_to(directory)
        if "__pycache__" in rel.parts or p.suffix == ".pyc" or p.is_dir():
            continue
        key = rel.as_posix()
        member_path(key)
        if skip_marker and key == CONTROLLED_MARKER:
            continue
        if not p.is_file():
            raise CollectionError("Non-regular controlled-build input")
        size = p.stat().st_size
        total += size
        if size > MAX_DOWNLOAD or total > MAX_EXPANDED or len(files) >= 20000:
            raise CollectionError("Controlled-build file limits exceeded")
        files[key] = {"sha256": file_digest(p), "bytes": size}
    return files


def validate_controlled_build(manifest_path: Path | None = None) -> dict:
    """Fail closed; a boolean in an unbound JSON file is not build provenance.

    Binds exact source archive + deterministic patches + reviewed recipe bytes +
    effective CMake/compiler config + generated data set + PE API/dependencies +
    actual installed files. Build evidence is local, not a signed attestation.
    """
    if (sys.platform != "win32" or struct.calcsize("P") != 8
            or platform.machine().lower() not in ("amd64", "x86_64")
            or sys.version_info[:2] != (3, 11) or sys.prefix == sys.base_prefix):
        raise CollectionError("Controlled native evidence requires the actual Python 3.11 Windows x64 build venv")
    site = Path(sysconfig.get_path("purelib"))
    package = site / "espeakng_loader"
    marker = json.loads(read_regular(package / CONTROLLED_MARKER))
    if set(marker) != {"schema", "manifest_path", "manifest_sha256"} or marker["schema"] != CONTROLLED_SCHEMA:
        raise CollectionError("Unrecognized controlled-build marker")
    selected = Path(marker["manifest_path"])
    if not selected.is_absolute() or (manifest_path is not None and selected.resolve() != manifest_path.resolve()):
        raise CollectionError("Installed marker selects a different/non-absolute manifest")
    raw = read_regular(selected)
    if digest(raw) != marker["manifest_sha256"]:
        raise CollectionError("Installed marker/manifest hash mismatch")
    m = json.loads(raw)
    base = selected.parent
    if m.get("schema") != CONTROLLED_SCHEMA or m.get("status") != "built-and-validated":
        raise CollectionError("Native build did not complete validation")
    if m.get("replaced") != {"dll": True, "data": True, "wrapper": True, "distribution_metadata": True}:
        raise CollectionError("DLL, whole data tree, local adapter and metadata must all be replaced")
    if m.get("environment", {}).get("platform") != "win32" or m["environment"].get("machine") != "AMD64":
        raise CollectionError("Manifest was not generated on Windows x64")
    if Path(m["environment"].get("python", "")).resolve() != Path(sys.executable).resolve():
        raise CollectionError("Controlled native build and freeze/collector must use the same Python")
    if Path(m.get("installed_package", "")).resolve() != package.resolve():
        raise CollectionError("Installed native package location mismatch")
    if not package.resolve().is_relative_to(Path(sys.prefix).resolve()):
        raise CollectionError("Controlled package must be in the actual build venv")
    inputs = controlled_files(base / "inputs")
    evidence = controlled_files(base / "evidence")
    if inputs != m.get("input_files") or evidence != m.get("evidence_files"):
        raise CollectionError("Source/recipe/evidence file set or hashes changed after the build")
    for relative in CONTROLLED_RECIPE_FILES:
        if read_regular(base / "inputs/recipes" / relative) != read_regular(ROOT / relative):
            raise CollectionError(f"Recipe changed since native build: {relative}; rebuild rather than re-stamp")
    script = read_regular(ROOT / "build/native_espeak.ps1").decode("utf-8-sig").replace("\r\n", "\n")
    start, end = "$nativeDriver = @'\n", "\n'@"
    if script.count(start) != 1 or script.count(end) != 1:
        raise CollectionError("Native driver's reviewed embedding changed")
    driver = script.split(start, 1)[1].split(end, 1)[0] + "\n"
    if read_regular(base / "inputs/recipes/native_driver.py") != driver.encode():
        raise CollectionError("Executed driver differs from the reviewed PowerShell recipe")
    url, filename, sha = SOURCE_ARCHIVES["espeak-ng"]
    if m.get("source") != {"url": url, "archive": "inputs/archives/" + filename, "sha256": sha, "commit": ESPEAK_COMMIT}:
        raise CollectionError("Unreviewed source identity")
    source = read_regular(base / "inputs/archives" / filename, MAX_DOWNLOAD)
    if digest(source) != sha:
        raise CollectionError("Official source archive differs from audit pin")
    with Archive(source, strip_root=True) as archive:
        changes, patches = controlled_source_changes(archive)
        if m.get("patches") != patches:
            raise CollectionError("Native build patch set differs from reviewed patch policy")
        patch_text = []
        for name, content in changes.items():
            if read_regular(base / "inputs/patched" / name) != content:
                raise CollectionError("Preserved patch postimage mismatch")
            patch_text += list(difflib.unified_diff(archive.read(name).decode().splitlines(True), content.decode().splitlines(True),
                                                  fromfile="a/" + name, tofile="b/" + name))
        if read_regular(base / "inputs/patches/speakcity-controlled-build.patch") != "".join(patch_text).encode():
            raise CollectionError("Preserved patch recipe differs from reviewed source changes")
        expected_data = expected_controlled_data(archive)
        copied_data = {name: digest(archive.read(name)) for name in archive.files
                       if name.startswith(("espeak-ng-data/lang/", "espeak-ng-data/voices/!v/"))}
    expected_inputs = {"archives/" + filename, "recipes/native_driver.py", "recipes/docs/THIRD_PARTY.md",
                       "local-adapter/espeakng_loader/__init__.py", "local-adapter/METADATA", "local-adapter/WHEEL",
                       "patches/speakcity-controlled-build.patch", "patches/changes.json"}
    expected_inputs |= {"recipes/" + name for name in CONTROLLED_RECIPE_FILES}
    expected_inputs |= {"patched/" + name for name in changes}
    if set(inputs) != expected_inputs:
        raise CollectionError("Unexpected or missing controlled source/recipe input files")
    links = {"src/include/espeak/speak_lib.h": "../espeak-ng/speak_lib.h"}
    patch_record = json.loads(read_regular(base / "inputs/patches/changes.json"))
    if (m.get("materialized_symlinks") != links or patch_record.get("materialized_symlinks") != links
            or patch_record.get("patches") != patches or patch_record.get("modified_at_utc") != m.get("created_at_utc")):
        raise CollectionError("Source extraction/modification record differs")
    for name, text in {"espeakng_loader/__init__.py": LOCAL_LOADER_SOURCE, "METADATA": LOCAL_LOADER_METADATA, "WHEEL": LOCAL_LOADER_WHEEL}.items():
        if read_regular(base / "inputs/local-adapter" / name) != text.encode():
            raise CollectionError("Local loader adapter source/metadata differs from reviewed source")
    build = m.get("build", {})
    allowed_toolchains = {("Visual Studio 17 2022", "v143,host=x64"), ("Visual Studio 18 2026", "v145,host=x64")}
    if (build.get("flags") != CONTROLLED_FLAGS or (build.get("generator"), build.get("toolset")) not in allowed_toolchains
            or build.get("architecture") != "x64"
            or build.get("configuration") != "Release" or build.get("target") != "data"
            or build.get("optional_dependencies") != []):
        raise CollectionError("Unreviewed CMake build options/toolchain/dependencies")
    cache = {}
    for line in read_regular(base / "evidence/CMakeCache.txt").decode("utf-8-sig").splitlines():
        match = re.match(r"([^#/:][^:]*):[^=]+=(.*)$", line)
        if match:
            cache[match[1]] = match[2]
    for key, value in CONTROLLED_FLAGS.items():
        if cache.get(key) != value:
            raise CollectionError(f"Effective native CMake cache differs: {key}")
    for key, value in {"CMAKE_GENERATOR": build["generator"], "CMAKE_GENERATOR_PLATFORM": "x64", "CMAKE_GENERATOR_TOOLSET": build["toolset"]}.items():
        if cache.get(key) != value:
            raise CollectionError(f"Effective native toolchain differs: {key}")
    configuration = read_regular(base / "evidence/config.h").decode()
    for key in ("USE_ASYNC", "USE_KLATT", "USE_LIBPCAUDIO", "USE_LIBSONIC", "USE_MBROLA", "USE_SPEECHPLAYER"):
        if f"#define {key} 0" not in configuration:
            raise CollectionError(f"Native feature was not disabled: {key}")
    compiler = read_regular(base / "evidence/CMakeCCompiler.cmake").decode("utf-8-sig")
    if 'set(CMAKE_C_COMPILER_ID "MSVC")' not in compiler or 'set(CMAKE_C_SIZEOF_DATA_PTR "8")' not in compiler:
        raise CollectionError("Native build compiler is not MSVC x64")
    for name in ("cmake", "msvc", "msbuild", "windows_sdk", "powershell", "python"):
        if not isinstance(m.get("tools", {}).get(name), str) or not m["tools"][name].strip():
            raise CollectionError(f"Missing actual tool version: {name}")
    if f'set(CMAKE_C_COMPILER_VERSION "{m["tools"]["msvc"]}")' not in compiler:
        raise CollectionError("Recorded MSVC version differs from compiler evidence")
    if (read_regular(base / "evidence/cmake-version.txt").decode("utf-8-sig").strip() != m["tools"]["cmake"]
            or read_regular(base / "evidence/msbuild-version.txt").decode("utf-8-sig").strip() != m["tools"]["msbuild"]):
        raise CollectionError("Tool versions differ from command evidence")
    for name in ("espeak-ng.vcxproj", "espeak-ng-bin.vcxproj", "ucd.vcxproj"):
        project = read_regular(base / "evidence" / name).decode("utf-8-sig")
        if "<RuntimeLibrary>MultiThreaded</RuntimeLibrary>" not in project or "MultiThreadedDLL" in project:
            raise CollectionError(f"Actual generated MSVC project did not select /MT: {name}")
        if name == "espeak-ng.vcxproj" and f'<WindowsTargetPlatformVersion>{m["tools"]["windows_sdk"]}</WindowsTargetPlatformVersion>' not in project:
            raise CollectionError("Actual Windows SDK differs from the build record")
    logs = ["vswhere.json", "cmake-version.txt", "configure.log", "msbuild-version.txt", "build.log", "install.log",
            "dumpbin-exports.txt", "dumpbin-dependents.txt", "smoke-isolated.log", "smoke-installed.log"]
    commands = json.loads(read_regular(base / "evidence/commands.json"))
    if not isinstance(commands, list) or [row.get("log") for row in commands] != logs or any(row.get("exit_code") != 0 for row in commands):
        raise CollectionError("Native build command sequence/exit evidence is incomplete")
    for row in commands:
        if not isinstance(row.get("argv"), list) or not row["argv"] or row.get("cwd") != str(base / "work"):
            raise CollectionError("Native build command invocation is unrecorded or inconsistent")
        if row["log"].startswith("smoke-") and row.get("system_only_path") is not True:
            raise CollectionError("Native smoke did not use an isolated system-only DLL search path")
        # A successful smoke process normally has no stdout; empty smoke logs
        # are valid, but both JSON results must exist and pass checks below.
        read_regular(base / "evidence" / row["log"], MAX_DOWNLOAD)
    toolchain = dict(line.split("=", 1) for line in read_regular(base / "evidence/toolchain.txt").decode("utf-8-sig").splitlines())
    tool_files = m.get("tool_files", {})
    if set(tool_files) != {"cmake", "cl", "dumpbin", "msbuild"}:
        raise CollectionError("Actual native tool executables/hashes were not recorded")
    for record in tool_files.values():
        if set(record) != {"path", "sha256"} or not Path(record["path"]).is_absolute() or not re.fullmatch(r"[0-9a-f]{64}", record["sha256"]):
            raise CollectionError("Invalid native tool identity")
    if (Path(toolchain.get("CMAKE_VS_MSBUILD_COMMAND", "")) != Path(tool_files["msbuild"]["path"])
            or toolchain.get("CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION") != m["tools"]["windows_sdk"]):
        raise CollectionError("Native tool selection differs from CMake generator evidence")
    recorded_compiler = re.search(r'^set\(CMAKE_C_COMPILER "([^"\r\n]+)"\)', compiler, re.M)
    if not recorded_compiler or Path(recorded_compiler[1]) != Path(tool_files["cl"]["path"]):
        raise CollectionError("Compiler executable differs from CMake compiler evidence")
    by_log = {row["log"]: row["argv"] for row in commands}
    cmake = tool_files["cmake"]["path"]
    work = base / "work"
    expected_commands = {
        "cmake-version.txt": [cmake, "--version"],
        "configure.log": [cmake, "-S", str(work / "source"), "-B", str(work / "cmake-build"),
                          "-G", build["generator"], "-A", "x64", "-T", build["toolset"],
                          "-DCMAKE_GENERATOR_INSTANCE=" + cache.get("CMAKE_GENERATOR_INSTANCE", ""),
                          "-DCMAKE_INSTALL_PREFIX=" + (work / "install").as_posix(),
                          *[f"-D{k}={v}" for k, v in sorted(CONTROLLED_FLAGS.items())]],
        "msbuild-version.txt": [tool_files["msbuild"]["path"], "-version", "-nologo"],
        "build.log": [cmake, "--build", str(work / "cmake-build"), "--config", "Release", "--target", "data", "--parallel", "2"],
        "install.log": [cmake, "--install", str(work / "cmake-build"), "--config", "Release"],
        "dumpbin-exports.txt": [tool_files["dumpbin"]["path"], "/nologo", "/exports", str(work / "install/bin/espeak-ng.dll")],
        "dumpbin-dependents.txt": [tool_files["dumpbin"]["path"], "/nologo", "/dependents", str(work / "install/bin/espeak-ng.dll")],
        "smoke-isolated.log": [sys.executable, "-I", "-B", str(base / "inputs/recipes/native_driver.py"), "smoke", str(ROOT),
                               str(work / "isolated runtime layout with spaces/espeakng_loader"), str(base / "evidence/smoke-isolated.json")],
        "smoke-installed.log": [sys.executable, "-I", "-B", str(base / "inputs/recipes/native_driver.py"), "smoke", str(ROOT),
                                str(package), str(base / "evidence/smoke.json")],
    }
    for name, argv in expected_commands.items():
        if by_log[name] != argv:
            raise CollectionError(f"Native build command differs from reviewed recipe: {name}")
    actual = controlled_files(package, skip_marker=True)
    if actual != m.get("installed_files"):
        raise CollectionError("Installed native DLL/data/wrapper file set or hashes changed")
    if set(actual) != expected_data | {"espeak-ng.dll", "__init__.py"}:
        raise CollectionError("Installed data is incomplete or has unexpected native/wrapper files")
    if any(actual[name]["sha256"] != sha for name, sha in copied_data.items()):
        raise CollectionError("Installed language/voice data differs from the pinned unmodified source")
    if read_regular(package / "__init__.py") != LOCAL_LOADER_SOURCE.encode():
        raise CollectionError("Installed adapter is not the locally written compatible adapter")
    dll = read_regular(package / "espeak-ng.dll", MAX_DOWNLOAD)
    if digest(dll) == DLL_SHA:
        raise CollectionError("The old unproven prebuilt DLL is not a controlled build")
    pe = pe_identity(dll)
    if not pe["dll"] or not REQUIRED_ESPEAK_EXPORTS.issubset(pe["exports"]) or pe != m.get("pe"):
        raise CollectionError("Native DLL lacks required eSpeak ABI or its import/export record differs")
    data = {name: info["sha256"] for name, info in actual.items() if name.startswith("espeak-ng-data/")}
    if m.get("data_tree_sha256") != tree_digest(data):
        raise CollectionError("Generated-data tree digest mismatch")
    produced = m.get("produced_files", {})
    if produced != {k: v for k, v in actual.items() if k != "__init__.py"}:
        raise CollectionError("Installed DLL/data do not match this build's outputs")
    for name in ("smoke.json", "smoke-isolated.json"):
        smoke = json.loads(read_regular(base / "evidence" / name))
        if (smoke.get("status") != "passed" or smoke.get("version") != ESPEAK_VERSION
                or smoke.get("dll_sha256") != digest(dll) or smoke.get("data_tree_sha256") != tree_digest(data)
                or smoke.get("adapter_sha256") != digest(LOCAL_LOADER_SOURCE.encode())
                or smoke.get("loaded_data_path_matches") is not True
                or smoke.get("isolated_copy_test") is not True):
            raise CollectionError("Required native/phonemizer isolated-layout smoke evidence missing or unbound")
        for kind in ("direct_api", "phonemizer"):
            for voice in ("en-us", "en-gb"):
                phonemes = smoke.get(kind, {}).get(voice)
                if not isinstance(phonemes, str) or not phonemes.strip() or phonemes.isascii():
                    raise CollectionError("Native/phonemizer smoke must produce IPA for both English voices")
    info_dir = site / CONTROLLED_DIST_INFO
    plain_path(info_dir)
    matches = [d for d in metadata.distributions() if canonical(d.metadata.get("Name", "")) == "espeakng-loader"]
    if len(matches) != 1 or matches[0].version != CONTROLLED_VERSION:
        raise CollectionError("Old or ambiguous loader distribution metadata remains")
    if read_regular(info_dir / "METADATA") != LOCAL_LOADER_METADATA.encode() or read_regular(info_dir / "WHEEL") != LOCAL_LOADER_WHEEL.encode():
        raise CollectionError("Local adapter distribution identity differs")
    for sibling in (site / "espeakng_loader.libs", site / "espeakng_loader.dylibs", site / "espeakng_loader.py"):
        if sibling.exists():
            raise CollectionError("Unproven loader sibling binary/module remains")
    return {"manifest": m, "manifest_path": selected, "manifest_sha256": digest(raw), "package": package}


class Collector:
    def __init__(self, output: Path, sources: Path):
        self.output, self.sources = output, sources
        self.blockers: list[str] = []
        self.warnings: list[str] = []
        self.distributions = {}
        self.inventory = []
        self.artifacts = []
        self.evidence = {}
        self.snapshot_paths = {}
        self.controlled = None

    def stage(self, label: str, action) -> None:
        print(f"[notices] {label}", flush=True)
        try:
            action()
        except Exception as exc:
            # Do not convert a partial failure into success. Continue independent
            # stages to leave useful evidence, then exit nonzero with all blockers.
            self.blockers.append(f"{label}: {type(exc).__name__}: {str(exc)[:1500]}")

    def download(self, url: str, destination: Path | None, sha: str | None = None,
                 *, basis: str = "audit-pinned SHA-256", limit: int = MAX_DOWNLOAD, size: int | None = None) -> bytes:
        data = fetch(url, sha, limit=limit, size=size)
        if destination is not None:
            put(destination, data)
        location = None
        if destination is not None:
            base = self.sources if destination.is_relative_to(self.sources) else self.output
            location = ("sources/" if base == self.sources else "notices/") + destination.relative_to(base).as_posix()
        self.artifacts.append({"url": url, "sha256": digest(data), "bytes": len(data),
                               "verification": basis, "saved_as": location,
                               "purpose": "collection" if destination else "verification input only; bytes not bundled"})
        return data

    def installed_inventory(self) -> None:
        required = {}
        for line in read_regular(ROOT / "speech/requirements.txt").decode("utf-8").splitlines():
            line = line.split("#", 1)[0].strip()
            if not line:
                continue
            match = re.fullmatch(r"([A-Za-z0-9_.-]+)==([A-Za-z0-9_.+!-]+)", line)
            if not match:
                raise CollectionError("requirements.txt has an unreviewed/non-exact requirement")
            name, version = canonical(match[1]), match[2]
            if name in required:
                raise CollectionError(f"Duplicate requirement: {name}")
            required[name] = version
        for name, version in PINS.items():
            if required.get(name) != version:
                self.blockers.append(f"Reviewed GPL-related pin changed: {name} must be {version}")
        venv = Path(sys.prefix).resolve()
        for dist in sorted(metadata.distributions(), key=lambda d: canonical(d.metadata.get("Name", ""))):
            info = dist.metadata
            name, version = info.get("Name", ""), dist.version
            key = canonical(name)
            if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", key) or not re.fullmatch(r"[A-Za-z0-9_.+!-]+", version):
                raise CollectionError("Invalid installed distribution name/version")
            if key in self.distributions:
                self.blockers.append(f"Duplicate installed distribution: {key}")
                continue
            self.distributions[key] = dist
            row = {"name": name, "normalized_name": key, "version": version,
                   "required_version": required.get(key), "license_expression": info.get("License-Expression"),
                   "license_metadata": info.get("License"), "license_classifiers": [s for s in info.get_all("Classifier", []) if s.startswith("License ::")],
                   "declared_license_files": info.get_all("License-File", []), "home_page": info.get("Home-page"),
                   "project_urls": info.get_all("Project-URL", []), "requires_dist": info.get_all("Requires-Dist", []),
                   "notice_files": [], "notice_errors": [], "record_available": dist.files is not None,
                   "scope": "installed build environment; not proof this distribution is frozen"}
            self.inventory.append(row)
            base = Path(dist.locate_file("")).resolve()
            if not base.is_relative_to(venv):
                self.blockers.append(f"Distribution outside the build venv: {key}")
                continue
            records = list(dist.files or [])
            candidates = {str(p).replace("\\", "/"): Path(dist.locate_file(p)) for p in records if is_notice(str(p))}
            # Also handle unrecorded PEP 639 license directories, and metadata
            # directories available when RECORD/SOURCES.txt is absent. No imports.
            info_dir = getattr(dist, "_path", None)
            metadata_dirs = set()
            if isinstance(info_dir, Path) and info_dir.is_dir():
                metadata_dirs.add(info_dir)
            for p in records:
                if str(p).endswith((".dist-info/METADATA", ".egg-info/PKG-INFO")):
                    metadata_dirs.add(Path(dist.locate_file(p)).parent)
            # Without an identifiable own metadata directory, fall back to the
            # strict original gate and demand the declared licence everywhere.
            declared_dirs = own_metadata_directories(metadata_dirs, base, key, info_dir) or set(metadata_dirs)
            satisfied, rejected = set(), set()
            for directory in metadata_dirs:
                plain_path(directory)
                if not directory.resolve().is_relative_to(venv):
                    raise CollectionError(f"Metadata outside build venv: {key}")
                for path in directory.rglob("*"):
                    if path.is_file() and is_notice(path.relative_to(directory).as_posix()):
                        candidates[directory.name + "/" + path.relative_to(directory).as_posix()] = path
                if directory not in declared_dirs:
                    continue
                for declared in row["declared_license_files"]:
                    if declared in satisfied:
                        continue
                    try:
                        relative = member_path(declared)
                    except CollectionError as exc:
                        row["notice_errors"].append(str(exc))
                        rejected.add(declared)
                        continue
                    for path in (directory / Path(*relative.parts), directory / "licenses" / Path(*relative.parts)):
                        if path.is_file():
                            satisfied.add(declared)
                            candidates[directory.name + "/" + path.relative_to(directory).as_posix()] = path
            # Only the distribution's own metadata directory has to provide a
            # declared licence. Reporting after the scan gives one accurate error
            # per declaration instead of one per candidate directory, and stays
            # silent when no own metadata directory could be inspected at all.
            if declared_dirs:
                for declared in row["declared_license_files"]:
                    if declared not in satisfied and declared not in rejected:
                        row["notice_errors"].append(f"Declared License-File missing: {declared}")
            seen_sources = set()
            for original, path in sorted(candidates.items()):
                try:
                    plain_path(path)
                    resolved = path.resolve()
                    if not resolved.is_relative_to(venv):
                        raise CollectionError("Notice resolves outside the build venv")
                    if resolved in seen_sources:
                        continue
                    seen_sources.add(resolved)
                    content = read_regular(path)
                    target = Path("packages", f"{key}-{version}") / safe_record_destination(original)
                    put(self.output / target, content)
                    row["notice_files"].append({"installed_record": original, "path": target.as_posix(),
                                                "sha256": digest(content), "bytes": len(content)})
                except (CollectionError, OSError) as exc:
                    row["notice_errors"].append(f"{original}: {exc}")
            if row["notice_errors"]:
                self.blockers.append(f"Unreadable/missing declared notices for {key}; see inventory.json")
            if not row["notice_files"]:
                self.warnings.append(f"No installed license/notice files found for {key}=={version}; metadata is not a substitute")
            if not row["record_available"]:
                self.warnings.append(f"No file inventory for {key}; review package-level/vendored notices manually")
        for name, version in required.items():
            dist = self.distributions.get(name)
            expected_version = CONTROLLED_VERSION if name == "espeakng-loader" and self.controlled else version
            if dist is None or dist.version != expected_version:
                self.blockers.append(f"Build dependency mismatch: require {name}=={expected_version}; installed {dist.version if dist else 'MISSING'}")
        if "pyinstaller" not in self.distributions:
            self.blockers.append("Build venv has no PyInstaller inventory; run in the actual freezing environment")
        # Copy just the interpreter's legal notice when present, NOT its files or
        # an interpreter/venv archive. Native/stdlib dependencies still need review.
        license_path = Path(sys.base_prefix) / "LICENSE.txt"
        if license_path.is_file():
            put(self.output / "interpreter/CPython-LICENSE.txt", read_regular(license_path))
        else:
            self.warnings.append("CPython LICENSE.txt not found at sys.base_prefix; include the shipped interpreter's actual notices")

    def source_notices(self, archive: Archive, component: str) -> None:
        for name in sorted(archive.files):
            # Root READMEs carry license/attribution statements (notably eSpeak
            # and Sonic); retain them verbatim alongside standalone license text.
            if is_notice(name) or name in ("README", "README.md", "README.rst"):
                target = safe_record_destination(name)
                put(self.output / "upstream" / component / target, archive.read(name, MAX_NOTICE))

    def pypi_json(self, name: str) -> dict:
        version = PINS[name]
        url = f"https://pypi.org/pypi/{name}/{version}/json"
        raw = self.download(url, self.sources / "evidence/pypi" / f"{name}-{version}.json",
                            basis="HTTPS PyPI release JSON snapshot; not a signed attestation", limit=2 * MIB)
        data = json.loads(raw)
        if canonical(data["info"]["name"]) != name or data["info"]["version"] != version:
            raise CollectionError("PyPI returned a different release")
        return data

    def pypi_artifact(self, item: dict, destination: Path | None, audit_sha: str | None = None) -> bytes:
        filename, url = item["filename"], item["url"]
        if item.get("yanked"):
            raise CollectionError(f"PyPI artifact is yanked: {filename}")
        member_path(filename)
        if "/" in filename or urllib.parse.urlsplit(url).path.rsplit("/", 1)[-1] != filename:
            raise CollectionError("Invalid PyPI artifact filename/URL")
        sha = item.get("digests", {}).get("sha256")
        if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{64}", sha):
            raise CollectionError("PyPI artifact lacks published SHA-256")
        if audit_sha is not None and sha != audit_sha:
            raise CollectionError("PyPI digest no longer matches the audited release pin")
        return self.download(url, destination, sha, basis="published PyPI SHA-256 and byte count" + (" + audit pin" if audit_sha else ""), size=item["size"])

    def sdist(self, name: str, data: dict) -> None:
        candidates = [item for item in data["urls"] if item["packagetype"] == "sdist"]
        if len(candidates) != 1:
            raise CollectionError(
                f"PYPI_SDIST_UNAVAILABLE: {name}=={PINS[name]} has {len(candidates)} sdists in its versioned PyPI JSON; "
                "exactly one verifiable source artifact is required. A wheel/GitHub snapshot is NOT substituted for a PyPI sdist. "
                "For loader 0.2.4 the upstream publish recipe removes dist/*.tar.gz. Obtain the exact sdist from the publisher "
                "or approve a reviewed alternative source/rebuild policy; do not bypass the failed build."
            )
        item = candidates[0]
        # Validate the untrusted filename before constructing a local path.
        member_path(item["filename"])
        if "/" in item["filename"]:
            raise CollectionError("Invalid sdist filename")
        path = self.sources / "upstream" / name / item["filename"]
        raw = self.pypi_artifact(item, path, PHONEMIZER_SDIST_SHA if name == "phonemizer" else None)
        with Archive(raw, strip_root=True) as archive:
            self.source_notices(archive, name)
            if name == "phonemizer":
                archive.read("pyproject.toml", MAX_NOTICE)
                archive.read("setup.cfg", MAX_NOTICE)
                archive.read("LICENSE", MAX_NOTICE)
                self.compare_installed_source(name, "phonemizer", archive)

    def compare_installed_source(self, name: str, package: str, archive: Archive) -> None:
        dist = self.distributions.get(name)
        if dist is None or dist.version != PINS[name]:
            raise CollectionError(f"Cannot bind source to missing/different installed {name}")
        expected = {n[len(package) + 1:]: archive.read(n) for n in archive.files if n.startswith(package + "/")}
        actual = self.installed_payload(dist, package)
        if set(expected) != set(actual):
            raise CollectionError(f"{name} installed source/data file set differs from the sdist; preserve/review patches")
        for n, content in expected.items():
            normalize = (lambda b: b.replace(b"\r\n", b"\n")) if n.endswith(".py") else (lambda b: b)
            if normalize(content) != normalize(actual[n]):
                raise CollectionError(f"{name} installed source differs from sdist: {n}; preserve/review patches")
        self.evidence[name] = {"installed_source_matches_sdist": True, "files_compared": len(expected),
                               "comparison": "byte-for-byte; CRLF normalized only for .py files"}

    def installed_payload(self, dist, package: str) -> dict[str, bytes]:
        path = Path(dist.locate_file(package))
        plain_path(path)
        if not path.resolve().is_relative_to(Path(sys.prefix).resolve()) or not path.is_dir():
            raise CollectionError(f"Missing/non-venv package directory: {package}")
        result = {}
        total = 0
        for p in sorted(path.rglob("*")):
            plain_path(p)
            rel = p.relative_to(path)
            if "__pycache__" in rel.parts or p.suffix == ".pyc" or p.is_dir():
                continue
            content = read_regular(p, MAX_DOWNLOAD)
            total += len(content)
            if total > MAX_EXPANDED or len(result) >= 20000:
                raise CollectionError("Installed package payload exceeds comparison limit")
            result[rel.as_posix()] = content
        return result

    def snapshot(self, name: str) -> None:
        url, filename, sha = SOURCE_ARCHIVES[name]
        # Candidate labeling is deliberate until the immutable native provenance
        # gate is resolved. Full source/data/build scripts remain in each archive.
        path = self.sources / "source-candidates" / filename
        raw = self.download(url, path, sha)
        self.snapshot_paths[name] = path
        with Archive(raw, strip_root=True) as archive:
            self.source_notices(archive, name)
            relevant = {
                "espeakng-loader": [".github/workflows/build.yml", ".github/workflows/pypi.yml", "build.sh", "hatch_build.py", "pyproject.toml", "uv.lock", "README.md", "src/espeakng_loader/__init__.py"],
                "espeak-ng": ["CMakeLists.txt", "cmake/deps.cmake", "cmake/config.cmake", "cmake/data.cmake", "src/libespeak-ng/CMakeLists.txt", "README.md", "docs/building.md"],
                "sonic": ["README", "sonic.c", "sonic.h"],
            }[name]
            for member in relevant:
                put(self.sources / "evidence/build-recipes" / name / safe_record_destination(member), archive.read(member, MAX_NOTICE))
            if name == "espeakng-loader":
                build = archive.read(".github/workflows/build.yml").decode()
                pypi = archive.read(".github/workflows/pypi.yml").decode()
                project = archive.read("pyproject.toml").decode()
                if f"-b {ESPEAK_VERSION} https://github.com/espeak-ng/espeak-ng" not in build or 'version = "0.2.4"' not in project:
                    raise CollectionError("Audited loader build/source version recipe changed")
                if "releases/latest/download/" not in pypi or "rm -f dist/*.tar.gz" not in pypi:
                    raise CollectionError("Audited loader publication recipe changed; re-review provenance")
            elif name == "espeak-ng":
                if f"VERSION {ESPEAK_VERSION}" not in archive.read("CMakeLists.txt").decode():
                    raise CollectionError("eSpeak source version mismatch")
                deps = archive.read("cmake/deps.cmake").decode()
                if SONIC_COMMIT not in deps or "https://github.com/waywardgeek/sonic.git" not in deps:
                    raise CollectionError("eSpeak Sonic dependency pin changed")

    def native_identity(self, pypi: dict) -> None:
        dist = self.distributions.get("espeakng-loader")
        if dist is None or dist.version != "0.2.4":
            raise CollectionError("Require installed espeakng-loader 0.2.4 to identify native payload")
        items = [f for f in pypi["urls"] if f["filename"] == LOADER_WHEEL and f["packagetype"] == "bdist_wheel"]
        if len(items) != 1:
            raise CollectionError("Audited Windows x64 wheel missing/ambiguous on PyPI")
        # Verification only: do not store native binaries in the source bundle.
        raw = self.pypi_artifact(items[0], None, LOADER_WHEEL_SHA)
        with Archive(raw) as wheel:
            installed = self.installed_payload(dist, "espeakng_loader")
            expected = {n[len("espeakng_loader/"):]: wheel.read(n) for n in wheel.files if n.startswith("espeakng_loader/")}
            if set(installed) != set(expected) or any(installed[n] != expected[n] for n in expected):
                raise CollectionError("Installed loader DLL/data/source differs from the audited PyPI wheel; no provenance claim is possible")
            dll = expected["espeak-ng.dll"]
            data_hashes = {n: digest(b) for n, b in expected.items() if n.startswith("espeak-ng-data/")}
            if digest(dll) != DLL_SHA or b"1.52.0\x00" not in dll or len(data_hashes) != 364 or tree_digest(data_hashes) != DATA_SHA:
                raise CollectionError("Audited native version/DLL/data-tree identity mismatch")
            with Archive(read_regular(self.snapshot_paths["espeakng-loader"], MAX_DOWNLOAD), strip_root=True) as source:
                if source.read("src/espeakng_loader/__init__.py") != expected["__init__.py"].replace(b"\r\n", b"\n"):
                    raise CollectionError("Loader repository source does not match wheel Python source")
            put(self.sources / "installed-reference/espeakng-loader/__init__.py", expected["__init__.py"])
        checksums = self.download(f"{RELEASE}/checksum.txt", self.sources / "evidence/loader-release-checksum.txt", RELEASE_FILES["checksum.txt"], limit=MAX_NOTICE)
        published = {}
        for line in checksums.decode("ascii").splitlines():
            filename, sha = line.split()
            if filename in published:
                raise CollectionError("Duplicate publisher checksum entry")
            published[filename] = sha
        for filename in ("espeak-ng-libs-windows-x86_64.tar.gz", "espeak-ng-data.tar.gz"):
            if published.get(filename) != RELEASE_FILES[filename]:
                raise CollectionError("Native publisher checksums changed; re-audit instead of refreshing pins")
            content = self.download(f"{RELEASE}/{filename}", None, published[filename], basis="publisher checksum.txt + audit pin")
            with Archive(content) as release:
                if "libs-windows" in filename:
                    for member in ("espeak-ng-libs/bin/espeak-ng.dll", "espeak-ng-libs/lib/espeak-ng.dll"):
                        if digest(release.read(member)) != DLL_SHA:
                            raise CollectionError("PyPI DLL does not match audited native release asset")
                elif payload_hashes(release, "espeak-ng-data/") != data_hashes:
                    raise CollectionError("PyPI data differs from audited native release data")
        record = {"observed_version_string": ESPEAK_VERSION, "recipe_source_commit": ESPEAK_COMMIT,
                  "source_declared_license": "GPL-3.0-or-later; additional component notices apply",
                  "license_evidence": "eSpeak source README.md and COPYING* at recipe_source_commit",
                  "sonic_source_declared_license": "Apache-2.0 at sonic_recipe_commit; ambient dependency choice unresolved",
                  "loader_source_commit": LOADER_COMMIT, "sonic_recipe_commit": SONIC_COMMIT,
                  "installed_matches_pypi_wheel": True, "pypi_wheel_sha256": LOADER_WHEEL_SHA,
                  "dll_matches_publisher_release": True, "dll_sha256": DLL_SHA,
                  "data_matches_publisher_release": True, "data_files": data_hashes, "data_tree_sha256": DATA_SHA,
                  "data_tree_hash_algorithm": "SHA256 of UTF-8 newline-joined '<file sha256>  <path>' sorted by path, no trailing newline",
                  "immutable_build_to_source_mapping": "UNRESOLVED; mutable latest/clobber pipeline, no complete build record",
                  "source_snapshot_status": "candidate; source version evidence is not build attestation"}
        self.evidence["espeak-ng"] = record
        put_json(self.sources / "evidence/espeak-native-identity.json", record)

    def controlled_native_sources(self) -> None:
        checked = validate_controlled_build()
        manifest = checked["manifest"]
        base = checked["manifest_path"].parent
        for destination in (self.output, self.sources):
            if destination.is_relative_to(base) or base.is_relative_to(destination):
                raise CollectionError("Notice/source outputs must not overlap controlled native build inputs/evidence")
        for folder, records in (("inputs", manifest["input_files"]), ("evidence", manifest["evidence_files"])):
            for relative in sorted(records):
                put(self.sources / "native-espeak" / folder / relative,
                    read_regular(base / folder / relative, MAX_DOWNLOAD))
        put(self.sources / "native-espeak/manifest.json", read_regular(checked["manifest_path"]))
        _, filename, sha = SOURCE_ARCHIVES["espeak-ng"]
        with Archive(read_regular(base / "inputs/archives" / filename, MAX_DOWNLOAD), strip_root=True) as archive:
            self.source_notices(archive, "espeak-ng")
        self.artifacts.append({"url": SOURCE_ARCHIVES["espeak-ng"][0], "sha256": sha,
                               "saved_as": "sources/native-espeak/inputs/archives/" + filename,
                               "verification": "pinned official archive; controlled build with exact recorded patch postimages and recipes"})
        self.evidence["espeak-ng"] = {"controlled_build_validated": True,
            "source_commit": ESPEAK_COMMIT, "version": ESPEAK_VERSION,
            "source_declared_license": "GPL-3.0-or-later; additional component notices apply",
            "manifest_sha256": checked["manifest_sha256"], "manifest_path": "native-espeak/manifest.json",
            "dll_sha256": manifest["installed_files"]["espeak-ng.dll"]["sha256"],
            "data_tree_sha256": manifest["data_tree_sha256"], "replaced": manifest["replaced"],
            "optional_dependencies": [], "adapter": "locally written, not upstream espeakng-loader wrapper",
            "limitations": "Locally recorded evidence, not signed/reproducible-build attestation or legal clearance"}
        self.controlled = checked

    def local_sources(self) -> None:
        required = {"speech/worker.py", "speech/windows_compat.py", "speech/download_assets.py", "speech/speech_worker.spec",
                    "speech/requirements.txt", "speech/assets-manifest.json", "speech/README.md",
                    "build/windows.ps1", "build/native_espeak.ps1", "build/installer.iss", "build/collect_notices.py", "docs/THIRD_PARTY.md",
                    "tests/speech_worker_test.py", "src/SpeakCity/SpeechWorkerClient.cs"}
        # Include any additional speech Python modules/hooks, never speech/models,
        # an installed environment, binaries, caches, logs, or learner recordings.
        excluded = {"models", ".venv", "venv", "__pycache__", "dist", "build", "out", "audit", ".git"}
        for path in (ROOT / "speech").rglob("*"):
            rel = path.relative_to(ROOT)
            if not (set(rel.parts) & excluded) and path.suffix in (".py", ".spec"):
                required.add(rel.as_posix())
        for path in ROOT.iterdir():
            if path.is_file() and is_notice(path.name):
                required.add(path.name)
        records = []
        for relative in sorted(required):
            content = read_regular(ROOT / relative)
            put(self.sources / "speakcity" / relative, content)
            records.append({"path": relative, "sha256": digest(content), "bytes": len(content)})
        put_json(self.sources / "speakcity/source-files.json", {"files": records, "scope": "speech adaptation, interface, tests, and build/install inputs; not the entire desktop application"})

    def model_notices(self) -> None:
        for destination, (url, sha) in NOTICE_DOWNLOADS.items():
            if self.controlled and destination.startswith("upstream/espeakng-loader/"):
                continue  # Local adapter contains no upstream loader wrapper.
            self.stage(f"upstream notice {destination}", lambda d=destination, u=url, s=sha: self.download(u, self.output / d, s, limit=MAX_NOTICE))
        manifest = json.loads(read_regular(ROOT / "speech/assets-manifest.json"))
        if manifest.get("whisper_revision") != WHISPER_REV:
            raise CollectionError("Whisper model manifest revision changed; review model notices/pins")
        info = {"weights_in_source_bundle": False, "asset_manifest": manifest,
                "models": [
                    {"name": "Kokoro v1.0 ONNX and voices-v1.0.bin", "declared_license": "Apache-2.0 (upstream model card)",
                     "source": "https://huggingface.co/hexgrad/Kokoro-82M", "code": "https://github.com/hexgrad/kokoro",
                     "conversion": "https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.1",
                     "notice_revision": KOKORO_DOC_REV, "notice_directory": "models/kokoro",
                     "note": "Model-card/voice documentation snapshot, not a claim the converted bytes were built at this documentation revision. Preserve the model card's CC BY attribution table and review the whole voices file, not only the two selected voices."},
                    {"name": "Whisper base.en / CTranslate2 conversion", "declared_license": "MIT",
                     "source": "https://github.com/openai/whisper", "conversion": f"https://huggingface.co/Systran/faster-whisper-base.en/tree/{WHISPER_REV}",
                     "license_text_revision": "6dea21fd7f7253bfe450f1e2512a0fe47ee2d258", "notice_directory": "models/whisper"},
                    {"name": "Silero VAD bundled by faster-whisper", "declared_license": "MIT",
                     "source": "https://github.com/snakers4/silero-vad", "consumer_source": "https://github.com/SYSTRAN/faster-whisper/tree/v1.2.1",
                     "license_text_revision": "fba061dc5559f696e62171e9a0741782b0fdc23c", "notice_directory": "models/silero-vad",
                     "note": "License snapshot only; final vendored ONNX origin/version must be reviewed against the actual faster-whisper wheel."}],
                "loader_license_note": "MIT LICENSE was added at 0ddc87a on 2025-11-01, after the 2025-01-17 wheel. Preserve it as later-upstream evidence only; it neither licenses the bundled GPL eSpeak code as MIT nor proves release-time wrapper licensing."}
        if self.controlled:
            info["loader_license_note"] = "The upstream loader wrapper/wheel is replaced, not relicensed. The locally written adapter source and native GPL/component notices are in native-espeak/ and upstream/espeak-ng/. SPEAKCITY adaptation licensing still requires review."
        put_json(self.output / "model-sources.json", info)

    def finish(self) -> int:
        if not self.controlled:
            self.blockers.append(PROVENANCE_BLOCKER)
        self.warnings.extend([
            "Review final frozen/native payload versus this overinclusive venv inventory, including PyAV/FFmpeg and every vendored DLL/codec.",
            "Pin actual Windows-only transitives and PyInstaller/hooks/toolchain versions; retain final freeze/build manifests.",
            "Resolve SPEAKCITY adaptation/combined-work licensing, change notices, recipient source access, and any installation-information obligations before distribution.",
        ])
        blocked = bool(self.blockers)
        put_json(self.output / "inventory.json", {"schema_version": 1, "scope": "all installed distributions in the build venv, including build-only tools; not a complete final-payload SBOM",
                                                  "packages": self.inventory,
                                                  "non_distribution_native_evidence": self.evidence.get("espeak-ng"),
                                                  "warnings": self.warnings})
        summary = ("SPEAKCITY third-party collection: " + ("BLOCKED / INCOMPLETE" if blocked else "COLLECTED; RELEASE REVIEW REQUIRED") + "\n\n"
                   "See collection-manifest.json for all gates, inventory.json for package notices, and model-sources.json for model links.\n"
                   "Source snapshots and build recipes are preserved, but unresolved provenance is never represented as GPL compliance.\n"
                   "No model weights, complete venv, interpreter, or verification-only native archives are included in the source directory.\n"
                   "Read speakcity/docs/THIRD_PARTY.md in the source companion. This is not a full legal/security audit.\n\n"
                   + "\n".join("BLOCKER: " + b for b in self.blockers) + "\n")
        put(self.output / "README.txt", summary.encode("utf-8"))
        put(self.sources / "README.txt", summary.encode("utf-8"))
        report = {"schema_version": 1, "status": "blocked" if blocked else "collected-review-required", "legal_clearance": False,
                  "created_at_utc": datetime.now(timezone.utc).isoformat(),
                  "environment": {"python": sys.version, "implementation": sys.implementation.name, "machine": platform.machine(),
                                  "platform": sys.platform, "venv": sys.prefix != sys.base_prefix},
                  "artifacts": self.artifacts, "source_evidence": self.evidence, "blockers": self.blockers, "warnings": self.warnings}
        put_json(self.output / "collection-manifest.json", report)
        put_json(self.sources / "collection-manifest.json", report)
        shutil.copytree(self.output, self.sources / "notices")
        # The inventory intentionally excludes itself, but covers every other
        # source-companion file, including scripts, notices, and raw archives.
        files = [{"path": p.relative_to(self.sources).as_posix(), "sha256": file_digest(p), "bytes": p.stat().st_size}
                 for p in sorted(self.sources.rglob("*")) if p.is_file()]
        put_json(self.sources / "file-manifest.json", {"algorithm": "sha256", "excludes": ["file-manifest.json"], "files": files})
        for blocker in self.blockers:
            print("BLOCKER: " + blocker, file=sys.stderr)
        print(f"[notices] {'BLOCKED' if blocked else 'Collected'}: {len(self.inventory)} distributions; {len(self.blockers)} blocking issue(s).")
        return 1 if blocked else 0

    def run(self) -> int:
        self.stage("validate controlled Windows native build and preserve matching sources", self.controlled_native_sources)
        self.stage("installed package inventory and notices", self.installed_inventory)
        self.stage("SPEAKCITY speech/build source", self.local_sources)
        pypi = {}
        # Only a fully validated local replacement retires the upstream wrapper's
        # absent-sdist gate. Invalid/missing manifests retain ALL historical gates.
        for name in (("phonemizer",) if self.controlled else PINS):
            self.stage(f"PyPI metadata {name}", lambda n=name: pypi.__setitem__(n, self.pypi_json(n)))
            if name in pypi:
                self.stage(f"exact PyPI sdist {name}", lambda n=name: self.sdist(n, pypi[n]))
        if not self.controlled:
            for name in SOURCE_ARCHIVES:
                self.stage(f"pinned source/build recipe {name}", lambda n=name: self.snapshot(n))
            if "espeakng-loader" in pypi and "espeakng-loader" in self.snapshot_paths:
                self.stage("bind installed Windows DLL/data to published artifacts", lambda: self.native_identity(pypi["espeakng-loader"]))
        self.stage("model/source notices and links", self.model_notices)
        return self.finish()


def prepare_destinations(output: Path, sources: Path) -> tuple[Path, Path]:
    paths = [p.absolute() for p in (output, sources)]
    for p in paths:
        plain_path(p)
    output, sources = [p.resolve() for p in paths]
    if output.is_relative_to(sources) or sources.is_relative_to(output):
        raise CollectionError("--output and --sources must be distinct, non-nested directories")
    for p in (output, sources):
        if ROOT.is_relative_to(p) or any(p.is_relative_to(ROOT / name) for name in ("speech", "src", "docs", "tests", "build")):
            raise CollectionError("Output directories cannot overlap checked-in source directories")
        if p.is_relative_to(Path(sys.prefix).resolve()):
            raise CollectionError("Output directories cannot be inside the build venv")
        if p.exists() and (not p.is_dir() or any(p.iterdir())):
            raise CollectionError("Use fresh/empty notice and source directories; stale/partial output is never merged or deleted")
    for p in (output, sources):
        p.mkdir(parents=True, exist_ok=True)
    return output, sources


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="Fresh/empty directory for installer notices")
    parser.add_argument("--sources", type=Path, required=True, help="Fresh/empty directory for the corresponding-source companion")
    args = parser.parse_args(argv)
    try:
        if sys.platform != "win32" or struct.calcsize("P") != 8 or platform.machine().lower() not in ("amd64", "x86_64"):
            raise CollectionError("Run on the actual Windows x64 build environment; cross-platform inventories are not release evidence")
        if sys.version_info[:2] != (3, 11) or sys.prefix == sys.base_prefix:
            raise CollectionError("Run with the Python 3.11 Windows speech build venv, not a system/interpreter-wide inventory")
        output, sources = prepare_destinations(args.output, args.sources)
        return Collector(output, sources).run()
    except (CollectionError, OSError, ValueError) as exc:
        print(f"NOTICE/SOURCE COLLECTION FAILED: {exc}. Do not compile/distribute the installer from this run.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())

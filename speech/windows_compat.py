"""Narrow compatibility adapter for CTranslate2 4.6.0 on modern setuptools.

CTranslate2 4.6.0's Windows __init__ imports removed pkg_resources solely to call
resource_filename(__name__, "") to locate its adjacent DLLs. The tested environment
pins setuptools 84.0.0, which no longer ships pkg_resources. Keep those tested
versions: before importing CTranslate2 on Windows only, provide that single
resource_filename operation when the real module is absent. No generic legacy
setuptools emulation, dynamic code, or arbitrary resource/path access.

This path requires verification in the final Windows build; Linux tests exercise
its constrained path resolution, not Windows DLL loading.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import types


def ctranslate2_resource_filename(package: str, resource: str) -> str:
    if package != "ctranslate2" or resource != "":
        raise RuntimeError("Unsupported legacy resource request.")
    spec = importlib.util.find_spec("ctranslate2")
    if spec is None or spec.origin is None:
        raise ImportError("CTranslate2 package is missing.")
    return str(Path(spec.origin).resolve().parent)


def prepare_ctranslate2_windows() -> bool:
    if sys.platform != "win32":
        return False
    if "pkg_resources" in sys.modules or importlib.util.find_spec("pkg_resources") is not None:
        return False
    adapter = types.ModuleType("pkg_resources")
    adapter.__doc__ = "SPEAKCITY's single-operation CTranslate2 4.6.0 Windows adapter."
    adapter.resource_filename = ctranslate2_resource_filename
    sys.modules["pkg_resources"] = adapter
    return True

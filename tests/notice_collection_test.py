"""Declared-licence gate tests for build/collect_notices.py.

From the desktop root:
  python -B tests/notice_collection_test.py

The Windows pipeline was blocked by a false positive: ``setuptools`` lists the
``*.dist-info`` directories of its vendored projects inside its own RECORD, and
every one of those nested directories was required to contain the licence file
that only the top-level ``setuptools-<version>.dist-info`` declares. These tests
pin both halves of that gate: a vendored directory without the declared licence
must not block, while a genuinely missing declared licence must still block.

No network, no venv creation, no interpreter other than the test runner. The
installed-distribution API is replaced with in-memory fakes over a temporary
directory tree.
"""
from __future__ import annotations

from email import message_from_string
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.dont_write_bytecode = True

_spec = importlib.util.spec_from_file_location("speakcity_collect_notices", ROOT / "build" / "collect_notices.py")
collect = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(collect)


class FakeDistribution:
    """The subset of importlib.metadata.Distribution that the collector uses."""

    def __init__(self, site: Path, directory: Path, metadata_text: str, records: list[str]):
        self.site, self._path = site, directory
        self.metadata = message_from_string(metadata_text)
        self.version = self.metadata.get("Version", "")
        self._records = records

    @property
    def files(self) -> list[str]:
        return list(self._records)

    def locate_file(self, path: object) -> Path:
        return self.site / str(path)


def build_site(site: Path, name: str, version: str, declared: list[str], own_licenses: list[str],
               vendor_licenses: list[str]) -> FakeDistribution:
    """Create a ``name`` distribution whose RECORD also lists a vendored dist-info."""
    info = site / f"{name}-{version}.dist-info"
    vendor = site / name / "_vendor" / "helper-2.0.dist-info"
    vendor.mkdir(parents=True, exist_ok=True)
    records = [f"{info.name}/METADATA,,", f"{info.name}/RECORD,,"]
    for filename in own_licenses:
        (info / "licenses").mkdir(parents=True, exist_ok=True)
        (info / "licenses" / filename).write_text(f"{name} licence text for {filename}\n", encoding="utf-8")
        records.append(f"{info.name}/licenses/{filename},,")
    for filename in vendor_licenses:
        (vendor / "licenses").mkdir(parents=True, exist_ok=True)
        (vendor / "licenses" / filename).write_text(f"vendored helper licence text for {filename}\n", encoding="utf-8")
        records.append(f"{name}/_vendor/helper-2.0.dist-info/licenses/{filename},,")
    # The vendored project's own metadata is part of the parent's file records.
    (vendor / "METADATA").write_text("Metadata-Version: 2.1\nName: helper\nVersion: 2.0\n", encoding="utf-8")
    (info / "METADATA").write_text(
        "Metadata-Version: 2.4\n"
        f"Name: {name}\nVersion: {version}\nLicense-Expression: MIT\n"
        + "".join(f"License-File: {entry}\n" for entry in declared),
        encoding="utf-8",
    )
    (info / "RECORD").write_text("\n".join(records) + "\n", encoding="utf-8")
    return FakeDistribution(site, info, (info / "METADATA").read_text(encoding="utf-8"),
                           [record.split(",")[0] for record in records])


def inventory_for(distributions: list[FakeDistribution], site: Path) -> tuple[collect.Collector, Path]:
    """Run only the installed-inventory stage against the fakes.

    The caller owns ``site`` and its parent, so the collected notice tree stays
    readable after this returns.
    """
    home = site.parent
    output, sources = home / "collected-notices", home / "collected-sources"
    output.mkdir()
    sources.mkdir()
    (home / "speech").mkdir(parents=True, exist_ok=True)
    (home / "speech" / "requirements.txt").write_text("", encoding="utf-8")
    collector = collect.Collector(output, sources)
    with mock.patch.object(collect.metadata, "distributions", lambda: distributions), \
            mock.patch.object(collect, "ROOT", home), \
            mock.patch.object(sys, "prefix", str(home)), \
            mock.patch.object(sys, "base_prefix", str(home)):
        collector.installed_inventory()
    return collector, output


class DeclaredLicenceTests(unittest.TestCase):
    def test_vendored_metadata_directory_does_not_block(self) -> None:
        with tempfile.TemporaryDirectory(prefix="speakcity-site-") as tmp:
            site = Path(tmp) / "venv" / "lib" / "site-packages"
            site.mkdir(parents=True)
            distributions = [build_site(site, "setuptools", "84.0.0", ["LICENSE"], ["LICENSE"], ["LICENSE"])]
            collector, output = inventory_for(distributions, site)
            rows = {row["normalized_name"]: row for row in collector.inventory}
            self.assertEqual(rows["setuptools"]["notice_errors"], [])
            self.assertFalse([blocker for blocker in collector.blockers if "declared notices" in blocker])
            # The declared licence and the vendored project's notice are both kept.
            collected = sorted(p.relative_to(output / "packages" / "setuptools-84.0.0").as_posix()
                               for p in (output / "packages" / "setuptools-84.0.0").rglob("*") if p.is_file())
            self.assertIn("setuptools-84.0.0.dist-info/licenses/LICENSE", collected)
            self.assertIn("setuptools/_vendor/helper-2.0.dist-info/licenses/LICENSE", collected)
            sha = next(entry["sha256"] for entry in rows["setuptools"]["notice_files"]
                       if entry["installed_record"].endswith("setuptools-84.0.0.dist-info/licenses/LICENSE"))
            self.assertRegex(sha, r"^[0-9a-f]{64}$")

    def test_genuinely_missing_declared_licence_still_blocks(self) -> None:
        with tempfile.TemporaryDirectory(prefix="speakcity-site-") as tmp:
            site = Path(tmp) / "venv" / "lib" / "site-packages"
            site.mkdir(parents=True)
            # LICENSE.txt exists, the declared NOTICE.md does not.
            distributions = [build_site(site, "brokenpkg", "1.2.3", ["LICENSE.txt", "NOTICE.md"],
                                        ["LICENSE.txt"], [])]
            collector, _ = inventory_for(distributions, site)
            rows = {row["normalized_name"]: row for row in collector.inventory}
            self.assertEqual(rows["brokenpkg"]["notice_errors"], ["Declared License-File missing: NOTICE.md"])
            self.assertIn("Unreadable/missing declared notices for brokenpkg; see inventory.json", collector.blockers)

    def test_own_metadata_directories_are_selected_by_identity(self) -> None:
        with tempfile.TemporaryDirectory(prefix="speakcity-site-") as tmp:
            base = Path(tmp).resolve() / "site-packages"
            own = base / "espeakng_loader-0.2.4+speakcity.1.dist-info"
            nested = base / "espeakng_loader" / "importlib_metadata-8.7.1.dist-info"
            egg = base / "legacy-1.0-py3.11.egg-info"
            for directory in (own, nested, egg):
                directory.mkdir(parents=True)
            selected = collect.own_metadata_directories({own, nested, egg}, base, "espeakng-loader", own)
            self.assertEqual(selected, {own})
            # A legacy egg-info layout without an authoritative _path is still own.
            selected = collect.own_metadata_directories({own, nested, egg}, base, "legacy", None)
            self.assertEqual(selected, {egg})
            # Unrecognizable layouts keep the strict gate over every candidate.
            self.assertEqual(collect.own_metadata_directories({nested}, base, "espeakng-loader", None), set())

    def test_notice_matching_stays_independent_of_the_gate(self) -> None:
        self.assertTrue(collect.is_notice("setuptools-84.0.0.dist-info/licenses/LICENSE"))
        self.assertTrue(collect.is_notice("setuptools/config/_validate_pyproject/NOTICE"))
        self.assertFalse(collect.is_notice("setuptools/config/setup.py"))


if __name__ == "__main__":
    unittest.main(verbosity=2)

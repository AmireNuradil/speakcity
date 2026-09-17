# SPEAKCITY Windows desktop: third-party notices and source companion

## Release status: controlled-build path prepared; Windows execution still required

`build/collect_notices.py` still returns **nonzero** for the unmodified `espeakng-loader==0.2.4` Windows wheel. Its exact native source/configuration mapping is unresolved and its release-time wrapper has neither a PyPI sdist nor an established release-time license notice. **Do not bypass that failure or label those prebuilt files compliant.**

The permitted alternative is now a **controlled Windows source build**, not a provenance waiver: `build/native_espeak.ps1` builds the pinned official eSpeak NG source and its complete generated data, replaces the entire installed DLL/data/wrapper package, and writes build evidence only after validation. The locally written path adapter contains no upstream loader Python code. Its truthful local distribution version is `0.2.4+speakcity.1` (PEP 440 permits that local version to satisfy the installed consumer's `==0.2.4` requirement). This replaces the upstream wrapper rather than pretending a GitHub archive is its missing PyPI sdist. eSpeak's native GPL and component notices remain mandatory.

**This implementation has not yet been executed on Windows.** A clean Windows runner build, native smoke tests, freeze, and installed-app tests remain required. The script, collector, and this document do not grant legal clearance, assign a license to SPEAKCITY, or establish that the desktop/worker is an independent aggregate. There is no `--ignore`/`--force` option and an absent, stale, or invalid controlled-build manifest leaves the historical gates closed.

## Running the controlled native build

Prerequisites: Windows x64, PowerShell 7.2+, the **same Python 3.11 x64 venv used for PyInstaller**, CMake 3.21+, and Visual Studio 2022 C++/MSBuild v143 with a Windows SDK. The script selects VS 2022 explicitly with `vswhere`; it does not silently substitute another compiler. GitHub `windows-latest` supplies these today, but a future image missing them must fail and be reviewed.

Run **after** the ordinary requirements/PyInstaller installation and **before** freezing the speech worker:

```powershell
& .\build\native_espeak.ps1 -PythonPath .\out\venv\Scripts\python.exe
# Optional explicit location (spaces supported):
# & .\build\native_espeak.ps1 -PythonPath 'C:\build folder\venv\Scripts\python.exe' `
#     -OutputRoot 'out/native espeak'
```

The script throws on failure. Use a fresh/empty output directory (default `out/native-espeak`) and a fresh venv containing the ordinary loader 0.2.4 wheel. It does not reuse CMake caches, re-stamp an old manifest, or merge generated data into a prebuilt data tree. On a failed replacement it attempts to restore the previous venv package; failed/partial outputs are diagnostic only. If interrupted during replacement, restore a clean venv and retry with a new output directory. Do not run pip installs between this step and freezing/collection.

### Fixed native build policy

- Only source download: `https://codeload.github.com/espeak-ng/espeak-ng/tar.gz/4870adfa25b1a32b4361592f1be8a40337c58d6c`, SHA-256 `cd83f84c4e495f281ac14e919aecf2834306ec1ea1b498de8ef6000f3a0f90de`. This is an immutable official-source commit and **audit-pinned archive hash**, not a publisher signature or provenance for the old wheel. Downloads are HTTPS/host-checked before redirects and SHA-checked before extraction/CMake execution. No downloaded source is executed on the Linux editing host.
- CMake/MSVC: `Visual Studio 17 2022`, `-A x64`, `-T v143,host=x64`, shared library, Release only, C11, UTF-8 source encoding, and static MSVC runtime (`CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`, `/MT`). The exact flags, actual tools/SDK, compiler records, generated projects, cache, commands, and logs are retained. Ambient compiler flags/toolchain/dependency-path variables are removed for build subprocesses; toolchain versions are recorded, not claimed bit-reproducibly pinned across moving runner images.
- Phonemization only: MBROLA, PCAudio, Sonic, async/pthread, Klatt, speechPlayer, tests, compat executables, and manpages are disabled. Upstream `cmake/deps.cmake` would fetch Sonic even with `USE_LIBSONIC=OFF`; a preserved exact patch removes that fetch and ambient dependency discovery entirely. **No Sonic source/binary is part of this controlled build.** The bundled static `ucd` source remains in the eSpeak archive.
- The patch set also raises CMake's minimum to 3.21 so CMP0091 makes `/MT` effective, gates upstream's otherwise-unconditional test/speechPlayer subdirectories, guards MSVC's missing `__has_include_next`, and adds `VERBATIM` escaping to all generated-data commands. Patch preimages/postimages, unified diff, reasons and modification time are retained. The one pinned header symlink is materialized as the identical upstream header, with its mapping recorded; arbitrary archive links are not followed.
- Build target **`data`** compiles the native CLI/DLL and all intonations, phoneme tables and language dictionaries on the **same Windows runner**. It is not the historical separate macOS data archive. `COMPILE_INTONATIONS`/`ENABLE_TESTS` from the publisher's old recipe are not relied on: those flags are ineffective in this pinned CMake source. The expected complete 364-file non-MBROLA data set is derived from the pinned source/recipe, not just a `phondata` existence check.
- The script requires CMake's actual `bin/espeak-ng.dll`; it does **not rename a guessed DLL**. It statically checks AMD64 PE exports for the eSpeak/phonemizer API, rejects forwarded exports/delay imports and any dependency outside a small Windows-system-DLL allowlist. `/MT` and disabled optional libraries avoid shipping unknown dependent DLLs. Native GPL/component terms and applicable Microsoft runtime redistribution notices still require review.
- A local `espeakng_loader/__init__.py` exposes `get_library_path()` / `get_data_path()` at the preserved layout (`espeak-ng.dll`, `espeak-ng-data/`). Before replacing the venv it is tested in a copied path **containing spaces**, with a system-only PATH. Both direct `ctypes` API calls and phonemizer produce nonempty IPA for `en-us`/`en-gb`; actual loaded data location/version and file hashes are bound to smoke evidence. The installed replacement is tested again. These are native-interface tests, not full Kokoro/model/inference or installer tests.

### Evidence and release gate

`out/native-espeak/manifest.json` is emitted only after build, full-tree replacement and smoke validation; the installed package holds a pointer plus its SHA-256. Custom `-OutputRoot` locations are discovered through that pointer, so the collector CLI needs no extra argument. Keep the output until freezing and collection finish. Never ship that runner-specific pointer as runtime data.

Both `speech/speech_worker.spec` and the collector revalidate the manifest against **actual installed bytes**, source archive, deterministic patch set, local adapter, current recipe/spec bytes, full generated-data file set, native exports/imports, effective CMake configuration and hash-bound smoke evidence. Boolean `status`/`replaced` fields alone are insufficient. Changed DLL/data/wrapper/recipe/evidence, an old prebuilt DLL, or an unproven extra dependency fails closed. The manifest is local build evidence, **not a signed attestation or proof against a compromised builder**.

The collector copies `native-espeak/inputs/` (whole source archive, all patch inputs/postimages, local adapter/metadata and exact scripts/spec) and `native-espeak/evidence/` plus the manifest into the existing source companion. It copies upstream native notices from the verified archive. Intermediate objects/native EXE/DLLs and the old wheel are not mislabeled as source.

`build/windows.ps1` is intentionally not modified by this work: the orchestrator must insert the invocation after pip install and before its PyInstaller command. Its existing collector invocation remains unchanged.

## Running the collector

Use the **same Python 3.11 x64 Windows venv used to freeze the worker**, after installing the speech dependencies/build tools and before compiling the installer. Do not run it with system Python, a Linux environment, or a different environment from the freeze. It reads installed distribution metadata without importing the packages.

```powershell
& .\out\venv\Scripts\python.exe .\build\collect_notices.py `
  --output .\out\app\third-party-notices `
  --sources .\out\third-party-source
if ($LASTEXITCODE -ne 0) { throw 'Third-party notice/source release gate failed' }
```

Both directories must be new or empty, distinct, outside the venv, and not overlap the repository's source directories. Existing partial/stale outputs are **not** merged, recursively deleted, or treated as a cache. Review and move/remove those outputs deliberately before retrying. Network access to the narrowly allowed official sources is required at build time; the learner-facing worker does not use this collector.

The existing `build/windows.ps1` calls this CLI and checks its exit status before installer compilation. This work does not modify that script, GitHub workflows, repository settings, or release configuration.

Exit codes:

- `0`: collection checks passed for a validated controlled native build and the other source checks; still **not** legal clearance. This status is not available for the unproven upstream wheel.
- `1`: collection completed with blockers; diagnostic notice/source directories are useful for review but **not a release-ready source bundle**.
- `2`: invalid environment/paths or a fatal I/O/configuration error. Any partial files must not be packaged.

## What is collected

### Notice directory

- `inventory.json`: every installed distribution's name, actual version, raw `License` metadata, `License-Expression`, license classifiers, project links, declared `License-File` values, copied notice paths/digests, requirements, and gaps. This is an **overinclusive build-venv inventory**, not proof that every package is in the frozen payload or a complete SBOM of bundled native code.
- `packages/<distribution>-<version>/...`: available installed LICENSE/LICENCE, COPYING, COPYRIGHT, NOTICE, AUTHORS, third-party notices, and nested license directories, preserving bytes and useful relative paths. Declared license files are also inspected. Missing metadata is not converted to an invented license. Unreadable or missing declared notices are blockers; absence of any discoverable notice is explicitly flagged for review.
- `upstream/`: license and notice files found in the verified source archives, including eSpeak's multiple `COPYING*` notices and the nested Unicode/ucd-tools notices. Source-file copyright headers remain intact in the source archives. A later upstream loader MIT license is clearly labeled as such.
- `interpreter/CPython-LICENSE.txt`, when the actual build interpreter provides it. No interpreter or full standard-library copy is made. Its other bundled-library notices still require review.
- `models/` and `model-sources.json`: verbatim publisher model cards, license texts, attribution material, pinned documentation references, and source/conversion links. No model weights or model-demo audio are downloaded by this collector.
- `collection-manifest.json` and `README.txt`: explicit status, blockers/warnings, environment details, URLs, SHA-256 digests, saved paths, and the limits of each piece of evidence.

### Source-companion directory

- `upstream/phonemizer/phonemizer-3.4.0.tar.gz`: exact PyPI sdist, checked against its published SHA-256 and byte count as well as the audited hash. The sdist includes the GPL text, Python source, package data, `pyproject.toml`, and `setup.cfg`. Installed phonemizer source/data is compared with it; only Python line endings are normalized. A local patch/file-set difference blocks collection rather than silently omitting modified source.
- **Validated controlled builds:** `native-espeak/inputs/archives/` contains the whole pinned official eSpeak archive. `inputs/patches/`, `inputs/patched/`, `inputs/local-adapter/`, and `inputs/recipes/` preserve exact modifications, the locally written compatible adapter, and build/freeze/validation inputs. `native-espeak/evidence/` and `manifest.json` preserve the actual Windows build configuration, tool versions, commands, native import/export inspection, smoke evidence, and output/replacement hashes. No upstream loader Python is shipped, so its absent PyPI sdist is not a substituted source artifact; the controlled adapter's own source is supplied instead.
- **Unproven/invalid builds only (blocked diagnostics):** the old exact loader sdist lookup still fails; `source-candidates/`, `evidence/build-recipes/`, native identity JSON and publisher checksum files retain the historical loader/eSpeak/Sonic evidence. A successful old wheel/source comparison may also retain `installed-reference/espeakng-loader/__init__.py`. These never become the controlled build's source mapping. Verification-only binary/wheel archives are not included in the source companion.
- `evidence/pypi/` retains required PyPI metadata snapshots. The exact phonemizer sdist gate remains unchanged in both paths.
- `speakcity/`: current `speech/worker.py`, `windows_compat.py`, `download_assets.py`, the freeze spec, exact requirements, asset manifest, speech README, additional speech Python hooks/modules, speech tests, the desktop speech-client interface source, build/install scripts, this collector, and this document. Available repository-root license/notice files are also copied. SHA-256 inventory captures the actual working-tree bytes, not an assumed Git HEAD. No model directories, venv, interpreter, compiled worker, cache, secrets file, or learner recording is swept into this set.
- `notices/`: a copy of the notice directory, so source recipients retain the same notices.
- `file-manifest.json`: hashes and lengths of all companion files except itself, including source archives, SPEAKCITY adaptation/build inputs, evidence, and notices.

Keep the complete SPEAKCITY desktop source archive for the **same actual build**, plus any additional combined-work source required after licensing review. The speech-focused source set above is not the entire desktop/UI source tree. `git archive HEAD` elsewhere in a release process does not cover uncommitted build inputs: reconcile the source-file hashes and build commit before release.

## Historical prebuilt audit (not the controlled-build provenance)

The following records explain why the upstream wheel remains blocked. They are not evidence that the new native build has run, nor a configuration to reuse instead of `native_espeak.ps1`. In particular its old source mapping, optional Sonic choice, and separate macOS-generated data remain unresolved.

Official evidence links:

- [phonemizer 3.4.0 PyPI JSON](https://pypi.org/pypi/phonemizer/3.4.0/json)
- [espeakng-loader 0.2.4 PyPI JSON](https://pypi.org/pypi/espeakng-loader/0.2.4/json)
- [Loader 0.2.4 source commit](https://github.com/thewh1teagle/espeakng-loader/tree/146599e29be31bf17d99f0bcb7dbb2f92aef3d95)
- [Historical native build workflow](https://github.com/thewh1teagle/espeakng-loader/blob/146599e29be31bf17d99f0bcb7dbb2f92aef3d95/.github/workflows/build.yml)
- [Historical wheel publication workflow](https://github.com/thewh1teagle/espeakng-loader/blob/146599e29be31bf17d99f0bcb7dbb2f92aef3d95/.github/workflows/pypi.yml)
- [Historical build.sh](https://github.com/thewh1teagle/espeakng-loader/blob/146599e29be31bf17d99f0bcb7dbb2f92aef3d95/build.sh)
- [Native asset release v0.1.0](https://github.com/thewh1teagle/espeakng-loader/releases/tag/v0.1.0) and [publisher checksums](https://github.com/thewh1teagle/espeakng-loader/releases/download/v0.1.0/checksum.txt)
- [eSpeak NG source commit](https://github.com/espeak-ng/espeak-ng/tree/4870adfa25b1a32b4361592f1be8a40337c58d6c), [1.52.0 tag ref](https://api.github.com/repos/espeak-ng/espeak-ng/git/ref/tags/1.52.0), and [dependency recipe](https://github.com/espeak-ng/espeak-ng/blob/4870adfa25b1a32b4361592f1be8a40337c58d6c/cmake/deps.cmake)
- [Sonic source commit](https://github.com/waywardgeek/sonic/tree/fbf75c3d6d846bad3bb3d456cbc5d07d9fd8c104)

The loader's source commit is `146599e29be31bf17d99f0bcb7dbb2f92aef3d95` (2025-01-17, version bump to 0.2.4). The reviewed Windows wheel's Python wrapper matches this source after CRLF normalization. The Git tree records eSpeak commit `4870adfa25b1a32b4361592f1be8a40337c58d6c`; the native workflow also explicitly clones tag `1.52.0`. The repository's present `main` and release labels are not used to choose the source.

Important build configuration from the preserved recipes:

- Native shared library: CMake `BUILD_SHARED_LIBS=ON`, `ENABLE_TESTS=OFF`, `COMPILE_INTONATIONS=OFF`, `CMAKE_BUILD_TYPE=Release`, installation prefix `_dynamic`; `cmake --build build --config Release`; Windows x86-64 has no ARM64 `-A` argument.
- Data is built separately in the `macos-14` job after deleting `build`, with `COMPILE_INTONATIONS=ON` and prefix `_espeak_ng_data`. The entire resulting `share/espeak-ng-data` is bundled. Supplying only DLL source without preferred dictionary/phoneme data source and this build recipe would be incomplete.
- `cmake/deps.cmake` looks for ambient Sonic first, otherwise fetches **waywardgeek/sonic commit `fbf75c3d6d846bad3bb3d456cbc5d07d9fd8c104`** and adds `sonic.c` as an object library. That exact commit's LICENSE, README, and C headers state **Apache-2.0**; do not assign another Sonic version's license to it. The candidate source archive includes it. Ambient optional-library detection, compiler/toolchain versions, and full CMake cache are part of the outstanding provenance review.
- Existing build scripts are preserved verbatim, not represented as a tested reproducible recipe. For a controlled rebuild, unpack the loader source, place the pinned eSpeak tree at `espeak-ng/`, and supply the pinned Sonic tree to CMake (for example via `FETCHCONTENT_SOURCE_DIR_SONIC-GIT`) instead of fetching moving dependencies. Review/fix the Windows build/install configuration and pin optional dependencies explicitly. Build the native library and complete data tree, put them under `src/espeakng_loader/`, then use the included hatch hook with `WHEEL_TAG=py3-none-win_amd64`. Record your actual scripts, changes, toolchain/cache, inputs, outputs, and verification results. These are reconstruction notes, **not a claim a byte-reproducible Windows rebuild was run**.

Identity checks retained by the collector:

| Artifact | SHA-256 |
| --- | --- |
| PyPI phonemizer 3.4.0 sdist | `e13231980c50bc671ec0466379ba027260ad9d61929952d8ae9665b3d0f251eb` |
| PyPI loader 0.2.4 Windows x64 wheel | `41f1e08ac9deda2efd1ea9de0b81dab9f5ae3c4b24284f76533d0a7b1dd7abd7` |
| Bundled `espeak-ng.dll` | `646d387acbc7ac2aa45e3625aa00a6835ae5d446ff8b0748298c3900b4dde258` |
| Publisher Windows x64 native archive | `94a66fd30ec94dbe573e3f65491e32b8aa5b890108f890d2b68b42343474f2b6` |
| Publisher complete data archive | `38896a06fde172b57828e6877a2dbf6d278590f7f6ef6007a155b4049891f650` |
| 364-file data-tree digest | `1e227c92894093749c72d99169a0ab30749015ec33c5f9826672d8f3113e8dd0` |

The data-tree digest hashes UTF-8, newline-joined `<file SHA256>  <relative path>` entries sorted by path, without a trailing newline; paths begin with `espeak-ng-data/`. Every file is compared, not just `phondata`. The collector also compares the **installed** loader payload to the checksum-verified PyPI wheel without loading its DLL. A mismatch blocks rather than silently associating the wrong source. GitHub-generated archive hashes are **local audit pins**, distinct from PyPI/publisher-published checksums; none are digital signatures or proof that published binaries were built from a particular source.

### License distinctions that must remain visible

- **phonemizer** provides the GNU GPL version 3 text and its source header specifies **version 3 or later**. Preserve those upstream terms/notices. Do not categorize the whole worker as MIT/Apache simply because the model licenses are permissive.
- **eSpeak NG** declares **GPL version 3 or later** in the pinned README, with additional component notices (`COPYING`, `COPYING.APACHE`, `COPYING.BSD2`, `COPYING.UCD`, nested notices and source headers). Preserve the actual source notices and any component-specific terms.
- The loader 0.2.4 wheel has **no LICENSE/NOTICE files and no license metadata**. Its release-time repository commit also lacks a LICENSE. The upstream MIT LICENSE was added on **2025-11-01**, at [commit 0ddc87adf77e5850d7eeb542ac8a87d421b64daa](https://github.com/thewh1teagle/espeakng-loader/blob/0ddc87adf77e5850d7eeb542ac8a87d421b64daa/LICENSE). The collector preserves that exact later notice as evidence, not an invented release-time copyright statement or automatic retroactive clearance. Resolve wrapper licensing with the publisher/reviewer. The wrapper's later MIT notice does not supersede eSpeak's GPL terms.

## Model source/notice links (no weights in this companion)

- **Kokoro v1.0 and voices:** [original model](https://huggingface.co/hexgrad/Kokoro-82M), [code](https://github.com/hexgrad/kokoro), and [ONNX/voice conversion release used by SPEAKCITY](https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.1). The publisher model card declares Apache-2.0. The collector includes the Apache license text and a pinned copy of the model card and voice documentation, including the card's **CC BY training-data attribution table**. This documentation revision is not claimed to be the build revision of the converted weights. The installer carries the whole `voices-v1.0.bin`, not just the default two voices; review the whole artifact's notices.
- **Whisper base.en:** [OpenAI source and model information](https://github.com/openai/whisper), MIT license; [exact CTranslate2 model snapshot](https://huggingface.co/Systran/faster-whisper-base.en/tree/3d3d5dee26484f91867d81cb899cfcf72b96be6c). Its converted model card and the actual upstream OpenAI MIT notice are retained. The asset manifest records the shipped files' digests.
- **Silero VAD:** [source/model repository](https://github.com/snakers4/silero-vad), MIT; included indirectly in the [faster-whisper 1.2.1 package](https://github.com/SYSTRAN/faster-whisper/tree/v1.2.1). An upstream MIT notice and consumer README are included. The license snapshot does not establish the exact origin of the vendored ONNX file: reconcile that with the final wheel/payload during release review.

Source links and permissive-model notices do **not** satisfy the GPL source obligations of phonemizer/eSpeak. The collector does not fetch training datasets, model weights, model-demo audio, or a complete model repository. Unmodified native source archives may retain their own small upstream test fixtures.

## Remaining release obligations

Before any candidate is distributed, the release owner must:

1. Execute the controlled native build in a clean Windows venv, review its exact **DLL plus generated data** evidence and locally written adapter, and pass the collector/freeze gates. The old wheel/wrapper remains unapproved and must not reappear. Retain complete preferred source, patches, tools/configuration and build/install inputs. If choosing to retain upstream prebuilt/wrapper bytes instead, their absent-sdist, exact native mapping and release-time wrapper license gaps remain unresolved; that is not permitted by the current release gate.
2. Decide and document the applicable licensing of SPEAKCITY's speech adaptation and any combined work, with an appropriate reviewer. Add accurate license/change notices and relevant modification dates without inventing copyright ownership or assuming a subprocess boundary settles the question. Include any additional application/interface source or installation information the applicable licenses require.
3. Reconcile this build-venv inventory with the final frozen worker and installer. Review missing notices and **native/vendored dependencies** (especially PyAV/FFmpeg libraries/codecs, ONNX Runtime, CTranslate2, NumPy/OpenMP/BLAS, CPython/stdlib, PyInstaller bootloader/exception, and .NET/WebView2/VC redistribution terms). Installed Python metadata alone does not establish native library license choices or fulfill their source/relinking obligations.
4. Pin/record actual Windows-only transitive dependencies, build-only PyInstaller/hooks, compiler/SDK/CMake/hatch/uv versions and effective configuration; preserve freeze manifests, native configuration and exact build inputs. Test a clean Windows rebuild and installation. This collector does not run source builds or establish reproducibility/security.
5. Supply notices with the installed application and provide the matching, complete corresponding-source companion and any necessary application source with equivalent access to **every recipient** of the installer, including private recipients. For network distribution, give clear source directions next to the exact binary and keep both accessible under the applicable license conditions. An inaccessible/private repository link, a wheel-only archive, or this document alone is not a source offer. If a written-offer distribution method is chosen instead, have its content, duration and fulfillment obligations reviewed; no written offer is generated here.
6. Preserve original source/license/attribution material, document modifications, verify final source/archive hashes against the build, and review the model/conversion/voice and vendored VAD notices. Publish only the source/notices that pass review; blocked diagnostic outputs must never be advertised as a compliant release archive.

## Implementation validation (not Windows release evidence)

The original prebuilt-audit collector validation in the Linux development sandbox passed 18 targeted standard-library tests covering official URL selection, HTTPS downgrade/foreign-host rejection, archive paths/links/size limits, known source/wheel hashes, missing sdists, corrupt downloads, published byte counts, fresh/non-nested output paths, missing declared notices, and requirement mismatches. Fixture-based collection using the actual downloaded GPL-related package contents (never imported/executed) verified phonemizer source equality, the DLL and all 364 eSpeak data files, verbatim notice copying, manifest hashes, absence of model/native payload copies, and exit status 1 for exactly the two documented source gates. Deliberately changed installed source/data was rejected. Live HTTPS redirect and digest checks were also exercised against the official PyPI, GitHub/codeload, Hugging Face, and Apache endpoints. The CLI rejects this non-Windows environment with status 2. **The real Windows venv/freeze/installer and a native-source rebuild have not been executed by this validation.**

The controlled-build implementation additionally passed 20 local/static assertions: Python AST parsing of the collector, embedded build driver, local adapter and freeze spec; the official source archive hash; exact seven-file patch preimages; all four data-command escaping changes; the 364-file data recipe; forbidden URL/PE cases; refusal of the old wheel's dynamic runtime dependency set under the controlled `/MT` policy; and both driver/collector rejection on Linux. PowerShell is unavailable in this editing sandbox, so these do not constitute PowerShell parsing or CMake/MSVC/Windows build results. No upstream archive source was executed and no native DLL was loaded during these checks.

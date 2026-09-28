$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
New-Item -ItemType Directory -Force out, out/app, out/release, out/bootstrap, out/reports, out/third-party-source | Out-Null
$stage = 'initializing'
$detail = @{ phase = 'desktop-build'; result = 'running'; run_id = $env:GITHUB_RUN_ID; commit = $env:GITHUB_SHA }

function Put-PrivateReport([string]$Path, [object]$Value) {
  if (-not $env:GH_TOKEN) { return }
  $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
  $uri = "https://api.github.com/repos/$env:GITHUB_REPOSITORY/contents/$Path"
  $json = $Value | ConvertTo-Json -Depth 15
  # Evidence belongs to the branch that was built (a pull request's head branch, not its merge ref),
  # so a work branch never rewrites main's records.
  $branch = @($env:GITHUB_HEAD_REF, $env:GITHUB_REF_NAME, 'main') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
  $body = @{ message = 'Record desktop build evidence'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)); branch = $branch }
  # The update needs the blob SHA on the branch being written; without ?ref the API answers with main's.
  $existing = Invoke-WebRequest -Uri "$($uri)?ref=$([uri]::EscapeDataString($branch))" -Headers $headers -SkipHttpErrorCheck
  if ($existing.StatusCode -eq 200) { $body.sha = ($existing.Content | ConvertFrom-Json).sha }
  elseif ($existing.StatusCode -ne 404) { throw "Cannot read report path: $($existing.StatusCode)" }
  Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 15) | Out-Null
}
function Download-Checked([string]$Url, [string]$Path, [string]$Sha) {
  Invoke-WebRequest -Uri $Url -OutFile $Path
  if ((Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha) { throw "Integrity check failed for $(Split-Path $Path -Leaf)" }
}
function Assert-Exit([string]$Name) { if ($LASTEXITCODE -ne 0) { throw "$Name returned exit code $LASTEXITCODE" } }
function Test-RepositoryPrivate {
  # An unsigned installer and its temporary signed download links may only be published from a private repository.
  if (-not $env:GH_TOKEN -or -not $env:GITHUB_REPOSITORY) { return $false }
  try {
    return ((Invoke-RestMethod -Uri "https://api.github.com/repos/$env:GITHUB_REPOSITORY" -Headers @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }).private) -eq $true
  } catch { return $false }
}
function Copy-Fresh([string]$Source, [string]$Destination) {
  # Copy-Item -Recurse aborts with "already exists" when the destination directory is left
  # over from an earlier run, which used to kill a ~20 minute build on its last stages.
  if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
  New-Item -ItemType Directory -Force (Split-Path $Destination -Parent) | Out-Null
  Copy-Item $Source $Destination -Recurse
}
function Publish-BuildRelease([string]$Tag, [string]$Title, [string]$Notes, [object[]]$Assets, [switch]$Prerelease) {
  # Only upload assets this run actually produced: a finished installer must not be
  # lost because an optional file (for example the UI smoke screenshot) was never
  # written. Re-running a job must not fail on an existing tag, so a creation error
  # is tolerated and the idempotent upload --clobber path is tried regardless.
  # Nothing is written to the success stream except the boolean return value.
  if (-not $env:GH_TOKEN -or -not $env:GITHUB_REPOSITORY) { return $false }
  $present = @($Assets | Where-Object { $_ -and (Test-Path $_) })
  if ($present.Count -eq 0) { return $false }
  $repo = @('--repo', $env:GITHUB_REPOSITORY)
  $create = $repo + @('--title', $Title, '--notes', $Notes)
  if ($Prerelease) { $create += '--prerelease' }
  $null = & gh release create $Tag $create 2>$null
  $null = & gh release upload $Tag $present --clobber $repo 2>$null
  if ($LASTEXITCODE -ne 0) {
    Write-Host "Release '$Tag' could not be published completely; built files remain under out/release."
    return $false
  }
  Write-Host "Published release '$Tag' with $($present.Count) asset(s)."
  return $true
}

try {
  $stage = 'build-temp'
  $buildTemp = $env:RUNNER_TEMP
  if ([string]::IsNullOrWhiteSpace($buildTemp)) { $buildTemp = $env:TEMP }
  if ([string]::IsNullOrWhiteSpace($buildTemp)) { $buildTemp = Join-Path $root 'out/tmp' }
  New-Item -ItemType Directory -Force $buildTemp | Out-Null
  $stage = 'decode-assets'
  $manifest = Get-Content assets-source/manifest.json -Raw | ConvertFrom-Json
  foreach ($asset in $manifest) {
    $target = Join-Path $root $asset.output
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    [IO.File]::WriteAllBytes($target, [Convert]::FromBase64String((Get-Content $asset.encoded -Raw)))
    if ((Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw "Asset hash mismatch: $($asset.output)" }
  }
  $stage = 'dotnet-sdk'
  # Portable .NET 10 discovery: explicit SPEAKCITY_DOTNET override, then
  # dotnet.exe from PATH, then DOTNET_ROOT, then a machine-local SDK install
  # into the portable scratch directory. No personal hard-coded path.
  $dotnetCandidates = @()
  if (-not [string]::IsNullOrWhiteSpace($env:SPEAKCITY_DOTNET)) { $dotnetCandidates += $env:SPEAKCITY_DOTNET }
  $pathDotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue).Source
  if ($pathDotnet) { $dotnetCandidates += $pathDotnet }
  if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) {
    $dotnetCandidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
  }
  $dotnet = $dotnetCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  if (-not $dotnet) {
    # Documented fallback: install the pinned SDK into the portable scratch
    # directory (RUNNER_TEMP -> TEMP -> out/tmp), never a personal folder.
    $dotnetDir = Join-Path $buildTemp 'dotnet10'
    $dotnet = Join-Path $dotnetDir 'dotnet.exe'
  }
  if (-not (Test-Path $dotnet)) {
    $dotnetDir = Split-Path $dotnet -Parent
    $script = Join-Path $buildTemp 'dotnet-install.ps1'
    Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/install-scripts/47940ac9fc30a2f2dd19167165d0bb0774625f67/src/dotnet-install.ps1' -OutFile $script
    & $script -Version '10.0.401' -InstallDir $dotnetDir -NoPath
    # dotnet-install.ps1 is a PowerShell script, not a native command, so
    # $LASTEXITCODE is left over from unrelated native helpers inside it and
    # says nothing about the install. Judge it by what it produced instead:
    # the Test-Path check below, then the version and architecture checks.
    $env:PATH = $dotnetDir + ';' + $env:PATH
    $env:DOTNET_ROOT = $dotnetDir
  }
  if (-not (Test-Path $dotnet)) { throw '.NET 10 SDK was not found. Install the .NET 10 x64 SDK or set SPEAKCITY_DOTNET to dotnet.exe.' }
  $dotnetVersion = (& $dotnet --version).Trim()
  if ($dotnetVersion -notmatch '^10\.0\.') { throw ".NET 10 SDK is required, but '$dotnet' reports version '$dotnetVersion'." }
  # NOTE: dotnet --info returns an array of lines. -notmatch on an array would
  # return all non-matching lines (always truthy), so join it into one string.
  $dotnetInfoText = (& $dotnet --info | Out-String)
  if ($dotnetInfoText -notmatch 'Architecture:\s*x64' -and $dotnetInfoText -notmatch 'RID:\s*win-x64') {
    throw ".NET host '$dotnet' does not report an x64 runtime ($dotnetVersion). Install the x64 .NET 10 SDK." }
  $env:DOTNET_ROOT = Split-Path $dotnet -Parent
  & $dotnet --info | Out-File out/reports/dotnet.txt
  $stage = 'speech-environment'
  # Portable Python 3.11 x64 discovery: explicit SPEAKCITY_PYTHON override,
  # then the Python launcher (py.exe -3.11), then the Windows registry
  # (HKCU/HKLM), then PATH (python.exe / python3.11.exe).
  $python = $null
  if (-not [string]::IsNullOrWhiteSpace($env:SPEAKCITY_PYTHON)) { $python = $env:SPEAKCITY_PYTHON }
  if (-not ($python -and (Test-Path $python))) {
    $pyLauncher = Get-Command py.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($pyLauncher) {
      try {
        $probe = (& py.exe -3.11 -c 'import sys; print(sys.executable)' 2>$null).Trim()
        if ($probe -and (Test-Path $probe)) { $python = $probe }
      } catch { $python = $null }
    }
  }
  if (-not ($python -and (Test-Path $python))) {
    $python = (Get-ItemProperty 'HKCU:\Software\Python\PythonCore\3.11\InstallPath' -ErrorAction SilentlyContinue).ExecutablePath
  }
  if (-not ($python -and (Test-Path $python))) {
    $python = (Get-ItemProperty 'HKLM:\Software\Python\PythonCore\3.11\InstallPath' -ErrorAction SilentlyContinue).ExecutablePath
  }
  if (-not ($python -and (Test-Path $python))) {
    foreach ($name in @('python3.11.exe', 'python.exe')) {
      $found = (Get-Command $name -CommandType Application -ErrorAction SilentlyContinue).Source
      if ($found -and (Test-Path $found)) { $python = $found; break }
    }
  }
  if (-not ($python -and (Test-Path $python))) { throw 'Python 3.11 x64 was not found. Install Python 3.11 64-bit or set SPEAKCITY_PYTHON to python.exe.' }
  $pythonProbe = @(& $python -c 'import sys,platform,struct; print(str(sys.version_info[0]) + "." + str(sys.version_info[1])); print(platform.machine()); print(struct.calcsize("P") * 8)')
  Assert-Exit 'Verify Python version'
  if ($pythonProbe[0].Trim() -ne '3.11') { throw "Python 3.11 is required, but '$python' reports version '$($pythonProbe[0].Trim())'." }
  if ($pythonProbe[1].Trim().ToLowerInvariant() -notin @('amd64', 'x86_64') -or $pythonProbe[2].Trim() -ne '64') {
    throw "Python 3.11 x64 is required, but '$python' reports '$($pythonProbe[1].Trim())' / $($pythonProbe[2].Trim())-bit." }
  # The controlled eSpeak build replaces espeakng-loader inside this venv and refuses to
  # re-stamp a venv it already replaced, so a leftover stamped venv has to be recreated
  # here instead of failing ten minutes into the run.
  if (Test-Path out/venv/Lib/site-packages/espeakng_loader/speakcity-native-build.json) {
    Write-Host 'Build venv already carries a controlled eSpeak build; recreating it.'
    Remove-Item out/venv -Recurse -Force
  }
  & $python -m venv out/venv
  Assert-Exit 'Create build environment'
  $buildPython = Join-Path $root 'out/venv/Scripts/python.exe'
  & $buildPython -m pip install --disable-pip-version-check --only-binary=:all: -r speech/requirements.txt pyinstaller
  Assert-Exit 'Install speech dependencies'
  $stage = 'controlled-native-speech'
  # The eSpeak driver deliberately refuses to reuse or remove its own output root, so the
  # previous run's build there is dropped by the orchestrator instead.
  if (Test-Path out/native-espeak) { Remove-Item out/native-espeak -Recurse -Force }
  & (Join-Path $root 'build/native_espeak.ps1') -PythonPath $buildPython 2>&1 | Tee-Object out/reports/native-espeak.log
  if (-not $?) { throw 'Controlled eSpeak source build failed.' }
  $stage = 'speech-model-download'
  & $buildPython speech/download_assets.py --models out/models
  Assert-Exit 'Download verified speech models'
  $stage = 'speech-package'
  & $buildPython -m PyInstaller --noconfirm --clean --distpath out/worker --workpath out/pyinstaller speech/speech_worker.spec 2>&1 | Tee-Object out/reports/pyinstaller.log
  Assert-Exit 'Freeze Windows speech worker'
  Copy-Fresh out/worker/speakcity-speech-worker out/app/speech/speakcity-speech-worker
  Copy-Fresh out/models out/app/speech/models
  $stage = 'desktop-compile'
  & $dotnet publish src/SpeakCity/SpeakCity.csproj -c Release -r win-x64 --self-contained true -o out/app 2>&1 | Tee-Object out/reports/dotnet-publish.log
  Assert-Exit 'Compile Windows desktop app'
  $stage = 'packaged-app-tests'
  $exe = Join-Path $root 'out/app/SpeakCity.exe'
  $report = Join-Path $root 'out/reports/windows-self-test.json'
  # Deliberately remove ambient Python paths: the app must use its bundled worker.
  $oldPath = $env:PATH
  $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
  $env:PYTHONHOME = ''; $env:PYTHONPATH = ''
  try {
    $test = Start-Process -FilePath $exe -ArgumentList @('--self-test', "`"$report`"") -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw "Packaged self-test failed: $($test.ExitCode)" }
  } finally { $env:PATH = $oldPath }
  $selfTests = Get-Content $report -Raw | ConvertFrom-Json
  Put-PrivateReport '.build/windows-self-test.json' $selfTests
  if (-not $selfTests.passed) { throw 'Packaged self-test did not pass.' }
  $stage = 'notices-and-sources'
  Copy-Fresh docs out/app/docs
  # collect_notices refuses to write over output from an earlier run rather than merging into it,
  # so the generated notices and collected sources from the previous build have to be dropped.
  foreach ($generated in 'out/app/third-party-notices', 'out/third-party-source') {
    if (Test-Path $generated) { Remove-Item $generated -Recurse -Force }
    New-Item -ItemType Directory -Force $generated | Out-Null
  }
  & $buildPython build/collect_notices.py --output out/app/third-party-notices --sources out/third-party-source 2>&1 | Tee-Object out/reports/notices.log
  Assert-Exit 'Collect bundled component notices and source'
  $stage = 'installer-tools'
  $innoSetup = Join-Path $buildTemp 'innosetup-6.4.3.exe'
  Download-Checked 'https://github.com/jrsoftware/issrc/releases/download/is-6_4_3/innosetup-6.4.3.exe' $innoSetup 'f3c42116542c4cc57263c5ba6c4feabfc49fe771f2f98a79d2f7628b8762723b'
  $inno = Join-Path $buildTemp 'inno'
  # A compiler from an earlier run, or one the owner installed by hand, is fine to use: the pinned
  # download below only matters when nothing is available. Inno's own installer asks for elevation,
  # which a detached build can never answer, so when it must install it installs per-user.
  $compilerPlaces = @((Join-Path $inno 'ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe')
  $iscc = @($compilerPlaces | Where-Object { Test-Path $_ }) | Select-Object -First 1
  if (-not $iscc) {
    $installCompiler = Start-Process $innoSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER') -Wait -PassThru
    if ($installCompiler.ExitCode -ne 0) {
      throw "Inno Setup 6.4.3 is needed to compile the installer and the silent per-user install failed (exit code $($installCompiler.ExitCode)). Run '$innoSetup' once and click through it, then repeat this build."
    }
    $iscc = @($compilerPlaces | Where-Object { Test-Path $_ }) | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup reported success but no ISCC.exe was found in ' + ($compilerPlaces -join ', ') }
  }
  $webview = Join-Path $root 'out/bootstrap/MicrosoftEdgeWebview2Setup.exe'
  Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webview
  $signature = Get-AuthenticodeSignature $webview
  if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') { throw 'Official WebView2 bootstrapper signature verification failed.' }
  $stage = 'installer-compile'
  & $iscc "/DPayloadDir=$root\out\app" "/DReleaseDir=$root\out\release" "/DBootstrapDir=$root\out\bootstrap" build/installer.iss 2>&1 | Tee-Object out/reports/installer.log
  Assert-Exit 'Compile installer'
  $installer = Join-Path $root 'out/release/SPEAKCITY-AI-Setup-x64.exe'
  $stage = 'installer-test'
  $installDir = Join-Path $buildTemp 'SpeakCity Test'
  # The point of this stage is a clean install, so an earlier run's copy must not be upgraded.
  # A running instance would also hold the files this cleanup has to delete.
  Get-Process SpeakCity -ErrorAction SilentlyContinue | Stop-Process -Force
  if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
  $install = Start-Process $installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$installDir`"") -Wait -PassThru
  if ($install.ExitCode -ne 0) { throw "Installer test failed: $($install.ExitCode)" }
  $installedReport = Join-Path $root 'out/reports/installed-self-test.json'
  $oldPath = $env:PATH; $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
  try {
    $test = Start-Process (Join-Path $installDir 'SpeakCity.exe') -ArgumentList @('--self-test',"`"$installedReport`"") -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw 'Installed application self-test failed.' }
  } finally { $env:PATH = $oldPath }
  Put-PrivateReport '.build/installed-self-test.json' (Get-Content $installedReport -Raw | ConvertFrom-Json)
  $stage = 'native-ui-test'
  $uiReport = Join-Path $root 'out/reports/native-ui.json'
  # A leftover instance holds the single-instance mutex, which would turn this whole
  # check into a skip.
  Get-Process SpeakCity -ErrorAction SilentlyContinue | Stop-Process -Force
  $uiExit = -1
  $env:SPEAKCITY_AUTOMATION = '1'
  try {
    $uiProcess = Start-Process (Join-Path $installDir 'SpeakCity.exe') -ArgumentList @('--ui-smoke',"`"$uiReport`"") -PassThru
    # The app bounds its own startup now; this is only the outer guard.
    # The smoke check now also opens the window, warms both speech models and renders screenshots.
    if (-not $uiProcess.WaitForExit(420000)) { $uiProcess.Kill($true); throw 'Native UI smoke test exceeded 420 seconds.' }
    $uiExit = $uiProcess.ExitCode
  } finally { Remove-Item Env:SPEAKCITY_AUTOMATION -ErrorAction SilentlyContinue }
  $ui = $null
  if (Test-Path $uiReport) { $ui = Get-Content $uiReport -Raw | ConvertFrom-Json; Put-PrivateReport '.build/native-ui.json' $ui }
  if ($null -eq $ui) { throw "Native UI check exited $uiExit without writing a report; startup did not reach its own verdict." }
  # The application decides whether this is a pass, a failure or an environment skip.
  # A skipped check is recorded as skipped here and in the release notes - never as a pass.
  if ($ui.status -eq 'skipped') {
    $detail.ui_smoke = "skipped ($($ui.skipped_reason))"
    Write-Output "Native UI check skipped: $($ui.skipped_reason). The interactive interface still needs a physical Windows 11 desktop."
  } elseif ($ui.passed -eq $true -and $uiExit -eq 0) {
    $detail.ui_smoke = 'passed'
  } else {
    throw "Native WebView2 UI smoke test failed (exit $uiExit): $($ui.error)"
  }
  # The test install registered itself as this machine's SPEAKCITY location, which makes a later
  # real install default into a temporary folder that Windows is free to delete. Once both checks
  # are done the test copy has to remove itself again.
  $uninstaller = Join-Path $installDir 'unins000.exe'
  if (Test-Path $uninstaller) {
    $remove = Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    Start-Sleep -Seconds 5
    if ($remove.ExitCode -ne 0 -or (Test-Path $installDir)) {
      Write-Output "The test install did not remove itself (exit $($remove.ExitCode)); delete $installDir by hand before installing SPEAKCITY for real."
    }
  }
  $stage = 'private-release'
  # Publishing needs a token, a repository, and a repository that is actually
  # private. The release carries an unsigned installer, and the download pointers
  # recorded below are temporary signed URLs: neither may be handed to the public.
  # A public repository therefore skips publishing and keeps its files under
  # out/release; that is a completed build, not a failure.
  $repoPrivate = Test-RepositoryPrivate
  $detail.repository_private = $repoPrivate
  if ([bool]$env:GH_TOKEN -and [bool]$env:GITHUB_REPOSITORY -and $repoPrivate) {
    $sourceZip = Join-Path $root 'out/release/SPEAKCITY-AI-source.zip'
    git archive -o $sourceZip HEAD
    Assert-Exit 'Archive source'
    Compress-Archive out/third-party-source/* out/release/SPEAKCITY-third-party-source.zip
    $tag = "desktop-preview-0.2.0-$env:GITHUB_RUN_NUMBER"
    $uiNote = if ($detail.ui_smoke) { $detail.ui_smoke } else { 'not run' }
    $published = Publish-BuildRelease $tag 'SPEAKCITY Windows desktop candidate' ("Private unsigned Windows x64 build. Local speech bundled; configure an eligible AI API in the native settings screen. Windows Server packaged tests are included; real provider and physical Windows 11 microphone tests remain pending. Interactive UI check on this build agent: $uiNote.") @($installer, $sourceZip, (Join-Path $root 'out/release/SPEAKCITY-third-party-source.zip'), (Join-Path $root 'out/reports/native-ui.png'))
    if (-not $published) { throw 'Create private release' }
    $detail.release_tag = $tag
    # Temporary GitHub-provided download redirects, recorded only because the
    # repository was verified private above. Never store or print GH_TOKEN.
    # These URLs are file-specific and expire.
    $release = (gh api "repos/$env:GITHUB_REPOSITORY/releases/tags/$tag" | ConvertFrom-Json)
    $handler = [System.Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
    $http = [System.Net.Http.HttpClient]::new($handler)
    $downloads = @()
    try {
      foreach ($asset in $release.assets) {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get,$asset.url)
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$env:GH_TOKEN)
        $request.Headers.Accept.ParseAdd('application/octet-stream'); $request.Headers.UserAgent.ParseAdd('SPEAKCITY-build')
        $response = $http.SendAsync($request,[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        if ([int]$response.StatusCode -eq 302 -and $response.Headers.Location.Scheme -eq 'https') {
          $downloads += @{ name = $asset.name; size = $asset.size; url = $response.Headers.Location.AbsoluteUri; digest = $asset.digest }
        }
        $response.Dispose(); $request.Dispose()
      }
    } finally { $http.Dispose() }
    Put-PrivateReport '.build/downloads.json' @{ release_tag = $tag; files = $downloads; note = 'Private temporary download pointers; not credentials or a public repository.' }
  } elseif ([bool]$env:GH_TOKEN -and [bool]$env:GITHUB_REPOSITORY) {
    Write-Output 'GitHub publishing skipped: the repository is public, so an unsigned installer and its temporary signed download links are not published. Make the repository private to publish a release; the built files remain under out/release.'
  } else {
    Write-Output 'GitHub publishing skipped (no GH_TOKEN/GITHUB_REPOSITORY); built files remain under out/release.'
  }
  $detail.result = 'success'; $detail.stage = 'private-release'
  $detail.installer_sha256 = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
  $detail.installer_bytes = (Get-Item $installer).Length
  $detail.signed = $false
  $detail.live_api_tested = $false
  $detail.physical_windows11_microphone_tested = $false
  $detail.interactive_ui_verified = ($detail.ui_smoke -eq 'passed')
  Put-PrivateReport '.build/desktop-build.json' $detail
} catch {
  $detail.result = 'failure'; $detail.stage = $stage
  $detail.error = $_.Exception.Message
  $noticeManifest = 'out/app/third-party-notices/collection-manifest.json'
  if (Test-Path $noticeManifest) {
    $notice = Get-Content $noticeManifest -Raw | ConvertFrom-Json
    $detail.notice_summary = @{ status = $notice.status; blockers = $notice.blockers; warnings = $notice.warnings }
  }
  $logs = @{}
  foreach ($file in @('out/reports/dotnet-publish.log','out/reports/pyinstaller.log','out/reports/native-espeak.log','out/reports/notices.log','out/reports/installer.log','out/reports/windows-self-test.json','out/reports/installed-self-test.json','out/reports/native-ui.json')) {
    if (Test-Path $file) { $logs[$file] = @(Get-Content $file | Select-Object -Last 55) }
  }
  $detail.logs = $logs
  # Best effort here: a failed evidence upload must not replace the build error that got us here.
  try { Put-PrivateReport '.build/desktop-build.json' $detail }
  catch { Write-Output "Build evidence could not be recorded: $($_.Exception.Message)" }
  # If the installer itself was compiled but a later verification step failed, keep
  # a copy of that unsigned candidate instead of losing it with the runner: the
  # physical Windows 11 microphone, WebView2 and clean-PC install checks need a real
  # build to be run at all. The job still fails, and the release says so in its own
  # title - an incomplete candidate is never presented as a passed build.
  try {
    $salvage = Join-Path $root 'out/release/SPEAKCITY-AI-Setup-x64.exe'
    # Same rule as a passing build: a public repository never publishes the unsigned installer,
    # not even as a pre-release of a failed run.
    if ((Test-Path $salvage) -and -not (Test-RepositoryPrivate)) {
      Write-Output 'Incomplete candidate not published: the repository is public. The installer stays under out/release.'
    } elseif (Test-Path $salvage) {
      $reportsZip = Join-Path $root 'out/release/SPEAKCITY-build-reports.zip'
      try { Compress-Archive -Path (Join-Path $root 'out/reports/*') -DestinationPath $reportsZip -Force }
      catch { Write-Output "Build report archive skipped: $($_.Exception.Message)" }
      # Quotes/backticks would corrupt the value as it passes through gh's argv.
      $reason = ($detail.error -replace '[\r\n"`]', ' ')
      $tag = "desktop-preview-0.2.0-$env:GITHUB_RUN_NUMBER-incomplete"
      $salvaged = Publish-BuildRelease $tag 'SPEAKCITY candidate - AUTOMATED VERIFICATION INCOMPLETE' "Do not distribute. The build stopped in stage '$stage' after the installer was compiled: $reason This installer is published only so the owner can run the physical Windows 11 microphone, WebView2 and clean-PC install checks that a Windows Server runner cannot prove." @($salvage, (Join-Path $root 'out/release/SPEAKCITY-AI-source.zip'), $reportsZip) -Prerelease
      if ($salvaged) { $detail.incomplete_release_tag = $tag; Put-PrivateReport '.build/desktop-build.json' $detail }
    }
  } catch { Write-Output "Incomplete-candidate release skipped: $($_.Exception.Message)" }
  throw
}








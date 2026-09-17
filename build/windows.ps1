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
  $body = @{ message = 'Record desktop build evidence'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)); branch = 'main' }
  $existing = Invoke-WebRequest -Uri $uri -Headers $headers -SkipHttpErrorCheck
  if ($existing.StatusCode -eq 200) { $body.sha = ($existing.Content | ConvertFrom-Json).sha }
  elseif ($existing.StatusCode -ne 404) { throw "Cannot read report path: $($existing.StatusCode)" }
  Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 15) | Out-Null
}
function Download-Checked([string]$Url, [string]$Path, [string]$Sha) {
  Invoke-WebRequest -Uri $Url -OutFile $Path
  if ((Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha) { throw "Integrity check failed for $(Split-Path $Path -Leaf)" }
}
function Assert-Exit([string]$Name) { if ($LASTEXITCODE -ne 0) { throw "$Name returned exit code $LASTEXITCODE" } }
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
  $stage = 'decode-assets'
  $manifest = Get-Content assets-source/manifest.json -Raw | ConvertFrom-Json
  foreach ($asset in $manifest) {
    $target = Join-Path $root $asset.output
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    [IO.File]::WriteAllBytes($target, [Convert]::FromBase64String((Get-Content $asset.encoded -Raw)))
    if ((Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw "Asset hash mismatch: $($asset.output)" }
  }
  $stage = 'dotnet-sdk'
  $sdks = @(& dotnet --list-sdks)
  if (-not ($sdks -match '^10\.0\.')) {
    $script = Join-Path $env:RUNNER_TEMP 'dotnet-install.ps1'
    Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/install-scripts/47940ac9fc30a2f2dd19167165d0bb0774625f67/src/dotnet-install.ps1' -OutFile $script
    & $script -Version '10.0.401' -InstallDir (Join-Path $env:RUNNER_TEMP 'dotnet10') -NoPath
    $env:PATH = (Join-Path $env:RUNNER_TEMP 'dotnet10') + ';' + $env:PATH
  }
  dotnet --info | Out-File out/reports/dotnet.txt
  $stage = 'speech-environment'
  $python = (& py -3.11 -c 'import sys; print(sys.executable)').Trim()
  if (-not (Test-Path $python)) { throw 'Python 3.11 x64 was not found on the Windows build runner.' }
  & $python -m venv out/venv
  Assert-Exit 'Create build environment'
  $buildPython = Join-Path $root 'out/venv/Scripts/python.exe'
  & $buildPython -m pip install --disable-pip-version-check --only-binary=:all: -r speech/requirements.txt pyinstaller
  Assert-Exit 'Install speech dependencies'
  $stage = 'controlled-native-speech'
  & (Join-Path $root 'build/native_espeak.ps1') -PythonPath $buildPython 2>&1 | Tee-Object out/reports/native-espeak.log
  if (-not $?) { throw 'Controlled eSpeak source build failed.' }
  $stage = 'speech-model-download'
  & $buildPython speech/download_assets.py --models out/models
  Assert-Exit 'Download verified speech models'
  $stage = 'speech-package'
  & $buildPython -m PyInstaller --noconfirm --clean --distpath out/worker --workpath out/pyinstaller speech/speech_worker.spec 2>&1 | Tee-Object out/reports/pyinstaller.log
  Assert-Exit 'Freeze Windows speech worker'
  Copy-Item out/worker/speakcity-speech-worker out/app/speech -Recurse
  Copy-Item out/models out/app/speech/models -Recurse
  $stage = 'desktop-compile'
  dotnet publish src/SpeakCity/SpeakCity.csproj -c Release -r win-x64 --self-contained true -o out/app 2>&1 | Tee-Object out/reports/dotnet-publish.log
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
  Copy-Item docs out/app/docs -Recurse
  & $buildPython build/collect_notices.py --output out/app/third-party-notices --sources out/third-party-source 2>&1 | Tee-Object out/reports/notices.log
  Assert-Exit 'Collect bundled component notices and source'
  $stage = 'installer-tools'
  $innoSetup = Join-Path $env:RUNNER_TEMP 'innosetup-6.4.3.exe'
  Download-Checked 'https://github.com/jrsoftware/issrc/releases/download/is-6_4_3/innosetup-6.4.3.exe' $innoSetup 'f3c42116542c4cc57263c5ba6c4feabfc49fe771f2f98a79d2f7628b8762723b'
  $inno = Join-Path $env:RUNNER_TEMP 'inno'
  $installCompiler = Start-Process $innoSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$inno`"") -Wait -PassThru
  if ($installCompiler.ExitCode -ne 0) { throw 'Installer compiler setup failed.' }
  $webview = Join-Path $root 'out/bootstrap/MicrosoftEdgeWebview2Setup.exe'
  Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webview
  $signature = Get-AuthenticodeSignature $webview
  if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') { throw 'Official WebView2 bootstrapper signature verification failed.' }
  $stage = 'installer-compile'
  & (Join-Path $inno 'ISCC.exe') "/DPayloadDir=$root\out\app" "/DReleaseDir=$root\out\release" "/DBootstrapDir=$root\out\bootstrap" build/installer.iss 2>&1 | Tee-Object out/reports/installer.log
  Assert-Exit 'Compile installer'
  $installer = Join-Path $root 'out/release/SPEAKCITY-AI-Setup-x64.exe'
  $stage = 'installer-test'
  $installDir = Join-Path $env:RUNNER_TEMP 'SpeakCity Test'
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
    if (-not $uiProcess.WaitForExit(240000)) { $uiProcess.Kill($true); throw 'Native WebView2 UI test exceeded 240 seconds.' }
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
  $stage = 'private-release'
  # Publishing needs a token and a repository. On a developer PC the build simply
  # stops once the files exist, and that is a completed build, not a failure.
  if ([bool]$env:GH_TOKEN -and [bool]$env:GITHUB_REPOSITORY) {
    $sourceZip = Join-Path $root 'out/release/SPEAKCITY-AI-source.zip'
    git archive -o $sourceZip HEAD
    Assert-Exit 'Archive source'
    Compress-Archive out/third-party-source/* out/release/SPEAKCITY-third-party-source.zip
    $tag = "desktop-preview-0.2.0-$env:GITHUB_RUN_NUMBER"
    $uiNote = if ($detail.ui_smoke) { $detail.ui_smoke } else { 'not run' }
    $published = Publish-BuildRelease $tag 'SPEAKCITY Windows desktop candidate' ("Private unsigned Windows x64 build. Local speech bundled; configure an eligible AI API in the native settings screen. Windows Server packaged tests are included; real provider and physical Windows 11 microphone tests remain pending. Interactive UI check on this build agent: $uiNote.") @($installer, $sourceZip, (Join-Path $root 'out/release/SPEAKCITY-third-party-source.zip'), (Join-Path $root 'out/reports/native-ui.png'))
    if (-not $published) { throw 'Create private release' }
    $detail.release_tag = $tag
    # Temporary GitHub-provided download redirects, stored only in this private repo.
    # Never store or print GH_TOKEN. These URLs are file-specific and expire.
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
  Put-PrivateReport '.build/desktop-build.json' $detail
  # If the installer itself was compiled but a later verification step failed, keep
  # a copy of that unsigned candidate instead of losing it with the runner: the
  # physical Windows 11 microphone, WebView2 and clean-PC install checks need a real
  # build to be run at all. The job still fails, and the release says so in its own
  # title - an incomplete candidate is never presented as a passed build.
  try {
    $salvage = Join-Path $root 'out/release/SPEAKCITY-AI-Setup-x64.exe'
    if (Test-Path $salvage) {
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

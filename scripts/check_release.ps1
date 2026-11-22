# Builds an isolated candidate and verifies it without opening the app or contacting a phone.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'PMKUnlocker/PMKUnlocker.csproj'
$runId = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $root "release-candidates/$runId"
New-Item -ItemType Directory -Path $output -Force | Out-Null

# Report every missing asset before a build, so an incomplete package fails with the
# whole list instead of surfacing as a confusing regression failure.
& (Join-Path $PSScriptRoot 'release_preflight.ps1') -Root $root -JsonPath (Join-Path $output 'preflight.json')
if ($LASTEXITCODE -ne 0) { throw 'Release preflight failed: the missing release assets are listed above.' }

function Invoke-CheckedDotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Release check failed: dotnet $($Arguments -join ' ')" }
}

Invoke-CheckedDotnet @('build', $project, '--no-restore', '-c', 'Release', '-v', 'quiet')
Invoke-CheckedDotnet @('build', (Join-Path $root 'PMKLicenseAdmin/PMKLicenseAdmin.csproj'), '--no-restore', '-c', 'Release', '-v', 'quiet')
Invoke-CheckedDotnet @('run', '--project', (Join-Path $root 'tests/Regression/Regression.csproj'), '--no-restore')
& (Join-Path $root 'tests/verify-loader-package.ps1')
Invoke-CheckedDotnet @('publish', $project, '--no-build', '--no-restore', '-c', 'Release', '-o', $output, '-v', 'quiet')

# The license server ships inside the installer: the app auto-starts server\PMKLicenseServer.exe when it
# finds one there. Self-contained + trimmed, so a shop PC needs no .NET runtime of its own (~28 MB);
# a running server locks bin/Release, so it is published straight into the candidate.
$serverOut = Join-Path $output 'server'
Invoke-CheckedDotnet @('publish', (Join-Path $root 'PMKLicenseServer/PMKLicenseServer.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishTrimmed=true',
    '-o', $serverOut, '-v', 'quiet')

$required = @(
    'tools/mtkclient/mtk.exe', 'pmk_models.json', 'integrity.json',
    'PMKUnlocker.exe', 'PMKUnlocker.dll', 'PMKUnlocker.deps.json', 'PMKUnlocker.runtimeconfig.json',
    'pmk_devices.json', 'pmk_qc_loaders.json', 'pmk_codenames.json', 'adb.exe', 'fastboot.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll',
    'fh_loader.exe', 'QSaharaServer.exe', 'samsung/heimdall.exe', 'samsung/libusb-1.0.dll',
    'libusb-1.0.dll', 'Potato.Fastboot.dll', 'Potato.ImageFlasher.dll', 'LibUsbDotNet.LibUsbDotNet.dll', 'System.IO.Ports.dll',
    'THIRD_PARTY_NOTICES.md',
    # bundled license server (self-contained: coreclr.dll is what proves no runtime install is needed)
    'server/PMKLicenseServer.exe', 'server/PMKLicenseServer.dll', 'server/appsettings.json', 'server/coreclr.dll'
)
$missingFiles = @()
foreach ($relative in $required) {
    $file = Join-Path $output $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        $missingFiles += $relative
    }
}
if ($missingFiles.Count -gt 0) {
    throw ("Missing or empty release files ({0}): {1}" -f $missingFiles.Count, ($missingFiles -join ', '))
}
$staleFiles = @()
foreach ($relative in @('pmk_devices.json', 'pmk_qc_loaders.json', 'pmk_codenames.json', 'pmk_models.json')) {
    if ((Get-FileHash (Join-Path $root "PMKUnlocker/$relative")).Hash -ne
        (Get-FileHash (Join-Path $output $relative)).Hash) { $staleFiles += $relative }
}
if ($staleFiles.Count -gt 0) { throw ("Stale published files: " + ($staleFiles -join ', ')) }

# The shipped server has to start with the shipped config, not just exist: run it from the candidate with a
# throwaway %LOCALAPPDATA%, prove it expands the DataFile there, bootstraps an admin key file, and answers
# login/admin/list. The port is picked free so a running shop server on 5260 cannot mask a failure.
$shippedSettings = Get-Content -LiteralPath (Join-Path $serverOut 'appsettings.json') -Raw | ConvertFrom-Json
if ($shippedSettings.DataFile -notmatch '%LOCALAPPDATA%') {
    throw "server/appsettings.json must place the license data in %LOCALAPPDATA% (found '$($shippedSettings.DataFile)')"
}
$smokeRoot = Join-Path $env:TEMP ('pmk-server-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$smokePort = 5380
while ($true) {
    $busy = $false
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $smokePort); $busy = $true; $c.Close() } catch { }
    if (!$busy) { break }
    $smokePort++
    if ($smokePort -gt 5440) { throw 'No free port for the license server smoke test' }
}
$smokePid = 0
$smokeVerdict = 'not run'
# The DataFile argument carries a literal %TEMP% placeholder: if the server failed to expand environment
# variables it would create a folder literally named "%TEMP%" and every check below would fail. The
# "data" folder is deliberately not pre-created - the server's own Directory.CreateDirectory proves it.
$smokeDataDir = Join-Path $smokeRoot 'data'
$smokeDataFile = Join-Path $smokeDataDir 'licenses.json'
$smokeDataArg = '%TEMP%\' + (Split-Path -Leaf $smokeRoot) + '\data\licenses.json'
try {
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    $smokeProc = Start-Process -FilePath (Join-Path $serverOut 'PMKLicenseServer.exe') -WorkingDirectory $serverOut `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $smokeRoot 'server.out.log') `
        -RedirectStandardError (Join-Path $smokeRoot 'server.err.log') `
        -ArgumentList @("--DataFile=$smokeDataArg", "--Urls=http://127.0.0.1:$smokePort")
    $smokePid = $smokeProc.Id
    $up = $false
    for ($i = 0; $i -lt 240 -and !$up; $i++) {
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $smokePort); $up = $true; $c.Close() } catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$up) { throw "The shipped server did not open port $smokePort from the candidate folder" }
    $base = "http://127.0.0.1:$smokePort"
    $smokeHttp = Invoke-WebRequest -Uri "$base/" -UseBasicParsing -TimeoutSec 10
    if ($smokeHttp.StatusCode -ne 200 -or $smokeHttp.Content -notmatch 'PMK License Server OK') { throw "Server root answered $($smokeHttp.StatusCode)" }
    if (!(Test-Path -LiteralPath $smokeDataDir -PathType Container)) { throw "The server did not expand %TEMP% - $smokeDataDir was not created" }
    if (!(Test-Path -LiteralPath ($smokeDataFile + '.lock'))) { throw "The server locked neither $smokeDataFile nor anything next to it" }
    if (Test-Path -LiteralPath (Join-Path $serverOut '%TEMP%')) { throw 'A literal %TEMP% folder was created - environment expansion did not run' }
    $keyFile = Get-ChildItem -LiteralPath $smokeDataDir -Filter 'admin-key-*.txt' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$keyFile) { throw 'The server did not write the one-time admin key file next to the license data' }
    $adminKey = ((Get-Content -LiteralPath $keyFile.FullName) -split "`r?`n" | Where-Object { $_.Trim() -match '^[0-9A-Fa-f]{64}$' } | Select-Object -First 1)
    if (!$adminKey) { throw "No 64-hex admin key inside $($keyFile.Name)" }
    $headers = @{ 'X-Admin-Key' = $adminKey }
    $badLogin = Invoke-RestMethod -Uri "$base/api/login" -Method Post -ContentType 'application/json' -TimeoutSec 10 -Body (@{ email = 'nobody@shop.local'; password = 'wrong-password' } | ConvertTo-Json)
    if ($badLogin.ok -ne $false) { throw 'A login with unknown credentials was not rejected' }
    $created = Invoke-RestMethod -Uri "$base/api/admin/upsert" -Method Post -Headers $headers -ContentType 'application/json' -TimeoutSec 10 -Body (@{ email = 'smoke@shop.local'; password = 'SmokePass-123'; plan = 'yearly' } | ConvertTo-Json)
    if ($created.ok -ne $true) { throw 'The admin endpoint refused to create a throwaway account' }
    $goodLogin = Invoke-RestMethod -Uri "$base/api/login" -Method Post -ContentType 'application/json' -TimeoutSec 10 -Body (@{ email = 'smoke@shop.local'; password = 'SmokePass-123' } | ConvertTo-Json)
    if ($goodLogin.ok -ne $true -or [int]$goodLogin.daysLeft -lt 300) { throw 'The account created through the admin API could not log in' }
    $list = @(Invoke-RestMethod -Uri "$base/api/admin/list" -Headers $headers -TimeoutSec 10)
    if ($list.Count -ne 1) { throw "admin/list returned $($list.Count) accounts instead of 1" }
    if (!(Test-Path -LiteralPath $smokeDataFile)) { throw "The account was not persisted to $smokeDataFile" }
    $smokeVerdict = 'passed'
}
catch {
    $smokeVerdict = 'failed: ' + $_.Exception.Message
    $errTail = ''
    $errLog = Join-Path $smokeRoot 'server.err.log'
    if (Test-Path -LiteralPath $errLog) { $errTail = ' | stderr: ' + ((Get-Content -LiteralPath $errLog -Tail 3) -join ' ') }
    throw "Bundled license server smoke test $smokeVerdict$errTail"
}
finally {
    if ($smokePid -gt 0) { Stop-Process -Id $smokePid -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$loaderCheck = & (Join-Path $PSScriptRoot 'verify_qc_loaders.ps1') -Root $output -SourceRoot (Join-Path $root 'PMKUnlocker/bundled-tools')
$null = Get-Content -LiteralPath (Join-Path $output 'pmk_codenames.json') -Raw -Encoding utf8 | ConvertFrom-Json
$database = Get-Content -LiteralPath (Join-Path $output 'pmk_devices.json') -Raw -Encoding utf8 | ConvertFrom-Json
$deviceCounts = [ordered]@{}
foreach ($key in @('mtk', 'qc', 'spd', 'samsung')) {
    if (!$database.$key -or $database.$key.Count -eq 0) { throw "Empty device catalog: $key" }
    $deviceCounts[$key] = $database.$key.Count
}

# A local Desktop/PATH dependency does not count as a bundled release dependency.
$dependencyChecks = [ordered]@{
    'MediaTek CLI' = @(Get-ChildItem -LiteralPath $output -Filter 'mtk.exe' -Recurse -File).Count -gt 0
    'Python runtime' = @(Get-ChildItem -LiteralPath $output -Filter 'python.exe' -Recurse -File).Count -gt 0
    'Qualcomm EDL module' = @(Get-ChildItem -LiteralPath $output -Filter 'edl.py' -Recurse -File).Count -gt 0
    'Unisoc spd_dump' = @(Get-ChildItem -LiteralPath $output -Filter 'spd_dump.exe' -Recurse -File).Count -gt 0
    'Unisoc PAC unpacker' = @(Get-ChildItem -LiteralPath $output -Filter 'unpac.exe' -Recurse -File).Count -gt 0
}
$missing = @($dependencyChecks.Keys | Where-Object { !$dependencyChecks[$_] })
$version = (Get-Item -LiteralPath (Join-Path $output 'PMKUnlocker.dll')).VersionInfo.FileVersion
$manifest = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($output.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'file-manifest.json') -Encoding utf8
# The published folder proves itself, not only the build: --self-check re-hashes every packed entry,
# resolves each required tool, parses the catalogs and audits any installed recipe. No phone is touched.
# The app manifest asks for administrator, so a non-elevated shell cannot start it; that is reported as a
# skip and never as a pass.
$selfCheckExe = Join-Path $output 'PMKUnlocker.exe'
$selfCheckLog = Join-Path $output 'self-check.log'
$selfCheckVerdict = 'skipped'
$selfCheckRan = $false
try {
    $selfCheckProcess = Start-Process -FilePath $selfCheckExe -ArgumentList '--self-check' -Wait -PassThru -WindowStyle Hidden
    $selfCheckRan = $true
    $selfCheckVerdict = if ($selfCheckProcess.ExitCode -eq 0) { 'passed' } else { "failed (exit $($selfCheckProcess.ExitCode))" }
}
catch {
    $selfCheckVerdict = if ($_.Exception.Message -match 'elevation') { 'skipped (needs an elevated shell)' }
                        else { 'skipped (' + $_.Exception.Message + ')' }
}
if ($selfCheckRan) {
    $selfCheckWritten = Join-Path $env:LOCALAPPDATA 'PMKMobileTool/pmk_selfcheck.log'
    if (Test-Path -LiteralPath $selfCheckWritten) { Copy-Item -LiteralPath $selfCheckWritten -Destination $selfCheckLog -Force }
}
if ($selfCheckVerdict -like 'failed*') {
    throw "Packaged self-check $selfCheckVerdict - read $selfCheckLog"
}

$report = [ordered]@{
    version = $version
    checkedAt = (Get-Date).ToString('o')
    buildPassed = $true
    regressionTestsPassed = $true
    requiredFilesVerified = $required.Count
    deviceCatalogEntries = $deviceCounts
    qualcommLoaders = $loaderCheck
    packagedSelfCheck = $selfCheckVerdict
    bundledLicenseServer = 'self-contained win-x64 (trimmed), smoke test ' + $smokeVerdict
    missingBundledDependencies = $missing
    selfContainedRuntime = Test-Path -LiteralPath (Join-Path $output 'hostfxr.dll')
    hardwareTested = $false
    cleanWindowsInstallTested = $false
    redistributionTermsReviewed = $false
    readyForShopDistribution = $false
    note = 'Build/test success is not device compatibility certification. Resolve dependencies, document supported devices, review bundled component redistribution terms, and validate on clean Windows before distribution.'
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'release-check.json') -Encoding utf8
Write-Output "Candidate: $output"
Write-Output "Build, regression tests, and $($required.Count) required files: PASS"
Write-Output "Packaged self-check: $selfCheckVerdict"
Write-Output "Bundled license server: smoke test $smokeVerdict"
Write-Output "Missing bundled dependencies: $($missing -join ', ')"
Write-Output 'Distribution readiness: NOT YET VERIFIED (see release-check.json).'

# Builds an isolated candidate and verifies it without opening the app or contacting a phone.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'PMKUnlocker/PMKUnlocker.csproj'
$runId = (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $root "release-candidates/$runId"
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Invoke-CheckedDotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Release check failed: dotnet $($Arguments -join ' ')" }
}

Invoke-CheckedDotnet @('build', $project, '--no-restore', '-c', 'Release', '-v', 'quiet')
Invoke-CheckedDotnet @('run', '--project', (Join-Path $root 'tests/Regression/Regression.csproj'), '--no-restore')
& (Join-Path $root 'tests/verify-loader-package.ps1')
Invoke-CheckedDotnet @('publish', $project, '--no-build', '--no-restore', '-c', 'Release', '-o', $output, '-v', 'quiet')

$required = @(
    'PMKUnlocker.exe', 'PMKUnlocker.dll', 'PMKUnlocker.deps.json', 'PMKUnlocker.runtimeconfig.json',
    'pmk_devices.json', 'pmk_mtk_op.py', 'pmk_qc_loaders.json', 'pmk_codenames.json', 'adb.exe', 'fastboot.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll',
    'fh_loader.exe', 'QSaharaServer.exe', 'samsung/heimdall.exe', 'samsung/libusb-1.0.dll',
    'libusb-1.0.dll', 'Potato.Fastboot.dll', 'Potato.ImageFlasher.dll', 'LibUsbDotNet.LibUsbDotNet.dll', 'System.IO.Ports.dll'
)
foreach ($relative in $required) {
    $file = Join-Path $output $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Missing or empty release file: $relative"
    }
}
foreach ($relative in @('pmk_devices.json', 'pmk_mtk_op.py', 'pmk_qc_loaders.json', 'pmk_codenames.json')) {
    if ((Get-FileHash (Join-Path $root "PMKUnlocker/$relative")).Hash -ne
        (Get-FileHash (Join-Path $output $relative)).Hash) { throw "Stale published file: $relative" }
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
$report = [ordered]@{
    version = $version
    checkedAt = (Get-Date).ToString('o')
    buildPassed = $true
    regressionTestsPassed = $true
    requiredFilesVerified = $required.Count
    deviceCatalogEntries = $deviceCounts
    qualcommLoaders = $loaderCheck
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
Write-Output "Missing bundled dependencies: $($missing -join ', ')"
Write-Output 'Distribution readiness: NOT YET VERIFIED (see release-check.json).'

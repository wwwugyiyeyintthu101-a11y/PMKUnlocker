# Reports every release asset this checkout is missing in one pass, before any build runs.
# Exit 1 = a mandatory asset, a seed/lock mismatch, or an incomplete loader store; 0 = ready to build.
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$JsonPath
)
$ErrorActionPreference = 'Stop'

$Root = [IO.Path]::GetFullPath($Root)
$project = Join-Path $Root 'PMKUnlocker'
$bundled = Join-Path $project 'bundled-tools'

function Test-Present([string]$Path) {
    return ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-Item -LiteralPath $Path).Length -gt 0)
}

# Bare tool names are resolved the way ShopServices.FindTool does at runtime.
function Find-Bundled([string]$Name) {
    foreach ($sub in @('', 'tools', 'samsung', 'spd', 'python', 'mtk', 'edl')) {
        $candidate = Join-Path (Join-Path $bundled $sub) $Name
        if (Test-Present $candidate) { return $candidate }
    }
    return $null
}

$required = @(
    @{ Name = 'tools/mtkclient/mtk.exe'; Path = (Join-Path $project 'tools/mtkclient/mtk.exe') },
    @{ Name = 'pmk_models.json'; Path = (Join-Path $project 'pmk_models.json') },
    @{ Name = 'pmk_devices.json'; Path = (Join-Path $project 'pmk_devices.json') },
    @{ Name = 'pmk_qc_loaders.json'; Path = (Join-Path $project 'pmk_qc_loaders.json') },
    @{ Name = 'pmk_codenames.json'; Path = (Join-Path $project 'pmk_codenames.json') },
    @{ Name = 'adb.exe'; Path = (Find-Bundled 'adb.exe') },
    @{ Name = 'fastboot.exe'; Path = (Find-Bundled 'fastboot.exe') },
    @{ Name = 'AdbWinApi.dll'; Path = (Find-Bundled 'AdbWinApi.dll') },
    @{ Name = 'AdbWinUsbApi.dll'; Path = (Find-Bundled 'AdbWinUsbApi.dll') },
    @{ Name = 'fh_loader.exe'; Path = (Find-Bundled 'fh_loader.exe') },
    @{ Name = 'QSaharaServer.exe'; Path = (Find-Bundled 'QSaharaServer.exe') },
    @{ Name = 'samsung/heimdall.exe'; Path = (Join-Path $bundled 'samsung/heimdall.exe') },
    @{ Name = 'samsung/libusb-1.0.dll'; Path = (Join-Path $bundled 'samsung/libusb-1.0.dll') },
    @{ Name = 'libusb-1.0.dll'; Path = (Join-Path $project 'libs/potato/libusb-1.0.dll') },
    @{ Name = 'Potato.Fastboot.dll'; Path = (Join-Path $project 'libs/potato/Potato.Fastboot.dll') },
    @{ Name = 'Potato.ImageFlasher.dll'; Path = (Join-Path $project 'libs/potato/Potato.ImageFlasher.dll') },
    @{ Name = 'LibUsbDotNet.LibUsbDotNet.dll'; Path = (Join-Path $project 'libs/potato/LibUsbDotNet.LibUsbDotNet.dll') }
)
# Produced by the build itself, so they are not source assets.
$buildOutputs = @('PMKUnlocker.exe', 'PMKUnlocker.dll', 'PMKUnlocker.deps.json', 'PMKUnlocker.runtimeconfig.json',
    'System.IO.Ports.dll', 'integrity.json')

$missingRequired = @($required | Where-Object { !(Test-Present $_.Path) } | ForEach-Object { $_.Name })

# The build target refuses to run when the seed does not match the lock, so report that here.
$seedPath = Join-Path $bundled 'integrity.json'
$lockPath = Join-Path $Root 'dependency-lock.json'
$seed = $null
if ((Test-Present $seedPath) -and (Test-Present $lockPath)) {
    $seedHash = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
    # ConvertFrom-Json does not enumerate a JSON array on the pipeline (PS 5.1), so count after binding.
    $seedEntries = Get-Content -LiteralPath $seedPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $seedCount = @($seedEntries).Count
    $seed = [ordered]@{
        path = 'PMKUnlocker/bundled-tools/integrity.json'
        entries = $seedCount
        sha256 = $seedHash
        lockSha256 = $lock.bundledInventory.sha256
        lockEntries = $lock.bundledInventory.entries
        matches = ($seedHash -eq $lock.bundledInventory.sha256 -and $seedCount -eq [int]$lock.bundledInventory.entries)
    }
}

$qc = [ordered]@{ brands = 0; entries = 0; unique = 0; present = 0; missing = 0; missingSample = @(); missingAll = @() }
$dbPath = Join-Path $project 'pmk_qc_loaders.json'
if (Test-Present $dbPath) {
    $db = Get-Content -LiteralPath $dbPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $brands = @($db.PSObject.Properties)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($brand in $brands) {
        foreach ($model in $brand.Value.PSObject.Properties) {
            if ($model.Value -is [string]) {
                $qc.entries++
                [void]$seen.Add($model.Value)
            }
        }
    }
    $qc.brands = $brands.Count
    $qc.unique = $seen.Count
    $missingLoaders = [Collections.Generic.List[string]]::new()
    foreach ($relative in $seen) {
        if (Test-Present (Join-Path $bundled $relative)) { $qc.present++ } else { $missingLoaders.Add($relative) }
    }
    $qc.missing = $missingLoaders.Count
    $qc.missingAll = @($missingLoaders)
    $qc.missingSample = @($missingLoaders | Select-Object -First 10)
}

$mtkRoot = Join-Path $bundled 'mtk/loaders'
$mtk = [ordered]@{
    path = 'PMKUnlocker/bundled-tools/mtk/loaders'
    present = (Test-Path -LiteralPath $mtkRoot -PathType Container)
    files = 0
}
if ($mtk.present) { $mtk.files = @(Get-ChildItem -LiteralPath $mtkRoot -Recurse -File -ErrorAction SilentlyContinue).Count }

$optional = [ordered]@{}
foreach ($name in @('python.exe', 'edl.py', 'spd_dump.exe', 'unpac.exe')) { $optional[$name] = [bool](Find-Bundled $name) }

$failures = [Collections.Generic.List[string]]::new()
if ($missingRequired.Count -gt 0) { $failures.Add("$($missingRequired.Count) required file(s) missing") }
if (!$seed) { $failures.Add('bundled-tools/integrity.json or dependency-lock.json missing') }
elseif (!$seed.matches) { $failures.Add('integrity.json seed does not match dependency-lock.json') }
if ($qc.missing -gt 0) { $failures.Add("$($qc.missing) Qualcomm loader(s) missing") }
if (!$mtk.present) { $failures.Add('bundled-tools/mtk/loaders tree missing') }
elseif ($mtk.files -eq 0) { $failures.Add('bundled-tools/mtk/loaders is empty') }

Write-Output ('Release preflight (source tree): ' + $Root)
Write-Output ("Required assets: {0}/{1} present" -f ($required.Count - $missingRequired.Count), $required.Count)
foreach ($name in $missingRequired) { Write-Output ('  MISSING ' + $name) }
Write-Output ('Build outputs (not checked here): ' + ($buildOutputs -join ', '))
if ($seed) {
    $seedState = 'MISMATCH'
    if ($seed.matches) { $seedState = 'ok' }
    Write-Output ("Seed: {0} ({1} entries, lock pin {2})" -f $seedState, $seed.entries, $seed.lockEntries)
} else {
    Write-Output 'Seed: MISSING'
}
if ($qc.unique -gt 0) {
    Write-Output ("Qualcomm loaders: {0}/{1} present under edl/loaders" -f $qc.present, $qc.unique)
    if ($qc.missing -gt 0) { Write-Output ('  missing sample: ' + ($qc.missingSample -join ', ')) }
} else {
    Write-Output 'Qualcomm loaders: database missing or empty'
}
if (!$mtk.present) { Write-Output ('MTK wired catalog: MISSING ' + $mtk.path) }
else { Write-Output ("MTK wired catalog: {0} file(s) under {1}" -f $mtk.files, $mtk.path) }
$optionalState = @($optional.Keys | ForEach-Object { '{0}={1}' -f $_, $(if ($optional[$_]) { 'present' } else { 'missing' }) })
Write-Output ('Optional runtimes: ' + ($optionalState -join ' '))

if ($failures.Count -gt 0) {
    Write-Output ('FAILED: ' + ($failures -join '; ') + '.')
} else {
    Write-Output 'PASS: release assets are present.'
}

if ($JsonPath) {
    $dir = Split-Path -Parent $JsonPath
    if ($dir -and !(Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $report = [ordered]@{
        checkedAt = (Get-Date).ToString('o')
        ok = ($failures.Count -eq 0)
        missingRequired = @($missingRequired)
        buildOutputs = @($buildOutputs)
        seed = $seed
        qualcommLoaders = $qc
        mtkCatalog = $mtk
        optionalRuntimes = $optional
        failures = @($failures)
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $JsonPath -Encoding UTF8
    Write-Output ('Report: ' + $JsonPath)
}

if ($failures.Count -gt 0) { exit 1 }
exit 0

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$verify = Join-Path $root 'scripts/verify_qc_loaders.ps1'
$fixture = Join-Path $PSScriptRoot ('loader-fixture-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'source'
$target = Join-Path $fixture 'target'
function Rejects([string]$pattern) {
    try { & $verify -Root $target -SourceRoot $source | Out-Null }
    catch { if ($_.Exception.Message -like $pattern) { return }; throw }
    throw "Expected validation failure: $pattern"
}
try {
    foreach ($base in @($source,$target)) {
        New-Item -ItemType Directory -Force (Join-Path $base 'edl/loaders/Test') | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $base 'edl/loaders/Test/test.elf'), [byte[]]@(1,2,3))
    }
    $json = Join-Path $target 'pmk_qc_loaders.json'
    '{"Test":{"Model":"edl/loaders/Test/test.elf"}}' | Set-Content -LiteralPath $json
    $result = & $verify -Root $target -SourceRoot $source
    if ($result.Entries -ne 1 -or !$result.HashesCompared) { throw 'Valid package failed.' }
    $file = Join-Path $target 'edl/loaders/Test/test.elf'
    [IO.File]::WriteAllBytes($file, [byte[]]@(3,2,1))
    Rejects 'Changed loader:*'
    [IO.File]::WriteAllBytes($file, [byte[]]@())
    Rejects 'Missing/empty loader:*'
    Remove-Item -LiteralPath $file
    Rejects 'Missing/empty loader:*'
    '{"Test":{"Model":"edl/loaders/../../../outside.elf"}}' | Set-Content -LiteralPath $json
    Rejects 'Loader escapes package:*'
    Write-Output '5 loader-package checks passed (valid, changed, empty, missing, path escape).'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

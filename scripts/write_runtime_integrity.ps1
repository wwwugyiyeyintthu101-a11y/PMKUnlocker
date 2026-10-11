param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [switch]$VerifyBundled
)
$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$seed = Join-Path $SourceRoot 'bundled-tools/integrity.json'
if (!(Test-Path -LiteralPath $seed)) { throw 'Missing bundled integrity inventory. Restore the approved tool package first.' }
$entries = @{}
foreach ($entry in (Get-Content -LiteralPath $seed -Raw | ConvertFrom-Json)) {
    if (!$entry.k -or $entry.h -notmatch '^[0-9a-fA-F]{64}$' -or $entries.ContainsKey($entry.k)) {
        throw 'Invalid or duplicate bundled integrity entry.'
    }
    $entries[$entry.k] = $entry.h
}
function Get-IntegrityHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
$approval = Get-Content -LiteralPath (Join-Path $SourceRoot '../dependency-lock.json') -Raw | ConvertFrom-Json
if ($approval.schema -ne 1 -or (Get-IntegrityHash $seed) -ne $approval.bundledInventory.sha256) {
    throw 'Bundled inventory differs from dependency-lock.json. Review replacement tools before updating the lock.'
}
if ($VerifyBundled) {
    foreach ($relative in $entries.Keys) {
        $target = [IO.Path]::GetFullPath((Join-Path $OutputRoot $relative))
        $allowed = $OutputRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
        if (!$target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
            !(Test-Path -LiteralPath $target -PathType Leaf) -or (Get-IntegrityHash $target) -ne $entries[$relative]) {
            throw "Missing, changed or unsafe bundled tool: $relative"
        }
    }
}
function Add-SourceFile([string]$Source, [string]$Relative) {
    $target = Join-Path $OutputRoot $Relative
    if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "Missing packaged file: $Relative" }
    $hash = Get-IntegrityHash $Source
    if ((Get-IntegrityHash $target) -ne $hash) { throw "Stale packaged file: $Relative" }
    $entries[$Relative.Replace('\','/')] = $hash
}
foreach ($name in @('pmk_mtk_op.py','pmk_flash_guard.py','pmk_sparse.py','pmk_gpt.py')) {
    Add-SourceFile (Join-Path $SourceRoot $name) $name
}
foreach ($library in $approval.libraries) {
    $source = Join-Path (Split-Path $SourceRoot -Parent) $library.path
    if ((Get-IntegrityHash $source) -ne $library.sha256) { throw "Library differs from dependency lock: $($library.path)" }
    Add-SourceFile $source $library.target
}
$extraTools = Join-Path $SourceRoot 'tools/mtkbypass'
if (Test-Path -LiteralPath $extraTools) {
    Get-ChildItem -LiteralPath $extraTools -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' } | ForEach-Object {
            Add-SourceFile $_.FullName ('tools/mtkbypass/' + $_.FullName.Substring($extraTools.Length + 1).Replace('\','/'))
        }
}
$map = @($entries.Keys | Sort-Object | ForEach-Object { [ordered]@{ k = $_; h = $entries[$_] } })
$json = ConvertTo-Json -InputObject $map -Depth 3
[IO.File]::WriteAllText((Join-Path $OutputRoot 'integrity.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Output "Runtime integrity: $($map.Count) entries (bundled inventory plus project helpers)."

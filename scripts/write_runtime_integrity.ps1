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
foreach ($library in $approval.libraries) {
    $source = Join-Path (Split-Path $SourceRoot -Parent) $library.path
    if ((Get-IntegrityHash $source) -ne $library.sha256) { throw "Library differs from dependency lock: $($library.path)" }
    Add-SourceFile $source $library.target
}
$extraTools = Join-Path $SourceRoot 'tools/mtkclient'
$mtkLock = Get-Content -LiteralPath (Join-Path $SourceRoot '../mtkclient-lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($mtkLock.schema -ne 1 -or !($mtkLock.files | Where-Object path -eq 'mtk.exe')) { throw 'Missing mtkclient approval inventory.' }
foreach ($file in $mtkLock.files) {
    $source = [IO.Path]::GetFullPath((Join-Path $extraTools $file.path))
    if (!$source.StartsWith($extraTools.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-IntegrityHash $source) -ne $file.sha256) { throw "mtkclient file differs from lock: $($file.path)" }
    Add-SourceFile $source ('tools/mtkclient/' + $file.path)
}
$map = @($entries.Keys | Sort-Object | ForEach-Object { [ordered]@{ k = $_; h = $entries[$_] } })
$json = ConvertTo-Json -InputObject $map -Depth 3
[IO.File]::WriteAllText((Join-Path $OutputRoot 'integrity.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Output "Runtime integrity: $($map.Count) entries (bundled inventory plus project helpers)."

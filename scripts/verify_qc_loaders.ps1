param(
    [Parameter(Mandatory=$true)][string]$Root,
    [string]$SourceRoot
)
$ErrorActionPreference = 'Stop'
$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\','/')
function LoaderPath([string]$base, [string]$relative) {
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw 'Invalid loader path.' }
    $normalized = $relative.Replace('\','/')
    if (!$normalized.StartsWith('edl/loaders/', [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected loader folder: $relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $base $relative))
    $allowed = [IO.Path]::GetFullPath((Join-Path $base 'edl/loaders')) + [IO.Path]::DirectorySeparatorChar
    if (!$path.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Loader escapes package: $relative" }
    return $path
}
$db = Get-Content -LiteralPath (Join-Path $resolvedRoot 'pmk_qc_loaders.json') -Raw -Encoding utf8 | ConvertFrom-Json
$brands = @($db.PSObject.Properties)
if ($brands.Count -eq 0) { throw 'Empty loader database.' }
$entries = 0
$unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($brand in $brands) {
    foreach ($model in $brand.Value.PSObject.Properties) {
        if ($model.Value -isnot [string]) { throw "Invalid loader entry: $($brand.Name) / $($model.Name)" }
        $path = LoaderPath $resolvedRoot $model.Value
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Missing/empty loader: $($model.Value)" }
        if ($SourceRoot) {
            $original = LoaderPath $SourceRoot $model.Value
            if ((Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath $path).Hash) { throw "Changed loader: $($model.Value)" }
        }
        $entries++
        [void]$unique.Add($path)
    }
}
if ($entries -eq 0) { throw 'No loader entries found.' }
[pscustomobject]@{ Brands=$brands.Count; Entries=$entries; UniqueFiles=$unique.Count; HashesCompared=[bool]$SourceRoot }

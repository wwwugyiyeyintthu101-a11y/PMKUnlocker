# Publishes the installer and update.json that make_release.ps1 just produced.
#
# Usage:
#   $env:GITHUB_TOKEN = 'ghp_...'                # or -Token 'ghp_...'
#   .\scripts\publish_release.ps1 -DryRun        # show the plan, no network, no writes
#   .\scripts\publish_release.ps1                # create/replace the release and upload both assets
#
# The token needs "Contents: read and write" on the repository (classic: "repo"). It is never printed and never
# written to disk. Clients download these assets unauthenticated, so the repository must be public - the same
# rule make_release.ps1 enforces for update.json.
param(
    [string]$Token = '',
    [string]$Repo = 'wwwugyiyeyintthu101-a11y/PMKUnlocker',
    [string]$Tag = '',
    [string]$Notes = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'installer/output'
$updatePath = Join-Path $outDir 'update.json'
$pinnedRepo = 'wwwugyiyeyintthu101-a11y/PMKUnlocker'
if ($Repo -ne $pinnedRepo) { throw "update.json points at '$pinnedRepo'; refusing to publish for '$Repo'." }

if (!(Test-Path -LiteralPath $updatePath)) { throw "Missing $updatePath - run scripts/make_release.ps1 first." }
$update = Get-Content -LiteralPath $updatePath -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$update.version -or !$update.url -or !$update.sha256) { throw 'update.json is incomplete (version/url/sha256).' }

# The asset name comes from update.json's own url: the installer carries the four-part file version
# (7.3.6.0) while the release tag is the three-part one (7.3.6), and the url is the thing a client follows.
$installerName = [IO.Path]::GetFileName([uri]$update.url)
$installerPath = Join-Path $outDir $installerName
if (!(Test-Path -LiteralPath $installerPath)) { throw "The installer named by update.json is missing: $installerName" }

# The manifest is the contract a client checks after downloading. A mismatch here is the one failure that
# would reach every shop, so it is proven before anything is uploaded.
$actual = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $update.sha256.ToLowerInvariant()) {
    throw "update.json pins $($update.sha256) but $installerName is $actual - rebuild with make_release.ps1."
}
if ($update.url -notmatch '/releases/download/v([^/]+)/' -or $Matches[1] -ne $update.version) {
    throw "update.json's url does not point at v$($update.version): $($update.url)"
}

if (!$Tag) { $Tag = 'v' + $update.version }
if ($Tag -ne ('v' + $update.version)) { throw "Tag $Tag does not match update.json version $($update.version)." }

$commit = (& git -C $root rev-parse HEAD 2>$null)
if (!$commit) { throw 'Cannot read the current commit; run this inside the repository.' }
$tagCommit = (& git -C $root rev-list -n 1 $Tag 2>$null)
if (!$tagCommit) { throw "Tag $Tag does not exist locally. Create it on the published commit first." }
if ($tagCommit.Trim() -ne $commit.Trim()) {
    Write-Output "NOTE: tag $Tag is at $($tagCommit.Trim()), HEAD is at $($commit.Trim()). The release publishes the tag," 
    Write-Output '      so a delta of scripts/docs is harmless; if compiled code moved, rebuild, re-tag, then publish.'
}

$assetBytes = (Get-Item -LiteralPath $installerPath).Length
Write-Output "Repository : $Repo"
Write-Output "Tag        : $Tag ($($tagCommit.Trim()))"
Write-Output "Version    : $($update.version)"
Write-Output "Installer  : $installerName ($assetBytes bytes, sha256 ok)"
Write-Output "Manifest   : update.json"

if ($DryRun) {
    Write-Output ''
    Write-Output 'DRY RUN - nothing was sent. Without -DryRun this would:'
    Write-Output "  1. POST /repos/$Repo/releases  tag_name=$Tag  draft=false"
    Write-Output "     (if it already exists: reuse it, keeping its current body unless -Notes is given)"
    Write-Output "  2. upload $installerName and update.json, replacing same-named assets"
    Write-Output '  3. print the release URL and verify releases/latest/download/update.json'
    return
}

if (!$Token) { $Token = $env:GITHUB_TOKEN }
if (!$Token) {
    throw 'No token. Pass -Token or set $env:GITHUB_TOKEN, then run again. (Create one with "Contents: read and write".)'
}
$headers = @{
    Authorization          = 'token ' + $Token
    Accept                 = 'application/vnd.github+json'
    'User-Agent'           = 'PMK-release-publisher'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$api = "https://api.github.com/repos/$Repo"

function Invoke-GitHub([string]$Method, [string]$Uri, [object]$Body) {
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 5)
        $params.ContentType = 'application/json'
    }
    try {
        return Invoke-RestMethod @params
    }
    catch {
        $detail = ''
        if ($_.Exception.Response) {
            try {
                $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                $detail = $reader.ReadToEnd()
            }
            catch { }
        }
        throw "GitHub $Method $Uri failed: $($_.Exception.Message.Trim()) $detail"
    }
}

# 1. The release itself. Re-running the script must not fail on "already exists" - it updates instead, so a
#    half-finished publish can be finished by running the same command again.
$release = $null
try {
    $release = Invoke-GitHub 'GET' "$api/releases/tags/$Tag" $null
    Write-Output "Release    : already exists (#$($release.id))"
}
catch {
    if ($_.Exception.Message -notmatch '404') { throw }
}

if (!$release) {
    $body = @{
        tag_name         = $Tag
        name             = $Tag
        body             = if ($Notes) { $Notes } else { $update.notes }
        draft            = $false
        prerelease       = $false
        generate_release_notes = $false
    }
    $release = Invoke-GitHub 'POST' "$api/releases" $body
    Write-Output "Release    : created (#$($release.id))"
}
elseif ($Notes) {
    $release = Invoke-GitHub 'PATCH' "$api/releases/$($release.id)" @{ body = $Notes }
    Write-Output 'Release    : body updated'
}

# 2. Assets. A same-named asset cannot be replaced in place, so it is deleted first (the id is what matters,
#    not the name), which keeps "re-run the script" idempotent.
$uploadBase = ($release.upload_url -split '\{')[0]
foreach ($asset in @(
    @{ Path = $installerPath; Name = $installerName; Type = 'application/octet-stream' },
    @{ Path = $updatePath; Name = 'update.json'; Type = 'application/json' }
)) {
    $existing = @($release.assets | Where-Object { $_.name -eq $asset.Name })
    foreach ($old in $existing) {
        Invoke-GitHub 'DELETE' "$api/releases/assets/$($old.id)" $null | Out-Null
        Write-Output "Asset      : replaced $($asset.Name)"
    }
    $uri = '{0}?name={1}' -f $uploadBase, [uri]::EscapeDataString($asset.Name)
    Invoke-RestMethod -Method 'POST' -Uri $uri -Headers $headers -ContentType $asset.Type `
        -InFile $asset.Path -TimeoutSec 1800 | Out-Null
    if ($existing.Count -eq 0) { Write-Output "Asset      : uploaded $($asset.Name)" }
}

# 3. Prove it from the public side: releases/latest is what UpdateChecker.cs reads.
$latest = Invoke-RestMethod -Uri "https://github.com/$Repo/releases/latest/download/update.json" -UseBasicParsing
$latestVersion = ($latest | ConvertFrom-Json).version
if ($latestVersion -ne $update.version) {
    throw "Published, but releases/latest/download/update.json still says $latestVersion (expected $($update.version)). The repository may be private, or the CDN has not caught up - re-check in a minute."
}

Write-Output ''
Write-Output "Published  : https://github.com/$Repo/releases/tag/$Tag"
Write-Output "Auto-update: releases/latest/download/update.json -> $latestVersion"

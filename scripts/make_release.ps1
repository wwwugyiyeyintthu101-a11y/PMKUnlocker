# Builds a verified release candidate, compiles the Inno installer, and writes the
# GitHub Releases auto-update manifest (update.json) consumed by PMKUnlocker\UpdateChecker.cs.
#
# Usage:
#   .\scripts\make_release.ps1 -Repo "owner/repo" -Notes "bug fixes"
#
# Then create a GitHub release for tag v<version> and upload BOTH:
#   installer\output\PMKMobileTool-<ver>-Setup.exe
#   installer\output\update.json
# and put "owner/repo" into %LocalAppData%\PMKMobileTool\pmk_update_repo.txt on client PCs.
# NOTE: the repository must be PUBLIC — clients download release assets unauthenticated.
param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,

    [string]$Notes = '',

    # မကြာခင် run ထားပြီးသား release-candidates ကို ပြန်သုံး (check_release ထပ်မလုပ်ချင်ရင်)
    [switch]$ReuseCandidate
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
$iss = Join-Path $root 'installer\PMKMobileTool.iss'
$candidatesRoot = Join-Path $root 'release-candidates'

if (!(Test-Path -LiteralPath $iscc)) { throw "Inno Setup compiler not found: $iscc" }
if ($Repo -notmatch '^[\w.-]+/[\w.-]+$') { throw '-Repo must be "owner/repo"' }

if (!$ReuseCandidate) {
    & (Join-Path $PSScriptRoot 'check_release.ps1')
}

$candidate = Get-ChildItem -LiteralPath $candidatesRoot -Directory |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (!$candidate) { throw 'No release candidate found — run without -ReuseCandidate.' }
$candidatePath = $candidate.FullName

$version = (Get-Item -LiteralPath (Join-Path $candidatePath 'PMKUnlocker.dll')).VersionInfo.FileVersion
$verCore = [regex]::Match($version, '^\d+\.\d+\.\d+').Value
if (!$verCore) { throw "Cannot parse version from '$version'" }

$outDir = Join-Path $root 'installer\output'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

& $iscc "/DSourceDir=$candidatePath" "/DOutputDir=$outDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Get-ChildItem -LiteralPath $outDir -Filter 'PMKMobileTool-*-Setup.exe' |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (!$setup) { throw 'Installer output not found.' }

$hash = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$assetUrl = "https://github.com/$Repo/releases/download/v$verCore/$($setup.Name)"
$update = [ordered]@{
    version = $verCore
    url     = $assetUrl
    sha256  = $hash
    notes   = $Notes
}
$updatePath = Join-Path $outDir 'update.json'
[System.IO.File]::WriteAllText($updatePath, ($update | ConvertTo-Json))

Write-Output "Candidate : $candidatePath"
Write-Output "Version   : $verCore"
Write-Output "Installer : $($setup.FullName)"
Write-Output "SHA-256   : $hash"
Write-Output "Manifest  : $updatePath"
Write-Output ''
Write-Output "Next steps:"
Write-Output "  1. git tag v$verCore && git push origin v$verCore"
Write-Output "  2. GitHub -> Releases -> New release on tag v$verCore"
Write-Output "     upload: $($setup.Name)  +  update.json   (repo must be PUBLIC)"
Write-Output "  3. On client PCs: write 'owner/repo' into %LocalAppData%\PMKMobileTool\pmk_update_repo.txt"

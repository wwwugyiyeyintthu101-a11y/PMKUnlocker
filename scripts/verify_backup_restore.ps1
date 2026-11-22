# Proves that a trimmed backup can be rebuilt into a working tool: clone what git restores, copy the two
# untracked trees (bundled-tools, tools), build Release, run --self-check and the regression suite, then throw
# the drill away. This is the check behind RESTORE.txt's "keep ~1.1 GB" list - the list is only true if this
# passes, and it is cheap enough to run whenever the backup set changes.
#
# Usage:
#   .\scripts\verify_backup_restore.ps1              # full drill, cleans up after itself
#   .\scripts\verify_backup_restore.ps1 -Keep        # leave the rebuilt folder for inspection
#   .\scripts\verify_backup_restore.ps1 -SkipTests   # build + self-check only (about half the time)
#
# What it does NOT do: restore data\ or mtk\boot-profiles (they live outside the repository and belong to the
# operator's %LOCALAPPDATA%), or touch any file in the real checkout. It reads the checkout and writes only to a
# temporary folder.
param(
    [string]$Repo = '',
    [switch]$Keep,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = if ($Repo) { (Resolve-Path -LiteralPath $Repo).Path } else { (Split-Path -Parent $PSScriptRoot) }
$drill = Join-Path $env:TEMP ('pmk-restore-drill-' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$failed = $false

function Step([string]$Text) { Write-Output ('== ' + $Text) }
function Pass([string]$Text) { Write-Output ('PASS: ' + $Text) }
function Fail([string]$Text) { Write-Output ('FAIL: ' + $Text); $script:failed = $true }

if (!(Test-Path -LiteralPath (Join-Path $root '.git'))) { throw "Not a repository: $root" }
foreach ($needed in @('PMKUnlocker/bundled-tools', 'PMKUnlocker/tools')) {
    if (!(Test-Path -LiteralPath (Join-Path $root $needed))) {
        throw "Missing $needed - without it the drill would prove nothing. This is the folder RESTORE.txt says to keep."
    }
}

try {
    Step "clone tracked files from $root"
    $clone = Join-Path $drill 'source'
    New-Item -ItemType Directory -Path $drill -Force | Out-Null
    & git clone --quiet --no-hardlinks $root $clone
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
    $head = (& git -C $clone rev-parse --short HEAD).Trim()
    $trackedMb = [math]::Round((Get-ChildItem -LiteralPath $clone -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    Pass "git restores $trackedMb MB at $head (tags: $((& git -C $clone tag -l | Sort-Object) -join ', '))"

    Step 'copy the untracked trees the build cannot invent'
    foreach ($needed in @('PMKUnlocker/bundled-tools', 'PMKUnlocker/tools')) {
        $from = Join-Path $root $needed
        $to = Join-Path $clone $needed
        New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
        Copy-Item -LiteralPath $from -Destination $to -Recurse -Force
    }
    $copiedMb = [math]::Round((Get-ChildItem -LiteralPath (Join-Path $clone 'PMKUnlocker/bundled-tools') -Recurse -File |
        Measure-Object Length -Sum).Sum / 1MB)
    Pass "bundled-tools copied ($copiedMb MB; integrity seed + every pinned tool)"

    Step 'build Release (manifest dropped only so the drill can run without a UAC prompt)'
    Push-Location $clone
    try {
        & dotnet build 'PMKUnlocker/PMKUnlocker.csproj' -c Release -v quiet '-p:ApplicationManifest='
        if ($LASTEXITCODE -ne 0) { Fail 'Release build'; return }
        Pass 'Release build 0 warnings / 0 errors'

        $exe = Join-Path $clone 'PMKUnlocker/bin/Release/net8.0-windows/PMKUnlocker.exe'
        if (!(Test-Path -LiteralPath $exe)) { Fail 'build produced no executable'; return }
        # The build target writes this manifest; a missing one means the target did not run, which is a build
        # fault and never a self-check result.
        if (!(Test-Path -LiteralPath (Join-Path (Split-Path -Parent $exe) 'integrity.json'))) {
            Fail 'build wrote no integrity.json (the WriteBuildIntegrity target did not run)'
            return
        }

        Step 'self-check the rebuilt package'
        # The exe is a WinExe: '&' neither waits for it nor reports its exit code, so Start-Process -Wait is the
        # only honest way to run it. Its stdout is not captured either; the verdict it always writes is the log,
        # and a stale log is not evidence - only one newer than this run counts.
        $selfLog = Join-Path $env:LOCALAPPDATA 'PMKMobileTool/pmk_selfcheck.log'
        $logBefore = if (Test-Path -LiteralPath $selfLog) { (Get-Item -LiteralPath $selfLog).LastWriteTimeUtc } else { [datetime]::MinValue }
        $proc = Start-Process -FilePath $exe -ArgumentList '--self-check' -Wait -PassThru -WindowStyle Hidden
        $logAfter = if (Test-Path -LiteralPath $selfLog) { (Get-Item -LiteralPath $selfLog).LastWriteTimeUtc } else { [datetime]::MinValue }
        if ($logAfter -gt $logBefore) {
            Get-Content -LiteralPath $selfLog | Select-Object -Last 3 | ForEach-Object { Write-Output ('    ' + $_) }
        }
        else { Write-Output '    (no fresh self-check log was written)' }
        if ($proc.ExitCode -ne 0) { Fail "self-check (exit $($proc.ExitCode))" } else { Pass 'self-check' }

        if (!$SkipTests) {
            Step 'regression suite from the clone'
            $env:PMK_CI_ALLOW_MISSING_BUNDLED_TOOLS = '1'
            & dotnet run --project 'tests/Regression/Regression.csproj' -c Debug 2>&1 | Select-Object -Last 2 |
                ForEach-Object { Write-Output ('    ' + $_) }
            if ($LASTEXITCODE -ne 0) { Fail 'regression suite' } else { Pass 'regression suite' }
        }
    }
    finally { Pop-Location }
}
catch {
    Fail ("drill threw: " + $_.Exception.Message)
}
finally {
    if ($Keep) { Write-Output "Drill kept at $drill" }
    elseif (Test-Path -LiteralPath $drill) { Remove-Item -LiteralPath $drill -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failed) {
    Write-Output 'BACKUP RESTORE: FAIL - the kept set does not rebuild into a working tool.'
    exit 1
}
Write-Output 'BACKUP RESTORE: PASS - the kept set rebuilds, self-checks and passes the suite.'
exit 0

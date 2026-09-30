param([Parameter(Mandatory=$true)][string]$Candidate)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $Candidate).Path
$manifest = Get-Content -LiteralPath (Join-Path $source 'file-manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $file = [IO.Path]::GetFullPath((Join-Path $source $entry.path))
    if (!$file.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes release directory.' }
    if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $entry.sha256) { throw "Release integrity failed: $($entry.path)" }
}
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup 6 compiler is required.' }
$output = Join-Path $root 'installer/output'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compileLog = & $compiler "/DSourceDir=$source" "/DOutputDir=$output" (Join-Path $root 'installer/PMKMobileTool.iss') 2>&1
$compileExit = $LASTEXITCODE
$compileLog | Set-Content -LiteralPath (Join-Path $output 'compiler.log') -Encoding utf8
if ($compileExit -ne 0) { $compileLog | Write-Output; throw 'Installer compilation failed.' }
$nonCommercial = ($compileLog -join "`n") -match 'Non-commercial use only'
$setup = Get-ChildItem -LiteralPath $output -Filter '*-Setup.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
@{
    installer = $setup.Name
    sha256 = (Get-FileHash -LiteralPath $setup.FullName).Hash
    compilerReportedNonCommercialOnly = $nonCommercial
    commercialDistributionApproved = $false
    cleanWindowsInstallTested = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'installer-check.json') -Encoding utf8
if ($nonCommercial) { Write-Output 'Compiler reports non-commercial use only. Review compiler licensing before commercial distribution; this installer is a trial artifact.' }
Write-Output "Installer built in $output. Compilation does not certify clean-PC or hardware compatibility."

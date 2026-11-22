# PMK SMTP setup — free SMTP relay (Brevo / SendGrid) or Outlook / custom
# Self-elevates because ProgramData\PMKMobileTool needs administrator.
$ErrorActionPreference = 'Stop'

$dstDir = 'C:\ProgramData\PMKMobileTool'
$dst    = Join-Path $dstDir 'smtp.json'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath)
    return
}

$providers = [ordered]@{
    '1' = @{ Name = 'Brevo  (free 300 emails/day)';   Host = 'smtp-relay.brevo.com'; Port = 587 }
    '2' = @{ Name = 'SendGrid (free 100 emails/day)'; Host = 'smtp.sendgrid.net';   Port = 587 }
    '3' = @{ Name = 'Outlook / Microsoft 365';       Host = 'smtp.office365.com';  Port = 587 }
    '4' = @{ Name = 'Custom (type your own SMTP)';   Host = '';                     Port = 587 }
}

Write-Host '=== PMK Mobile Tool - SMTP setup ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Select provider:' -ForegroundColor Cyan
foreach ($k in $providers.Keys) { Write-Host ("  {0}) {1}" -f $k, $providers[$k].Name) }
Write-Host ''
$sel = (Read-Host 'Choice (1-4)').Trim()
if (-not $providers.Contains($sel)) { Write-Host 'Invalid choice.' -ForegroundColor Red; Read-Host 'Press Enter to close'; return }
$p = $providers[$sel]

$host_ = $p.Host
$port_ = $p.Port
if ($sel -eq '4') {
    $host_ = (Read-Host 'SMTP host (e.g. mail.yourdomain.com)').Trim()
    $p2 = (Read-Host 'Port [587]').Trim(); if ($p2) { $port_ = [int]$p2 }
}
if ([string]::IsNullOrWhiteSpace($host_)) { Write-Host 'Host is required.' -ForegroundColor Red; Read-Host 'Press Enter to close'; return }

Write-Host ''
switch ($sel) {
    '1' { Write-Host 'Brevo: Dashboard -> SMTP & API -> SMTP. Copy the SMTP login + SMTP key.' -ForegroundColor Yellow }
    '2' { Write-Host 'SendGrid: Settings -> API Keys. User = apikey , Password = your API key (SG.xxxx).' -ForegroundColor Yellow }
    '3' { Write-Host 'Outlook/Microsoft: enable 2-Step Verification, then create an app password.' -ForegroundColor Yellow }
}

$user_ = (Read-Host 'SMTP user / login').Trim()
$pass_ = (Read-Host 'SMTP password / key').Trim()
$from_ = (Read-Host "From (blank = user)").Trim()
if ([string]::IsNullOrWhiteSpace($from_)) { $from_ = $user_ }

$cfg = [ordered]@{
    Host        = $host_
    Port        = $port_
    UseStartTls = $true
    User        = $user_
    Password    = $pass_
    From        = $from_
    PublicUrl   = ''
}

if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }
($cfg | ConvertTo-Json) | Set-Content -LiteralPath $dst -Encoding utf8

Write-Host ''
Write-Host "smtp.json written to: $dst" -ForegroundColor Green
Write-Host 'Now restart PMKLicenseServer: close the tool and open it again.' -ForegroundColor Green
Read-Host 'Press Enter to close'
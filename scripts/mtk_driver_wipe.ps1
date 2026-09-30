# PMK - MediaTek driver cleanup tool (DESTRUCTIVE - needs Administrator)
#
# Modes:
#   -Mode Instances     : (default, safe) remove MediaTek device instances (present + hidden/stale).
#                         Driver packages stay installed -> phone stays usable with mtkclient.
#   -Mode MtkPackages   : ALSO delete the MediaTek-specific driver packages (oem*.inf):
#                         MediaTek_USB_Port_V1633 (Zadig/libusbK), mtk_sp_usb2ser, mtkmbim* (modem),
#                         USB_Serial_Device.  => BROM port and VCOM/serial modes become unreachable
#                         until you re-install drivers (Zadig + MTK USB driver).
#   Add -IncludeGenericLibusb to MtkPackages to also delete the generic libusb packages
#                         (QHSUSB__BULK = Qualcomm EDL, WinUSB_Generic_Device) -> breaks the
#                         Qualcomm/EDL and any other Zadig-bound device on this PC.
#
# Usage (elevated PowerShell):
#   powershell -NoProfile -ExecutionPolicy Bypass -File mtk_driver_wipe.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File mtk_driver_wipe.ps1 -Mode MtkPackages
#   powershell -NoProfile -ExecutionPolicy Bypass -File mtk_driver_wipe.ps1 -Mode MtkPackages -IncludeGenericLibusb -Force

param(
    [ValidateSet('Instances', 'MtkPackages')][string]$Mode = 'Instances',
    [switch]$IncludeGenericLibusb,
    [switch]$Force
)

$ErrorActionPreference = "Continue"

function Line($t) { Write-Output ("=" * 72); Write-Output $t; Write-Output ("=" * 72) }

# ---- admin check -------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Output "ERROR: Administrator rights required."
    Write-Output "  -> Open PowerShell as Administrator and re-run this script."
    exit 1
}

# ---- collect targets ---------------------------------------------------------
$devices = Get-PnpDevice | Where-Object { $_.InstanceId -like '*VID_0E8D*' }

$mtkPkgs = @()
$genPkgs = @()
foreach ($f in (Get-ChildItem 'C:\Windows\INF\oem*.inf')) {
    $txt = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $txt) { continue }
    if ($txt -match 'MediaTek|MTK_|PreLoader|MediaTek_USB_Port|mtkmbim') { $mtkPkgs += $f.Name }
    elseif ($txt -match 'libusbK|libwdi|libusb-win32|QHSUSB|WinUSB_Generic') { $genPkgs += $f.Name }
}

# ---- backup / report before touching anything --------------------------------
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backup = Join-Path ([Environment]::GetFolderPath('Desktop')) "pmk_driver_backup_$stamp.txt"
Line "BACKUP -> $backup"
& pnputil /enum-drivers | Out-File -FilePath $backup -Encoding utf8
"`n=== MediaTek device instances ===" | Out-File -FilePath $backup -Append -Encoding utf8
$devices | ForEach-Object {
    "  {0} | {1} | {2}" -f $_.InstanceId, $_.FriendlyName, $_.Status
} | Out-File -FilePath $backup -Append -Encoding utf8
Write-Output "  saved."

# ---- plan --------------------------------------------------------------------
Line "PLAN (Mode=$Mode, IncludeGenericLibusb=$IncludeGenericLibusb)"
Write-Output ("  MediaTek device instances to remove : {0}" -f $devices.Count)
if ($Mode -eq 'MtkPackages') {
    Write-Output ("  MediaTek driver packages to delete   : {0}" -f $mtkPkgs.Count)
    $mtkPkgs | ForEach-Object { Write-Output "      $_" }
    if ($IncludeGenericLibusb) {
        Write-Output ("  Generic libusb packages to delete    : {0}  (Qualcomm EDL / other Zadig devices!)" -f $genPkgs.Count)
        $genPkgs | ForEach-Object { Write-Output "      $_" }
    }
} else {
    Write-Output "  Driver packages: KEPT (phone remains usable with mtkclient)."
}

if (-not $Force) {
    Write-Output ""
    $answer = Read-Host "Type WIPE to continue, anything else to abort"
    if ($answer -ne 'WIPE') { Write-Output "Aborted - nothing changed."; exit 0 }
}

# ---- backup DriverStore package folders (so pnputil /add-driver can restore) ---
$storeRoot = Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository'
$backupStore = Join-Path ([Environment]::GetFolderPath('Desktop')) "pmk_driver_backup_$stamp\DriverStore"
New-Item -ItemType Directory -Path $backupStore -Force | Out-Null
$copied = 0
foreach ($d in (Get-ChildItem $storeRoot -Directory -ErrorAction SilentlyContinue)) {
    $n = $d.Name.ToLowerInvariant()
    $hit = $false
    foreach ($p in 'mediatek_usb_port', 'usb_serial_device', 'mtk_sp_usb2ser', 'mtkmbim') {
        if ($n.StartsWith($p)) { $hit = $true; break }
    }
    if ($hit) {
        Copy-Item $d.FullName -Destination $backupStore -Recurse -Force -ErrorAction SilentlyContinue
        $copied++
    }
}
Write-Output ("  DriverStore folders backed up: {0} -> {1}" -f $copied, $backupStore)

# ---- 1) remove device instances ---------------------------------------------
Line "Removing MediaTek device instances"
foreach ($d in $devices) {
    $out = & pnputil /remove-device "$($d.InstanceId)" 2>&1
    Write-Output ("  {0} -> {1}" -f $d.InstanceId, (($out | Select-Object -First 1) -replace '\s+', ' '))
}

# ---- 2) delete driver packages ----------------------------------------------
if ($Mode -eq 'MtkPackages') {
    Line "Deleting MediaTek driver packages"
    foreach ($inf in $mtkPkgs) {
        $out = & pnputil /delete-driver $inf /uninstall /force 2>&1
        Write-Output ("  {0} -> {1}" -f $inf, (($out | Select-Object -First 1) -replace '\s+', ' '))
    }
    if ($IncludeGenericLibusb) {
        Line "Deleting generic libusb packages"
        foreach ($inf in $genPkgs) {
            $out = & pnputil /delete-driver $inf /uninstall /force 2>&1
            Write-Output ("  {0} -> {1}" -f $inf, (($out | Select-Object -First 1) -replace '\s+', ' '))
        }
    }
}

# ---- 3) rescan ---------------------------------------------------------------
Line "Rescanning devices"
& pnputil /scan-devices | Out-Null

Line "DONE"
if ($Mode -eq 'MtkPackages') {
    Write-Output "Next steps to use MTK tools again:"
    Write-Output "  1) Install an MTK USB driver package (gives PreLoader/DA VCOM COM ports)."
    Write-Output "  2) Phone -> power off, hold Vol+ & Vol-, connect USB (BROM mode)."
    Write-Output "  3) Run Zadig -> Options > List All Devices -> 'MediaTek USB Port (0e8d:0003)'"
    Write-Output "     -> install WinUSB (or libusbK)."
    Write-Output "  4) Verify with:  powershell -File mtk_driver_report.ps1"
} else {
    Write-Output "Device instances cleaned. Driver packages untouched - BROM binding still works."
}

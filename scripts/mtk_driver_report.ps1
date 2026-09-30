# PMK - MediaTek driver inventory (READ-ONLY, admin not required)
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File mtk_driver_report.ps1
#          powershell -NoProfile -ExecutionPolicy Bypass -File mtk_driver_report.ps1 -Full

param([switch]$Full)

$ErrorActionPreference = "SilentlyContinue"

function Line($t) { Write-Output ("=" * 72); Write-Output $t; Write-Output ("=" * 72) }

Line "1) MediaTek device instances (present + hidden)"
$devs = Get-PnpDevice | Where-Object { $_.InstanceId -like '*VID_0E8D*' }
if (-not $devs) { Write-Output "  (none)" }
foreach ($d in $devs) {
    $svc = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_Service').Data
    $inf = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath').Data
    $p = if ($d.Present) { "PRESENT" } else { "hidden " }
    Write-Output ("  [{0}] {1}" -f $p, $d.InstanceId)
    Write-Output ("       name={0} | status={1} | service={2} | inf={3}" -f $d.FriendlyName, $d.Status, $svc, $inf)
}

Line "2) Driver packages (oem*.inf) related to MediaTek / libusb bindings"
$mtk = @()
$generic = @()
foreach ($f in (Get-ChildItem 'C:\Windows\INF\oem*.inf')) {
    $txt = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $txt) { continue }
    $isMtk = $txt -match 'MediaTek|MTK_|PreLoader|DA USB VCOM|MediaTek_USB_Port'
    $isLibusb = $txt -match 'libusbK|libwdi|libusb-win32|libusb0'
    if (-not ($isMtk -or $isLibusb)) { continue }
    $orig = (Select-String -Path $f.FullName -Pattern '^\s*;\s*(.+\.inf)\s*$' | Select-Object -First 1).Matches.Groups[1].Value
    $prov = ((Select-String -Path $f.FullName -Pattern 'Provider\s*=' | Select-Object -First 1).Line) -replace '\s+', ' '
    $row = "  {0,-12} {1,-42} {2}" -f $f.Name, $orig, $prov.Trim()
    if ($isMtk) { $mtk += $row } else { $generic += $row }
}

Write-Output "  -- MediaTek-specific packages (safe to target) : $($mtk.Count)"
$mtk | ForEach-Object { Write-Output $_ }
Write-Output ""
Write-Output "  -- Generic libusb packages (ALSO used by other devices, e.g. Huawei/Kirin) : $($generic.Count)"
$generic | ForEach-Object { Write-Output $_ }

Line "3) Driver services"
foreach ($s in 'libusbK', 'libusb0', 'WinUSB', 'usbser', 'wdm_usb', 'usbccgp') {
    $k = 'HKLM:\SYSTEM\CurrentControlSet\Services\' + $s
    if (Test-Path $k) {
        Write-Output ("  {0,-10} INSTALLED  {1}" -f $s, (Get-ItemProperty $k).DisplayName)
    } else {
        Write-Output ("  {0,-10} not present" -f $s)
    }
}

Write-Output ""
Write-Output "NOTE: mtkclient (libusb) can ONLY talk to a device bound to WinUSB / libusbK /"
Write-Output "      libusb-win32. Removing those bindings makes the BROM port unreachable"
Write-Output "      until you re-install a driver with Zadig."

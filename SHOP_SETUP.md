# PMK Mobile Tool — shop setup

This is a release candidate. Device operations still require validation on the models your shop will service.

## Installation

1. Install Microsoft .NET 8 **Desktop Runtime x86**. An x64-only runtime is insufficient. The installer checks the normal x86 runtime location and stops if it is absent.
2. Run the PMK installer. It installs for the current Windows user without requiring administrator rights.
3. Open **Setup / Backups → Tool setup**. Bundled ADB, fastboot, Heimdall and Qualcomm executables are listed there. “File found” means only that the file exists; it does not certify drivers, runtime dependencies or device compatibility.
4. Configure optional backends for the tabs you use:
   - MediaTek: `mtk.exe` and a `python.exe` environment with mtkclient and its required dependencies installed.
   - Qualcomm: a Python environment with the EDL module dependencies, and `edl.py`. Select the appropriate loader using the existing Qualcomm controls.
   - Spreadtrum/Unisoc: `spd_dump.exe`, `unpac.exe` and device-specific FDL files.
   - HiSilicon: a compatible bootloader package through the existing Bootloaders control.
5. Use Drivers to check the USB setup. Test first with read-only operations on a shop-owned test phone.

Do not copy a Python executable by itself: its standard library and installed modules are also required. Missing or incompatible optional backends do not become supported merely by selecting an executable.

### Qualcomm loader selection

The loader catalog and its 860 referenced files are included in the current package. The release check verifies every referenced file exists, is nonempty, and matches the project copy by SHA-256. Catalog membership does not certify compatibility with a particular phone or firmware.

Selecting a model or a manual loader invalidates the previous authenticated session. **Auto Detect** clears the manual loader; automatic discovery then depends on the selected backend and device support. Previously saved paths appear under **Manual / saved loader**, so they are not mislabeled as automatic detection. This session reset does not interrupt an authentication operation already in progress.

## Settings and updates

Settings, configured tool paths, test records, backup index and logs live in `%LOCALAPPDATA%\PMKMobileTool`. The installer does not delete this directory on uninstall. Backups selected by the operator stay in their chosen directories. Existing `pmk_settings.txt` and `pmk_paths.txt` beside the old executable are copied on first launch only when the corresponding user setting does not already exist.

## Backup Manager and restore

New MTK NV/Safe Bundle backups include `backup-manifest.json`, byte sizes, SHA-256 hashes, app version, timestamp and the observed GPT partition-size map fingerprint. Backup Manager imports or verifies these folders; it never writes to the phone.

NV Restore now requires this manifest and compares its files with a fresh device GPT. Old folders without a manifest are rejected; keep them unchanged and make a new verified backup when possible. A matching layout does **not** prove that two phones are the same physical device. The tool labels identity as unverified and requests confirmation before restore. SHA-256 detects changed files against the manifest; an unsigned manifest does not provide publisher authenticity.

## Firmware preflight

MediaTek folder flashing checks names and image sizes against a fresh device GPT. Sparse image capacity is checked using expanded size. Unknown partitions, empty images and oversized images block the operation. Model, signatures, boot-chain compatibility and sparse chunk payload integrity are not certified by this size check. Confirm the firmware source and model before proceeding.

## Device test records

Use **Setup / Backups → Device test records** to record platform, model, firmware, operation, result, date and evidence. Records are entered by the operator. No existing catalog entry is automatically marked tested.

Before distributing beyond a trial shop, complete clean-Windows installation, update/uninstall, dependency/runtime and representative hardware tests. Review the distribution terms and notices for every bundled third-party component. EDL authentication retains the requested behavior: STOP does not forcibly interrupt that stage.

The installed Inno Setup compiler reported **“Non-commercial use only”** when this candidate was built. The generated setup is a trial artifact; commercial distribution has not been approved. See `installer/output/installer-check.json` and confirm the applicable compiler license before a commercial release.

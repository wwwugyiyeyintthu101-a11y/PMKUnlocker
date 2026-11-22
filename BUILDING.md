# Local build preferences

Use the existing desktop app output location for ongoing local changes:

`PMKUnlocker/bin/Debug/net8.0-windows/PMKUnlocker.exe`

Build with `dotnet build PMKUnlocker/PMKUnlocker.csproj --no-restore`.
Do not create another review-publish/output directory as a workaround for a locked executable.
If the application is running, ask the user to close it after any device operation finishes;
never terminate an active flashing operation to complete a build.

The user does not want additional hardware-free test files. Use build/syntax checks and
report device validation separately; do not claim throughput gains without a measured flash.

The solution now includes desktop, license server, admin, regression, and login-flow projects.
Use `dotnet build PMKUnlocker/PMKUnlocker.sln --no-restore -m:1` to check all five.
The build writes a runtime integrity manifest from the bundled inventory plus
project helpers/libraries. `dependency-lock.json` pins the inventory and native
library bytes; missing optional external backends are not silently certified.
Release builds and publishing verify all bundled inventory hashes. Restore the
approved tool inventory when cloning; do not replace missing tools with an empty
manifest. The per-process PowerShell execution setting in the build does not
change machine-wide execution policy.

## Clean Windows install test

`scripts/check_release.ps1` proves the package is complete. It cannot prove the package runs on a machine that
has never built it, which is the only machine a shop actually uses. Run this once per release on a Windows VM
(Hyper-V or VMware, no dev tools, snapshot before starting), and record the result in `release-check.json` by
flipping `cleanWindowsInstallTested` — never claim it from a green build.

```
□ snapshot a clean Windows 11 VM (no .NET, no drivers, no PMK folders)
□ run Setup.exe as a normal user
     → the .NET 8 Desktop (x86) message appears and setup stops (proves the check is real)
□ install .NET 8 Desktop x86, run Setup.exe again → install completes, shortcut created
□ start PMKUnlocker.exe → one UAC prompt, then the login screen (no crash, no silent exit)
□ %LOCALAPPDATA%\PMKMobileTool is created; pmk_startup.log reaches "login UI tree built"
□ sign in against the real server (local or HTTPS) — the bundled `server\` starts on first launch; and once
  with the server stopped, to see the error it shows
□ %LOCALAPPDATA%\PMKMobileTool\license-server\ has licenses.json + admin-key-<time>.txt after first launch
□ plug an ADB phone → adb.exe resolves from the package (Setup panel lists it as found)
□ plug an MTK phone in BROM → the COM port appears (this is where a missing Preloader/VCOM driver shows up)
□ NATIVE tab: present only when mtk\boot-profiles was copied in; absent and quiet when it was not
□ QC tab with no edl.py/python: every button fails with a sentence, never a crash
□ SPD tab with no spd_dump/unpac: the tab says what to put in the tool folder
□ close the app → the hidden license server keeps running; reopen → no second server, no port error
□ uninstall → %LOCALAPPDATA%\Programs\PMKMobileTool is gone; %LOCALAPPDATA%\PMKMobileTool (data) is kept
□ reinstall over the top → settings and login survive
```

Two prerequisites that are deliberately *not* bundled, and therefore belong in this test rather than in the
installer: the MediaTek Preloader/VCOM driver and the Qualcomm QDLoader 9008 driver (`scripts/mtk_driver_report.ps1`
reports the current state), and the optional Python/`edl.py` and Unisoc `spd_dump`/`unpac` engines.

On the packaged folder itself, `PMKUnlocker.exe --self-check` re-hashes every packed file, resolves each
required tool, parses the catalogs and audits any installed recipe — no phone, no window, exit 0 or 1. The app
manifest asks for administrator, so run it from an elevated shell (a non-elevated attempt is a SKIP).

## The bundled licence server

`check_release.ps1` publishes `PMKLicenseServer` **self-contained and trimmed** (win-x64, ~28 MB) into
`release-candidates/<id>/server/`, and the installer copies that folder as-is. An installed tool therefore has
its server next to it, `PMKUnlocker.exe` starts it on launch, and a shop PC needs no .NET runtime of its own.

- data and keys live in `%LOCALAPPDATA%\PMKMobileTool\license-server\` — `licenses.json`, `adminkey.txt`
  (hash only), and `admin-key-<timestamp>.txt` (the one-time plaintext, written because a hidden server has no
  console to print it to)
- the shipped `server\appsettings.json` names that folder as `DataFile` and the server expands `%LOCALAPPDATA%`
  at startup, so a non-admin operator can write it. A shared/locked-down install can override the path:
  `PMKLicenseServer.exe --DataFile=C:\ProgramData\PMKMobileTool\licenses.json`
- first start generates the admin key; that plaintext file is the only copy, so move it to a password manager
  and delete it. `http://127.0.0.1:5260/admin` is the management UI
- the check is a proof, not a file listing: it runs the shipped `server\PMKLicenseServer.exe` against a
  throwaway `%LOCALAPPDATA%` on a free port, requires `licenses.json` and the admin-key file to appear there,
  then drives `admin/upsert` → `login` → `admin/list` and records the result as `bundledLicenseServer` in
  `release-check.json`. A trimmed publish drops reflection metadata unless it is rooted — that was a real
  failure mode here (`JsonTypeInfo metadata ... was not provided`), hence `TrimmerRootAssembly` in the
  server's csproj; keep the smoke test covering it.

## CI

`.github/workflows/ci.yml` restores everything, builds the license server and admin,
runs the desktop app's full Build target with `-p:SkipToolIntegrity=true`, and runs the
regression and login-flow suites. `tests/LicenseLogin` starts the real license server on
a throwaway `DataFile` and drives the real `LicenseClient`/`LocalLogin` code: registration
and approval, PBKDF2 and legacy-hash login, remember-me tokens (issue, 30-day window,
revocation on block/password change), admin-key enforcement, DataFile persistence across
a restart, and the single-owner lock. It reads and writes only its temp directory, so it
never touches the operator's `%LOCALAPPDATA%\PMKMobileTool` files. The runtime integrity
manifest cannot be written without the untracked tool inventory, so CI opts out of that
one target; every local, release and publish build still writes and verifies it by
default — `SkipToolIntegrity` is an explicit CI-only opt-out. Since `bundled-tools/` and `tools/` are not tracked, CI also sets
`PMK_CI_ALLOW_MISSING_BUNDLED_TOOLS=1`: only the checks that read those on-disk
assets report SKIP there, and they stay mandatory on a machine that has them.
CI also runs the default Build once and requires it to fail with the missing-inventory
error and to leave no manifest, so the opt-out cannot silently become the default.

MTK native recipes (`mtk/boot-profiles` next to the exe) are PMK's own DA/EMI
artefacts: they are not tracked and not shipped, and without them the NATIVE tab
is deliberately absent. Keep the master copy at
`%LOCALAPPDATA%\PMKMobileTool\mtk\boot-profiles`; the build copies it into every
output (`CopyMtkBootRecipes`), so a clean or rebuild cannot silently remove the
tab. Point `-p:MtkRecipeSource=<folder>` elsewhere if the recipes live outside
that directory.

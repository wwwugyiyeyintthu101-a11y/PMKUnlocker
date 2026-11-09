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

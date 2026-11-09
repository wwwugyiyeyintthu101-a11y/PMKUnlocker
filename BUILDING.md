# Local build preferences

Use the existing desktop app output location for ongoing local changes:

`PMKUnlocker/bin/Debug/net8.0-windows/PMKUnlocker.exe`

Build with `dotnet build PMKUnlocker/PMKUnlocker.csproj --no-restore`.
Do not create another review-publish/output directory as a workaround for a locked executable.
If the application is running, ask the user to close it after any device operation finishes;
never terminate an active flashing operation to complete a build.

The user does not want additional hardware-free test files. Use build/syntax checks and
report device validation separately; do not claim throughput gains without a measured flash.

The solution now includes desktop, license server, admin, and regression projects.
Use `dotnet build PMKUnlocker/PMKUnlocker.sln --no-restore -m:1` to check all four.
The build writes a runtime integrity manifest from the bundled inventory plus
project helpers/libraries. `dependency-lock.json` pins the inventory and native
library bytes; missing optional external backends are not silently certified.
Release builds and publishing verify all bundled inventory hashes. Restore the
approved tool inventory when cloning; do not replace missing tools with an empty
manifest. The per-process PowerShell execution setting in the build does not
change machine-wide execution policy.

## CI

`.github/workflows/ci.yml` restores everything, builds the license server and admin,
compiles the desktop app with `-t:Compile` (the full Build target writes the runtime
integrity manifest and therefore needs the untracked bundled tools), and runs the
regression suite. Since `bundled-tools/` and `tools/` are not tracked, CI sets
`PMK_CI_ALLOW_MISSING_BUNDLED_TOOLS=1`: only the checks that read those on-disk
assets report SKIP there, and they stay mandatory on a machine that has them.

MTK native recipes (`mtk/boot-profiles` next to the exe) are PMK's own DA/EMI
artefacts: they are not tracked and not shipped, and without them the NATIVE tab
is deliberately absent. Keep the master copy at
`%LOCALAPPDATA%\PMKMobileTool\mtk\boot-profiles`; the build copies it into every
output (`CopyMtkBootRecipes`), so a clean or rebuild cannot silently remove the
tab. Point `-p:MtkRecipeSource=<folder>` elsewhere if the recipes live outside
that directory.

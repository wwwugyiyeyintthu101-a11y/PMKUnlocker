# PMK Mobile Tool — third-party components and notices

**Status: draft, 2026-11-22.** This file lists every third-party component the tool ships, calls, or points at,
what is known about its licence, and what still has to be confirmed before the package is distributed. It is
not legal advice, and a row marked **UNVERIFIED** is not a permission — it is a to-do. Nothing here has been
approved by the upstream projects or by PMK.

Two rules govern this file:

1. **Only what can be pointed at is written down as fact.** A licence is stated when the shipped binary carries
   it (libusb) or when the component's own repository states it; everything else says what evidence is missing.
2. **The shipped package is the unit of review.** A component that is downloaded by the operator and never
   redistributed by PMK is listed separately from one that travels inside the installer, because the
   obligations differ.

---

## 1. Shipped inside the installer (`installer/output`)

| Component | Where | What it is | Licence status |
|---|---|---|---|
| `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll` | package root | Android platform-tools | **UNVERIFIED** — Android SDK/platform-tools terms. Confirm whether these binaries may be redistributed inside a third-party installer, or require the operator to install platform-tools themselves. |
| `fastboot.exe` | package root | Android platform-tools | **UNVERIFIED** — same question as `adb.exe`. |
| `fh_loader.exe`, `QSaharaServer.exe` | package root | Qualcomm firehose/Sahara host tools | **UNVERIFIED** — vendor tooling, no licence shipped with the binary. Assume redistribution is not granted until Qualcomm (or the tool's distributor) says it is. |
| `heimdall.exe` | `samsung/` | Samsung flashing client | **UNVERIFIED** — no licence text in the binary. Heimdall's upstream terms must be read before redistribution. |
| `libusb-1.0.dll` (root and `samsung/`) | root, `samsung/` | USB backend | **LGPL-2.1-or-later** — stated in both binaries' `LegalCopyright` ("See individual source files, GNU LGPL v2.1 or later"). Obligations: ship this notice, and keep the library replaceable (dynamic load from the app folder satisfies this; do not statically link or bundle it into a single file). |
| `Potato.Fastboot.dll`, `Potato.ImageFlasher.dll` | package root | Huawei/Honor Kirin bootloader support (PotatoNV-next, mashed-potatoes) | **UNVERIFIED — highest risk item.** Copyright is mashed-potatoes; no licence text ships in the DLLs. If upstream is GPL-family, shipping these DLLs inside a closed-source app requires the corresponding source and terms, or the Potato path has to be dropped. Read the upstream repository's LICENSE before distribution. |
| `LibUsbDotNet.LibUsbDotNet.dll` | package root | .NET libusb binding | **UNVERIFIED** — upstream project licence must be confirmed (its terms are not embedded in the assembly). |
| `PMKUnlocker.exe/.dll` and the PMK-authored code | package root | PMK's own work | PMK's own licence is **not yet chosen** — see §4. |
| `pmk_devices.json`, `pmk_models.json`, `pmk_codenames.json`, `pmk_qc_loaders.json`, `pmk_mtk_devices.json` | package root | device/loader catalogues compiled by PMK | PMK-authored data. The catalogue values were collected from public sources; the collection itself is PMK's, the underlying facts are not owned by anyone. Keep the provenance note in each file. |
| `exploits/manifest.json`, `exploits/profiles/*.bin` | `exploits/` | kernel exploit profiles measured by PMK | PMK-authored measurements. The profiles describe third-party kernels; no vendor code is redistributed. |
| `exploits/payloads/dfroot/*.apk` | `exploits/` | KernelSU manager + DirtyFrag harness | **UNVERIFIED** — KernelSU and DirtyFrag are separate projects with their own licences and their own trust story. Confirm both, and note that the package installs them on a customer phone. |
| `exploits/payloads/ghostlock/ghostlock` | `exploits/` | GhostLock payload | **UNVERIFIED** — state the source and licence of this binary, or remove it from the shipped package. |
| `tools/mtkclient/mtk.exe` and `tools/mtkclient/payloads/*` | `tools/mtkclient/` | mtkclient CLI (bkerler/mtkclient, MIT) built by PMK | The **client** is MIT (upstream states it) and `mtkclient-lock.json` records the exact upstream commit and every file hash. The **payload/DA images** it carries are a separate question — see §2. Ship the MIT notice with the binary. |
| `mtk/loaders/**` (MTK DA/auth catalog) | `mtk/loaders` | OEM DA/auth files | **UNVERIFIED** — OEM/vendor blobs. PMK's own note in `ROADMAP §10` leaves this open: ship, drop, or re-source. |
| `edl/loaders/**` (1,062 Qualcomm firehose loaders) | `edl/loaders` | OEM firehose loaders | **UNVERIFIED** — same as above; these are vendor-signed images, not open-source artefacts. |

`integrity.json` pins the SHA-256 of every file above, so this list and the hash manifest describe the same set;
`PMKUnlocker.exe --self-check` re-verifies the set on the packaged folder.

## 2. Referenced but not shipped (operator-supplied)

| Component | How it is used | Why it is separate |
|---|---|---|
| Python 3 + `edl.py` (bkerler/edl) | No longer required by any PMK code path (the QC tab and the QC auto-reboot speak Sahara/firehose themselves as of 7.3.6). | Listed in the Setup panel as an optional operator tool. If an operator adds it, their own licence obligations apply. |
| `spd_dump.exe`, `unpac.exe` (Unisoc) | SPD PAC unpack + flash | Not bundled; the SPD tab says so when opened. |
| `fh_loader`/`QSaharaServer` when run from another install | Qualcomm flashing | Same binaries as §1; the four files above are what PMK actually redistributes. |
| Microsoft .NET 8 Desktop Runtime (x86), .NET 10 ASP.NET Core Runtime | runtime for the app and the local licence server | Installed by the operator or by Microsoft's installer; PMK does not redistribute it. The installer checks for .NET 8 Desktop only. |

## 3. Build-time tools (not redistributed)

| Tool | Use | Status |
|---|---|---|
| .NET SDK 8 and 10 | build | Microsoft SDK terms. |
| Inno Setup 6 | installer compiler | `scripts/build_installer.ps1` records `compilerReportedNonCommercialOnly` because the compiler can print a "non-commercial use only" banner. Read the compiler's licence for the edition that produced the released installer before selling it. |
| PowerShell 5.1 (Windows) | build/integrity scripts | OS component. |

## 4. Decisions this file cannot make

These are the open decisions in `ROADMAP §10`, restated as what changes in the package:

1. **OEM blob packs (`edl/loaders`, `mtk/loaders`, mtkclient payloads).** Ship them (and accept the vendor's
   terms), drop them (and lose off-line flashing until the operator supplies them), or re-source them from a
   distributor whose terms allow redistribution. Today they ship.
2. **PotatoNV DLLs.** GPL-family or not decides whether the Huawei path stays in a closed-source tool.
3. **PMK's own licence.** The repository has no `LICENSE` file. Until one exists, "all rights reserved" is the
   default, and that is worth writing down explicitly rather than leaving implicit.
4. **Auto-update channel.** `update.json` points at a public GitHub release; the asset is downloadable without
   authentication, so anything shipped there is public.

## 5. What "done" looks like

- Every **UNVERIFIED** row either has a confirmed licence with its notice text, or the component is out of the
  package.
- A `LICENSE` file exists at the repository root and names PMK's own terms.
- This file ships beside the exe (the project already copies `SHOP_SETUP.md` and `LICENSE_SERVER_SETUP.md` the
  same way), and `check_release.ps1`'s required-file list includes it, so a release cannot lose it silently.
- `scripts/release_preflight.ps1` (or the release checklist) refuses to mark a package "ready for shop
  distribution" while any row above is still unverified.

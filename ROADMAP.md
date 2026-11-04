# PMK Unlocker — Roadmap & Work Plan

**Status date: 2026-11-04.** This file is the single plan of record: what the tool can actually do today, what was proven on hardware, what is uncommitted, and the ordered list of work that remains. Detailed RUN logs are summarised here; the raw evidence stays in the files named in §9.

**Current baseline**
- `tests/Regression`: **700 checks** in the working tree (664 at `HEAD 36b9e9c`); the B6/B5.9 batch raises the baseline to 700 when it lands.
- Desktop Debug and Release: **0 warnings / 0 errors**; solution build (desktop + license server + admin + regression): **0/0**.
- Native MTK DA session is hardware-proven on **MT6765 (HW 0x0766)** and reached a live DA on **MT6877 (HW 0x0959)**; every other chipset is first-run risk.
- Recipes (`mtk/boot-profiles`) are untracked licensing-sensitive artefacts; without them the NATIVE tab is deliberately absent.

---

## 1. Platform coverage today

| Platform | Working today | Proven on hardware | Missing / next |
|---|---|---|---|
| **MTK — mtkclient flows** (shipped default) | Read Info/GPT, Save GPT, FRP Remove, Userlock Remove, Mi Cloud Remove, NV Reset/Backup/Restore/Erase, Bootloader Lock/Unlock (`da seccfg`), single-partition Backup/Restore, scatter flash, SHA-256 readback verify, Format All (keep bootchain/seccfg/NV), IMEI Read (NV), IMEI Write (`da imei`), Xiaomi Temp Root (GLK1), DA catalog (279 folders / 18 brands / 326 files / 39 auth), loader auto-detect from BROM/DA log or ADB `ro.soc.model`, failure-driven catalog DA fallback | Redmi 9A (MT6765): scatter flash (22 partitions), NV Erase → NV Restore (RUN 51/52, 6 partitions, IMEI + signal back), bootloader unlock/lock, FRP/userlock | Arbitrary NV item repair; per-device auth for Vivo/Tecno/Infinix (DA present, auth missing); SLA/DAA auth server for newer chipsets; validation of catalog mappings per chipset |
| **MTK — native DA session** (B1–B6) | Boot DA from a recorded recipe, Eject, Geometry + GPT, Dump / Restore / Erase one partition, per-partition **Read / Write / Erase** columns on the FLASH grid, Reboot Device, Seccfg State (read-only), live GPT hand-off to the scatter flows, device-bound GPT cache, one session per plug cycle | Redmi 9A (MT6765, HW 0x0766): RUN 42/43 (DA ready + live commands), RUN 47 (UI end-to-end), RUN 48 (seccfg state), B5.6 (reboot auto-boots). MT6877 (HW 0x0959): live DA reached (RUN B6-4), UFS geometry/read captured and regression-pinned | Native FRP/userlock/IMEI/NV/Format All/backup-all; bootloader unlock (no measured payload — deliberately absent); SLA/DAA chipsets; sparse images; UFS in the INFO status line; other chipsets first-run; B6 hardware closure |
| **Qualcomm** | Read Info/GPT, Save GPT+XML, Full/EFS Backup, EFS Restore (IMEI repair), FRP Remove, Userlock Reset, Reset, Restore-from-folder, Firehose full flash (rawprogram/XML), native firehose dispatch (Sahara + ping + sig auth + `printgpt`/`gpt`/`r`/`w`/`e`/`rl`/`reset`), EDL auth bypass, loader catalog (29 brands / 1025 loaders) | Redmi 9A is MTK; QC paths are code-complete but the EFS round-trip and Format userdata still need a real SD device | Format userdata; real QMI/diag IMEI write; move multi-partition flash, `--erase=frp`/`config` and reboot off `fh_loader.exe`; QC loader auto-detect; USB-ID/hwcode detection before a command |
| **Samsung** | ADB Info/FRP/Knox, MTP FRP/Factory Reset, Download Info, SoftBrick Fix, Heimdall flash, Download PIT + binary PIT parse → `.tar` mapping, EFS Backup/Restore (ADB root) | — (no current device in the bench matrix) | Odin-protocol flash; server FRP on new models; testpoint/EDL; full partition read without root |
| **Spreadtrum** | Info, FRP, Diag FRP/Userlock, PAC flash (`spd_dump`), Read GPT, Read/Write/Erase partition | — | PAC format-all; NV read/write; FDL coverage (FDL1 is device-specific — PAC firmware is the only clean source) |
| **HiSilicon** | Kirin unlock (PotatoNV core), FRP, OEM unlock code, driver install, ADB enable | — | Newer Kirin server unlock; EMUI testpoint FRP |
| **ADB / Fastboot** | Read Full Info, Battery, Screenshot, Apps, Install APK, Partitions (root), FRP (root/non-root), Remove Lock, Factory Reset, Temp Root, Flash .img, Boot Temp, Erase Userdata, Slot switch, Reboot ops | Used daily | Largely complete |

---

## 2. MTK native DA session — what exists in detail

### 2.1 Capability matrix (native vs mtkclient)

| Capability | mtkclient flow | Native DA session | Notes |
|---|---|---|---|
| Read device GPT | yes | yes (auto after boot + `Geometry + GPT`) | Native reads the device's own table once per session and caches it |
| Save GPT to disk | yes | **no** | P1 item |
| Dump one partition | yes | yes (grid Read / Dump) | Native dump is raw, exact extent |
| Restore one partition | yes | yes (verify readback) | Native restores only bytes it read or a scatter image |
| Erase one partition | yes | yes | seccfg refused by design (B5.9) |
| Multi-partition flash | yes (scatter) | **no** | P1 item |
| Backup all / selected to folder + manifest | yes | **no** | P1 item |
| Format All (keep bootchain/NV) | yes | **no** | P1 item |
| NV Erase / Restore (6 partitions) | yes (hardware-proven) | **no** | P1 item |
| IMEI Read / Write | yes (NV / `da imei`) | **no** | P2 item (needs NV item semantics) |
| FRP / Userlock remove | yes | **no** | P2 item (needs measured payloads) |
| Bootloader Unlock / Lock | yes (`da seccfg`) | **no, by design** | No unlock payload has ever been measured; native is read-only on seccfg |
| Reboot | yes (`reset`) | yes (TOPRGU stub) | Auto-boots (B5.6) |
| Seccfg state read | yes | yes | Read-only (B5.1–B5.2) |
| UFS support | no | yes (B6, in progress) | `0x40004` geometry + `0x30/3` extent pair |
| One plug cycle per operation | yes | yes (one boot, then many ops) | The native advantage |

### 2.2 Measured wire facts (the parts that must not be re-derived)

- **Two protocol layers, opposite endianness.** BROM/preloader is big-endian (`0xFD` HW code, `0xD4` watchdog, `0xD8` target config, `0xDA`/`0xD1` register access, `0xD7` DA1 upload, `0xC0` DA1 alive). DA is little-endian with magic `EF EE EE FE`, `type u32le = 1`, `length u32le`, then payload.
- **DA framing is two writes, never merged.** Every command is the 12-byte header, then the 4-byte id as its own write; the host ack is a 4-byte zero in its own frame. Both boot recordings (mt6765: 51 command frames, MT6877: 51) show this split. RUN B6-4's MT6877 DA answered the split shape 17 times and refused the merged 16-byte transfer — the old merged helper was our invention, not the recording's.
- **Command ids in use:** `0x10003` ERASE, `0x10004` WRITE, `0x10005` READ, `0x10008` upload+jump, `0x10009` preamble (31× before op groups), `0x1000A` EMI/DRAM, `0x40001` storage descriptor (104 B), `0x40004` UFS geometry (208 B), `0x40007` transfer limits, `0x4000A` `"brom"`, `0x4000B` `"high-speed"`, `0x40011` DA version, `0x40016` SLA state, extensions `0xF0000`/`0xF0005`.
- **The 56-byte extent struct** (READ/WRITE/ERASE): `u32le region`, `u32le field4`, `u64le offset`, `u64le length`, 32 zero bytes. The pair is **not a constant**: MT6765 eMMC uses `1 + 8`, MT6877 UFS uses `0x30 + 3`; both were measured against the unit's own READ structs and both equal the first word of that unit's geometry reply.
- **Write path:** `0x10009` → `0x40007` → `0x10004` + struct → per `0x200000` chunk two 4-byte frames (`0x0`, then a checksum) + data, one status each → `0x10009` → `0x80005` (no argument) → status → 4-byte status. The checksum is **Σ bytes mod 65536, sent as u32** — not a CRC.
- **Read path is frame-driven:** header + payload + host ack + status, repeated until the requested byte count is reached (a 2 MiB read is 32 frames at the DA's `0x40007` packet limit). Acking once at the end hangs on every partition dump above 64 KiB.
- **Erase path:** bare `0x10003` + struct (no `0x10009` preamble, no limits query), then report words `0x40040004`, `0x64`, host ack, `0x40040005` — loop on the report, do not hard-code two words.
- **Control probes are real protocol.** Before each pre-download jump the working client sends a CDC `SET_LINE_CODING` (accepted) and a vendor `GET_DESCRIPTOR` (stalled), with extension words `91C2, 92C2, 93C2, 8AC2, 89C2, 88C2`. This is the traffic no bulk trace or command log could see; without it the BROM refuses step 65 with `0x1D1A`.
- **Greeting is a hardware step.** Four one-byte pairs `A0/0A/50/05` each answered by its complement, then the recipe's own `0xFD`. A face already past the greet answers `A0` with itself; the HW code still decides.
- **Stale DA lies.** A phone with a DA still in RAM re-enumerates as `0E8D:0003` and answers `0xFE`; PnP cannot tell it from a BROM. Fail closed when the boot did not visibly walk BROM (real menu Power off + BOTH volume keys).
- **UFS geometry:** `0x40001` answers 104 zero bytes on a UFS unit; `0x40004` answers 208 bytes: region `0x30`, block 4096, boot LUN1/LUN2 4 MiB, user area at +24 (`0x3B96000000` on the capture), then disk identity at +32 which is **never decoded or stored**. RPMB and general-purpose areas have no measured slot and stay 0.
- **Control asks can hang.** RUN B6-1: the post-payload `SET_LINE_CODING` went out and never completed; the phone re-initialised and left the bus. Every ask is therefore bounded by timeout + 1 s, a no-answer is a fourth state (`CtrlNoAnswer = -1000`), and an abandoned transfer is watched and reported late instead of being dropped.

### 2.3 Recipes (`mtk/boot-profiles`)

- One JSON per chipset, flat ordered `tx` / `rx` / `blob` / `ctrl` steps plus hash-pinned blobs. The loader is fail-closed: every blob is size- and sha256-verified, every framed upload's header must match the file, and a wrong HW code stops the boot.
- `"opt": true` on a `ctrl` step marks a step the recorded boot accepted but the boot does not depend on (the two MT6877 line-setting probes). An optional step that cannot be carried is logged and stepped over **on the pipe in hand** — no reclaim, no dispose, because the greet after it needs the live pipe.
- Master copy lives at `%LOCALAPPDATA%\PMKMobileTool\mtk\boot-profiles`; the `CopyMtkBootRecipes` build target copies it into every output (override `-p:MtkRecipeSource=<folder>`). Without the folder the NATIVE tab is absent by design.
- The recipes and blobs are **not tracked in git and not shipped**. Licensing decision remains open (see §7).

### 2.4 Hardware validation matrix

| Device / chip | Storage | Proven | Date | Evidence |
|---|---|---|---|---|
| Redmi 9A `dandelion`, MT6765 HW 0x0766 | eMMC CJTD4R | Native boot to DA; live GPT (43 extents); grid Read/Write/Erase; seccfg state; reboot auto-boot; NV erase→restore (mtkclient path) | 2026-11-02 → 2026-11-03 | `native-da.log`, RUN 42/43/47/48/51/52, B5.6 |
| MT6877 HW 0x0959 | UFS | Live DA reached; UFS geometry (`0x40004`) and `0x30/3` extents captured; optional control step measured; split framing answered 17× | 2026-11-03 | RUN B6-1/B6-3/B6-4, `capture` notes |
| Any other MTK chipset | — | Not yet | — | first-run risk |

**Known limits:** native boot depends on the kamakiri race (or an auth file); SLA/DAA chipsets are not supported natively; UFS RPMB/GP sizes are not reported; seccfg write is intentionally absent; sparse images are refused; native multi-partition flash/backup/Format All/NV/IMEI/FRP are not implemented yet.

### 2.5 Guards implemented (must stay)

- `NeverProgramNames` (`pgpt`, `sgpt`, `gpt`, `otp`, `flashinfo`) — brick guard.
- `ReadOnlyPayloadNames` (`seccfg`) — no measured program payload; read stays open.
- Extent checks: block alignment, zero length, past-end-of-user-area, using the DA's own block size.
- One client per BROM: native session and mtkclient workflows refuse each other (`Eject Device first`).
- Confirmation dialog shows the exact extent, image and readback promise before any write.
- Write verify: every native write is read back and compared; a mismatch marks the partition damaged.
- Device-bound GPT cache only (`SaveMediaTekGptCache` refuses unbound tables; `LoadMediaTekGptCache` deletes one it finds).
- Identity policy: eMMC part number is the only identity-adjacent field shown; CID serial, UFS product/serial and chip-unique ids are never decoded, stored or logged.

---

## 3. Completed milestones (condensed, with evidence)

| Milestone | What it established | Evidence |
|---|---|---|
| **B0** (2026-10-28) | A DA survives a process exit, but mtkclient cannot re-init a DA it did not upload → the session-state shortcut is ruled out; the native client is the only route | `%TEMP%\mtk-da-probe`, verdict in §2 |
| **B1** (2026-10-29 → 10-30) | Wire protocol captured read-only; READ/WRITE/ERASE triads measured; checksum solved; frame-driven read corrected | `framing-spec.md`, RUN 6/7/8/9 |
| **B2** (2026-10-30) | Recipe format + fail-closed loader + boot machine + serial transport | `MtkBootProfile.cs`, `MtkDaSession.cs`, `MtkDaTransport.cs` |
| **B3** (2026-10-30) | Native READ/WRITE/ERASE + live GPT once per session | `MtkDaCommands.cs` |
| **B4** (2026-10-31 → 11-02) | Control probes, greeting, re-open, raw-USB transport, UI (Boot/Eject, grid columns, guards), RUN 42/43/47 hardware wins | `MtkNativePanel.cs`, RUN 42/43/47 |
| **B5.1–B5.2** (2026-11-02) | Seccfg lock state read over the live session (read-only by construction) | RUN 48, 647 checks |
| **B5.4** (2026-11-03) | Boot auto-reads GPT; eMMC part number; honest status line; device-bound cache; layout fixes | 664 checks, Release 0/0 |
| **B5.5** (2026-11-03) | NV Erase → NV Restore hardware-proven: 6 partitions, same extents, 6/6 SHA-256, IMEI + signal back | RUN 51/52, backup `20261027_165526` |
| **B5.6** (2026-11-03) | Native Reboot Device auto-boots (Mi logo → charging screen); RUN 49 power-off note retired | `native-da.log` 09:40:09 → 09:40:54 |
| **B5.7–B5.8** (2026-11-03) | Startup first paint 5,390 → 682 ms (double-buffered login card); FLASH grid repaint costs fixed (double buffer, bulk fill, idempotent restyle, dead-session click = one line) | `pmk_startup.log`, commit `36b9e9c` |
| **B5.9** (2026-11-03, uncommitted) | seccfg read-only on the native session (write/erase refused before the dialog; read untouched; mtkclient `da seccfg` unaffected) | tests + `ReadOnlyPayloadNames` |
| **B6** (2026-11-03, uncommitted) | MT6877/UFS support (`0x40004`, `0x30/3`), optional control steps, no-answer state + bounded asks, split framing | RUN B6-1/3/4, tests |

---

## 4. In progress — B6 batch (uncommitted)

**Goal:** make the native session work on a UFS (MT6877) unit without weakening the proven MT6765 path.

**Changed files:** `MediaTek/MtkBootProfile.cs`, `MtkDaCommands.cs`, `MtkDaSession.cs`, `MtkNativeBench.cs`, `MtkWire.cs`, `tests/Regression/Program.cs`, `ROADMAP.md`; master recipes gained `mt6877/` (profile + 5 blobs). One extra line in `MediaTekPanel.cs` (`MultiSelect = false`).

**What is verified now**
- Solution build 0/0; regression **700 checks** PASS; recipes 14/14 copied into Debug and Release outputs.
- UFS geometry decode, shape discrimination, `0x30/3` extent framing, UFS block-size checks, identity non-exposure.
- Optional-step skip on the pipe in hand (no reclaim/dispose); strict step still reclaims once and refuses.
- No-answer bound measured against a fake that never returns (3 s budget, refusal names the reclaim and the recovery).
- Split framing asserted against both recordings; the mt6765 recipe carries no `opt` flag, so its path is unchanged in intent.

**Still open before the batch can be called closed**
1. **MT6765 hardware re-run** after the split-framing change: boot → live GPT → one dump → one sacrificial write/erase. This is the only protocol change that touches the hardware-proven path; the recording supports it, but it has not been run on the 9A with the new build.
2. **MT6877 hardware run:** boot to live DA, UFS read, optional-step skip, reboot. The capture and RUN B6-4 prove the DA answers; a full session is not yet logged.
3. **Docs:** this file's baseline becomes 700 (done here); add the B6 entry with the run evidence; correct the stale "669" wording in B5.9.
4. **Commit** the batch (push only on request).

**Risks**
- Leaving an abandoned control transfer in flight while continuing on the same pipe is measured (RUN B6-3) but is still the least-understood state; keep the late-ask line as evidence.
- `CtrlProbeTimeoutMs` is now 2,000 ms (was 1,000) — a strict refusal on a silent face can take longer than before.
- The MT6877 recipe/blobs are licensing-sensitive and stay untracked.

---

## 5. Remaining work plan (ordered)

### P0 — Close B6 (next)
**Why:** a protocol change is sitting on top of the only hardware-proven boot path.
**Steps:** (1) 9A run: boot, Geometry + GPT, dump `para`, sacrificial write/erase, readback; (2) MT6877 run if the unit is present: boot, UFS read, optional skip, reboot; (3) add the B6 ROADMAP entry + fix the check count; (4) commit.
**Done when:** both runs are in `native-da.log`, regression 700, Debug/Release 0/0, docs consistent.

### P1 — Native MTK parity for the core flows (replace mtkclient for daily work)
**Why:** the native session already owns the phone after one plug; the remaining daily operations still shell out to `mtk.exe`.
**Steps:**
1. **Native Save GPT** — write the live table to a file (same format the mtkclient flow saves).
2. **Native backup selected partitions** — batch dump in one session, folder + manifest + SHA-256 per file.
3. **Native NV Erase → Restore** — reuse the RUN 51/52 extents (`nvram, nvdata, nvcfg, proinfo, protect1, protect2`), restore only from a verified backup, hash before/after, reset at the end.
4. **Native Format All** — whitelist from the live GPT, keep bootchain/seccfg/NV, reset.
5. **Native multi-partition flash** — scatter images through one session (raw only; sparse stays on the mtkclient path until a sparse writer is measured).
6. Keep mtkclient as the fallback and keep the two-client refusal.
**Done when:** each flow has a hardware run on the 9A with a readback/verify, and the mtkclient equivalent still passes its own tests.

### P2 — MTK platform gaps
- **SLA/DAA auth** for newer chipsets (needs auth files or a server; business decision).
- **Per-device auth coverage** (Vivo/Tecno/Infinix: DA present, auth missing).
- **Arbitrary NV item repair** beyond IMEI (needs NV item semantics per chipset).
- **FRP / userlock native removal** (needs measured payloads; do not guess).
- **Bootloader unlock/lock on native** — stays blocked until an unlock payload is captured; seccfg remains read-only.
- **Device validation** per chipset family (6761/6762/6765/6768/6769).

### P3 — Qualcomm
1. Format userdata on the native firehose session.
2. Real QMI/diag IMEI write (today's "IMEI repair" is an EFS/NV restore + heuristic read).
3. Move multi-partition flash, `--erase=frp`/`config` and reboot off `fh_loader.exe`.
4. QC loader auto-detect (mirror the MTK resolver) and USB-ID/hwcode detection before a command.
5. Device validation: EFS backup → restore round-trip, then Format userdata.

### P4 — Samsung / Spreadtrum / HiSilicon
- **Samsung:** Odin-protocol flash; server FRP on new models; testpoint/EDL; full partition read without root.
- **Spreadtrum:** PAC format-all; NV read/write; FDL coverage only from named-device PAC firmware.
- **HiSilicon:** newer Kirin server unlock; EMUI testpoint FRP.

### P5 — Validation backlog (device runs, kept separate from build/regression)
- MTK: one device per chipset family through FRP/userlock + scatter flash; record from the session log.
- MT6877/UFS: full session (boot, GPT, read, write, erase, reboot).
- QC: EFS round-trip + Format userdata on a real SD device.
- Report device results separately; never merge them into the build/regression claim.

### P6 — Polish & release
- Partition manager UX; driver bundle; remote activation; report export.
- Keep the startup and grid performance probes (`pmk_startup.log`); they are the only way to catch a regression.
- Licensing review before any shipped build (Phase 0).

---

## 6. Phase plan

| Phase | Content | Status |
|---|---|---|
| **0 — Foundation** | Keep this file current; decide the licensing of OEM DA/auth/firehose blobs; decide whether to run an auth server | **Open decision** |
| **1 — Deepen existing platforms** | MTK gaps (P1/P2), QC native coverage (P3), Samsung/SPD/HiSilicon (P4) | In progress |
| **1.5 — Validation** | Device matrix, separate from build/regression (P5) | Continuous |
| **1.75 — MTK Boot Device** | Native DA session B0–B6 | **Hardware-proven on MT6765; B6 open** |
| **2 — Samsung deepening** | Odin protocol, EDL testpoint (PIT done) | Not started |
| **3 — Server auth** | FRP/EDL/Mi-account/Samsung via server | Business decision |
| **4 — Apple + ISP** | Highest legal/technical risk; third-party components only | Not started |
| **5 — Polish** | One-click flows, QC loader auto-detect, UX, drivers, activation, reports | Partially done (MTK auto-detect) |

---

## 7. Constraints & safety rules

- **Licensing (MTK native):** mtkclient is GPL-3.0. The native session is clean-room from the public SP-Flash-Tool protocol and observed wire traffic — never translated from `xflash_*` source. Reading a GPL program's output is observation; do not open its source while implementing.
- **Licensing (blobs):** OEM-signed DA/auth/firehose/FDL blobs are proprietary. The current packs are unlicensed re-hosts — decide whether to ship, drop or source them legally. The native recipes (`mtk/boot-profiles`) are in the same position and are not tracked or shipped.
- **Never commit** `mtk/boot-profiles`, `bundled-tools/`, `tools/` or `edl/`; the rollback reference for loader binaries is `bundled-tools/integrity.json` + `dependency-lock.json`. After any loader change: `scripts/make_tool_integrity.ps1` → sync `bundledInventory.sha256`/`entries` → Release build → `scripts/verify_qc_loaders.ps1`.
- **Never copy** a differently-named Common DA into an existing loader folder — it silently replaces a working DA; hash-check against the seed first.
- **MTK loader rule:** never auto-pass a catalog `--loader` over mtkclient's built-in DA by default — the xflash DA-extension patch path only works with the DA mtkclient bundles itself. Built-in first, catalog on failure (measured on the Redmi 9A).
- **One client per BROM:** a native session and an mtkclient workflow refuse each other; two clients corrupt both.
- **One plug cycle = one command** for mtkclient flows; the native session is the exception (boot once, then many operations).
- **Device validation is separate** from build/regression status. Never claim a throughput or success-rate gain without a measured flash.
- **Identity policy:** never decode, store or log the eMMC CID serial, UFS product/serial, ME_ID, SOC_ID or chip-unique ids. The eMMC part number is the only identity-adjacent field shown.
- **GPT cache:** only a table bound to the device's own geometry may be reused; an unbound table is refused/deleted.
- **Log display:** `DeviceClass - USBError(5, 'Input/Output Error')` must not appear in the app log (the raw transcript keeps it).
- **Never delete** `%LOCALAPPDATA%\PMKMobileTool\MediaTekSessions\seccfg-backup` or `archive-20261013`; never delete `hwparam.json` (device NV samples) or `port_trace.txt`.
- **Recipe handling:** master copy at `%LOCALAPPDATA%\PMKMobileTool\mtk\boot-profiles`; `CopyMtkBootRecipes` copies it into every build output; override with `-p:MtkRecipeSource=<folder>`.

---

## 8. Build, test and device gates

```powershell
# desktop (Debug / Release)
dotnet build PMKUnlocker.csproj --no-restore
dotnet build PMKUnlocker.csproj -c Release --no-restore

# all four projects
dotnet build PMKUnlocker.sln --no-restore -m:1

# regression (hardware-free)
dotnet run --project tests/Regression/Regression.csproj
```

- A running `PMKUnlocker.exe` locks the output; ask the user to close it after the current device operation finishes — never kill an active flash and never create an alternate output directory.
- Debug and Release must both be 0/0; the regression baseline is 700 after the B6 batch lands (664 at `36b9e9c`).
- Hardware runs are reported separately, with the session log as evidence, and are never inferred from a build.

---

## 9. Evidence index

| What | Where |
|---|---|
| Native session log | `%LOCALAPPDATA%\PMKMobileTool\MediaTekSessions\native-da\native-da.log` |
| Pacing / knobs note | `%LOCALAPPDATA%\PMKMobileTool\MediaTekSessions\native-da\pacing.txt` |
| Startup probe | `%LOCALAPPDATA%\PMKMobileTool\pmk_startup.log` |
| Recipe master | `%LOCALAPPDATA%\PMKMobileTool\mtk\boot-profiles` (`mt6765/`, `mt6877/`) |
| NV backup used for B5.5 | `C:\Users\PMK\Desktop\New folder\20261027_165526` (6 bins + manifest, 190,701,568 B) |
| Wire captures / spikes | `%TEMP%\mtk-da-spike\capture` (framing spec, write/erase/read transcripts, RUN B6 notes) |
| Regression | `tests/Regression/Program.cs` (700 checks) |
| Loader integrity | `bundled-tools/integrity.json`, `dependency-lock.json` |

---

## 10. Open decisions

1. **Blob licensing** — ship, drop or re-source the OEM DA/auth/firehose/FDL packs; same question for the native recipes.
2. **Auth server** — build Phase 3 or stay local-only.
3. **MT6877 recipe** — confirm provenance/licensing before any distribution.
4. **Native vs mtkclient default** — once P1 lands, decide which path is the default per operation (proposal: native for boot/GPT/dump/restore/erase/reboot, mtkclient for scatter flash until P1.5 is proven).

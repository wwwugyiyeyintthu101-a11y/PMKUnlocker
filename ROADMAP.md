# PMK Unlocker — Roadmap & Work Plan

**Status date: 2026-11-04.** This file is the single plan of record: what the tool can actually do today, what was proven on hardware, what is uncommitted, and the ordered list of work that remains. Detailed RUN logs are summarised here; the raw evidence stays in the files named in §9.

**Current baseline**
- `tests/Regression`: **739 checks** (735 at `c93ea36`, +4 for the B6.10 walk-timing marks; 722 before the B6.7 auto-pipe wiring, 703 before the B6.6 auto-recipe wiring, 700 at `7bc88a5`).
- Desktop Debug and Release: **0 warnings / 0 errors**; solution build (desktop + license server + admin + regression): **0/0**.
- Native MTK DA session is hardware-proven on **MT6765 (HW 0x0766)** and **MT6877 (HW 0x0959)**; every other chipset is first-run risk.
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
| UFS support | no | yes (B6, hardware-proven on MT6877) | `0x40004` geometry + `0x30/3` extent pair |
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
| MT6877 HW 0x0959 | UFS, 4096 B blocks, 238 GiB | **Full native session:** boot to live DA; `0x40001` refused as non-eMMC → `0x40004` UFS geometry; limits 2 MiB / 64 KiB; READ 4 KiB and 16 KiB; live GPT **77 partitions**; both optional line-setting asks stepped over on the pipe in hand; Reboot Device answered status 0 and the phone rebooted | 2026-11-04 09:19:45 → 09:25:03 | `native-da.log` (281 lines), RUN B6-1/B6-3/B6-4 + this run |
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
| **B5.9** (2026-11-03, `7bc88a5`) | seccfg read-only on the native session (write/erase refused before the dialog; read untouched; mtkclient `da seccfg` unaffected) | tests + `ReadOnlyPayloadNames` |
| **B6** (2026-11-03 → 11-04, `7bc88a5`) | MT6877/UFS support (`0x40004`, `0x30/3`), optional control steps, no-answer state + bounded asks, split framing; **hardware-closed on MT6877** (boot, UFS geometry, 77-partition GPT, READ, optional skip, reboot) | RUN B6-1/3/4, `native-da.log` 2026-11-04, tests |

---

## 4. B6 — hardware-closed on MT6877, one re-run left on MT6765

**Goal:** make the native session work on a UFS (MT6877) unit without weakening the proven MT6765 path.

**Committed in `7bc88a5`:** `MediaTek/MtkBootProfile.cs`, `MtkDaCommands.cs`, `MtkDaSession.cs`, `MtkNativeBench.cs`, `MtkWire.cs`, `tests/Regression/Program.cs`, `ROADMAP.md`; master recipes gained `mt6877/` (profile + 5 blobs). One extra line in `MediaTekPanel.cs` (`MultiSelect = false`).

**MT6877 hardware run (2026-11-04 09:19:45 → 09:25:03) — PASS**
- BROM → DA1 (454,632 B) → DA2 (542,012 B) → DAext (16,428 B) → live DA, 34 s including one 15 s DA1 stall. *(The "stall" is re-stated in B6.10: it is 21 s here, spread over 7,487 untraced steps plus two 3 s optional probes, and the phone is measured fast.)*
- `0x40001` answered 104 bytes in a non-eMMC shape → `0x40004` returned type 48 (UFS), block 4096, user 255,919,652,864 B (238 GiB), boot1/2 4 MiB, rpmb 0.
- Limits 0x200000 / 0x10000; READ 4 KiB and 16 KiB came back; live GPT listed **77 partitions** and the extents were bound to `HW 0x0959 · block 4096 · user 255919652864 · 77`.
- Both optional line-setting asks (`SET_LINE_CODING`, `SET_CONTROL_LINE_STATE`) never completed; both were logged and stepped over on the pipe in hand, and the greet + DA upload still succeeded.
- Reboot Device sent the TOPRGU reset, the DA answered status 0, the session closed and the phone rebooted.
- No `DeviceClass - USBError(5, ...)` line reached the app log.

**Still open**
1. **MT6765 re-run after the split-framing change — DONE, and its write half closed on the MT6877 instead** (boot + geometry + live GPT 2026-11-04 10:42/11:12/11:15; `para` read → read determinism → write → `VERIFY OK` → independent re-read 2026-11-04 11:24–11:31, see B6.9). What is left of it is only the optional sacrificial **erase** + restore, and a 9A `para` dump if that unit specifically wants one.
2. **15 s DA1 stall on MT6877 — attributed, and the phone is exonerated** (B6.10, 2026-11-04, measured from the log and from the capture the recipe was compiled from — no phone touched). It is **20–22 s in every one of the eight recorded MT6877 boots**, and the same window on MT6765 is 1–2 s. Of that, **6 s is two named, optional CDC control probes** (steps 162/163, `CtrlProbeTimeoutMs` 2,000 + `CtrlProbeAskSlackMs` 1,000 each, answered only ~300–600 s later by the abandoned transfer); ~1 s is the steps the log does show; and the remaining **~14 s is host-side per-step cost in the window the wire trace stops printing at step 220** — 4,711 reads and 2,776 writes, ≈3 ms per read, against the reference client's measured **0.834 ms per 512-byte IN and 0.093 ms per 512-byte OUT on this same phone**. What is left is not *"is the phone slow?"* (it is not) but *"what inside 221→7708 costs the 3 ms?"*, which `WalkPhaseLine` now answers from the next boot's own log.
3. **Docs/commit of this closure batch** (0x0959 in `BootProvenOnHardware`, KiB size text, simple-log screen, log retry).

**B6.6 — boot recipe = Auto (2026-11-04)**
PMK's word: *"boot profile နေရာကိုလဲပြင်ပေးပါ auto ထားပေးပါ ခနခန သွားချိန်းနေရလို့ပါ"* — with two phones on the bench he was switching the recipe dropdown before every boot.
- The NATIVE combo's row 0 is **Auto (detect chipset)** and is the default; `MtkNativeBench.ProfileChoice` maps a combo row to the installed file (the old code indexed the file list with the combo index, so Auto booted recipe 0 and the last name was out of range).
- `NativeBootAsync` loads **every** installed recipe (a recipe whose blobs do not load is logged and skipped, not fatal), prints `AutoBootLine`, and passes `autoProfiles:` to `MtkDaSession.BootAsync`.
- The session starts on the first recipe and switches at the BROM **HW-code reply** — the first byte of the walk that is chipset-specific. The switch is guarded by `MtkBootProfile.SamePrefix(…, index + 1)`, and `MtkBootProfile.Load` already refuses any recipe that does not open with `0xfd` / its echo / the 4-byte HW-code reply, so the three guarded frames are identical by construction for every installable recipe. Nothing is replayed and the pipe is not reopened; an HW code no installed recipe claims stays the fail-closed refusal.
- Both installed recipes were checked against this: `mt6765` (4486 BROM steps) and `mt6877` (7717) open with the same three steps, so a switch works in either direction on the bench.
- Regression 722 (+19): `ProfileChoice` mapping incl. the out-of-range row, `AutoBootLine`, `PickByHwCode`, `SamePrefix` (whole-prefix, boundary at the 4th frame, hand-built reply mismatch, short list), an end-to-end auto boot that starts on the 0x0766 fixture and lands on 0x0959 with the same 29 writes / 173 bytes as the hand-picked run, the 0x0BAD refusal costing one byte, a late-divergent recipe stopped by the next echo (2 writes, nothing after it), the hand-picked path keeping its old refusal wording, and the simple-log line for the auto start.
- **Hardware-PROVEN on both bench phones (PMK's clicks, dropdown untouched):**
  - **MT6877 10:40:31 → 10:41:01** — `boot recipe = auto — 2 installed`, `starting on recipe mt6765_boot_profile.json`, then `[*] Auto: recipe MT6877 (HW 0x0959)` + `BROM face proven: HW 0x0959` at the HW-code reply; only the mt6877 blobs streamed (DA1 454,632 / DA2 542,012 / DAext 16,428); UFS `0x40001`→`0x40004` fallback, 77-partition live GPT bound to `HW 0x0959 · block 4096`.
  - **MT6765 10:42:11 → 10:42:59** — same two auto lines, then `[*] Auto: recipe MT6765 (HW 0x0766)`: the resolver names the recipe it landed on **even when that is the one it started on**, so the line is not evidence of a switch. `0x40001` answered the eMMC shape directly (type 1, block 512, 58 GiB) with **no `0x40004` fallback**; limits 0x200000/0x10000; READ 0x200+0x200 and 0x400+0x4000; live GPT 43 partitions bound to `HW 0x0766 · block 512 · user 62537072640 B · 43`.
  - Neither run logged a refusal, a `[!]` line or a replay. The split-framing change therefore now sits under a hardware run on the MT6765 path as well.
- The 9A run stopped at the GPT fill; the write half it was waiting on has since been run on the MT6877 unit instead — see B6.9.

**B6.7 — pipe = Auto (2026-11-04)**
PMK's next word: *"pipe raw USB-pipe COM port နေရာမှာလဲ Auto ထားပေးပါ"* — the same complaint one control further down the row.
- The combo's row 0 is now **Pipe: Auto (detect)**, default; raw USB is row 1 and the COM port row 2, so a forced A/B comparison is still one click away. `MtkNativeBench.PipeChoice` maps the row and a row past the end falls back to Auto rather than inventing a pipe.
- Auto reads the bus before anything is sent: the bulk face is looked for through libusb (`EnumerateFaces`, accepted PIDs only) and the serial face through the MediaTek COM list, and `PickRawPipe` gives raw USB **any tie** — every hardware-proven boot on both chipsets came through the bulk endpoints, and the same node on the COM face was refused in RUN 10/RUN 11. The COM port is chosen only when no bulk face is listed while a BROM-PID COM port is. Nothing listed at all is a phone that has not been plugged in yet, so the wait stays on the proven pipe and its own 90 s refusal stays the honest answer.
- Only a **BROM PID** counts on either side (`BulkBromFaceListed` / `ComBromPortListed`): a phone an earlier session left behind still advertises VID 0E8D under the DA's 2001, and counting that would move Auto onto a port that cannot be in BROM.
- The choice is logged in one line — `[*] Native DA: pipe = auto (raw USB) — the USB face was listed and the MediaTek COM port was not listed; …` — so a later reader sees what each side said. A COM list Windows cannot enumerate is reported as a missing look, not a refusal to boot.
- Fix found while wiring this: `ConciseNativeLine` trimmed the line at its em dash **before** the pipe branch tested for "power the phone off", so the plug-in reminder never reached the simple screen. The branch now rebuilds the reminder and names the pipe it is waiting on (`[>] Pipe: raw — power off, hold BOTH volume keys, plug in`).
- Second fix found in the same reading: the session-closed line was the COM-era constant `[+] Native DA session closed — the COM port is released.`, and the 10:41:49 / 10:50:49 runs in `native-da.log` show it printed under **raw-USB** boots (both say `pipe = raw USB bulk endpoints` a few hundred lines earlier). With Auto picking the pipe, the dropdown stops being a record of what a run used, so the line now names `nativePipeName` — the branch the boot actually took: `… — raw USB bulk pipe is released.` / `… — COM port is released.`
- Regression 735 (+13): the row mapping incl. out-of-range, the tie/exception/nothing-listed rules, the BROM-PID filter on both sides, the auto line's wording for each choice, and the two simple-screen shapes.
- Hardware PROVEN on the bench (2026-11-04 11:12 and 11:15, MT6765, pipe left on Auto): `[*] Native DA: pipe = auto (raw USB) — the USB face was not listed and the MediaTek COM port was not listed; raw USB is the pipe every recorded boot came through` → `pipe = raw USB bulk endpoints` → `11:12:24 BROM: usb 0E8D:0003` → `11:12:44 [+] MTK DA ready — MT6765 (HW 0766)` → `11:12:48 [+] Live GPT: 43 partition(s)` + `43 extent(s) are the live table now`. Both sides "not listed" is the phone-not-plugged-yet case the rule was written for, and Auto correctly stayed on the proven pipe; the screen showed `[+] Pipe: auto (raw USB)` and `[>] Pipe: raw — power off, hold BOTH volume keys, plug in` — the ConciseNativeLine fix is live on the operator's screen. No `[!]` line in either run.
- Still owed: nothing — the Auto-pipe boot ran on the MT6877 unit too (below).
- **MT6877 Auto-pipe boot (2026-11-04 11:23:41 → 11:24:11)**, PMK's clicks, both dropdowns untouched: `pipe = auto (raw USB)` → `[*] Auto: recipe MT6877 (HW 0x0959)` at the HW-code reply → `11:24:11 [+] MTK DA ready — MT6877 (HW 0959)` → UFS `type 48, block 4096, user 255919652864 B (238 GiB)`, limits 0x200000/0x10000, live GPT **77 partitions** bound to `HW 0x0959 · block 4096`. The pipe rule is therefore proven on both chipsets, and the recipe-Auto switch fired in the mt6765-start → mt6877-land direction exactly as the `SamePrefix` check predicts.
- **The session-closed fix is live on hardware**: `11:23:39 [+] Native DA session closed — raw USB bulk pipe is released.` — the first run of the new wording, on a raw-USB session, replacing the COM-era constant that the 10:41:49 and 10:50:49 runs show was false.
- **RUN 49's 9A reboot note resolved by the same bench**: the 10:50:49 Reboot Device reported `the bus still carries 0e8d:0003 — the phone did not move`, but the 11:23:39 Reboot on the same unit reported `0e8d:0003 is gone and no MediaTek device is on the bus at all — the phone left download mode or powered off while the boot was talking to it` — the phone did leave. Same 3-byte `0x000000` then silence on the wire both times, so the difference is the phone's state, not our recipe; the bus check is what separates them, and it said the right thing.

**B6.8 — the native GPT read shows the partition grid (2026-11-04)**
PMK's word: *"Live GPT: 43 partition(s) ရောက်တာနဲ့ Partition ဇယားကွက်ပေါ်လာစေချင်တယ်"*.
- Nothing was missing from the data: `FillFlashGridFromNativeTable` already wrote 43 `Live GPT` rows. They sat on the **FLASH** sub-tab while a boot is started from **NATIVE**, so the operator read a partition *count* with no partition *table*.
- `NativeReadDeviceTableAsync` now calls `ShowMediaTekFlashPage()` inside the same `NativeUi` marshal as the grid fill, so the table is on screen at the moment the `Live GPT` line lands. The page is found by its own `"FLASH"` caption rather than index 3, because the NATIVE page was appended after it and a future tab must not silently change which page this opens.
- Same path serves the NATIVE tab's Geometry + GPT button, which is the other caller of `NativeReadDeviceTableAsync`.
- Debug 0/0, regression 735 unchanged (the change sits in the panel, which the hardware-free test project does not link).
- Hardware PROVEN (2026-11-04 11:16, MT6765): second consecutive clean Auto-pipe boot (`pipe = auto (raw USB)` → `11:16:26 MTK DA ready — MT6765 (HW 0766)` → `11:16:30 Live GPT: 43 partition(s)` + `43 extent(s) are the live table now`), and PMK's screenshot shows the **FLASH** page forward on its own at that moment with the 43 `Live GPT` rows and their Read/Write/Erase cells — his word: *"partition grid ပေါ်လာပါပြီ"*.

**B6.9 — MT6877 write half proven on hardware (2026-11-04 11:24–11:31, Redmi Note 12 Pro, PMK's clicks)**
The first write ever pushed through the native session on this chipset (RUN 44's write was the MT6765 `logo`), and it landed on the UFS geometry: block 4096, `para = LBA 1032..1159 = 0x408000 + 0x80000`.
- **Read** (grid) → `READ 0x408000 + 0x80000 — 524288 byte(s) in 8 frame(s)` → `para-20261104-112440.img`. The file is exactly the extent size and is **not** an all-zero echo: 135 non-zero bytes in two structures at `0x20000..0x20083` and `0x23ff4..0x23ffd`, which is the MTK para layout — so a mostly-zero para dump is a *good* read, not a dead one.
- **Read determinism**: a second Read five minutes later (`11:29:41`) produced a byte-for-byte identical file. Only then was anything written.
- **Write** of that same dump back (`11:31:04`) → `WRITE 0x408000 + 0x80000 committed.` → `Verifying the write by reading the same range back...` → `READ … 524288 byte(s) in 8 frame(s)` → `VERIFY OK — the device answers with the same bytes for the whole range.` → `[OK] Native DA: para written from para-20261104-112941.img and read back identical.` Commit-to-verified in ~1 s, one 512 KiB chunk against the DA's own 0x200000 write-chunk limit.
- **Independent confirmation**: a third Read after the write (`11:31:52`) is identical too. All four artifacts — the three dumps plus the Temp backup taken before anything was written — hash to `c7b92e5763ad8712c8ece2bdab134c292daa1c3e55144cb28d8805e8804ed635`. The device's `para` is unchanged by the exercise, which is the point: the write path was tested with content the device already held.
- The grid Write always verifies — `verify: true` is hardcoded in `NativeGridWriteAsync`, so the FLASH tab's "Verify readback (SHA-256)" checkbox plays no part in this route, and `ConfirmText("write", …)` states the raw-bytes honesty and the smaller-image rule before the port is touched.
- **Still owed:** the sacrificial **erase** + restore (step ဂ) — deliberately not run, because it needs PMK's separate word and leaves the extent genuinely empty for a window.

**B6.10 — the MT6877 pre-DA1 wait measured without a phone (2026-11-04)**
Open item 2 said "15 s DA1 stall", guessed at either 15 × 1 s armed-read timeouts or a device-side pause, and asked for a measurement before the UFS boot's timing could be called understood. Nothing on the bench was needed: the log already had eight boots and the capture the recipe was compiled from times the phone itself.
- **It is not 15 s and not a stall — it is 20–22 s, every time, in a stable shape.** Payload ack → `streamed …da1.bin` across the eight MT6877 boots: 11-03 16:01 (22 s), 16:03 (22), 16:33 (22), 17:39 (22), 11-04 09:20 (22), 09:41 (21), 10:40 (20), 11:24 (21). The identical window on MT6765 is **1–2 s** in every one of its seventeen boots (10:42:34→10:42:35, 11:12:24→11:12:25, 11:16:05→11:16:06 …). A device-side pause that appears on one chipset and not the other, always the same size, is not a pause — the recipes differ by step count.
- **6 s of it is named in the log already.** Steps 162/163 (`SET_LINE_CODING` + `SET_CONTROL_LINE_STATE` after the payload) are the optional CDC probes: `CtrlProbeTimeoutMs = 2000` + `CtrlProbeAskSlackMs = 1000` = 3 s each, `after 1 ask(s)`, stepped over. 11:23:49 → 11:23:52 → 11:23:55. The abandoned asks then report themselves ~300–600 s later (`came back 603711 ms after it was submitted — libusb -9`), which is the RUN B6-3 state, not a new one. On MT6765 the same two steps do not cost 3 s, which is why that phone's window is 1 s.
- **The remaining ~14 s is invisible by construction.** `WireTraceSteps = 220`: the wire trace prints through step 220 and nothing after, so 11:23:56 (wire 220) → 11:24:10 (`streamed`) is 7,487 recipe steps with zero log lines. Those steps are **4,711 reads and 2,776 writes** for MT6877 (vs 2,837/1,420 for MT6765) — the MT6877 recipe is 7,717 BROM steps precisely because its recorded boot replays ~3,230 more wire events, and the extra ones are reads of 512 bytes each.
- **The control measurement says the phone is fast.** In `b6-mt6877-20261103-132708.pcap` — the accepted boot this recipe was compiled from, same unit, same pipe — the reference client's two bulk upload runs are **944 OUTs completing in 0.088 s** and **1,062 OUTs in 0.159 s**, which match DA1 (454,632 / 512 = 888 chunks) and DA2 (542,012 / 512 = 1,058) in that order; its 512-byte read run is **128 INs in 0.107 s**. Per transfer that is **0.093–0.150 ms out and 0.834 ms in**. Its whole capture has **no gap larger than 1.006 s** and only 2.85 s cumulative in gaps over 200 ms. So 454,632 bytes cross this pipe in a tenth of a second and the device answers a 512-byte read in under 1 ms; our ≈3 ms per read is a host cost, not the phone's. (Cross-check from the log, same conclusion from the other end: our own DA2 upload — 542,012 bytes, *bigger* than DA1 — takes **1 s** once the DA is live.)
- **So the two guesses in the old item were both wrong, in opposite directions:** not 15 × 1 s of armed-read timeouts (that would show as many ~1 s gaps; the log shows one 14 s block), and not a device-side pause (the accepted boot on the same phone has no such pause). It is the per-step cost of a read-dominated replay, plus 6 s of optional probes this chipset never answers.
- **Instrumented rather than guessed.** `MtkNativeBench.WalkPhaseLine` prints one mark where the trace goes quiet — `[*] Walk timing: 7708/7717 step(s) in 14123 ms (4711 read(s), 2776 write(s), 220 control(s)) — uploading mt6877_da1.bin now` — and `StreamedLine` appends the upload's own duration to the blob line it already printed. The mark is emitted **before** the upload so a refusal inside it cannot cost the operator the only line explaining the wait before it, and only on a hardware run (`_traceOn`), so a recorded replay stays byte-identical. No byte sent to the phone changed: `RunStageAsync` counts steps it already performed and times them.
- Regression **739** (+4): the phase mark's wording and counters (including the all-zero case, which prints zeros rather than omitting them), and the blob line keeping its old text with only the duration added (a sub-millisecond upload must still say `in 0 ms` so the fast path remains visible as a control). Debug/Release **0/0**.
- **Not done — and each is a decision, not a measurement:** the 6 s is still on the wire because `mt6877_boot_profile.json` records those two probes as steps the capture *saw*; skipping them is a recipe change and needs a boot re-proved on hardware. The 14 s split (libusb per-transfer overhead vs the `Task.Delay(10)` in `ReadCoreAsync` vs NAK pacing) is what the next MT6877 boot's `Walk timing` line will say. USBPcap is uninstalled with a reboot pending, so a fresh capture of our own boot is not available right now either.

**Risks**
- Leaving an abandoned control transfer in flight while continuing on the same pipe is measured (RUN B6-3) but is still the least-understood state; keep the late-ask line as evidence. On this run the abandoned ask came back 306,611 ms later, at session close.
- `CtrlProbeTimeoutMs` is now 2,000 ms (was 1,000) — a strict refusal on a silent face can take longer than before.
- The MT6877 recipe/blobs are licensing-sensitive and stay untracked.

---

## 5. Remaining work plan (ordered)

### P0 — Finish B6 (next)
**Why:** the MT6877 half is closed and the Auto boot has run on both phones; the write half of the MT6765 re-run is the only protocol exposure left.
**Steps:** (1) 9A write half: dump `para` and one sacrificial write/erase with readback, through the live session already open; (2) ~~measure the 15 s DA1 stall on the MT6877 unit if it is on the bench~~ — **done from the log and the owned capture instead of the bench, see B6.10**; (3) commit this closure batch.
**Done when:** the `para` dump and the write/erase + readback are in `native-da.log`, regression 739, Debug/Release 0/0, docs consistent.

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
- Debug and Release must both be 0/0; the regression baseline is 739 (735 at `c93ea36`, 722 after the B6.6 auto-recipe wiring, 703 after the B6 closure batch, 700 at `7bc88a5`, 664 at `36b9e9c`).
- Hardware runs are reported separately, with the session log as evidence, and are never inferred from a build.

---

## 9. Evidence index

| What | Where |
|---|---|
| Native session log | `%LOCALAPPDATA%\PMKMobileTool\MediaTekSessions\native-da\native-da.log` — the 2026-11-04 MT6877 run is the 281 lines from 09:19:45 to 09:20:20, plus the reset at 09:25:03 |
| Pacing / knobs note | `%LOCALAPPDATA%\PMKMobileTool\MediaTekSessions\native-da\pacing.txt` |
| Startup probe | `%LOCALAPPDATA%\PMKMobileTool\pmk_startup.log` |
| Recipe master | `%LOCALAPPDATA%\PMKMobileTool\mtk\boot-profiles` (`mt6765/`, `mt6877/`) |
| NV backup used for B5.5 | `C:\Users\PMK\Desktop\New folder\20261027_165526` (6 bins + manifest, 190,701,568 B) |
| Wire captures / spikes | `%TEMP%\mtk-da-spike\capture` (framing spec, write/erase/read transcripts, RUN B6 notes) |
| Regression | `tests/Regression/Program.cs` (739 checks) |
| Loader integrity | `bundled-tools/integrity.json`, `dependency-lock.json` |

---

## 10. Open decisions

1. **Blob licensing** — ship, drop or re-source the OEM DA/auth/firehose/FDL packs; same question for the native recipes.
2. **Auth server** — build Phase 3 or stay local-only.
3. **MT6877 recipe** — confirm provenance/licensing before any distribution.
4. **Native vs mtkclient default** — once P1 lands, decide which path is the default per operation (proposal: native for boot/GPT/dump/restore/erase/reboot, mtkclient for scatter flash until P1.5 is proven).

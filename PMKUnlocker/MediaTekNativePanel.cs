#nullable disable
using System.Diagnostics;
using PMKUnlocker.MediaTek;

namespace PMKUnlocker;

// ================= NATIVE DA SESSION TAB (ROADMAP Phase 1.75 "Path B", stage B4) =================
//
// WHAT THIS IS. The bench harness for the native MTK client — the first thing in the product that talks to a
// MediaTek phone without spawning mtkclient. B2 replays the recorded boot, B3 reads/writes/erases extents on top
// of it, and this tab is the smallest operator path that can put those two stages on a real phone: boot, ask the
// DA for its geometry, read the live GPT, dump a partition, restore that dump with a read-back, erase.
//
// WHY IT IS GATED ON A RECIPE. The boot needs DA/EMI images that come from unlicensed packs, and that licensing
// call is PMK's. So when <c>mtk/boot-profiles</c> next to the exe is empty — which is every shipped build today —
// this tab does not exist at all. Fail-closed by absence: there is no button to press and nothing to guess.
//
// WHY THE STEPS ARE SEPARATE BUTTONS. Nothing here has been validated on hardware. Every click is one transaction
// group from the captures, so a failure names the group that failed instead of leaving a half-run flash behind.
// The destructive pair (restore, erase) shows the exact byte extent and needs an explicit Yes.
public partial class Form1
{
    private MtkDaSession nativeDa;
    private CancellationTokenSource nativeCancel;
    private ComboBox cmbNativeProfile;
    private ComboBox cmbNativeTransport;
    private TextBox txtNativePartition;
    private Label lblNativeState;
    private Label lblNativeProgress;
    private Button btnNativeBoot, btnNativeClose, btnNativeGeometry, btnNativeDump, btnNativeRestore, btnNativeErase;
    private bool nativeBusy;
    private bool nativeBenchLogBroken;
    private bool nativeCloseRequested;
    private string nativeLastDump;
    // FindProfiles walks the recipe tree, so the combo shows a friendly name and the boot uses the full path.
    private readonly List<string> nativeProfilePaths = new();

    private void BuildNativeDaTab()
    {
        IReadOnlyList<string> profiles;
        try { profiles = MtkNativeBench.DiscoverProfiles(); }
        catch (Exception ex) { NativeSay("[!] Native DA: recipe folder could not be listed (" + ex.Message + ")"); return; }
        if (profiles.Count == 0) return;      // no recipe installed => no tab. The licensing decision stays PMK's.

        TabPage nativeTab = new("NATIVE") { BackColor = Color.FromArgb(28, 31, 38) };
        nativeTab.Padding = new Padding(8);
        nativeTab.ForeColor = Color.Gainsboro;
        mtkModes.TabPages.Add(nativeTab);

        cmbNativeProfile = new ComboBox
        {
            Location = new Point(UiX(0) + 350, UiY(4) - 2),
            Size = new Size(250, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(38, 42, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f)
        };
        var lblProfileTag = new Label
        {
            Text = "Boot recipe:",
            Location = new Point(UiX(0) + 260, UiY(4) + 4),
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 8.5f)
        };

        foreach (string profile in profiles)
        {
            nativeProfilePaths.Add(profile);
            cmbNativeProfile.Items.Add(Path.GetFileNameWithoutExtension(profile));
        }
        cmbNativeProfile.SelectedIndex = 0;

        // The pipe is a choice, not a constant. On this phone the same 0xfd is echoed over the device's bulk
        // endpoints and answered 0xfe over the COM face Windows shows on the same node, so the only honest bench
        // offers both and writes into the log which one a run used. Raw USB is the default because that is the pipe
        // the recorded boot actually came through.
        cmbNativeTransport = new ComboBox
        {
            Location = new Point(UiX(0) + 662, UiY(4) - 2),
            Size = new Size(180, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(38, 42, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f)
        };
        cmbNativeTransport.Items.Add("Pipe: raw USB");
        cmbNativeTransport.Items.Add("Pipe: COM port");
        cmbNativeTransport.SelectedIndex = 0;
        if (toolTipMain != null)
            Tip(cmbNativeTransport, "Raw USB claims the device's bulk endpoints through UsbDk/WinUSB — the pipe the recorded boot was captured through, and the one mtkclient uses. COM port talks through Windows' serial driver; RUN 10 and RUN 11 were refused there. Pick raw USB for the first try; switch only to compare, and read the line it logs either way.");

        btnNativeBoot = Create3DButton("🔌 Boot DA (recipe)", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnNativeBoot.Click += async (_, _) => await NativeGuarded("boot", NativeBootAsync);
        if (toolTipMain != null) Tip(btnNativeBoot, "Replay the recorded BROM→DA1→DA2 recipe on the MTK VCOM — the phone must be powered off, Volume Down held, then plugged in. Nothing is sent before the HW code is proven.");

        btnNativeGeometry = Create3DButton("📐 Geometry + GPT", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnNativeGeometry.Click += async (_, _) => await NativeGuarded("geometry", NativeGeometryAsync);
        if (toolTipMain != null) Tip(btnNativeGeometry, "0x40001 storage descriptor + 0x40007 transfer limits + one GPT read, all cached on the session. This is the 'boot once, then work' promise.");

        btnNativeClose = Create3DButton("✖ Close session", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnNativeClose.Click += (_, _) => NativeClose();
        if (toolTipMain != null) Tip(btnNativeClose, "Release the COM port. Cancels a running operation at its next frame boundary.");

        btnNativeDump = Create3DButton("📥 Dump partition", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Green);
        btnNativeDump.Click += async (_, _) => await NativeGuarded("dump", NativeDumpAsync);
        if (toolTipMain != null) Tip(btnNativeDump, "READ 0x10005 over the whole partition extent, frame by frame, into a timestamped file under the tool's data folder.");

        btnNativeRestore = Create3DButton("⬆ Restore (verify)", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnNativeRestore.Click += async (_, _) => await NativeGuarded("restore", NativeRestoreAsync);
        if (toolTipMain != null) Tip(btnNativeRestore, "WRITE 0x10004 of the last dump + 0x80005 commit, then read the whole range back and compare — success means the bytes came home.");

        btnNativeErase = Create3DButton("🗑 Erase partition", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Red);
        btnNativeErase.Click += async (_, _) => await NativeGuarded("erase", NativeEraseAsync);
        if (toolTipMain != null) Tip(btnNativeErase, "ERASE 0x10003 — sent bare, answered with report words. Physical zeroing is NOT proven by this tool; dump first.");

        Panel grpSession = CreateGroupPanel("NATIVE DA SESSION", UiX(0), UiY(0), 3, btnNativeBoot, btnNativeGeometry, btnNativeClose);
        Panel grpData = CreateGroupPanel("DUMP / RESTORE / ERASE", UiX(0), UiY(2), 3, btnNativeDump, btnNativeRestore, btnNativeErase);

        var lblPartition = new Label { Text = "Partition:", Location = new Point(UiX(0), UiY(4) + 4), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5f) };
        txtNativePartition = new TextBox
        {
            Location = new Point(UiX(0) + 70, UiY(4)),
            Size = new Size(160, 24),
            Text = "logo",
            BackColor = Color.FromArgb(38, 42, 50),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };
        lblNativeState = new Label
        {
            Location = new Point(UiX(0), UiY(5) + 2),
            Size = new Size(856, 20),
            Text = MtkNativeBench.StatusLine(false, "", 0, 0),
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f),
            AutoEllipsis = true
        };
        lblNativeProgress = new Label
        {
            Location = new Point(UiX(0), UiY(6)),
            Size = new Size(856, 20),
            Text = "",
            ForeColor = Color.FromArgb(86, 145, 250),
            Font = new Font("Segoe UI", 8f),
            AutoEllipsis = true
        };

        var lblNativeHint = new Label
        {
            Location = new Point(UiX(0), UiY(7)),
            Size = new Size(856, 56),
            Text = "BENCH HARNESS — the native DA session has not been validated on hardware yet. Recipes are read from mtk\\boot-profiles next to the exe and are not shipped (the DA/EMI blob licence is PMK's call). " +
                  "Do not use this on a phone you cannot afford to reflash, and never unplug between Boot and Close. " +
                  "RUN 10/11 were refused by the BROM on the COM pipe (0xfd -> 0xfe); the recorded boot came through the device's bulk endpoints, so try raw USB first and read the pipe line in the log.",
            ForeColor = Color.FromArgb(255, 170, 60),
            Font = new Font("Segoe UI", 8f)
        };

        nativeTab.Controls.AddRange(new Control[]
        {
            grpSession, grpData, lblPartition, txtNativePartition, lblProfileTag, cmbNativeProfile, cmbNativeTransport,
            lblNativeState, lblNativeProgress, lblNativeHint
        });
        SetNativeButtonsLive();
    }

    // ---- the plumbing -------------------------------------------------------------------------------

    /// <summary>One operation at a time, and every failure written out with the stage that produced it.</summary>
    private async Task NativeGuarded(string what, Func<Task> operation)
    {
        if (nativeBusy)
        {
            NativeSay("[!] Native DA: the " + what + " click was ignored because another native operation is still running — one port, one writer.");
            return;
        }
        nativeBusy = true;
        SetNativeButtonsLive();
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            NativeSay("[!] Native DA: " + what + " cancelled. The port is still open; close the session to release it.");
        }
        catch (MtkDataVerifyException ex)
        {
            NativeSay("[FAIL] Native DA " + what + ": " + ex.Message);
            NativeSay("[!] The DA accepted the bytes and read them back differently — treat this partition as damaged, not as flashed.");
        }
        catch (Exception ex)
        {
            NativeSay("[FAIL] Native DA " + what + ": " + ex.Message);
        }
        finally
        {
            // Whatever the run ended on, nothing may stay queued behind it — the last lines of a failed boot are the
            // ones the operator reads.
            FlushNativeWire();
            nativeBusy = false;
            // A Close clicked mid-operation asked for cancellation; honour it now that no frame is in flight.
            if (nativeCloseRequested) { nativeCloseRequested = false; NativeClose(); }
            SetNativeButtonsLive();
            NativeProgress(null, 0);
        }
    }

    private void SetNativeButtonsLive()
    {
        if (btnNativeBoot == null) return;
        bool booted = nativeDa != null;
        btnNativeBoot.Enabled = !booted && !nativeBusy;
        btnNativeClose.Enabled = booted;
        foreach (Button b in new[] { btnNativeGeometry, btnNativeDump, btnNativeRestore, btnNativeErase })
            b.Enabled = booted && !nativeBusy;
        if (lblNativeState != null && !booted) lblNativeState.Text = MtkNativeBench.StatusLine(false, "", 0, 0);
    }

    private async Task NativeBootAsync()
    {
        // RUN 15's node was not claimable on the first open, and a forced takeover left the BROM answering 0xfe to
        // every 0xfd. A client that polls for this phone is exactly how that happens, so it is refused by name rather
        // than discovered from the reply bytes.
        string competitor = MtkNativeBench.CompetingClient(RunningProcessNames());
        string blocker = competitor != null
            ? "The MediaTek client " + competitor + " is running and claims this phone the moment it appears. Close " +
              "it before a native boot — two clients on one BROM corrupt both sessions, and a phone that was taken " +
              "over mid-handshake answers 0xfe to every byte (RUN 15)."
            : MtkNativeBench.BootBlocker(nativeDa != null, NativeOtherClientHoldingPort());
        if (blocker != null) throw new InvalidOperationException(blocker);
        int picked = cmbNativeProfile.SelectedIndex;
        if (picked < 0 || picked >= nativeProfilePaths.Count)
            throw new InvalidOperationException("No boot recipe is selected — the recipe list is " + nativeProfilePaths.Count + " entry(ies).");
        string profilePath = nativeProfilePaths[picked];

        nativeCancel = new CancellationTokenSource();
        NativeSay("[*] Native DA: loading recipe " + Path.GetFileName(profilePath) + " (blobs verified against their recorded sha256 before anything is sent)");
        MtkBootProfile profile = MtkBootProfile.Load(profilePath);

        // The note is read per run, before the pipe is opened, because it can now change what the open itself does.
        // The timings and the greeting are the B4.17 knobs; setconfig/claimall are B4.20's, added because the
        // working client's own log prints commands and cannot show a SET_CONFIGURATION or a second claim — and those
        // are the only steps its API trace has that ours does not. arm= is B4.24's: RUN 32 put our own client under
        // the same API trace and found the reads, not the open, diverging — we abort an in-flight bulk transfer every
        // 50 ms we find nothing, the working client aborts twice in a whole boot. uiwire= is B4.25's, and it is not a
        // claim about the wire at all: RUN 33 armed the reads and was still refused, and the same trace showed the
        // phone answering before we asked while our own logging ate 26-45 ms between two bytes.
        MtkNativeBench.Knobs knobs = MtkNativeBench.ReadKnobs(ShopServices.DataDirectory);
        string knobNote = MtkNativeBench.DescribeKnobs(knobs);
        if (knobNote != null)
            NativeSay(knobNote.Trim() + " (read from " + MtkNativeBench.PacingPath(ShopServices.DataDirectory) + ")");
        var openOptions = new MtkUsbOpenOptions(knobs.SetConfig, knobs.ClaimAll, knobs.Arm);
        SetNativeUiWire(knobs.UiWire);

        bool rawUsb = cmbNativeTransport?.SelectedIndex == 0;
        IMtkTransport transport;
        IMtkDaStageHandoff handoff = null;
        IMtkBromRecovery recovery;
        if (rawUsb)
        {
            NativeSay("[*] Native DA: pipe = raw USB bulk endpoints — power the phone off, hold BOTH volume keys and plug it in");
            MtkUsbFace face = await MtkUsbTransport.WaitForFaceAsync(MtkUsbTransport.BromPids,
                TimeSpan.FromSeconds(90), NativeLog, nativeCancel.Token).ConfigureAwait(false);
            transport = await MtkUsbTransport.OpenAsync(face, NativeLog, nativeCancel.Token, openOptions).ConfigureAwait(false);
            // No stage handoff on this pipe. RUN 30 (2026-11-01 08:28) re-read the accepted boot's own trace: it opens
            // ONE libusb handle and keeps it through the whole GPT, and the phone stays 0E8D:0003 while the DA answers.
            // RUN 41 (17:36) proved the same from our side — DA1 answered 0xc0 on the very handle the BROM was proven
            // on, and the boot then died waiting 30 s for a 0e8d:2001 face the oracle never waits for. The DA stage
            // continues on this pipe; the phone re-enumerates only after a session ends (B0).
            // RUN 18's handle died on the seventh proof byte; the proof reclaims the same face instead of ending the
            // boot (B4.11). The face is pinned by PID, never by "whatever MediaTek node is there now", and the
            // recovery opens it the same way the first open did — a knob that only applied to attempt 1 would make
            // the retry a different experiment.
            recovery = new MtkUsbBromRecovery(face.Pid, NativeLog, openOptions);
        }
        else
        {
            NativeSay("[*] Native DA: pipe = COM port (serial) — power the phone off, hold BOTH volume keys and plug it in");
            MtkVcomPort port = await MtkVcom.WaitForPortAsync(TimeSpan.FromSeconds(90), NativeLog, nativeCancel.Token).ConfigureAwait(false);
            transport = MtkSerialTransport.Open(port);
            // The same re-enumeration happens on this pipe, with a COM number Windows moves too (COM3 -> COM37),
            // so the serial side needs its own handoff rather than a silent assumption that the port survives.
            handoff = new MtkSerialStageHandoff(NativeLog, TimeSpan.FromSeconds(30));
            recovery = new MtkSerialBromRecovery(NativeLog);
        }

        // From this line the session owns the pipe: BootAsync disposes it on any failed boot, and the live session
        // disposes it at Close. Nobody else may close this port or release this interface.
        // A real face answers every byte with (b + 1) until the four-pair USB handshake has been exchanged (run P,
        // 2026-10-31), so the hardware path — serial or raw USB — greets it before the recipe's 0xFD query, unless
        // the note says greet=off (the working client's own wire starts at the query itself).
        nativeDa = await MtkDaSession.BootAsync(transport, profile, NativeLog, nativeCancel.Token,
            handoff: handoff, recovery: recovery, bromHandshake: true, bromGreet: knobs.Greet,
            bromStartupMs: knobs.Startup, bromSettleMs: knobs.Settle, bromStepMs: knobs.Step)
            .ConfigureAwait(false);
        SetNativeButtonsLive();
        NativeSay("[OK] Native DA session is live on " + nativeDa.TransportDescription + " — the cable is now touched once, everything else runs over this session.");
    }

    private async Task NativeGeometryAsync()
    {
        RequireNativeSession();
        var storage = await nativeDa.QueryStorageAsync(nativeCancel.Token).ConfigureAwait(false);
        var limits = await nativeDa.QueryLimitsAsync(nativeCancel.Token).ConfigureAwait(false);
        NativeSay("[+] DA geometry: " + storage.Describe);
        NativeSay("[+] DA transfer: write chunk 0x" + limits.WriteChunkSize.ToString("x") + ", frame 0x" + limits.TransferPacketSize.ToString("x"));
        MtkDaPartitionTable table = await nativeDa.ReadPartitionTableAsync(nativeCancel.Token).ConfigureAwait(false);
        lblNativeState.Text = MtkNativeBench.StatusLine(true, nativeDa.TransportDescription, nativeDa.DeviceHwCode, table.Extents.Count);
        foreach (MtkDaExtent extent in table.Extents.Take(12))
            NativeSay("    " + extent.Describe);
        if (table.Extents.Count > 12) NativeSay("    … " + (table.Extents.Count - 12) + " more (all resolvable by name)");
    }

    private async Task NativeDumpAsync()
    {
        RequireNativeSession();
        string name = txtNativePartition.Text.Trim();
        string dest = MtkNativeBench.DumpPathFor(ShopServices.DataDirectory, name, DateTime.Now);
        NativeSay("[*] Native DA: dumping " + name + " to " + Path.GetFileName(dest));
        await nativeDa.ReadPartitionToFileAsync(name, dest, p => NativeProgress("dump " + name, p), nativeCancel.Token).ConfigureAwait(false);
        nativeLastDump = dest;
        NativeSay("[OK] Dump written: " + dest);
    }

    private async Task NativeRestoreAsync()
    {
        RequireNativeSession();
        if (string.IsNullOrWhiteSpace(nativeLastDump) || !File.Exists(nativeLastDump))
            throw new InvalidOperationException("There is no dump from this session to restore. Dump the partition first — this harness only ever writes back bytes it read itself.");
        string name = txtNativePartition.Text.Trim();
        MtkDaExtent extent = await nativeDa.RequirePartitionAsync(name, nativeCancel.Token).ConfigureAwait(false);
        if (!MtkNativeBench.ProgramAllowed(extent.Name, out string reason)) throw new IOException(reason);
        if (MessageBox.Show(this, MtkNativeBench.ConfirmText("restore", extent, nativeLastDump),
                "Native DA restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            NativeSay("[*] Native DA: restore declined by the operator — nothing was sent.");
            return;
        }
        await nativeDa.WritePartitionAsync(extent.Name, nativeLastDump, p => NativeProgress("restore " + extent.Name, p),
            verify: true, ct: nativeCancel.Token).ConfigureAwait(false);
        NativeSay("[OK] Native DA: " + extent.Name + " restored and read back identical.");
    }

    private async Task NativeEraseAsync()
    {
        RequireNativeSession();
        string name = txtNativePartition.Text.Trim();
        MtkDaExtent extent = await nativeDa.RequirePartitionAsync(name, nativeCancel.Token).ConfigureAwait(false);
        if (!MtkNativeBench.ProgramAllowed(extent.Name, out string reason)) throw new IOException(reason);
        if (MessageBox.Show(this, MtkNativeBench.ConfirmText("erase", extent, null),
                "Native DA erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            NativeSay("[*] Native DA: erase declined by the operator — nothing was sent.");
            return;
        }
        await nativeDa.ErasePartitionAsync(extent.Name, nativeCancel.Token).ConfigureAwait(false);
        NativeSay("[OK] Native DA: " + extent.Name + " erase accepted by the DA. Dump it again if you need to see what it looks like now.");
    }

    private void NativeClose()
    {
        if (nativeDa == null) return;
        if (nativeBusy)
        {
            nativeCloseRequested = true;
            nativeCancel?.Cancel();
            NativeSay("[*] Native DA: cancel requested — the session closes once the running operation reaches a frame boundary.");
            return;
        }
        try { nativeDa.Dispose(); } catch (Exception ex) { NativeSay("[!] Native DA: closing the port reported " + ex.Message); }
        nativeDa = null;
        nativeLastDump = null;
        nativeCancel?.Dispose();
        nativeCancel = null;
        SetNativeButtonsLive();
        NativeSay("[+] Native DA session closed — the COM port is released.");
    }

    /// <summary>The form is going away: the port must not stay claimed by a dead process.</summary>
    private void ReleaseNativeDaSession()
    {
        if (nativeDa == null) return;
        try { nativeCancel?.Cancel(); } catch { }
        try { nativeDa.Dispose(); } catch { }
        nativeDa = null;
    }

    private void RequireNativeSession()
    {
        if (nativeDa == null) throw new InvalidOperationException("No native DA session is open. Run Boot first.");
        nativeCancel ??= new CancellationTokenSource();
    }

    private bool NativeOtherClientHoldingPort() =>
        mediaTekCancel != null || (activeProcess != null && TryProcessAlive(activeProcess));

    /// <summary>
    /// Every process name on the machine, judged by <see cref="MtkNativeBench.CompetingClient"/>. Never throws: a
    /// safety check that fails open would be worse than no check, and this one only has to name a competitor that
    /// is already there.
    /// </summary>
    private static IEnumerable<string> RunningProcessNames()
    {
        try
        {
            return Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return null; } })
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool TryProcessAlive(Process p)
    {
        try { return !p.HasExited; } catch { return false; }
    }

    /// <summary>Session log lines arrive from the boot thread; the color follows the marker the session itself wrote.</summary>
    private void NativeLog(string line) => NativeSay(line);

    // B4.25 — where the boot thread's own time goes, measured from our own LIBUSB_DEBUG=4 trace (RUN 33, 10:40:36).
    // Every transfer there completes at the timestamp of its own submit ("poll() returned 1", revents already set), so
    // the phone is never waited for; the 26-45 ms between two wire events sits entirely between libusb_free_transfer
    // and the next libusb_alloc_transfer — inside this method. Log() marshals to the UI thread synchronously and
    // scrolls the box once per line, so a boot pays a UI round-trip per byte, and the working client's prelude runs
    // 19x faster because its host does less between bytes. uiwire=off queues the per-byte chatter and posts it in one
    // redraw; every other line flushes the queue first, so the screen still reads in order and native-da.log still
    // gets every line, immediately, whatever the knob says.
    private readonly List<KeyValuePair<string, Color>> nativeWirePending = new();
    private readonly object nativeWireGate = new();
    private bool nativeUiWireEach = true;
    private const int NativeWireBatchLines = 24;

    /// <summary>
    /// The one place a NATIVE line is shown, and the one place it is recorded. The first real boot proves things no
    /// offline test can reach — whether the SerialPort settings hold, whether the DA stage stays on the same COM
    /// face after the jump — and that proof has to outlive the window, so every line also lands in native-da.log.
    /// </summary>
    private void NativeSay(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            MtkNativeBench.AppendBenchLog(ShopServices.DataDirectory, line, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            if (nativeBenchLogBroken) return;
            nativeBenchLogBroken = true;   // say it once; a dead log must not interrupt a live phone
            Log("[!] Native DA: the bench log is not writable (" + ex.Message + "). The run continues, but its evidence has to come from this screen.", Color.Orange);
            return;
        }

        string head = line.TrimStart();
        Color color = head.StartsWith("[+]") || head.StartsWith("[OK]") ? Color.LightGreen
            : head.StartsWith("[FAIL]") ? Color.Red
            : head.StartsWith("[!]") ? Color.Orange
            : head.StartsWith("[*]") ? Color.Cyan
            : Color.Gainsboro;

        bool queue = false;
        lock (nativeWireGate)
        {
            if (!nativeUiWireEach && MtkNativeBench.IsWireChatter(head))
            {
                nativeWirePending.Add(new KeyValuePair<string, Color>(line, color));
                queue = nativeWirePending.Count < NativeWireBatchLines;
            }
        }
        if (queue) return;
        FlushNativeWire();
        Log(line, color);
    }

    /// <summary>
    /// Hand the queued wire chatter to the screen without making the boot thread wait for it. A batch that cannot be
    /// posted is not lost: the bench log already holds every line, and a dead window is not a reason to stall a phone.
    /// </summary>
    private void FlushNativeWire()
    {
        KeyValuePair<string, Color>[] batch;
        lock (nativeWireGate)
        {
            if (nativeWirePending.Count == 0) return;
            batch = nativeWirePending.ToArray();
            nativeWirePending.Clear();
        }
        try
        {
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            if (!rtbLog.InvokeRequired) { AppendNativeWire(batch); return; }
            rtbLog.BeginInvoke(new Action(() => AppendNativeWire(batch)));
        }
        catch (ObjectDisposedException) { /* the form has gone; native-da.log has every line */ }
        catch (InvalidOperationException) { /* the form is going away */ }
    }

    /// <summary>One layout pass for the whole batch instead of one per line; Log() runs on the UI thread either way.</summary>
    private void AppendNativeWire(KeyValuePair<string, Color>[] batch)
    {
        // Log() paints per line and scrolls the caret per line, which is what a 24-line batch would otherwise pay for.
        // WM_SETREDRAW off freezes the box for the batch and one Invalidate repaints it once; a control that refuses
        // the message still takes every line, just the slow way.
        bool frozen = false;
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = rtbLog.Handle;
            SendMessage(handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            frozen = true;
        }
        catch { frozen = false; }
        try
        {
            foreach (KeyValuePair<string, Color> line in batch) Log(line.Key, line.Value);
        }
        finally
        {
            if (frozen)
            {
                try
                {
                    SendMessage(handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    rtbLog.Invalidate();
                }
                catch { /* the window is going away; the bench log already holds every line */ }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_SETREDRAW = 0x000B;

    /// <summary>
    /// Choose the mode for this run — and drain what the previous one queued, so switching mid-session cannot
    /// reorder the log the operator is reading.
    /// </summary>
    private void SetNativeUiWire(bool eachLine)
    {
        FlushNativeWire();
        nativeUiWireEach = eachLine;
    }

    private void NativeProgress(string what, int percent)
    {
        if (lblNativeProgress == null || lblNativeProgress.IsDisposed) return;
        try
        {
            if (lblNativeProgress.InvokeRequired)
            {
                lblNativeProgress.BeginInvoke(new Action(() => NativeProgress(what, percent)));
                return;
            }
            lblNativeProgress.Text = what == null ? "" : MtkNativeBench.ProgressLine(what, percent);
        }
        catch (InvalidOperationException) { /* the form is going away */ }
    }
}

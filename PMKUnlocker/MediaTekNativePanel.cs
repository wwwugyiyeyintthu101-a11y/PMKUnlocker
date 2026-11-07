#nullable disable
using System.Diagnostics;
using PMKUnlocker.MediaTek;
using PMKUnlocker.MtkFirmware;

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
    /// <summary>Which pipe the session now in hand was opened on, so the close line can name it. Since B6.7 Auto can
    /// pick either pipe on its own, the dropdown is no longer a record of what a run actually used.</summary>
    private string nativePipeName;
    private TextBox txtNativePartition;
    private Label lblNativeState;
    private Label lblNativeProgress;
    private Button btnNativeBoot, btnNativeClose, btnNativeGeometry, btnNativeDump, btnNativeRestore, btnNativeErase;
    // B4 UI wiring — the same session surfaced on the INFO tab (Boot/Eject + badge) and the FLASH grid (per-partition actions)
    private Button btnInfoNativeBoot, btnInfoNativeReboot, btnInfoNativeEject;
    // B5.2 — the seccfg lock STATE, read over the live session (read-only: no measured unlock payload)
    private Button btnInfoNativeSeccfg;
    private Label lblNativeBadge;
    private DataGridViewButtonColumn colNativeRead, colNativeWrite, colNativeErase;
    private bool nativeBusy;
    // The last (booted, busy) pair the buttons and the badge were styled for, so a repeat call costs nothing.
    private string nativeButtonsState;
    private bool nativeBenchLogBroken;
    private bool nativeCloseRequested;
    private string nativeLastDump;
    // The last line the simple screen painted, so two identical milestones (two DA syncs) do not read as two events.
    private string nativeLastScreenLine;
    // Simple log: the screen shows milestones, native-da.log keeps every byte. Off = watch the wire step by step.
    private CheckBox chkNativeSimple;
    // FindProfiles walks the recipe tree, so the combo shows a friendly name and the boot uses the full path.
    private readonly List<string> nativeProfilePaths = new();
    // The first visit to the NATIVE tab runs the offline recipe audit once per app run (hash every blob).
    private bool nativeRecipesAudited;

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

        // Auto is the default: the chipset is read from the BROM itself and the matching recipe is chosen at that
        // byte, so an operator moving between phones does not have to remember which recipe belongs to which one.
        // Each row names the CPU and the HW code that actually selects it — "does this build cover MT6768" is
        // answered by looking at the list, not by booting and reading a refusal.
        IReadOnlyList<MtkRecipeHeader> recipeHeaders = MtkRecipeAudit.Headers(profiles);
        cmbNativeProfile.Items.Add("Auto (detect chipset)");
        for (int i = 0; i < profiles.Count; i++)
        {
            nativeProfilePaths.Add(profiles[i]);
            string label = i < recipeHeaders.Count && recipeHeaders[i].Readable
                ? recipeHeaders[i].Label
                : Path.GetFileNameWithoutExtension(profiles[i]);
            const string suffix = "_boot_profile";
            if (label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                label = label.Substring(0, label.Length - suffix.Length);
            cmbNativeProfile.Items.Add(label);
        }
        cmbNativeProfile.SelectedIndex = 0;
        NativeSay("[*] Native DA: " + MtkRecipeAudit.CoverageLine(recipeHeaders) + ".");
        // The follow-up question is always "then what do I add next?". The loader packs already on disk answer
        // half of it, so the tab says which CPU is one recorded boot away instead of leaving it to a failed plug-in.
        NativeSay("[*] Native DA next: " +
            MtkRecipeWorkshop.NextCpuLine(MtkRecipeWorkshop.ScanPacks(MtkRecipeWorkshop.DefaultLoadersDirectory),
                recipeHeaders) + ".");
        foreach (string problem in MtkRecipeAudit.Problems(recipeHeaders))
            NativeSay("[!] Native DA recipe: " + problem + ".");
        if (toolTipMain != null)
            Tip(cmbNativeProfile, "Auto reads the chipset from the BROM's own HW code (the first reply that is chipset-specific) and runs the recipe recorded on it — nothing chipset-specific is sent before that. Pick a name only to force one recipe.");

        // The pipe is a choice, not a constant. On this phone the same 0xfd is echoed over the device's bulk
        // endpoints and answered 0xfe over the COM face Windows shows on the same node, so the only honest bench
        // offers both and writes into the log which one a run used. Auto is the default because the phone itself
        // says which pipe it is reachable on: the bulk face being listed at all is the whole decision.
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
        cmbNativeTransport.Items.Add("Pipe: Auto (detect)");
        cmbNativeTransport.Items.Add("Pipe: raw USB");
        cmbNativeTransport.Items.Add("Pipe: COM port");
        cmbNativeTransport.SelectedIndex = 0;
        if (toolTipMain != null)
            Tip(cmbNativeTransport, "Auto looks at the bus before it sends anything: if the phone's bulk face (0e8d:0003) is listed it runs the raw USB pipe — the pipe every hardware-proven boot came through — and only when no bulk face is listed while a MediaTek COM port is does it run the serial pipe. Pick a name to force one and compare; every run logs which pipe it took either way. RUN 10 and RUN 11 were refused on the COM port.");

        // The screen is for the operator, the file is for the bench. Simple mode (the default) shows the milestones
        // and hides the per-byte wire; unchecking it is how a run is watched byte by byte. native-da.log always gets
        // every line either way, so this toggle can never cost evidence.
        chkNativeSimple = new CheckBox
        {
            Text = "Simple log",
            Location = new Point(UiX(0) + 505, UiY(0) + 8),
            Size = new Size(110, 24),
            Checked = true,
            ForeColor = Color.Gainsboro,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8.5f)
        };
        chkNativeSimple.CheckedChanged += (_, _) => nativeSimpleLog = chkNativeSimple.Checked;
        if (toolTipMain != null)
            Tip(chkNativeSimple, "Show only the milestones on screen (connected, chipset, DA ready, storage, GPT, session live). The full BROM wire still goes to native-da.log. Uncheck to watch every byte on screen.");

        btnNativeBoot = Create3DButton("🔌 Boot DA (recipe)", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnNativeBoot.Click += async (_, _) => await NativeGuarded("boot", NativeBootAsync);
        if (toolTipMain != null) Tip(btnNativeBoot, "Replay the recorded BROM→DA1→DA2 recipe on the MTK VCOM — the phone must be powered off, BOTH volume keys held, then plugged in. Nothing is sent before the HW code is proven.");

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
            Size = new Size(856, 88),
            Text = "CPUs this build can boot natively: " + MtkRecipeAudit.CoverageNames(recipeHeaders) + ". " +
                  "The session itself is chipset-generic; a recipe for a CPU that is not listed is a first run, not a supported device. " +
                  "Proven on hardware: MT6765 (boot, READ/WRITE/ERASE) and MT6877 (boot, UFS GPT). " +
                  "Recipes are read from mtk\\boot-profiles next to the exe and are not shipped (the DA/EMI blob licence is PMK's call). " +
                  "Do not use this on a phone you cannot afford to reflash, and never unplug between Boot and Close. " +
                  "If the BROM refuses the greet, try the raw USB pipe first and read the pipe line in the log.",
            ForeColor = Color.FromArgb(255, 170, 60),
            Font = new Font("Segoe UI", 8f)
        };

        nativeTab.Controls.AddRange(new Control[]
        {
            grpSession, grpData, lblPartition, txtNativePartition, lblProfileTag, cmbNativeProfile, cmbNativeTransport,
            chkNativeSimple, lblNativeState, lblNativeProgress, lblNativeHint
        });
        BuildNativeInfoGroup();
        BuildNativeFlashActions();
        SetNativeButtonsLive();

        // A recipe for a new CPU is checked before the first boot, not during it: the first visit to this tab hashes
        // every installed recipe once. That is the same gate the boot runs, so a swapped DA image or a missing blob is
        // named here — with no phone on the bench — instead of surfacing as a failed boot.
        nativeTab.Enter += (_, _) =>
        {
            if (nativeRecipesAudited) return;
            nativeRecipesAudited = true;
            string[] paths = nativeProfilePaths.ToArray();
            _ = Task.Run(() =>
            {
                var checks = MtkRecipeAudit.Verify(paths);
                Log("[*] Native DA: " + MtkRecipeAudit.VerifySummary(checks) + ".", Color.Gray);
                foreach (var check in checks)
                    Log((check.Loaded ? "[OK] " : "[!] ") + "Recipe: " + MtkRecipeAudit.VerifyLine(check) + ".",
                        check.Loaded ? Color.LightGreen : Color.Orange);

                // The tool has two MTK engines and they cover different CPUs: the standard engine picks a catalog DA
                // by chipset, the native session replays a recorded recipe. An operator with a phone on the bench
                // needs both answers at once, so the first visit writes the full per-CPU table beside the logs.
                var packs = MtkRecipeWorkshop.ScanPacks(MtkRecipeWorkshop.DefaultLoadersDirectory);
                var payloads = MtkRecipeWorkshop.ScanPayloads(MtkRecipeWorkshop.DefaultPayloadsDirectory);
                var coverage = MtkRecipeWorkshop.Coverage(packs, recipeHeaders, chip =>
                {
                    var match = MtkDaCatalog.FindByChipset(mtkDeviceDb, Path.Combine(Application.StartupPath, "mtk"), chip);
                    return match == null ? "" : match.Brand + "\\" + match.Model + " (" + match.Basis + ")";
                }, payloads);
                Log("[*] CPU coverage: " + MtkRecipeWorkshop.CoverageSummary(coverage) + ".", Color.Gray);
                try
                {
                    string folder = Path.Combine(ShopServices.DataDirectory, "mtk");
                    Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, "cpu-coverage.md");
                    File.WriteAllText(file, MtkRecipeWorkshop.CoverageMarkdown(coverage,
                        DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")));
                    Log("[*] CPU coverage table: " + file, Color.Gray);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log("[!] CPU coverage table not written: " + ex.Message, Color.Orange);
                }
            });
        };
    }

    // ================= B4 — UI WIRING: the session on the INFO and FLASH tabs =================
    //
    // The NATIVE tab is the bench; these are the product controls. The rule is the same fail-closed-by-absence as
    // the tab itself: no recipe installed means no Boot Device button and no grid actions, nothing to guess. And the
    // rule the ROADMAP names: every write path refuses while the session is not confirmed live — a grid click with
    // no session is a logged refusal, never a boot, and the mtkclient workflows refuse the reverse way, because a
    // live session owns the only BROM face and two clients on one BROM corrupt both (RUN 15).

    /// <summary>Boot Device / Reboot Device / Eject Device plus the live-session badge, on the MTK INFO tab under SERVICE.</summary>
    private void BuildNativeInfoGroup()
    {
        btnInfoNativeBoot = Create3DButton("🔌 Boot Device", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnInfoNativeBoot.Click += async (_, _) => await NativeGuarded("boot", NativeBootAsync);
        if (toolTipMain != null)
            Tip(btnInfoNativeBoot, "Boot the DA once through the recorded recipe (power off, hold BOTH volume keys, plug). After this, Read/Write/Erase on the FLASH grid run over the live session with no replug.");

        btnInfoNativeReboot = Create3DButton("🔁 Reboot Device", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Green);
        btnInfoNativeReboot.Click += async (_, _) => await NativeGuarded("reboot", NativeRebootAsync);
        if (toolTipMain != null)
            Tip(btnInfoNativeReboot, "Send the measured TOPRGU watchdog reset through the live session (RUN 48's recipe - no bare stock reset id exists, this rides the DA extension the boot already loaded). The session ends and the phone leaves DA mode; re-proven on 2026-11-03: the phone came back by itself (Mi logo, then the charging screen), the same shape mtkclient's reset produces. Unplug the cable after the reset line.");

        btnInfoNativeEject = Create3DButton("⏏ Eject Device", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnInfoNativeEject.Click += (_, _) => NativeClose();
        if (toolTipMain != null)
            Tip(btnInfoNativeEject, "Release the live session and the pipe it holds. The mtkclient operations need this ejected — they cannot share the phone with a live session.");

        TabPage infoTab = mtkModes.TabPages[0];
        Panel grpNativeInfo = CreateGroupPanel("BOOT DEVICE (NATIVE)", UiX(0), UiY(2), 3, btnInfoNativeBoot, btnInfoNativeReboot, btnInfoNativeEject);

        // B5.2 — the seccfg lock STATE, read over the live session. Its own group on the next row: the LIVE badge
        // already owns the right-hand columns of the BOOT DEVICE row, and a fourth button there would sit under it.
        btnInfoNativeSeccfg = Create3DButton("🔐 Seccfg State", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnInfoNativeSeccfg.Click += async (_, _) => await NativeGuarded("seccfg state", NativeSeccfgStateAsync);
        if (toolTipMain != null)
            Tip(btnInfoNativeSeccfg, "READ the seccfg config block head over the live session (the extent the device's own GPT names seccfg) and print the bootloader lock state: 0x00 = LOCKED, 0x03 = UNLOCKED, anything else shown raw. Read-only by construction — no WRITE or ERASE frame is sent, and the unlock payload has never been measured, so this tool does not lock or unlock through a DA session.");
        Panel grpSeccfgInfo = CreateGroupPanel("SECCFG (NATIVE)", UiX(0), UiY(4), 1, btnInfoNativeSeccfg);
        // The badge used to start at UiX(0)+332, inside the BOOT DEVICE frame's own width (3 columns reach x=495),
        // and the frame paints in front of it — so the state line appeared to begin mid-sentence. It now sits in
        // the clear space to the right of the SECCFG frame (that group is one column wide), on the same band, and
        // wraps rather than ellipsising because the LIVE line carries the transport description, which is long.
        lblNativeBadge = new Label
        {
            Location = new Point(UiX(0) + 185, UiY(4) + 14),
            Size = new Size(650, 44),
            Text = "Session: not booted",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoEllipsis = false
        };
        if (toolTipMain != null)
            Tip(lblNativeBadge, "Live native DA session state. While it reads LIVE, the mtkclient operations (FLASH, Unlock, Backup) are refused — Eject Device first; two clients on one BROM corrupt both sessions.");
        infoTab.Controls.Add(grpNativeInfo);
        infoTab.Controls.Add(grpSeccfgInfo);
        infoTab.Controls.Add(lblNativeBadge);
    }

    /// <summary>Per-partition Read / Write / Erase columns on the FLASH grid, wired to the live session.</summary>
    private void BuildNativeFlashActions()
    {
        colNativeRead = new DataGridViewButtonColumn { Name = "nativeRead", HeaderText = "Read", Text = "Read", UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat, Width = 46, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
        colNativeWrite = new DataGridViewButtonColumn { Name = "nativeWrite", HeaderText = "Write", Text = "Write", UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat, Width = 50, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
        colNativeErase = new DataGridViewButtonColumn { Name = "nativeErase", HeaderText = "Erase", Text = "Erase", UseColumnTextForButtonValue = true, FlatStyle = FlatStyle.Flat, Width = 48, AutoSizeMode = DataGridViewAutoSizeColumnMode.None };
        foreach (DataGridViewButtonColumn col in new[] { colNativeRead, colNativeWrite, colNativeErase })
        {
            col.DefaultCellStyle.BackColor = Color.FromArgb(28, 31, 38);
            col.DefaultCellStyle.SelectionBackColor = Color.FromArgb(28, 31, 38);
            mediaTekGrid.Columns.Add(col);
        }
        mediaTekGrid.CellContentClick += NativeGridCellClick;
        SetNativeGridActionLook();
    }

    /// <summary>One click on a grid action. Read sets the NATIVE tab's partition box so both surfaces act on the same name.</summary>
    private async void NativeGridCellClick(object sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= mediaTekGrid.Rows.Count) return;
        string column = mediaTekGrid.Columns[e.ColumnIndex].Name;
        if (column is not ("nativeRead" or "nativeWrite" or "nativeErase")) return;
        if (mediaTekGrid.Rows[e.RowIndex].Cells["partition"].Value is not string name || string.IsNullOrWhiteSpace(name)) return;

        // The action cells stay clickable while the session is down: a DataGridViewButtonColumn has no per-cell
        // Enabled, so the dimmed text is all the grid can show. Running such a click through the guarded operation
        // meant a thrown exception, a bench-log write and two restyles of every button, the badge and all three
        // action columns — several full invalidations of a 43-row grid, which is what made a partition row feel
        // heavy to press. The refusal is the same answer for the cost of one log line.
        if (nativeDa == null)
        {
            string verb = column == "nativeRead" ? "Read" : column == "nativeWrite" ? "Write" : "Erase";
            NativeSay("[!] Native DA: " + verb + " " + name + " needs a live session — press Boot Device first. Nothing was sent.");
            return;
        }

        try
        {
            if (column == "nativeRead")
            {
                txtNativePartition.Text = name;
                await NativeGuarded("dump " + name, NativeDumpAsync);
            }
            else if (column == "nativeWrite")
                await NativeGuarded("write " + name, () => NativeGridWriteAsync(name, mediaTekGrid.Rows[e.RowIndex]));
            else
                await NativeGuarded("erase " + name, () => NativeGridEraseAsync(name));
        }
        catch (Exception ex) { Log("[FAIL] Native DA " + name + ": " + ex.Message, Color.Red); }
    }

    /// <summary>
    /// Grid Write over the live session. The image must be one the tool can vouch for: a scatter image that exists
    /// on disk, or the dump this session itself read from the same partition. Sparse images are refused outright —
    /// the native session writes raw bytes, and a sparse container written raw is corruption.
    /// </summary>
    private async Task NativeGridWriteAsync(string name, DataGridViewRow row)
    {
        RequireNativeSession();
        string image = null;
        if (row?.Tag is ScatterEntry entry && entry.ImageExists && !string.IsNullOrEmpty(entry.ResolvedPath))
            image = entry.ResolvedPath;
        else if (MtkNativeBench.DumpMatchesPartition(nativeLastDump, name) && File.Exists(nativeLastDump))
            image = nativeLastDump;
        if (image == null)
            throw new InvalidOperationException("No image to write to " + name + " — load a scatter whose image file exists, or Read (dump) the partition in this session first. Nothing was sent to the device.");
        if (ReviewSafety.IsAndroidSparse(image))
            throw new InvalidDataException(image + " is a sparse image and the native session writes raw bytes only — flash it through FLASH SELECTED instead. Nothing was sent to the device.");
        MtkDaExtent extent = await NativeConfirmProgramAsync(name, "write", image);
        if (extent == null) return;
        await nativeDa.WritePartitionAsync(extent.Name, image, p => NativeProgress("write " + extent.Name, p),
            verify: true, ct: nativeCancel.Token).ConfigureAwait(false);
        NativeSay("[OK] Native DA: " + extent.Name + " written from " + Path.GetFileName(image) + " and read back identical.");
    }

    private async Task NativeFlashAsync(MtkFlashStep step)
    {
        RequireNativeSession();
        MtkDaPartitionTable table = await nativeDa.ReadPartitionTableAsync(nativeCancel.Token);
        IReadOnlyList<MtkNativeBench.NativeFlashItem> plan =
            MtkNativeBench.NativeFlashPlan(table, step.Files, out string refusal);
        if (plan == null) throw new InvalidOperationException(refusal);

        if (MessageBox.Show(this, MtkNativeBench.NativeFlashConfirmText(plan), "Native DA flash",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            NativeSay("[*] Native DA: flash declined by the operator — nothing was sent.");
            return;
        }

        using MtkNativeBench.NativeFlashPreparation prepared = await Task.Run(() =>
            MtkNativeBench.NativeFlashPreparation.Create(plan, Path.GetTempPath(), nativeCancel.Token,
                NativeSay, (name, percent) => NativeProgress("prepare " + name, percent)), nativeCancel.Token).ConfigureAwait(false);
        NativeSay("[*] Native DA: all images prepared and held read-only until verify finishes.");
        NativeSay("[*] Native DA: " + plan.Count + " partition(s) to write over the session already open — no GPT " +
                  "rebuild, no replug between partitions.");
        foreach (MtkNativeBench.NativeFlashItem item in prepared.Plan)
        {
            nativeCancel.Token.ThrowIfCancellationRequested();
            NativeSay("[*] Native DA: writing " + item.Extent.Name + " from " + Path.GetFileName(item.ImagePath) +
                      " (" + MtkDaExtent.SizeText((ulong)item.ImageLength) + ")...");
            await nativeDa.WritePartitionAsync(item.Extent.Name, item.ImagePath,
                p => NativeProgress("flash " + item.Extent.Name, p), verify: true,
                ct: nativeCancel.Token).ConfigureAwait(false);
            NativeSay("[OK] Native DA: " + item.Extent.Name + " written and read back identical.");
        }
        nativeCancel.Token.ThrowIfCancellationRequested();
        NativeSay("[OK] Native DA: " + plan.Count + " partition(s) flashed in one session. The phone is rebooting — " +
                  "this session closes with it.");
        await NativeRebootAsync();
    }

    /// <summary>ERASE one named partition over the live session — the NATIVE tab textbox and the FLASH grid share it.</summary>
    private async Task NativeGridEraseAsync(string name)
    {
        RequireNativeSession();
        MtkDaExtent extent = await NativeConfirmProgramAsync(name, "erase", null);
        if (extent == null) return;
        await nativeDa.ErasePartitionAsync(extent.Name, nativeCancel.Token).ConfigureAwait(false);
        NativeSay("[OK] Native DA: " + extent.Name + " erase accepted by the DA. Dump it again if you need to see what it looks like now.");
    }

    /// <summary>
    /// The chain every destructive native op shares: resolve the extent against the live GPT, refuse the
    /// never-program list, then ask with the exact byte extent. Returns null when the operator declines.
    /// </summary>
    private async Task<MtkDaExtent> NativeConfirmProgramAsync(string name, string verb, string imagePath)
    {
        MtkDaExtent extent = await nativeDa.RequirePartitionAsync(name, nativeCancel.Token);
        if (!MtkNativeBench.ProgramAllowed(extent.Name, out string reason)) throw new IOException(reason);
        if (MessageBox.Show(this, MtkNativeBench.ConfirmText(verb, extent, imagePath),
                "Native DA " + verb, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            NativeSay("[*] Native DA: " + verb + " declined by the operator — nothing was sent.");
            return null;
        }
        return extent;
    }

    /// <summary>The badge and the grid action columns follow the session state, on every transition the bench already had.</summary>
    private void SetNativeBadge(bool booted)
    {
        if (lblNativeBadge == null || lblNativeBadge.IsDisposed) return;
        if (booted)
        {
            lblNativeBadge.Text = "Session: LIVE — " + nativeDa.TransportDescription + ", HW 0x" + nativeDa.DeviceHwCode.ToString("X4") +
                " — FLASH grid Read/Write/Erase run over this session";
            lblNativeBadge.ForeColor = Color.LightGreen;
        }
        else if (nativeBusy)
        {
            lblNativeBadge.Text = "Session: booting — the phone answers nothing else until the recipe finishes";
            lblNativeBadge.ForeColor = Color.Cyan;
        }
        else
        {
            lblNativeBadge.Text = "Session: not booted — Boot Device replays the recorded recipe once, then the FLASH grid works without replug";
            lblNativeBadge.ForeColor = Color.Gray;
        }
    }

    /// <summary>The grid action cells read as the state they are in: colored while live, dimmed while the session is down.</summary>
    private void SetNativeGridActionLook()
    {
        if (colNativeRead == null) return;
        bool booted = nativeDa != null;
        Color read = booted ? Color.FromArgb(140, 230, 140) : Color.DimGray;
        Color write = booted ? Color.FromArgb(255, 180, 80) : Color.DimGray;
        Color erase = booted ? Color.FromArgb(240, 100, 100) : Color.DimGray;
        colNativeRead.DefaultCellStyle.ForeColor = read;
        colNativeWrite.DefaultCellStyle.ForeColor = write;
        colNativeErase.DefaultCellStyle.ForeColor = erase;
        colNativeRead.DefaultCellStyle.SelectionForeColor = read;
        colNativeWrite.DefaultCellStyle.SelectionForeColor = write;
        colNativeErase.DefaultCellStyle.SelectionForeColor = erase;
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
        // Every line below is a function of these two flags, and callers run on each transition — including ones
        // that change nothing. Restyling ten buttons, the badge and three grid columns is several full invalidations
        // of the FLASH grid, so a repeat of the same state is dropped here instead of painted again.
        string state = (booted ? "booted" : "down") + (nativeBusy ? "-busy" : "-idle");
        if (nativeButtonsState == state) return;
        nativeButtonsState = state;
        btnNativeBoot.Enabled = !booted && !nativeBusy;
        btnNativeClose.Enabled = booted;
        foreach (Button b in new[] { btnNativeGeometry, btnNativeDump, btnNativeRestore, btnNativeErase })
            b.Enabled = booted && !nativeBusy;
        if (lblNativeState != null && !booted) lblNativeState.Text = MtkNativeBench.StatusLine(false, "", 0, 0);
        if (btnInfoNativeBoot != null)
        {
            btnInfoNativeBoot.Enabled = !booted && !nativeBusy;
            btnInfoNativeReboot.Enabled = booted && !nativeBusy;
            btnInfoNativeEject.Enabled = booted && !nativeBusy;
            btnInfoNativeSeccfg.Enabled = booted && !nativeBusy;
        }
        SetNativeBadge(booted);
        SetNativeGridActionLook();
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
        var choice = MtkNativeBench.ProfileChoice(cmbNativeProfile.SelectedIndex, nativeProfilePaths.Count);
        if (!choice.Auto && choice.ProfileIndex < 0)
            throw new InvalidOperationException("No boot recipe is selected — the recipe list is " +
                nativeProfilePaths.Count + " entry(ies) and combo row " + cmbNativeProfile.SelectedIndex + " has none behind it.");

        nativeCancel = new CancellationTokenSource();
        MtkBootProfile profile;
        IReadOnlyList<MtkBootProfile> autoProfiles = null;
        if (choice.Auto)
        {
            // Auto means the operator does not know (or should not have to know) which phone is plugged in. The
            // chipset is only knowable from the BROM's own HW-code reply, so every installed recipe is loaded and
            // the run starts on the first one; the session switches at that reply and refuses if anything
            // chipset-specific went out before it. A recipe that does not load is reported and skipped — one
            // incomplete pack must not make the other phone unbootable.
            var installed = new List<MtkBootProfile>();
            string firstFileLoaded = null;
            foreach (string path in nativeProfilePaths)
            {
                try
                {
                    MtkBootProfile loaded = MtkBootProfile.Load(path);
                    installed.Add(loaded);
                    firstFileLoaded ??= Path.GetFileName(path);
                }
                catch (Exception ex)
                {
                    NativeSay("[!] Native DA: " + Path.GetFileName(path) + " is installed but did not load (" + ex.Message + ") — auto will not use it.");
                }
            }
            if (installed.Count == 0)
                throw new InvalidOperationException("Auto has no usable recipe — " + nativeProfilePaths.Count +
                    " recipe file(s) are installed and none of them passed its blob check (see the lines above).");
            profile = installed[0];
            autoProfiles = installed;
            NativeSay(MtkNativeBench.AutoBootLine(installed.Count));
            NativeSay("[*] Native DA: starting on recipe " + firstFileLoaded +
                " (every installed recipe was loaded and its blobs verified against the recorded sha256 before anything was sent)");
        }
        else
        {
            string profilePath = nativeProfilePaths[choice.ProfileIndex];
            NativeSay("[*] Native DA: loading recipe " + Path.GetFileName(profilePath) + " (blobs verified against their recorded sha256 before anything is sent)");
            profile = MtkBootProfile.Load(profilePath);
        }

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

        // Auto asks the bus which face the phone is really showing before anything is sent: the bulk pair this tool
        // has every hardware-proven boot on, or — only when no bulk face is listed at all — the serial face Windows
        // has a driver for. The two looks are read here rather than left to the pipe's own 90 s wait, because that
        // wait is the refusal for a phone that is not in BROM yet and it must stay exactly as worded on each pipe.
        var pipe = MtkNativeBench.PipeChoice(cmbNativeTransport?.SelectedIndex ?? 0);
        bool bulkListed = false;
        bool comListed = false;
        if (pipe.Auto)
        {
            bulkListed = MtkNativeBench.BulkBromFaceListed(MtkUsbTransport.EnumerateFaces().Select(f => f.Pid),
                MtkUsbTransport.BromPids);
            try
            {
                comListed = MtkNativeBench.ComBromPortListed(MtkVcom.Enumerate().Select(p => p.Pid),
                    MtkUsbTransport.BromPids);
            }
            catch (Exception ex)
            {
                // A Windows device list that cannot be read is a missing look, not a refusal to boot: the bulk face
                // was already read through libusb, and the raw pipe is the one every recorded boot came through.
                NativeSay("[!] Native DA: Windows could not list the COM faces (" + ex.Message +
                    ") — auto treated the serial side as not listed.");
            }
        }
        bool rawUsb = pipe.Auto ? MtkNativeBench.PickRawPipe(bulkListed, comListed) : pipe.RawUsb;
        if (pipe.Auto)
            NativeSay(MtkNativeBench.AutoPipeLine(rawUsb, bulkListed, comListed));
        IMtkTransport transport;
        IMtkDaStageHandoff handoff = null;
        IMtkBromRecovery recovery;
        if (rawUsb)
        {
            nativePipeName = "raw USB bulk pipe";
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
            nativePipeName = "COM port";
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
            bromStartupMs: knobs.Startup, bromSettleMs: knobs.Settle, bromStepMs: knobs.Step,
            autoProfiles: autoProfiles)
            .ConfigureAwait(false);
        // NativeBootAsync reaches this on a pool thread (ConfigureAwait(false) all the way through the wire), and a
        // state line plus a grid refresh are window state — so they are applied on the window's thread.
        NativeUi(() =>
        {
            // A boot only happens after the cable was touched, so whatever table is in memory came from the phone
            // that was HERE before this one. It is dropped before anything new is read: a scatter flow must never
            // act on extents that were not read from the device in front of the operator.
            if (mediaTekLiveTable != null)
            {
                SetLiveTable(null, null);
                NativeSay("[*] Native DA: the partition table in memory is from the previous device — dropped; the live read replaces it.");
            }
            SetNativeButtonsLive();
        });
        NativeSay("[OK] Native DA session is live on " + nativeDa.TransportDescription + " — the cable is now touched once, everything else runs over this session.");
        // Honest provenance for a chipset this tool has never booted: the DA command layer is generic and the run
        // will behave the same, but the BROM prelude and its blobs are new here — say so instead of letting the
        // operator read a green line as "supported device".
        if (!MtkNativeBench.BootProvenOnHardware(nativeDa.DeviceHwCode))
            NativeSay("[!] Native DA: HW 0x" + nativeDa.DeviceHwCode.ToString("X4") + " has not been booted to a live DA by this " +
                "tool before — the recipe is replayed exactly as recorded, and this run is its first test on hardware.");

        // B5.4: the table is read as part of the boot, because the FLASH grid, the scatter flows and the device
        // status line all wait on it and the operator should not have to press a second button to find out what is
        // plugged in. A table read that fails does not take the session down with it — the DA is live, and
        // Geometry + GPT retries the read.
        try
        {
            await NativeReadDeviceTableAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            NativeSay("[!] Native DA: the session is live, but the partition table did not read (" + ex.Message +
                ") — press Geometry + GPT to retry it; the session itself is intact.");
        }
    }

    /// <summary>
    /// The three queries that have to answer before any partition can be touched — storage geometry, transfer
    /// limits, then the device's own GPT — and everything a live table feeds: the state line, the FLASH grid, the
    /// scatter flows' table, and the INFO tab's device status. Both the boot and the Geometry + GPT button run it,
    /// and the answers are cached on the session, so a second call costs nothing but the GPT read.
    /// </summary>
    private async Task NativeReadDeviceTableAsync()
    {
        RequireNativeSession();
        MtkStorageInfo storage = await nativeDa.QueryStorageAsync(nativeCancel.Token).ConfigureAwait(false);
        MtkLimits limits = await nativeDa.QueryLimitsAsync(nativeCancel.Token).ConfigureAwait(false);
        // The screen gets the two facts an operator acts on; the indented line keeps the device's raw words in
        // native-da.log. Simple mode hides the detail line, so a UFS phone reads "Storage: UFS, 4096 B blocks, 238 GiB"
        // instead of a 130-character sentence.
        NativeSay("[+] Storage: " + storage.StorageName + ", " + storage.BlockSize + " B blocks, " + MtkDaExtent.SizeText(storage.UserAreaSize));
        NativeSay("    DA geometry: " + storage.Describe);
        NativeSay("[+] Transfer: " + MtkDaExtent.SizeText(limits.WriteChunkSize) + " chunk / " + MtkDaExtent.SizeText(limits.TransferPacketSize) + " frame");
        NativeSay("    DA limits: write chunk 0x" + limits.WriteChunkSize.ToString("x") + ", frame 0x" + limits.TransferPacketSize.ToString("x"));
        MtkDaPartitionTable table = await nativeDa.ReadPartitionTableAsync(nativeCancel.Token).ConfigureAwait(false);

        // The cache is written while a loaded scatter is still known — the grid fill below clears the package, and
        // the cache key is that scatter's own hash, so handing over first is what makes the table survive a restart.
        HandNativeTableToScatterFlows(storage, table);

        NativeUi(() =>
        {
            lblNativeState.Text = MtkNativeBench.StatusLine(true, nativeDa.TransportDescription, nativeDa.DeviceHwCode, table.Extents.Count);
            SetNativeDeviceStatus(storage);
            FillFlashGridFromNativeTable(table);
            // PMK's word (2026-11-04): "Live GPT: 43 partition(s) ရောက်တာနဲ့ Partition ဇယားကွက်ပေါ်လာစေချင်တယ်".
            // The rows were already there — on the FLASH page, while a boot is started from NATIVE, so the operator
            // watched a line count a table he could not see. The session survives the switch and the NATIVE tab is
            // one click away again.
            ShowMediaTekFlashPage();
        });
        foreach (MtkDaExtent extent in table.Extents.Take(12))
            NativeSay("    " + extent.Describe);
        if (table.Extents.Count > 12) NativeSay("    … " + (table.Extents.Count - 12) + " more (all resolvable by name)");
    }

    /// <summary>Geometry and GPT as a button: the same read the boot already ran, on demand.</summary>
    private async Task NativeGeometryAsync() => await NativeReadDeviceTableAsync();

    /// <summary>
    /// The INFO tab's device status line, in the same slot mtkclient's Read Info writes it, from what the session
    /// measured: the recipe-proven chipset and BROM HW code, the eMMC part number out of the storage descriptor
    /// and the user-area size the DA advertises. A label, not a log line — the descriptor's serial bytes are not
    /// decoded anywhere, so nothing device-bound can reach native-da.log through here.
    /// </summary>
    private void SetNativeDeviceStatus(MtkStorageInfo storage)
    {
        if (lblMtkDeviceStatus == null || lblMtkDeviceStatus.IsDisposed || nativeDa == null) return;
        lblMtkDeviceStatus.Text = MtkNativeBench.DeviceStatusLine(nativeDa.RecipeChipset, nativeDa.DeviceHwCode,
            storage.EmmcPartNumber, FormatBytesLong((long)storage.UserAreaSize));
        lblMtkDeviceStatus.ForeColor = Color.LightGreen;
    }

    /// <summary>
    /// The live GPT is the device's partition table whether it came off our own DA or off mtkclient's printgpt, so
    /// the session hands it to the same two places that path fills: the in-memory table every scatter flow asks for
    /// first (<see cref="GetMediaTekTableAsync"/>) and the disk cache keyed by the loaded scatter. That is what saves
    /// the extra plug cycle — after one native boot the FLASH and scatter operations already know the extents.
    /// The binding says which device the table came from, so a second phone cannot inherit the first one's extents.
    /// </summary>
    private void HandNativeTableToScatterFlows(MtkStorageInfo storage, MtkDaPartitionTable table)
    {
        var parts = new List<MtkPartition>(table.Extents.Count);
        foreach (MtkDaExtent extent in table.Extents)
            parts.Add(new MtkPartition(extent.Name, (long)extent.ByteOffset, (long)extent.ByteLength));
        string binding = MtkNativeBench.GptBinding(nativeDa.DeviceHwCode, storage.BlockSize, storage.UserAreaSize,
            table.Extents.Count);
        if (mediaTekTableBinding != null && !string.Equals(mediaTekTableBinding, binding, StringComparison.Ordinal))
            NativeSay("[*] Native DA: the table from the previous run (" + mediaTekTableBinding + ") is replaced — " +
                "this phone reports " + binding + ".");
        SetLiveTable(parts, binding);
        SaveMediaTekGptCache(parts, binding);
        NativeSay("[+] Native DA: " + parts.Count + " extent(s) are the live table now (bound to " + binding + ") — " +
            "scatter flows reuse them without another plug cycle.");
    }

    /// <summary>
    /// The FLASH grid's per-partition columns need rows to act on, and while a native session is live the mtkclient
    /// printgpt fill is refused — so the live table fills it itself, in the same Live-GPT shape that path uses.
    /// </summary>
    private void FillFlashGridFromNativeTable(MtkDaPartitionTable table)
    {
        mediaTekPackage = null;
        mediaTekScatter.Clear();
        BeginMediaTekGridFill();
        try
        {
            mediaTekGrid.Rows.Clear();
            foreach (MtkDaExtent extent in table.Extents)
                mediaTekGrid.Rows.Add(false, extent.Name, "", "0x" + extent.ByteOffset.ToString("x"),
                    FormatBytesLong((long)extent.ByteLength), "Live GPT");
        }
        finally { EndMediaTekGridFill(); }
    }

    /// <summary>Brings the FLASH page forward, found by its own caption so a later tab inserted above it cannot
    /// silently turn this into "show whichever page happens to sit at index 3".</summary>
    private void ShowMediaTekFlashPage()
    {
        for (int i = 0; i < mtkModes.TabPages.Count; i++)
        {
            if (string.Equals(mtkModes.TabPages[i].Text, "FLASH", StringComparison.Ordinal))
            {
                mtkModes.SelectedIndex = i;
                return;
            }
        }
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
        MtkDaExtent extent = await NativeConfirmProgramAsync(txtNativePartition.Text.Trim(), "restore", nativeLastDump);
        if (extent == null) return;
        await nativeDa.WritePartitionAsync(extent.Name, nativeLastDump, p => NativeProgress("restore " + extent.Name, p),
            verify: true, ct: nativeCancel.Token).ConfigureAwait(false);
        NativeSay("[OK] Native DA: " + extent.Name + " restored and read back identical.");
    }

    private async Task NativeEraseAsync()
    {
        RequireNativeSession();
        await NativeGridEraseAsync(txtNativePartition.Text.Trim());
    }

    /// <summary>
    /// The measured DA-extension reboot. The session is spent the moment the stub is out — the phone is on
    /// its way down — so the close is requested for <see cref="NativeGuarded"/>'s finally rather than made
    /// mid-operation, where it would read as the cancellation of a live run.
    /// </summary>
    private async Task NativeRebootAsync()
    {
        RequireNativeSession();
        await nativeDa.RebootAsync(nativeCancel.Token).ConfigureAwait(false);
        nativeCloseRequested = true;
        NativeSay("[*] Native DA: once Android is up, press Verify Boot (ADB) on the INFO tab — it waits for " +
                  "sys.boot_completed=1 and reports what it can prove (the DA readback already proved the bytes).");
    }

    /// <summary>
    /// The read-only seccfg state query. The session logs the decoded block itself; what is added here is the
    /// operator's verdict in the tool's own words, and the honest limit under it: this reads a state, it does
    /// not change one. Locking and unlocking stay unimplemented because no unlock payload has ever been
    /// captured — the RUN 48 unit answered "Device is already unlocked" and sent nothing to write.
    /// </summary>
    private async Task NativeSeccfgStateAsync()
    {
        RequireNativeSession();
        MtkSeccfgConfig config = await nativeDa.ReadSeccfgConfigAsync(nativeCancel.Token).ConfigureAwait(false);
        if (config.IsUnlocked)
            NativeSay("[OK] Native DA: bootloader lock state = UNLOCKED (seccfg v" + config.Version + ", state word 0x" +
                config.LockState.ToString("x8") + ").");
        else
            NativeSay("[!] Native DA: bootloader lock state = " + config.LockStateName + " (seccfg v" + config.Version +
                ", state word 0x" + config.LockState.ToString("x8") + ")." +
                (config.LockState == MtkSeccfg.StateLocked ? "" : " — an unexpected value, so it is shown raw rather than named."));
        if (!config.MarkerExpected)
            NativeSay("[!] Native DA: the block's second marker is '" + config.Marker + "', not the measured 'EEEE' — the state is printed as decoded, but this is not the config layout this tool was built against.");
        NativeSay("[*] Native DA: that is a read of the state only. This tool does not lock or unlock seccfg over a DA session — the unlock payload has never been measured, and a guessed one silently changes a lock policy instead of failing.");
    }

    /// <summary>
    /// P1.1 — Save GPT over the session that is already open. The primary header and entry array are bytes the boot's
    /// table read already cost, so the whole save is one extra READ: the backup array and its header, at the LBA the
    /// primary header itself names. The batched client needs a second boot for the same folder.
    /// Read-only by construction — <c>MtkDaSession.NeverProgramNames</c> keeps pgpt/sgpt/gpt off every program path.
    /// </summary>
    private async Task NativeSaveGptAsync()
    {
        RequireNativeSession();
        string destination;
        using (var pick = new FolderBrowserDialog { Description = "Save GPT backup" })
        {
            if (pick.ShowDialog(this) != DialogResult.OK)
            {
                NativeSay("[*] Native DA: GPT save declined — no folder was chosen, so nothing was read.");
                return;
            }
            destination = Path.Combine(pick.SelectedPath, "GPT-" + Guid.NewGuid().ToString("N"));
        }
        await nativeDa.SaveGptAsync(destination, nativeCancel.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// P1.2 — the partition backup over the live session: one boot, then one READ per partition. What the operator
    /// gets is the same folder the batched path produces — one <c>&lt;name&gt;.bin</c> per partition plus
    /// <c>backup-manifest.json</c> with the sizes and SHA-256 — and the manifest is written by the same
    /// <see cref="ShopServices"/> call, so a native backup restores through the existing restore path unchanged.
    /// Every extent comes from the device's own GPT, and the sizes the manifest records are the same extents the
    /// reads were addressed with.
    /// <para>
    /// <paramref name="destRoot"/> is the NV caller's remembered root: an NV backup always lands in
    /// <c>&lt;root&gt;\&lt;timestamp&gt;</c>, the shape <c>NV Restore</c> opens on, whether it was read natively or
    /// through mtkclient. Null means ask for a folder and name it <c>MTK-&lt;guid&gt;</c> — the ad-hoc backup shape.
    /// </para>
    /// </summary>
    private async Task NativeBackupAsync(bool nvOnly, string destRoot = null)
    {
        RequireNativeSession();
        // No ConfigureAwait here on purpose: the folder dialog below has to run on the UI thread, and this is the
        // last await before it.
        MtkDaPartitionTable table = await nativeDa.ReadPartitionTableAsync(nativeCancel.Token);

        Dictionary<string, long> layout = MtkNativeBench.BackupLayout(table,
            nvOnly ? NvPartitions : SelectedMediaTekNames(),
            nvOnly ? "This device's GPT names none of the six NV partitions (" + table.Extents.Count +
                " entries read). Nothing was read."
                   : "Select partitions in Flash first.",
            out string refusal);
        if (layout == null) throw new InvalidOperationException(refusal);

        string dir;
        if (destRoot != null)
        {
            dir = Path.Combine(destRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);
        }
        else using (var pick = new FolderBrowserDialog { Description = "Select backup destination" })
        {
            if (pick.ShowDialog(this) != DialogResult.OK)
            {
                NativeSay("[*] Native DA: backup declined — no folder was chosen, nothing was read.");
                return;
            }
            dir = Path.Combine(pick.SelectedPath, "MTK-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        NativeSay("[*] Native DA: " + layout.Count + " partition(s) to read over the session already open — one boot, " +
                  "no replug between partitions.");
        foreach ((string name, long size) in layout)
        {
            NativeSay("[*] Native DA: reading " + name + " (" + MtkDaExtent.SizeText((ulong)size) + ")...");
            await nativeDa.ReadPartitionToFileAsync(name, Path.Combine(dir, name + ".bin"),
                p => NativeProgress("backup " + name, p), nativeCancel.Token).ConfigureAwait(false);
        }
        ShopServices.WriteBackup(dir, "MTK", layout, layout.Keys, Application.ProductVersion);
        RegisterBackup(dir);
        NativeSay("[OK] Native DA: " + layout.Count + " partition(s) backed up to " + dir +
                  " — sizes and SHA-256 recorded in the manifest, and the phone was never replugged for it.");
        // The NV set keeps its own folder shape (six images and a manifest is not a firmware package); everything
        // else this button reads is one, and the scatter is what makes it flashable.
        if (!nvOnly) TryWriteFirmwareScatter(dir, table, layout.Keys);
    }

    /// <summary>
    /// P1.4 — the folder a firmware dump lands in gets a scatter beside it, so the bytes that cost one boot can go
    /// back as a package. It describes what was written next to it at the offsets this device's own GPT reported,
    /// and every other live extent as a reserved row: the flasher rebuilds the GPT from the rows it parses, so a
    /// package holding only the dumped partitions would delete the rest of the phone's table. The file also names
    /// the FLASH tab's own load pattern so it is not invisible to the flow it feeds.
    /// </summary>
    private void TryWriteFirmwareScatter(string dir, MtkDaPartitionTable table, IEnumerable<string> dumped)
    {
        IReadOnlyList<ScatterRow> rows = MtkNativeBench.ScatterRows(table, dumped,
            nativeDa != null ? nativeDa.Storage : null, out string refusal);
        if (rows == null)
        {
            NativeSay("[*] Native DA: no scatter written — " + refusal);
            return;
        }
        string chipset = nativeDa.RecipeChipset;
        try
        {
            string path = Path.Combine(dir, ScatterWriter.DefaultFileName(chipset));
            File.WriteAllText(path, ScatterWriter.Build(chipset, "PMK-device-dump", "EMMC", rows));
            NativeSay("[+] Native DA: firmware package written — " + Path.GetFileName(path) + ": " +
                      rows.Count(r => r.IsDownload) + " flashable row(s) of " + rows.Count + " — the " +
                      rows.Count(r => r.IsReserved) + " rows without an image keep the rest of the live table.");
            NativeSay("[*] Native DA: load that file on the FLASH tab to re-flash these images; the reserved rows " +
                      "stop the GPT rebuild from deleting partitions this dump did not read. The names a live session " +
                      "may never program (otp, flashinfo, seccfg) carry is_download: false.");
        }
        catch (Exception ex)
        {
            // The images and the manifest are already on disk; losing the index must not throw the run away.
            NativeSay("[!] Native DA: dumps saved, scatter NOT written: " + ex.Message);
        }
    }

    /// <summary>
    /// P1.3 — the NV erase over the live session: all six extents resolved against the device's own GPT, one ERASE
    /// per partition, then a single reset at the end. This is the mtkclient <c>e nvram,nvdata,…;reset</c> flow with
    /// the batched boot taken out of it — and a name the device does not carry stops the run before the first frame
    /// instead of after four partitions are already gone.
    /// </summary>
    private async Task NativeNvEraseAsync() => await NativeEraseSetAsync(NvPartitions, "NV erase");

    /// <summary>NV Reset over the live session — the same flow, one partition (nvdata).</summary>
    private async Task NativeNvResetAsync() => await NativeEraseSetAsync(new[] { "nvdata" }, "NV reset");

    /// <summary>
    /// The set-erase flow every native NV wipe shares. The confirmation is the button's own, on either route — one
    /// dialog per operation — so what happens here is the decision a name list cannot make: the extents are resolved
    /// against the device's own GPT, and a name the device does not have throws before the first frame.
    /// </summary>
    private async Task NativeEraseSetAsync(string[] names, string label)
    {
        RequireNativeSession();
        MtkDaPartitionTable table = await nativeDa.ReadPartitionTableAsync(nativeCancel.Token);
        IReadOnlyList<MtkDaExtent> plan = MtkNativeBench.NvErasePlan(table, names,
            "This device's GPT names none of the partitions to erase.", out string refusal);
        if (plan == null) throw new InvalidOperationException(refusal);

        NativeSay("[*] Native DA: " + label + " of " + plan.Count + " partition(s) " +
                  string.Join(", ", plan.Select(p => p.Name)) + ", over the session already open.");
        foreach (MtkDaExtent extent in plan)
        {
            NativeSay("[*] Native DA: erasing " + extent.Name + " (" + MtkDaExtent.SizeText(extent.ByteLength) + ")...");
            await nativeDa.ErasePartitionAsync(extent.Name, nativeCancel.Token).ConfigureAwait(false);
        }
        NativeSay("[OK] Native DA: " + plan.Count + " partition(s) erased in one session. The phone is rebooting — this " +
                  "session closes with it, so an NV backup goes back over a new one: plug the phone in again and press NV Restore.");
        // The reset is the mtkclient flow's trailing `;reset`, and it spends this session: the phone is on its way
        // down, so NativeRebootAsync asks NativeGuarded to close the port when the operation returns.
        await NativeRebootAsync();
    }

    /// <summary>
    /// Format All over the live session: the same whitelist resolver and the same one dialog as the mtkclient
    /// route, then the proven set-erase machinery. The phone keeps its bootchain, seccfg and NV/IMEI, and the
    /// trailing reset spends this session.
    /// </summary>
    private async Task NativeFormatAllAsync()
    {
        RequireNativeSession();
        MtkDaPartitionTable table = await nativeDa.ReadPartitionTableAsync(nativeCancel.Token);
        var targets = MtkNativeBench.FormatAllTargets(table.Extents.Select(p => p.Name), NvPartitions).ToList();
        if (!ConfirmFormatAll(targets)) { NativeSay("[*] Native DA: format all cancelled."); return; }

        IReadOnlyList<MtkDaExtent> plan = MtkNativeBench.NvErasePlan(table, targets,
            "This device's GPT names none of the partitions to erase.", out string refusal);
        if (plan == null) throw new InvalidOperationException(refusal);

        NativeSay("[*] Native DA: format all of " + plan.Count + " partition(s) " +
                  string.Join(", ", plan.Select(p => p.Name)) + ", over the session already open.");
        foreach (MtkDaExtent extent in plan)
        {
            NativeSay("[*] Native DA: erasing " + extent.Name + " (" + MtkDaExtent.SizeText(extent.ByteLength) + ")...");
            await nativeDa.ErasePartitionAsync(extent.Name, nativeCancel.Token).ConfigureAwait(false);
        }
        NativeSay("[OK] Native DA: " + plan.Count + " partition(s) erased in one session — bootchain, " +
                  "seccfg and NV/IMEI are kept. The phone is rebooting — flash full firmware to recover; this " +
                  "session closes with it.");
        await NativeRebootAsync();
    }

    /// <summary>
    /// P1.3 — NV Restore over the live session. The folder must be a verified backup: its manifest is hash-checked
    /// on disk first, then every NV entry it records is matched to the device's own extent byte for byte, and only
    /// then does anything reach the port. That per-extent equality is what stands in for the manifest's
    /// <c>LayoutHash</c>, which cannot be compared here — a native NV backup hashes the six names it read and an
    /// mtkclient NV backup hashes the whole partition table, so the two kinds of folder would fail each other's
    /// check while being equally good to restore from.
    /// <para>
    /// <paramref name="srcDir"/> comes from the shared folder picker in <c>NvRestoreAsync</c>; the confirmation, the
    /// writes and the reset are all this method's, so the operator sees one dialog whichever route the button took.
    /// </para>
    /// </summary>
    private async Task NativeNvRestoreAsync(string srcDir)
    {
        RequireNativeSession();
        if (!File.Exists(Path.Combine(srcDir, "backup-manifest.json")))
            throw new InvalidDataException("A live session restores only from a verified backup, and " + srcDir +
                " has no backup-manifest.json. Pick the folder an NV backup wrote, or press Eject Device and restore " +
                "a folder of raw dumps through mtkclient. Nothing was sent to the device.");

        var manifest = ShopServices.VerifyBackup(srcDir);
        NativeSay("[*] Native DA: backup verified, " + manifest.Files.Count + " file(s) hash-checked on disk (SHA-256) from " + srcDir + ".");

        // Again no ConfigureAwait before the dialog: the table read has to come back to the UI thread.
        MtkDaPartitionTable table = await nativeDa.ReadPartitionTableAsync(nativeCancel.Token);
        IReadOnlyList<MtkNativeBench.NvRestoreItem> plan = MtkNativeBench.NvRestorePlan(table, srcDir,
            manifest.Files, NvPartitions, out string refusal);
        if (plan == null) throw new InvalidOperationException(refusal);

        string notRestoring = MtkNativeBench.MissingFromPlan(NvPartitions, plan.Select(p => p.Extent.Name));
        if (notRestoring.Length > 0)
            NativeSay("[*] Native DA: this backup holds " + plan.Count + " of the six NV partitions — not restoring " +
                      notRestoring + ".");

        if (MessageBox.Show(this, MtkNativeBench.NvRestoreConfirmText(plan, srcDir), "Native DA NV restore",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            NativeSay("[*] Native DA: NV restore declined by the operator — nothing was sent.");
            return;
        }

        foreach (MtkNativeBench.NvRestoreItem item in plan)
        {
            NativeSay("[*] Native DA: writing " + item.Extent.Name + " from " + Path.GetFileName(item.ImagePath) +
                      " (sha256 " + item.Sha256.Substring(0, 12).ToLowerInvariant() + "...)...");
            await nativeDa.WritePartitionAsync(item.Extent.Name, item.ImagePath,
                p => NativeProgress("restore " + item.Extent.Name, p), verify: true,
                ct: nativeCancel.Token).ConfigureAwait(false);
        }
        NativeSay("[OK] Native DA: " + plan.Count + " partition(s) restored and read back byte-identical in one session. The phone is rebooting. Check the IMEI in the dialer once it is up.");
        await NativeRebootAsync();
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
        // Named from the pipe the boot actually took, not from the dropdown: since B6.7 Auto can choose either pipe,
        // and RUN 42/43/48/49 all closed a raw-USB session under the old wording, which told the operator the
        // serial port had just been let go — a claim about a port that was never opened.
        NativeSay("[+] Native DA session closed — " + (nativePipeName ?? "the pipe") + " is released.");
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

    /// <summary>
    /// Run a step on the window's thread, whatever thread the awaited call handed control back on. The native paths
    /// await with ConfigureAwait(false) — a boot must not idle a wire waiting for a repaint — and their continuations
    /// therefore land on a pool thread, while a WinForms control may only be touched by the thread that owns it.
    /// A window that is already gone runs nothing: the bench log holds every line the run produced.
    /// </summary>
    private void NativeUi(Action step)
    {
        if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
        try
        {
            if (rtbLog.InvokeRequired) rtbLog.Invoke(step);
            else step();
        }
        catch (ObjectDisposedException) { /* the form is going away mid-run */ }
        catch (InvalidOperationException) { /* same, one beat later */ }
    }

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
    private bool nativeSimpleLog = true;      // screen = milestones; native-da.log = every byte
    private const int NativeWireBatchLines = 24;

    /// <summary>
    /// The one place a NATIVE line is shown, and the one place it is recorded. The first real boot proves things no
    /// offline test can reach — whether the SerialPort settings hold, whether the DA stage stays on the same COM
    /// face after the jump — and that proof has to outlive the window, so every line also lands in native-da.log.
    /// Simple mode only changes what reaches the screen; the file always keeps the full run.
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
            // A locked or unwritable file must not also hide the screen. Warn once and keep going; the next line
            // retries the append, so a transient lock (an editor, a scanner) does not cost the rest of the run.
            if (!nativeBenchLogBroken)
            {
                nativeBenchLogBroken = true;
                Log("[!] Native DA: the bench log is not writable (" + ex.Message + "). The screen still shows the run.", Color.Orange);
            }
        }

        string head = line.TrimStart();
        bool wire = MtkNativeBench.IsWireChatter(head);

        if (nativeSimpleLog)
        {
            if (wire) return;                                  // the file has it; the screen does not need it
            string concise = MtkNativeBench.ConciseNativeLine(line);
            if (concise == null) return;
            if (concise == nativeLastScreenLine) return;       // two DA syncs are one milestone on the screen
            nativeLastScreenLine = concise;
            line = concise;
            head = line.TrimStart();
        }

        Color color = head.StartsWith("[+]") || head.StartsWith("[OK]") ? Color.LightGreen
            : head.StartsWith("[FAIL]") ? Color.Red
            : head.StartsWith("[!]") ? Color.Orange
            : head.StartsWith("[*]") || head.StartsWith("[>]") ? Color.Cyan
            : Color.Gainsboro;

        if (!nativeUiWireEach && wire)
        {
            bool full;
            lock (nativeWireGate)
            {
                nativeWirePending.Add(new KeyValuePair<string, Color>(line, color));
                full = nativeWirePending.Count >= NativeWireBatchLines;
            }
            // The line is already in the batch. Flushing it and then logging it again was the duplicate the screen
            // showed every 24 wire lines; the batch owns it either way.
            if (full) FlushNativeWire();
            return;
        }

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

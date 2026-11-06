#nullable disable
using System.Text;
using PMKUnlocker.MtkFirmware;

namespace PMKUnlocker.MediaTek;

/// <summary>
/// The decisions the NATIVE bench tab makes before it touches a phone — kept out of the UI file so they can be
/// asserted without a form, a port or a device (ROADMAP Phase 1.75 "Path B", stage B4).
///
/// This is deliberately only the parts that carry risk: where a dump is allowed to land on disk, what may boot
/// at all, which names may never be programmed, and what the operator is actually shown before a destructive
/// click. The byte protocol lives in <see cref="MtkDaSession"/> and <see cref="MtkDaCommands"/>; this class
/// never sends anything.
/// </summary>
internal static class MtkNativeBench
{
    /// <summary>Dumps go under their own folder so a native-session dump can never overwrite an mtkclient recording.</summary>
    internal const string DumpFolder = "native-da";

    /// <summary>
    /// Recipes are not shipped: the DA images come from unlicensed packs, so the honest default is no recipe and
    /// therefore no tab. An empty or missing directory is a supported state, not an error.
    /// </summary>
    internal static IReadOnlyList<string> DiscoverProfiles() => MtkBootProfile.FindProfiles(MtkBootProfile.DefaultDirectory);

    internal static string SessionDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.GetFullPath(Path.Combine(dataDirectory, "MediaTekSessions", DumpFolder));
    }

    /// <summary>
    /// Where a bench run's evidence lands. A first real boot proves things no offline test can — whether the
    /// SerialPort settings hold, whether the DA stage stays on the same COM face after the jump — and the only
    /// way that survives the session is a file the tool wrote by itself, not a screenshot.
    /// </summary>
    internal static string BenchLogPath(string dataDirectory) => Path.Combine(SessionDirectory(dataDirectory), "native-da.log");

    /// <summary>
    /// The operator's note for a hardware run, read on every boot: three millisecond numbers that slow a hardware
    /// BROM session down, and the switch that says whether the four-pair greet goes out at all. It sits next to the
    /// bench log so a run and the numbers that shaped it stay together.
    ///
    /// RUN 21 (2026-10-31 14:40) replayed 64 recorded steps byte for byte, was refused at step 65, and did the whole
    /// session inside one second — while the working client, which is Python and pays a round trip per command,
    /// sends the same bytes over the same face and is answered. The protocol is not what differs, so the pace is
    /// what has to be tried, and trying it must not cost a rebuild: these numbers are read per run.
    ///
    /// The greet is the other half of the same question. The working client's own log has no handshake byte at all
    /// (its first TX is the read-only query), so the four pairs we send are the one thing our client puts on the
    /// wire that it does not — which makes them the first thing a trial has to be able to take out.
    /// </summary>
    internal const string PacingFile = "pacing.txt";

    internal static string PacingPath(string dataDirectory) => Path.Combine(SessionDirectory(dataDirectory), PacingFile);

    /// <summary>
    /// What a hardware run is told to do by the note file.
    ///
    /// <c>setconfig</c> and <c>claimall</c> (B4.20) are a different kind of knob from the three timings: they are
    /// what the working client does to the <em>USB device</em> before its first command byte, and they were only
    /// discovered because they are invisible in its own log. Running <c>mtk.exe</c> with <c>LIBUSB_DEBUG=4</c>
    /// (control boot 2026-11-01 08:28:17, exit 0, full GPT, stderr 25 MB) shows its whole open sequence:
    /// <c>libusb_open</c> → <c>libusb_get_configuration</c> ("active config 0") → <c>libusb_set_configuration</c>
    /// ("configuration 1") → <c>libusb_claim_interface</c> interface 0 → <c>libusb_claim_interface</c> interface 1 →
    /// 9366 transfers. <c>set_interface_alt_setting</c> and <c>libusb_reset_device</c> appear <strong>zero</strong>
    /// times, so B4.8's removal of the reset stays correct — but <c>SET_CONFIGURATION</c> is sent by the client that
    /// demonstrably boots this phone, and it is the one thing B4.8 took out of ours on the strength of a log that
    /// could not have shown it. Both are off by default so an unedited note keeps the measured behaviour.
    ///
    /// <c>arm</c> (B4.24) is the third of that kind, and it came from running <em>our own</em> client under the same
    /// <c>LIBUSB_DEBUG=4</c> (2026-11-01 10:19:50, six boots). The open matched the working client's exactly, and the
    /// reads did not: our transport asks the bus for bytes on a 50 ms timer, so every empty wait is a bulk-IN URB the
    /// host aborts — 45 <c>errcode 995 → detected operation aborted</c> completions in one boot, median 26 ms between
    /// submissions. The working client arms one transfer at 1000 ms and aborts <strong>twice</strong> in a boot of 9366
    /// transfers, at 1.9 ms apart. Same bytes, different USB. <c>arm=1000</c> makes our wait theirs; 0 (the default)
    /// leaves a run exactly as RUN 32 recorded it.
    ///
    /// <c>uiwire</c> (B4.25) is the fourth, and RUN 33 is where it came from. Armed at 1000 ms, our reads became the
    /// working client's reads — 84 transfers, 5 of them empty (the two listen windows), 79 carrying real bytes, one
    /// open, one handle — and step 65 still answered <c>0x1D1A</c>. What the same trace then showed is that we are not
    /// waiting for the phone at all: <strong>every</strong> completion is logged at the same timestamp as its own
    /// submit (<c>poll() returned 1</c> with <c>revents</c> already set), so the bytes were in the pipe before we asked.
    /// The 26–45 ms sits entirely between <c>libusb_free_transfer</c> and the next <c>libusb_alloc_transfer</c> — in our
    /// own code, on the boot thread, between two wire events. That span is one synchronous
    /// <c>rtbLog.Invoke(…)</c> plus a <c>ScrollToCaret()</c> per log line, and the working client's whole prelude runs
    /// 19× faster because its host does less between bytes. <c>uiwire=off</c> takes the per-line UI round-trip out of
    /// the boot path; on (the default) leaves a run exactly as RUN 33 recorded it.
    /// </summary>
    internal readonly struct Knobs
    {
        internal Knobs(int startup, int settle, int step, bool greet) :
            this(startup, settle, step, greet, false, false)
        {
        }

        internal Knobs(int startup, int settle, int step, bool greet, bool setConfig, bool claimAll) :
            this(startup, settle, step, greet, setConfig, claimAll, 0)
        {
        }

        internal Knobs(int startup, int settle, int step, bool greet, bool setConfig, bool claimAll, int arm) :
            this(startup, settle, step, greet, setConfig, claimAll, arm, true)
        {
        }

        internal Knobs(int startup, int settle, int step, bool greet, bool setConfig, bool claimAll, int arm,
            bool uiWire)
        {
            Startup = startup;
            Settle = settle;
            Step = step;
            Greet = greet;
            SetConfig = setConfig;
            ClaimAll = claimAll;
            Arm = arm;
            UiWire = uiWire;
        }

        /// <summary>Milliseconds to wait after the face is claimed and before the first byte goes out.</summary>
        internal int Startup { get; }

        /// <summary>Milliseconds to wait after the greet and before the read-only query.</summary>
        internal int Settle { get; }

        /// <summary>Milliseconds to wait after every BROM step.</summary>
        internal int Step { get; }

        /// <summary>Whether the four-pair greet is sent. Off means the recipe's first byte is the first byte on the wire.</summary>
        internal bool Greet { get; }

        /// <summary>Whether SET_CONFIGURATION is sent to the face before it is claimed, as the working client does.</summary>
        internal bool SetConfig { get; }

        /// <summary>Whether every interface of the face is claimed, not just the one carrying our endpoints.</summary>
        internal bool ClaimAll { get; }

        /// <summary>
        /// How long a read waits on one armed bulk transfer instead of polling and aborting. 0 = today's 50 ms poll;
        /// 1000 is what the working client arms every one of its 9366 transfers at.
        /// </summary>
        internal int Arm { get; }

        /// <summary>
        /// Whether every per-wire line is put on the screen the moment it is produced. True (the default) is one
        /// blocking UI round-trip per byte of the boot; false batches the wire chatter onto the UI thread and lets the
        /// boot thread go straight to the next transfer. Either way the bench log gets every line, immediately.
        /// </summary>
        internal bool UiWire { get; }
    }

    /// <summary>
    /// Read the note. A missing file, an unknown key or a number that will not parse all mean "no pacing" and the
    /// greeting stays on: the note is an operator's scratchpad, not a contract, and a broken note must never stop a
    /// boot. Only an explicit <c>greet=off</c> takes the greeting out, and only an explicit <c>setconfig=on</c> or
    /// <c>claimall=on</c> puts a USB-level action into a run — those two change what the device sees, so they are
    /// never a side effect of a typo.
    /// </summary>
    internal static Knobs ReadKnobs(string dataDirectory)
    {
        int startup = 0, settle = 0, step = 0;
        bool greet = true, setConfig = false, claimAll = false, uiWire = true;
        int arm = 0;
        string path = PacingPath(dataDirectory);
        if (!File.Exists(path)) return new Knobs(startup, settle, step, greet, setConfig, claimAll, arm, uiWire);
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return new Knobs(startup, settle, step, greet, setConfig, claimAll, arm, uiWire); }
        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();
            if (key == "greet")
            {
                if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) greet = false;
                else if (value.Equals("on", StringComparison.OrdinalIgnoreCase)) greet = true;
                continue;
            }
            if (key == "setconfig" || key == "claimall" || key == "uiwire")
            {
                bool on = value.Equals("on", StringComparison.OrdinalIgnoreCase);
                if (on || value.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    if (key == "setconfig") setConfig = on;
                    else if (key == "claimall") claimAll = on;
                    else uiWire = on;
                }
                continue;
            }
            if (!int.TryParse(value, out int ms) || ms < 0) continue;
            if (key == "startup") startup = ms;
            else if (key == "settle") settle = ms;
            else if (key == "step") step = ms;
            else if (key == "arm") arm = ReadArmMs(ms);
        }
        return new Knobs(startup, settle, step, greet, setConfig, claimAll, arm, uiWire);
    }

    /// <summary>
    /// Is this line the boot's per-byte chatter — "wire 41: tx 0x00000000" and its replies — rather than something
    /// the operator has to see the instant it happens? Only the chatter is batched, so a [+], [!], [FAIL] or [*] line
    /// still reaches the screen on its own, in order, ahead of anything queued behind it.
    /// </summary>
    internal static bool IsWireChatter(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        string head = line.TrimStart();
        return head.StartsWith("[i]", StringComparison.Ordinal) &&
            (head.Contains("wire ") || head.Contains("reply ") || head.Contains("packet "));
    }

    /// <summary>
    /// The screen's version of a native line: the same run, told in the few lines an operator acts on. The bench log
    /// still gets every byte, so a shortened screen never shortens the evidence. Null means "do not show this line".
    /// Kept here, not in the form, so the wording is asserted by the hardware-free regression run.
    /// </summary>
    internal static string ConciseNativeLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        string t = line.TrimStart();
        if (t.Length == 0) return null;
        if (t.StartsWith("[i]", StringComparison.Ordinal) || t.StartsWith("[ ]", StringComparison.Ordinal)) return null;
        if (!t.StartsWith("[", StringComparison.Ordinal)) return null;   // partition rows and other indented detail

        // The long "why" sits after an em dash. The screen keeps the fact; native-da.log keeps the reason.
        int dash = line.IndexOf(" \u2014 ", StringComparison.Ordinal);
        if (dash > 0) line = line.Substring(0, dash).TrimEnd();

        if (line.Contains("Native DA knobs", StringComparison.Ordinal) ||
            line.Contains("BROM pacing", StringComparison.Ordinal) ||
            line.Contains("BROM greet:", StringComparison.Ordinal)) return null;

        if (line.Contains("loading recipe", StringComparison.Ordinal))
        {
            int at = line.IndexOf("loading recipe ", StringComparison.Ordinal) + "loading recipe ".Length;
            int end = line.IndexOf(" (", at, StringComparison.Ordinal);
            string name = end > at ? line.Substring(at, end - at) : line.Substring(at);
            return "[*] Recipe: " + name;
        }
        if (line.Contains("starting on recipe", StringComparison.Ordinal))
        {
            int at = line.IndexOf("starting on recipe ", StringComparison.Ordinal) + "starting on recipe ".Length;
            string name = line.Substring(at).Split(' ')[0];
            return "[*] Recipe: " + name + " (auto)";
        }
        if (line.Contains("pipe = auto", StringComparison.Ordinal))
        {
            int at = line.IndexOf("pipe = auto (", StringComparison.Ordinal);
            string which = at < 0 ? "" : line.Substring(at + "pipe = auto (".Length).Split(')')[0];
            return "[*] Pipe: auto (" + which + ")";
        }
        // This is the one line the operator has to act on — the boot then waits up to 90 s for the face to appear —
        // so the plug-in order is re-written here instead of matched against the clause the em-dash trim above has
        // already taken off the string.
        if (line.Contains("pipe = ", StringComparison.Ordinal))
        {
            int at = line.IndexOf("pipe = ", StringComparison.Ordinal) + "pipe = ".Length;
            return "[>] Pipe: " + line.Substring(at).Split(' ')[0] + " — power off, hold BOTH volume keys, plug in";
        }
        if (line.Contains("BROM handshake complete", StringComparison.Ordinal)) return "[+] Handshake OK";
        if (line.Contains("BROM face proven:", StringComparison.Ordinal))
            return "[+] Chipset: " + line.Substring(line.IndexOf("BROM face proven:", StringComparison.Ordinal) + "BROM face proven:".Length).Trim();
        if (line.Contains("DA1 jumped", StringComparison.Ordinal)) return "[+] DA1 loaded";
        if (line.Contains("DA SYNC received", StringComparison.Ordinal)) return "[+] DA sync";
        if (line.Contains("MTK DA ready", StringComparison.Ordinal)) return "[+] DA ready";
        if (line.Contains("session is live", StringComparison.Ordinal)) return "[OK] Session live - phone ready";
        if (line.Contains("has not been booted to a live DA", StringComparison.Ordinal))
            return "[!] First run on this chipset - the recipe is replayed exactly as recorded";
        if (line.Contains("Live GPT:", StringComparison.Ordinal))
        {
            int at = line.IndexOf("Live GPT:", StringComparison.Ordinal);
            string rest = line.Substring(at);
            int comma = rest.IndexOf(',');
            return "[+] " + (comma > 0 ? rest.Substring(0, comma) : rest);
        }
        if (line.StartsWith("[+] READ ", StringComparison.Ordinal))
        {
            int at = line.IndexOf(" - ", StringComparison.Ordinal);
            return at > 0 ? line.Substring(0, at) : line;
        }
        if (line.Contains("MTK USB face", StringComparison.Ordinal))
        {
            int at = line.IndexOf(" - ", StringComparison.Ordinal);
            return at > 0 ? line.Substring(0, at) : line;
        }
        if (line.Contains("extent(s) are the live table", StringComparison.Ordinal)) return null; // Live GPT already said it
        if (line.Contains("Reset command was sent", StringComparison.Ordinal)) return "[+] Reset sent - phone rebooting";
        if (line.Contains("session closed", StringComparison.Ordinal)) return "[+] Session closed";
        return line;
    }

    /// <summary>
    /// The <c>arm=</c> knob in milliseconds, or 0 for "keep polling". It is a wait, not a pacing gap, so it is bounded:
    /// a value large enough to outlast a step's own timeout would move the refusal out of the recipe and into the
    /// transport, and an operator typing the wrong number should get the experiment they meant, not a hang.
    /// </summary>
    internal static int ReadArmMs(int requested)
    {
        if (requested <= 0) return 0;
        return requested > MaxReadArmMs ? MaxReadArmMs : requested;
    }

    /// <summary>The longest a read wait may be armed for. The working client arms 1000 ms; this leaves room to overshoot it.</summary>
    internal const int MaxReadArmMs = 5000;

    /// <summary>
    /// Does a note describe anything other than the default run? A run that changes the device's USB state has to be
    /// recognisable in the log afterwards, so this decides both whether the line is printed and what it says.
    /// </summary>
    internal static string DescribeKnobs(Knobs knobs)
    {
        if (knobs.Startup == 0 && knobs.Settle == 0 && knobs.Step == 0 && knobs.Greet && !knobs.SetConfig &&
            !knobs.ClaimAll && knobs.Arm == 0 && knobs.UiWire)
            return null;
        var text = new StringBuilder(" [*] Native DA knobs — startup " + knobs.Startup + " ms, settle " + knobs.Settle +
            " ms, step " + knobs.Step + " ms, greet " + (knobs.Greet ? "on" : "off"));
        if (knobs.SetConfig) text.Append(", SET_CONFIGURATION before the claim");
        if (knobs.ClaimAll) text.Append(", claim every interface");
        if (knobs.Arm > 0) text.Append(", every read armed " + knobs.Arm + " ms instead of a 50 ms poll");
        if (!knobs.UiWire) text.Append(", wire lines batched to the screen (no UI round-trip per byte)");
        return text.ToString();
    }

    /// <summary>
    /// The USB-level choices a raw-USB open makes for a run. It lives here rather than inside the transport so the
    /// fail-closed rule behind it can be asserted without a device on the bus.
    ///
    /// A face already in a configuration gets that same value sent back to it; a face that reports none
    /// (<c>active config 0</c> — what the control boot measured on <c>0E8D:0003</c>) gets the value read out of its
    /// own configuration descriptor, which is where the working client's "configuration 1" comes from. The point of
    /// the knob is that a <c>SET_CONFIGURATION</c> request goes on the wire before the first byte, not that we
    /// invent a number: when neither the device nor its descriptor offers one, the answer here is 0 and the
    /// transport refuses instead of guessing.
    /// </summary>
    internal static int ConfigValueToSelect(bool knobOn, int activeConfiguration, int descriptorConfigurationValue)
    {
        if (!knobOn) return 0;
        if (activeConfiguration > 0) return activeConfiguration;
        return descriptorConfigurationValue > 0 ? descriptorConfigurationValue : 0;
    }

    /// <summary>
    /// Which interfaces a claimed face should hand back at Dispose. The bulk pair is always there because the boot
    /// cannot run without it; with <c>claimall</c> off nothing else is added, so the plain run claims exactly what
    /// RUN 27/28 claimed. With it on the set is sorted ascending, because that is the order the working client's own
    /// API trace puts its claims in (interface 0, then interface 1).
    /// </summary>
    internal static List<int> InterfacesToClaim(int bulkInterface, IEnumerable<int> otherInterfaces, bool claimAll)
    {
        var claim = new List<int> { bulkInterface };
        if (!claimAll || otherInterfaces == null) return claim;
        foreach (int number in otherInterfaces)
        {
            if (number != bulkInterface && !claim.Contains(number)) claim.Add(number);
        }
        claim.Sort();
        return claim;
    }

    /// <summary>
    /// Append one timestamped bench line. A transient lock (an editor, a scanner, another reader) is retried twice
    /// before the caller is told: one locked instant must not cost the rest of the run its evidence. A real disk
    /// failure still throws, because a run with no log is a run with no proof.
    /// </summary>
    internal static void AppendBenchLog(string dataDirectory, string line, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        string path = BenchLogPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string text = at.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line.Trim() + Environment.NewLine;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(path, text, Encoding.UTF8);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(15);
            }
        }
    }

    /// <summary>
    /// What a MediaTek PID face means on this bench, in words an operator can act on. RUN 4 measured that a
    /// completed boot holds <c>0003</c> (COM3) exclusively through the whole BROM stage, and RUN 9 measured that
    /// <c>0003</c> is also what a phone with a stale DA in RAM wears — so even the measured face is only a candidate,
    /// and the recipe's own HW-code echo is what decides. Naming that before 4,440 frames are spent discovering it
    /// is all this does; it never substitutes itself for the proof.
    /// </summary>
    internal static string DescribeFace(string pid) => (pid ?? "").Trim().ToUpperInvariant() switch
    {
        "0003" => "MediaTek USB Port — the face a completed boot was measured riding (RUN 4), and also the face a " +
                  "phone with a stale DA in RAM wears (RUN 9): the HW-code echo is the only decider",
        "2000" => "PreLoader VCOM — the port mtkclient's log names while waiting for BROM; this bench has never " +
                  "booted natively through it",
        "2001" => "DA VCOM — a DA is resident, so the phone is mid-session rather than waiting in BROM",
        "20FF" => "PreLoader/HID — the phone is running its preloader rather than sitting in BROM",
        "2006" or "2026" => "Android-mode VCOM (ADB/serial) — the phone is booted, not in download mode",
        _ => "unrecognized MediaTek face 0e8d:" + (string.IsNullOrWhiteSpace(pid) ? "????" : pid.Trim()) +
             " — only the recipe's HW-code echo can say what this is"
    };

    /// <summary>
    /// How much this bench trusts a face as the start of a boot: <c>measured</c> (0003 — a completed boot rode it),
    /// <c>unproven</c> (a face that might be BROM but has never carried a native boot here) or <c>not-brom</c>
    /// (a face this bench has only ever seen on a phone that is not waiting in BROM).
    /// </summary>
    internal static string FaceBootReadiness(string pid)
    {
        string value = (pid ?? "").Trim().ToUpperInvariant();
        if (value == "0003") return "measured";
        if (value == "2000" || value.Length == 0) return "unproven";
        return value is "2001" or "20FF" or "2006" or "2026" ? "not-brom" : "unknown";
    }

    /// <summary>The recovery a refused BROM handshake calls for, in the words RUN 9's stale-DA trap proved necessary.</summary>
    internal const string StaleDaRecovery =
        "A phone that still holds a DA in RAM wears this same face (RUN 9): boot Android normally, Power off from the " +
        "menu (not a reboot), wait ~20 s, then hold BOTH volume keys and plug — replugging alone does not clear a stale DA.";

    /// <summary>
    /// One MediaTek node on the bus at the moment of the question: its PID and, when the node carries a serial
    /// face, the COM port Windows gave it. A node with no COM port is still an answer — a phone that walked to a
    /// HID-only face is a phone that left download mode, which is exactly what a refusal has to distinguish.
    /// </summary>
    internal readonly record struct MtkBusNode(string Pid, string ComPort);

    /// <summary>
    /// Whether the phone is still where the boot left it, phrased for a refusal line.
    ///
    /// RUN 10 and RUN 11 both refused with <c>0xfd -&gt; 0xfe</c> and no further bytes, and from inside the tool
    /// those two runs looked identical. They were not: the wire had gone quiet because the phone had already
    /// changed faces — RUN 11's log showed COM3 gone and only a non-serial <c>0e8d:20FF</c> HID node left, i.e. a
    /// phone booting through rather than sitting in BROM. A wrong echo and a vanished port are different failures
    /// with different fixes, and the boot cannot tell them apart from bytes alone. So after a refusal the session
    /// asks Windows what is on the bus now, and this turns that answer into the sentence the operator reads.
    /// </summary>
    internal static string DescribeBusAfterRefusal(string wantedComPort, string wantedPid, IReadOnlyList<MtkBusNode> nodes)
    {
        string wanted = (wantedComPort ?? "").Trim().ToUpperInvariant();
        string startedOn = (wantedPid ?? "").Trim().ToUpperInvariant();
        var present = (nodes ?? Array.Empty<MtkBusNode>()).ToList();

        MtkBusNode? still = null;
        foreach (var node in present)
            if (string.Equals((node.ComPort ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(node.ComPort))
            { still = node; break; }

        if (still != null)
        {
            string pidNow = (still.Value.Pid ?? "????").Trim().ToUpperInvariant();
            string face = pidNow == startedOn
                ? wanted + " is still enumerated as 0e8d:" + pidNow
                : wanted + " is now 0e8d:" + pidNow + " (the boot started on 0e8d:" + startedOn + ")";
            return face + " — the phone did not move, so the answer it gave is the answer it gives: it is not in the " +
                   "state this recipe was recorded from";
        }

        string serialFaces = string.Join(", ", present
            .Where(n => !string.IsNullOrWhiteSpace(n.ComPort))
            .Select(n => n.ComPort.Trim().ToUpperInvariant() + "=0e8d:" + (n.Pid ?? "????")));
        if (present.Count == 0)
            return wanted + " is gone and no MediaTek node is on the bus at all — the phone powered off or left " +
                   "download mode while the boot was talking to it";
        if (serialFaces.Length > 0)
            return wanted + " is gone; the phone is on a different serial face now (" + serialFaces + ") — it " +
                   "re-enumerated mid-boot rather than waiting in BROM";
        string hidOnly = string.Join(", ", present.Select(n => "0e8d:" + (n.Pid ?? "????")).Distinct());
        return wanted + " is gone; what is left on the bus has no COM port at all (" + hidOnly + ") — the phone " +
               "re-enumerated into a non-serial face, which is what a phone that is booting normally looks like";
    }

    /// <summary>
    /// The same question asked from the raw-USB side, where there is no COM port to name.
    ///
    /// The A/B run of 2026-10-30 16:01 settled which pipe the working client uses: mtkclient's own debug log for a
    /// boot that reached and printed the whole GPT says <c>TX:fd / USB echo / RX:fd</c> — it talks to the bulk
    /// endpoints of the USB device and never opens the COM port Windows was showing on the same face. So a refusal
    /// on that path has to be described by device face, not by port name.
    /// </summary>
    internal static string DescribeBusAfterRefusalUsb(string startedPid, IReadOnlyList<string> pidsNow)
    {
        string started = NormalizePid(startedPid);
        var present = (pidsNow ?? Array.Empty<string>())
            .Select(NormalizePid)
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

        if (present.Contains(started))
            return "the bus still carries 0e8d:" + started + " — the phone did not move, so the answer it gave is " +
                   "the answer it gives: it is not in the state this recipe was recorded from";
        if (present.Count == 0)
            return "0e8d:" + started + " is gone and no MediaTek device is on the bus at all — the phone left " +
                   "download mode or powered off while the boot was talking to it";
        string faces = string.Join(", ", present.Select(p => "0e8d:" + p));
        return "0e8d:" + started + " is gone; the bus now carries " + faces + " — the phone re-enumerated mid-boot " +
               "rather than waiting in BROM";
    }

    private static string NormalizePid(string pid) => (pid ?? "").Trim().Replace("0E8D:", "").ToUpperInvariant();

    /// <summary>
    /// The one line that says which backend opened the face. RUN 16 spent a whole BROM window on the legacy
    /// libusb-win32 path and the log could only say "driver LibUsb" after the claim had already failed; B4.9 moves
    /// the open onto libusb-1.0, and every run has to name the backend it actually got so the next refusal can be
    /// compared against this one instead of guessed at.
    /// </summary>
    internal static string UsbOpenVia(string face, string how) =>
        " MTK USB — opened " + (string.IsNullOrWhiteSpace(face) ? "the face" : face.Trim()) + " through " +
        (string.IsNullOrWhiteSpace(how) ? "an unnamed backend" : how.Trim());

    /// <summary>
    /// What the one call that decides the whole raw-USB route returned, in words.
    ///
    /// RUN 17 (2026-10-31 10:35) opened <c>0E8D:0003</c> through libusb-1.0 and then could not claim its data
    /// interface once: <c>libusb -3 ACCESS</c>. The cause was not the phone and not the endpoint pick — libusb-1.0's
    /// default Windows backend is WinUSB, and WinUSB cannot take an interface a kernel driver already owns, which
    /// <c>usbser</c> does here because Windows had given the same face COM3. A probe on the bundled DLL measured the
    /// route that can: <c>libusb_set_option(ctx, USE_USBDK=1)</c> returns 0, the device list changes, and claiming a
    /// keyboard-driver-owned HID interface succeeds. Since the difference between "the phone refused" and "the
    /// backend never switched" is a single integer, every run names it.
    /// </summary>
    internal static string UsbBackendText(int setOptionRc) => setOptionRc switch
    {
        0 => "UsbDk backend engaged (libusb_set_option -> 0) — the route measured to claim an interface another " +
             "driver holds",
        int.MinValue => "no backend switch was attempted — the default WinUSB route, which is RUN 17's ErrorAccess",
        _ => "WinUSB backend only (libusb_set_option -> " + setOptionRc + ") — RUN 17's ErrorAccess on claim is " +
             "this route, not the phone"
    };

    /// <summary>
    /// The result of a control ask that ran out the wall clock it was given and never came back. It is not a libusb
    /// code — no libusb error number means "this transfer is still outstanding" — and it is a fourth state next to
    /// accepted, stalled and timed-out: RUN B6-1 (2026-11-03) watched the MT6877 leave the bus with the transfer in
    /// flight, so the driver never completed it and a call that trusts libusb's timeout sits there for the life of
    /// the process. Naming it lets the boot say "nothing answered" instead of pretending the phone refused.
    /// </summary>
    internal const int CtrlNoAnswer = -1000;

    /// <summary>
    /// A libusb return code in the words a refusal can be compared against. Only the codes this transport can
    /// actually produce are named; anything else keeps its number rather than inventing a meaning.
    /// </summary>
    internal static string UsbErrorText(int rc) => rc switch
    {
        0 => "success",
        -1 => "IO error",
        -2 => "invalid parameter",
        -3 => "Access denied — an interface is held by a driver this backend cannot detach (RUN 17)",
        -4 => "no such device",
        -5 => "not found",
        -6 => "resource busy",
        -7 => "timeout",
        -8 => "overflow",
        -9 => "pipe error",
        -10 => "interrupted",
        -11 => "no memory",
        -12 => "not supported",
        _ => "libusb error " + rc
    };

    /// <summary>
    /// The one parenthesised clause of a pipe failure, for a retry line that must stay one line.
    ///
    /// A write failure's message is a full sentence ending with what the operator should fear ("the boot was stopped
    /// with the recipe half-applied"). During the face proof that sentence is wrong — the proof only ever sends one
    /// read-only byte, and it is about to try again — so the retry line quotes the measured cause only:
    /// <c>failed (libusb -9 pipe error, 0/1 byte(s) accepted)</c> becomes <c>(libusb -9 pipe error, 0/1 byte(s)
    /// accepted)</c>. Anything else keeps a fixed phrase rather than repeating a guess.
    /// </summary>
    internal static string TransportFailureBrief(string message)
    {
        if (string.IsNullOrEmpty(message)) return "(no detail was recorded)";
        int at = message.IndexOf("failed (", StringComparison.Ordinal);
        if (at >= 0)
        {
            // ")." rather than ")" because the detail itself contains a parenthesis — "(0/1 byte(s) accepted)".
            int close = message.IndexOf(").", at + 7, StringComparison.Ordinal);
            if (close > at) return message.Substring(at + 7, close - at - 6);
        }
        int period = message.IndexOf(". ", StringComparison.Ordinal);
        return period > 0 ? message.Substring(0, period + 1) : message.Trim();
    }

    /// <summary>
    /// What a claim race means while the retry budget still has room: the node is closed and opened again, and the
    /// phone is not touched. <c>null</c> means the budget is spent and the caller must refuse with the measured
    /// detail instead — the one refusal that is honest after twelve silent seconds.
    ///
    /// The wording matters as much as the policy: it says in the log that nothing was sent, so a later reader of the
    /// session file cannot mistake "could not take the interface" for "the phone answered".
    /// </summary>
    internal static string UsbClaimRetryNote(int attempt, TimeSpan elapsed, TimeSpan budget, string detail)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "A claim attempt is numbered from 1.");
        if (elapsed >= budget) return null;
        return " MTK USB — claim attempt " + attempt + " could not take the data interface yet (" +
               (string.IsNullOrWhiteSpace(detail) ? "no detail from libusb" : detail.Trim()) + "); closing the node " +
               "and opening it again for up to " + Math.Round(budget.TotalSeconds) + " s — nothing has been sent to " +
               "the phone, and no configuration is selected";
    }

    /// <summary>
    /// Processes that poll for a MediaTek download face the moment one appears. A native boot that starts while one
    /// of them is alive gets a node that is already busy: RUN 15's first claim failed and the forced takeover left
    /// the BROM answering <c>0xfe</c> to every <c>0xfd</c>, which is exactly the "two masters on one device" failure
    /// this bench refuses rather than debugs. Matching is by process name without the <c>.exe</c> suffix.
    /// Returns the name that was found, or null when the bench is alone.
    /// </summary>
    internal static string CompetingClient(IEnumerable<string> processNames)
    {
        var running = (processNames ?? Enumerable.Empty<string>())
            .Select(n => (n ?? "").Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .Select(n => n.EndsWith(".exe", StringComparison.Ordinal) ? n[..^4] : n)
            .ToHashSet();
        foreach (string known in new[] { "mtk", "mtkclient", "flash_tool", "spflashtool", "unlocktool", "dftpro" })
            if (running.Contains(known)) return known + ".exe";
        return null;
    }

    /// <summary>
    /// A serial pipe, described with the two knobs no capture has ever measured.
    ///
    /// 115200 is a formality (USB CDC ignores the baud), but DTR and RTS are real control-line state changes sent to
    /// the device, and the pipe that demonstrably boots this phone asserts neither.
    ///
    /// The greeting is part of the identity of the pipe. RUN 10 and RUN 11 refused on the first byte and neither
    /// could tell "this face is a BROM that did not like our command" from "this face is something else, answering
    /// in its own protocol"; a face that speaks unprompted is the second kind, and that is only observable before
    /// the first write, so the transports listen, keep the bytes out of the recipe's way and report them here. Both
    /// descriptions end up in the boot log, because a run that refused is only evidence if a later run can tell
    /// what differed.
    /// </summary>
    internal static string DescribeSerialOpen(string comPort, string pid, bool dtrEnabled, bool rtsEnabled, byte[] preWriteBytes) =>
        (string.IsNullOrWhiteSpace(comPort) ? "?" : comPort.Trim().ToUpperInvariant()) + " " +
        "0e8d:" + NormalizePid(pid) + " [serial, " + (dtrEnabled ? "DTR on" : "DTR off") + "/" +
        (rtsEnabled ? "RTS on" : "RTS off") + ", pre-write wire: " + ShortListening(preWriteBytes) + "]";

    /// <summary>The bulk-endpoint pipe the working client uses, described the same way: what it is, and what it heard first.</summary>
    internal static string DescribeUsbOpen(int vid, int pid, MtkBulkPair pair, byte[] preWriteBytes) =>
        "usb " + (vid.ToString("X4") + ":" + pid.ToString("X4")) + " [raw bulk, iface " + pair.InterfaceNumber +
        " class 0x" + pair.InterfaceClass.ToString("X2") + ", OUT 0x" + pair.WriteEndpoint.ToString("X2") +
        " / IN 0x" + pair.ReadEndpoint.ToString("X2") + ", no COM driver in the way, pre-write wire: " +
        ShortListening(preWriteBytes) + "]";

    private static string ShortListening(byte[] heard) =>
        heard == null || heard.Length == 0 ? "nothing" : string.Join("", heard.Take(8).Select(b => b.ToString("X2") + " ")).Trim();

    /// <summary>
    /// One bulk endpoint pair as the descriptor walk found it: the interface it sits on, that interface's class and
    /// the two endpoint addresses. MTK's download faces are CDC composites — a communication interface with an
    /// interrupt endpoint plus a data interface with the bulk pair — so the class is what separates the two.
    /// </summary>
    internal readonly record struct MtkBulkPair(int InterfaceNumber, int InterfaceClass, int WriteEndpoint, int ReadEndpoint);

    /// <summary>
    /// Which interface a raw-USB session should claim.
    ///
    /// The rule is the one the measured descriptor gives: the data interface (CDC class 0x0A) owns bulk OUT 0x01 and
    /// bulk IN 0x81, and the communication interface owns only an interrupt endpoint — writing to the wrong one is a
    /// silent dead end. When a face is not a CDC composite, any interface carrying a real bulk pair is accepted,
    /// and nothing at all is returned when none does, because the caller must refuse rather than guess an endpoint.
    /// </summary>
    internal static MtkBulkPair? ChooseBulkPair(IReadOnlyList<MtkBulkPair> candidates)
    {
        var usable = (candidates ?? Array.Empty<MtkBulkPair>())
            .Where(c => c.WriteEndpoint != 0 && c.ReadEndpoint != 0)
            .ToList();
        if (usable.Count == 0) return null;
        foreach (var candidate in usable)
            if (candidate.InterfaceClass == 0x0A) return candidate;
        return usable[0];
    }

    /// <summary>
    /// A partition name comes off the device's own GPT, so it is input, not trusted text. Anything that is not a
    /// plain token cannot become a filename — the same rule the scatter path applies to scatter file names.
    /// </summary>
    internal static string SafePartitionToken(string rawName)
    {
        string name = (rawName ?? "").Trim();
        var kept = new StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') kept.Append(c);
        if (kept.Length == 0)
            throw new InvalidOperationException("'" + name + "' has no filename-safe characters, so no dump file can be named after it.");
        string token = kept.ToString();
        return token.Length > 40 ? token[..40] : token;
    }

    /// <summary>
    /// Whether a dump file was read from a partition, judged by the filename token the dumper itself minted
    /// (SafePartitionToken + timestamp). A grid write may reuse a session dump only when the name proves it —
    /// writing an image of unknown origin to a live extent is not a restore, it is a gamble.
    /// </summary>
    internal static bool DumpMatchesPartition(string dumpPath, string partitionName)
    {
        if (string.IsNullOrWhiteSpace(dumpPath) || string.IsNullOrWhiteSpace(partitionName)) return false;
        try
        {
            string token = SafePartitionToken(partitionName);
            return Path.GetFileNameWithoutExtension(dumpPath)
                .StartsWith(token + "-", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// The dump destination: a fresh timestamped file inside the native folder, verified to still be inside it
    /// after the path is resolved. A dump that could be pointed at a system path is not a dump, it is a write.</summary>
    internal static string DumpPathFor(string dataDirectory, string partitionName, DateTime stamp)
    {
        string token = SafePartitionToken(partitionName);
        string directory = SessionDirectory(dataDirectory);
        Directory.CreateDirectory(directory);
        string root = directory + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(directory, token + "-" + stamp.ToString("yyyyMMdd-HHmmss") + ".img"));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The dump path " + path + " escaped " + directory + ". Nothing was written.");
        return path;
    }

    /// <summary>
    /// Why a boot may not start right now, or null when it may. One COM port has one master: a native session on a
    /// port that mtkclient is holding would interleave frames into both, and a second session beside a live one
    /// would put two writers on the same phone.
    /// </summary>
    internal static string BootBlocker(bool sessionAlreadyOpen, bool otherMediaTekClientRunning)
    {
        if (sessionAlreadyOpen)
            return "A native DA session is already open on this port. Close it first — two sessions on one MTK VCOM corrupt both.";
        if (otherMediaTekClientRunning)
            return "An mtkclient operation is running. It holds the MediaTek port; wait for it to finish or press STOP.";
        return null;
    }

    /// <summary>The never-program list, in the words the operator needs before clicking.</summary>
    internal static bool ProgramAllowed(string partitionName, out string reason)
    {
        string name = (partitionName ?? "").Trim();
        if (MtkDaSession.NeverProgramNames.Contains(name))
        {
            reason = "'" + partitionName + "' is the partition table or the chip identity — erasing or rewriting it " +
                "through a live session bricks the phone with no BROM-side recovery.";
            return false;
        }
        if (MtkDaSession.ReadOnlyPayloadNames.Contains(name))
        {
            reason = "'" + partitionName + "' holds the bootloader lock policy, and its program payload has never " +
                "been measured on this bench — writing unmeasured bytes changes the lock state silently instead of " +
                "failing. Read it, or dump it, but do not write or erase it here.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>
    /// What the confirmation dialog says. The extent is spelled out in the units the DA addresses because that is
    /// the only way the operator can check it against a scatter file before agreeing to the write.
    /// </summary>
    internal static string ConfirmText(string operation, MtkDaExtent extent, string imagePath)
    {
        ArgumentNullException.ThrowIfNull(extent);
        string where = extent.Describe;
        return operation switch
        {
            "restore" => "Restore " + extent.Name + " from\n" + imagePath +
                "\n\nWriting to: " + where +
                "\n\nThe DA will be asked to commit, then the whole range is read back and compared byte for byte. " +
                "A mismatch is reported as corruption, not as a success.",
            "erase" => "Erase " + extent.Name + "?\n\n" + where +
                "\n\nThe DA accepts an erase with report words only. Physical zeroing is not proven by this tool, " +
                "so dump the partition first if you need it back.",
            "write" => "Write " + extent.Name + " from\n" + imagePath +
                "\n\nWriting to: " + where +
                "\n\nThe image is written as raw bytes and only the written length is read back and compared. " +
                "An image larger than the extent is refused before the port is touched; a smaller one leaves the rest of the extent untouched.",
            _ => throw new ArgumentException("Unknown native operation '" + operation + "'.", nameof(operation))
        };
    }

    /// <summary>
    /// The BROM HW codes this tool has booted to a live DA on real hardware, from its own runs. MT6765 (0x0766) got
    /// there on 2026-11-02 (RUN 42, and four clean boots after it). MT6877 (0x0959) followed on 2026-11-04: the UFS
    /// recipe replayed to a live DA, 0x40004 answered the 208-byte UFS geometry, the live GPT listed 77 extents at
    /// 4096 B, a READ came back, the two optional line-setting asks were stepped over, and Reboot Device sent the
    /// reset (native-da.log 09:19:45 -> 09:25:03). Nothing else is claimed: the DA command layer this tool runs is
    /// chipset-generic, but the BROM prelude and its blobs are recorded per chipset, and a recipe that has never been
    /// replayed is a first run, not a supported device.
    /// </summary>
    internal static bool BootProvenOnHardware(ushort hwCode) => hwCode == 0x0766 || hwCode == 0x0959;

    /// <summary>
    /// Auto-detect: which installed recipe belongs to the HW code the BROM just reported.
    ///
    /// A boot cannot know the chipset before it starts — the BROM prelude is what reads the code — so the run begins
    /// on one recipe and switches at the HW-code reply, which is the first byte that is chipset-specific and still
    /// before any DA image or register spray. Null means no installed recipe claims that chipset: the boot keeps the
    /// fail-closed refusal it has always had rather than guessing at a neighbouring chipset's spray.
    /// </summary>
    internal static T PickByHwCode<T>(IReadOnlyList<T> candidates, Func<T, ushort> hwCode, ushort deviceHwCode) where T : class
    {
        if (candidates == null || hwCode == null) return null;
        foreach (T candidate in candidates)
        {
            if (candidate != null && hwCode(candidate) == deviceHwCode) return candidate;
        }
        return null;
    }

    /// <summary>The one line that says the recipe was not chosen by hand, so a later reader knows why it switched.</summary>
    internal static string AutoBootLine(int recipes) =>
        "[*] Native DA: boot recipe = auto — " + recipes + " installed; the BROM's own HW code picks one";

    /// <summary>
    /// What a boot-recipe combo row means. Row 0 is Auto and the installed files fill the rows after it in order,
    /// so a named row is the file at (row - 1). A row past the last file is a combo filled before the list changed:
    /// no recipe is returned rather than booting one the operator never chose.
    /// </summary>
    internal static (bool Auto, int ProfileIndex) ProfileChoice(int selectedIndex, int installed)
    {
        if (selectedIndex <= 0) return (true, -1);
        int index = selectedIndex - 1;
        return index >= installed ? (false, -1) : (false, index);
    }

    /// <summary>Which recipe the HW code landed on. Printed whether it matched the first try or was switched to.</summary>
    internal static string AutoRecipeLine(ushort hwCode, string chipset) =>
        "[*] Auto: recipe " + chipset + " (HW 0x" + hwCode.ToString("X4") + ")";

    /// <summary>
    /// What a pipe-combo row means. Row 0 is Auto and the two named pipes follow it, so raw USB is row 1 and the
    /// COM port is row 2. A row past the end has no pipe behind it and gets Auto rather than an invented choice.
    /// </summary>
    internal static (bool Auto, bool RawUsb) PipeChoice(int selectedIndex) => selectedIndex switch
    {
        1 => (false, true),
        2 => (false, false),
        _ => (true, false),
    };

    /// <summary>
    /// Which pipe Auto runs when both sides of the bus have been looked at.
    ///
    /// Raw USB wins whenever the bulk face is listed, because every boot this tool has proven on hardware — MT6765
    /// and MT6877 alike — came through it, and the same node answering over the COM face was refused in RUN 10 and
    /// RUN 11. The COM port is picked only when it is the sole face the phone is actually showing: a node that
    /// UsbDk cannot see but Windows has a serial driver for. Nothing listed at all is not a choice between pipes,
    /// it is a phone that has not been plugged in yet, so the wait stays on the proven pipe and its own refusal is
    /// the honest answer.
    /// </summary>
    internal static bool PickRawPipe(bool bulkFaceListed, bool comPortListed) => bulkFaceListed || !comPortListed;

    /// <summary>
    /// The bulk side is reachable when the USB list holds a node whose PID is one a BROM boot may start on. The
    /// list is what libusb reported; a PID that is not in the accepted set is a different state of the phone, not
    /// a BROM face.
    /// </summary>
    internal static bool BulkBromFaceListed(IEnumerable<int> listedPids, IEnumerable<int> acceptedPids) =>
        listedPids != null && acceptedPids != null && listedPids.Any(acceptedPids.Contains);

    /// <summary>
    /// The serial side is reachable when a COM port sits on a BROM PID. It has to be that PID and not "some
    /// MediaTek port": a phone an earlier session left behind keeps advertising the same VID under the DA's own
    /// PID, and counting that would pull Auto off the proven pipe onto a port that cannot be in BROM at all.
    /// </summary>
    internal static bool ComBromPortListed(IEnumerable<string> listedPids, IEnumerable<int> acceptedPids)
    {
        if (listedPids == null || acceptedPids == null) return false;
        var wanted = acceptedPids.Select(p => p.ToString("X4")).ToList();
        return listedPids.Any(p => wanted.Any(w => string.Equals(p, w, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The one line that says the pipe was not chosen by hand, and what the bus said when it was decided.</summary>
    internal static string AutoPipeLine(bool rawUsb, bool bulkFaceListed, bool comPortListed) =>
        "[*] Native DA: pipe = auto (" + (rawUsb ? "raw USB" : "COM port") + ") — the USB face was " +
        (bulkFaceListed ? "listed" : "not listed") + " and the MediaTek COM port was " +
        (comPortListed ? "listed" : "not listed") + "; " +
        (rawUsb
            ? "raw USB is the pipe every recorded boot came through"
            : "no bulk face is visible while a serial face is, so the COM port is the only pipe the phone can be reached on");

    /// <summary>
    /// One phase mark of a replayed walk, printed where the wire trace goes quiet.
    ///
    /// <see cref="MtkDaSession"/> stops printing individual steps after <c>WireTraceSteps</c>, which is why every
    /// MT6877 boot so far has shown a 14 s block between the last traced step and the blob line with nothing in it:
    /// the recipe's own step count is not a duration, and the phone was already exonerated by the recorded boot
    /// (the accepted client moved the same DA1 through the same pipe in 0.088 s). This line says what the quiet
    /// block was made of — how many steps, how many of them reads, and how long they actually took — in one line,
    /// so the next hardware run attributes its own wait instead of needing a capture.
    /// </summary>
    internal static string WalkPhaseLine(int index, int total, long elapsedMs, long reads, long writes,
        long controls, string nextBlob) =>
        "[*] Walk timing: " + index + "/" + total + " step(s) in " + elapsedMs.ToString() + " ms (" +
        reads + " read(s), " + writes + " write(s), " + controls + " control(s)) — uploading " + nextBlob + " now";

    /// <summary>The same mark for one blob's own upload, appended to the line that names it, so a reader can tell
    /// a slow step sequence apart from a slow transfer without a second instrument.</summary>
    internal static string StreamedLine(string blobFile, int bytes, long elapsedMs) =>
        "    streamed " + blobFile + " (" + bytes + " bytes) in " + elapsedMs + " ms";

    /// <summary>The one-line state the tab shows, so a screenshot of a bench run says what the session is doing.</summary>
    internal static string StatusLine(bool booted, string chipset, ushort hwCode, int partitionCount) =>
        booted
            ? "Session: BOOTED  " + chipset + "  HW 0x" + hwCode.ToString("X4") + "  " + partitionCount + " partition(s) cached"
            : "Session: closed — nothing is sent until Boot runs";

    /// <summary>
    /// Which device a partition table came from. Every number in it is one the DA itself reported — the BROM's HW
    /// code the recipe was proven against, the storage block size, the user-area size and how many extents the
    /// device's own GPT lists — so it answers "is this the phone in front of me?" without carrying a card serial
    /// or any other identity value into a file on disk. Two different phones landing on the same string would have
    /// to share a chipset, a block size, an exact capacity and a partition count, and at that point the table is
    /// the same table.
    /// </summary>
    internal static string GptBinding(ushort hwCode, uint blockSize, ulong userBytes, int partitionCount) =>
        "HW 0x" + hwCode.ToString("X4") + " · block " + blockSize + " · user " + userBytes +
        " B · " + partitionCount + " partition(s)";

    /// <summary>
    /// What the device's own GPT header says about itself, printed before the save runs. The two CRC verdicts are
    /// always stated because they are the difference between a folder an operator can restore from and one he has to
    /// re-derive — and neither one refuses the save, since this tool has never read a UFS GPT through its own session
    /// and a mismatch on a snapshot is information, not a danger.
    /// </summary>
    internal static string GptInspectLine(MtkGptLayout layout, int partitionCount) =>
        "[*] GPT: " + layout.Describe + " — " + partitionCount + " partition(s) — " + layout.CrcText +
        (layout.BackupMatchesCapacity ? "" : "; note: its backup LBA is not the last LBA of the user area");

    /// <summary>The save's own line: where the folder is, and the fact that the port only ever carried reads.</summary>
    internal static string GptSavedLine(string folder, int fileCount, int partitionCount, long totalBytes) =>
        "[+] Native DA: GPT saved to " + folder + " — " + fileCount + " file(s) for " + partitionCount +
        " partition(s), " + totalBytes + " byte(s) on disk. Everything in it was read off this device; nothing was written.";

    /// <summary>
    /// P1.2 — what one native backup will read: every requested name resolved against the device's own GPT, mapped to
    /// the byte length its extent reports. The map is both the read list and the manifest layout, so the sizes written
    /// into <c>backup-manifest.json</c> are the same extents the reads were addressed with rather than a second claim
    /// about the device. Names are folded case-insensitively, which is how the live table itself matches them.
    ///
    /// Null means nothing is read, and <paramref name="refusal"/> says why: a name the device does not have (backing
    /// up what is not there would write a manifest that lies) or an empty selection.
    /// </summary>
    internal static Dictionary<string, long> BackupLayout(MtkDaPartitionTable table, IEnumerable<string> requested,
        string emptyReason, out string refusal)
    {
        refusal = null;
        var layout = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in requested)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!table.TryFind(name, out MtkDaExtent extent))
            {
                refusal = "The device's own GPT has no partition named '" + name + "' (" + table.Extents.Count +
                    " entries read), so it cannot be backed up. Nothing was read.";
                return null;
            }
            if (!layout.ContainsKey(extent.Name)) layout[extent.Name] = (long)extent.ByteLength;
        }
        if (layout.Count == 0)
        {
            refusal = emptyReason;
            return null;
        }
        return layout;
    }

    /// <summary>Table-level names the flasher rebuilds itself; they are not partitions a scatter row can carry.</summary>
    private static readonly HashSet<string> GptHeaderNames =
        new(StringComparer.OrdinalIgnoreCase) { "pgpt", "sgpt", "gpt" };

    /// <summary>
    /// P1.4 — the rows that turn a dump folder into a firmware package. Every offset is the device's own GPT
    /// converted to bytes (the same conversion the DA's extent structs use), and a name a live session may never
    /// program still gets dumped but is marked <c>is_download: false</c>, because a package that offers to rewrite
    /// the chip identity is a package that bricks the phone it came from.
    ///
    /// Every extent of the live table gets a row, not only the dumped ones. <c>wfscatter</c> rebuilds the GPT from
    /// the rows it parses and prints "device partitions not in scatter will be removed" for the rest, so a package
    /// holding just the three partitions somebody ticked would rewrite the phone's table down to those three. The
    /// undumped extents travel as RESERVED rows (no image, extent from the scatter, re-read from the live table by
    /// the client), which keeps the rebuilt table identical to the one the dump was taken from.
    ///
    /// UFS is refused rather than guessed: the region word of a UFS scatter has never been read off a real one on
    /// this bench, and an invented region string is exactly the kind of unmeasured byte this project does not send.
    /// </summary>
    internal static IReadOnlyList<ScatterRow> ScatterRows(MtkDaPartitionTable table, IEnumerable<string> dumped,
        MtkStorageInfo storage, out string refusal)
    {
        refusal = null;
        if (storage != null && !storage.IsEmmcUserAddressed)
        {
            refusal = "this device addresses its user area as " + storage.StorageName + " and the region word a " +
                "scatter carries for it has never been read off a real UFS package on this bench — the images and " +
                "the manifest are saved, no scatter is written.";
            return null;
        }
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in dumped ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(name)) wanted.Add(name.Trim());
        foreach (string name in wanted)
        {
            if (table.TryFind(name, out _)) continue;
            refusal = "'" + name + "' was read but the live GPT does not report it, so no scatter is written — " +
                "a row with an invented offset is a write list aimed at the wrong bytes.";
            return null;
        }
        if (wanted.Count == 0)
        {
            refusal = "nothing was dumped, so there is no package to describe.";
            return null;
        }
        var rows = new List<ScatterRow>(table.Extents.Count);
        foreach (MtkDaExtent extent in table.Extents)
        {
            // The flasher writes the GPT headers itself at the standard LBAs and skips these names when it builds
            // the table, so a row for them would be a row nothing reads.
            if (extent.ByteLength == 0 || GptHeaderNames.Contains(extent.Name)) continue;
            bool isDumped = wanted.Contains(extent.Name);
            rows.Add(new ScatterRow(extent.Name, isDumped ? extent.Name + ".bin" : "NONE",
                (long)extent.ByteOffset, (long)extent.ByteLength, ScatterWriter.EmmcUserRegion,
                isDumped && ProgramAllowed(extent.Name, out _), !isDumped));
        }
        if (rows.Count == 0)
        {
            refusal = "the live GPT reports no user-area partition, so there is no package to describe.";
            return null;
        }
        return rows;
    }

    /// <summary>
    /// P1.3 — what one native NV erase wipes: every requested name resolved to the extent the device's own GPT
    /// reports, in the order given, duplicates folded. All or nothing, exactly like <see cref="BackupLayout"/>:
    /// a set that names a partition this device does not have, or one a live session may never program, erases
    /// nothing at all instead of four of six and then throwing mid-run with the phone half-wiped.
    /// </summary>
    internal static IReadOnlyList<MtkDaExtent> NvErasePlan(MtkDaPartitionTable table, IEnumerable<string> requested,
        string emptyReason, out string refusal)
    {
        refusal = null;
        var plan = new List<MtkDaExtent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in requested)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!table.TryFind(name, out MtkDaExtent extent))
            {
                refusal = "The device's own GPT has no partition named '" + name + "' (" + table.Extents.Count +
                    " entries read), so this set is not erased. Nothing was sent to the device.";
                return null;
            }
            if (!ProgramAllowed(extent.Name, out string reason))
            {
                refusal = reason + " Nothing in this set was erased — the whole set is refused before the first frame.";
                return null;
            }
            if (seen.Add(extent.Name)) plan.Add(extent);
        }
        if (plan.Count == 0)
        {
            refusal = emptyReason;
            return null;
        }
        return plan;
    }

    /// <summary>
    /// One partition of a verified backup, matched to the extent it belongs in. The path is already containment-
    /// checked against the backup folder, and the hash is the manifest's own claim about the bytes at that path.
    /// </summary>
    internal sealed record NvRestoreItem(MtkDaExtent Extent, string ImagePath, string Sha256);

    /// <summary>
    /// P1.3 — what one native NV restore writes: the backup's manifest entries that name a requested partition,
    /// each matched to the device's own extent, with exact byte equality as the gate.
    ///
    /// <para>
    /// The equality test replaces the manifest's <c>LayoutHash</c> comparison, which cannot be used here: an
    /// mtkclient NV backup records the hash of the whole 43-partition layout it read, while a native NV backup can
    /// only ever record the six it read — so a six-name layout would reject a backup that is good. What both kinds
    /// do agree on is the per-partition extent, and that is the number a write is addressed by, so that is what
    /// this checks. Entries for partitions outside <paramref name="requested"/> are not an error, they are simply
    /// not restored — which is what lets a full-layout backup serve the six NV names.
    /// </para>
    ///
    /// Null means nothing is written, and <paramref name="refusal"/> says which partition broke the match. A
    /// caller that gets a plan back has already had every file hash-checked on disk.
    /// </summary>
    internal static IReadOnlyList<NvRestoreItem> NvRestorePlan(MtkDaPartitionTable table, string sourceDirectory,
        IReadOnlyCollection<ShopServices.BackupFile> manifestFiles, IEnumerable<string> requested, out string refusal)
    {
        refusal = null;
        var byName = new Dictionary<string, ShopServices.BackupFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ShopServices.BackupFile entry in manifestFiles)
            if (!byName.ContainsKey(entry.Partition)) byName[entry.Partition] = entry;

        var plan = new List<NvRestoreItem>();
        foreach (string name in requested)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!byName.TryGetValue(name, out ShopServices.BackupFile file)) continue;
            if (!table.TryFind(name, out MtkDaExtent extent))
            {
                refusal = "This backup carries " + name + " (" + file.Bytes + " byte(s)), but the device's own GPT has no " +
                    "partition named '" + name + "' (" + table.Extents.Count + " entries read) — it is not this phone's " +
                    "layout, so nothing was written.";
                return null;
            }
            if (extent.ByteLength != (ulong)file.Bytes)
            {
                refusal = name + " is " + file.Bytes + " byte(s) in this backup and " + extent.ByteLength +
                    " byte(s) on this device. A backup whose extent does not match the device is not restored over it — " +
                    "nothing was written.";
                return null;
            }
            if (!ProgramAllowed(extent.Name, out string reason))
            {
                refusal = reason + " Nothing in this set was written.";
                return null;
            }
            plan.Add(new NvRestoreItem(extent, ShopServices.SafeChild(sourceDirectory, file.File), file.Sha256));
        }
        if (plan.Count == 0)
        {
            refusal = "This backup's manifest records none of the NV partitions this tool restores (" +
                manifestFiles.Count + " file(s) recorded). Pick the folder an NV backup wrote, or Eject and restore a " +
                "folder of raw dumps through mtkclient. Nothing was written.";
            return null;
        }
        return plan;
    }

    /// <summary>
    /// The native NV restore's confirmation. The button shows one dialog, not six, and it is shown only after the
    /// manifest has been hash-checked and every extent matched — so the dialog can say those two things as facts
    /// about work already done rather than as promises about work about to start.
    /// </summary>
    internal static string NvRestoreConfirmText(IReadOnlyList<NvRestoreItem> plan, string sourceDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return "Restore " + plan.Count + " partition(s) through the live DA session?\n\n" +
            string.Join("\n", plan.Select(i => "  " + i.Extent.Describe + "  <-  " + Path.GetFileName(i.ImagePath))) +
            "\n\nFrom: " + sourceDirectory +
            "\n\nEvery file was hash-checked against the backup manifest before this dialog, and each extent matches " +
            "this device byte for byte. The bytes are written raw over what is there now — no erase first, which is " +
            "what the mtkclient restore does too — then each whole range is read back and compared. The phone is " +
            "rebooted once, after the last partition.";
    }

    /// <summary>
    /// The names in <paramref name="requested"/> a plan does not cover, in the caller's own order. A partial restore
    /// is legitimate — an NV backup of a device that has no proinfo is a five-partition folder — but it is never
    /// left unsaid, because the operator needs to know which NV partitions are still whatever the phone held.
    /// </summary>
    internal static string MissingFromPlan(IEnumerable<string> requested, IEnumerable<string> planned)
    {
        var have = new HashSet<string>(planned, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", requested.Where(n => !string.IsNullOrWhiteSpace(n) && !have.Contains(n)));
    }

    /// <summary>
    /// The INFO tab's device status line, built only from what the live session read: the chipset the recipe was
    /// proven on, the BROM's own HW code, the eMMC part number out of the storage descriptor and the user-area
    /// size the DA advertises. A missing part number shows as nothing rather than a placeholder — an invented
    /// "EMMC ID" reads like the device's own answer.
    ///
    /// This is a SCREEN line. The full 16-byte CID and the serial bytes inside it are not decoded anywhere, so
    /// they cannot reach a log; the part number is a model name shared by every unit of that eMMC, which is why
    /// it is the one field the status line is allowed to show.
    /// </summary>
    internal static string DeviceStatusLine(string chipset, ushort hwCode, string emmcPartNumber, string userSizeText)
    {
        string line = string.IsNullOrWhiteSpace(chipset) ? "HW 0x" + hwCode.ToString("X4")
            : chipset + " (HW " + hwCode.ToString("X4") + ")";
        if (!string.IsNullOrWhiteSpace(emmcPartNumber)) line += " · " + emmcPartNumber.Trim();
        if (!string.IsNullOrWhiteSpace(userSizeText)) line += " · " + userSizeText.Trim();
        return line;
    }

    /// <summary>Progress text for the tab label. Percentages are the DA's own frame counts, not an estimate.</summary>
    internal static string ProgressLine(string what, int percent) => what + " — " + percent + "%";
}

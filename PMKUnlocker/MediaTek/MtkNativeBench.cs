#nullable disable
using System.Text;

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

    /// <summary>Append one timestamped bench line. Throws on a real disk failure: a run with no log is a run with no proof.</summary>
    internal static void AppendBenchLog(string dataDirectory, string line, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        string path = BenchLogPath(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.AppendAllText(path, at.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line.Trim() + Environment.NewLine, Encoding.UTF8);
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
    /// The dump destination: a fresh timestamped file inside the native folder, verified to still be inside it
    /// after the path is resolved. A dump that could be pointed at a system path is not a dump, it is a write.
    /// </summary>
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
        if (MtkDaSession.NeverProgramNames.Contains((partitionName ?? "").Trim()))
        {
            reason = "'" + partitionName + "' is the partition table or the chip identity — erasing or rewriting it " +
                "through a live session bricks the phone with no BROM-side recovery.";
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
            _ => throw new ArgumentException("Unknown native operation '" + operation + "'.", nameof(operation))
        };
    }

    /// <summary>The one-line state the tab shows, so a screenshot of a bench run says what the session is doing.</summary>
    internal static string StatusLine(bool booted, string chipset, ushort hwCode, int partitionCount) =>
        booted
            ? "Session: BOOTED  " + chipset + "  HW 0x" + hwCode.ToString("X4") + "  " + partitionCount + " partition(s) cached"
            : "Session: closed — nothing is sent until Boot runs";

    /// <summary>Progress text for the tab label. Percentages are the DA's own frame counts, not an estimate.</summary>
    internal static string ProgressLine(string what, int percent) => what + " — " + percent + "%";
}

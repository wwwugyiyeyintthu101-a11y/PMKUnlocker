#nullable disable
using System.Management;
using System.IO.Ports;

namespace PMKUnlocker.MediaTek;

/// <summary>
/// One MediaTek COM port node as Windows sees it. The VID/PID are carried because the boot log has to say
/// which face the bytes are going through — a phone sitting on the DA/rescue face enumerates a COM port too,
/// and the port alone cannot tell the two states apart.
/// </summary>
internal sealed record MtkVcomPort(string ComPort, string Vid, string Pid, string DeviceId, string FriendlyName)
{
    internal string Identity => Vid + ":" + Pid;

    public override string ToString() => ComPort + " " + Identity + " (" + FriendlyName + ")";
}

/// <summary>
/// Finding the VCOM. Two rules, both learned the hard way on the Redmi 9A:
///
/// 1. No single-port fallback. The old helper returned <c>ports[0]</c> when exactly one COM port existed on
///    the machine, which silently pointed a bypass spray at whatever serial device happened to be attached.
///    Here a device is only usable when Windows attributes it to MediaTek's VID, and more than one candidate
///    is a refusal rather than a guess.
/// 2. The PID is not the identity proof. It only selects the port; the recipe's own HW-code query (0xfd) and
///    the DA1 alive byte (0xc0) prove what state the SoC is really in. A DA-resident phone lies about being in
///    BROM, so the boot machine — not this resolver — decides whether the device is in the right state.
/// </summary>
internal static class MtkVcom
{
    internal const string MediaTekVid = "0E8D";

    /// <summary>All COM-bearing MediaTek nodes. Returns empty when there are none; never falls back to other vendors.</summary>
    internal static IReadOnlyList<MtkVcomPort> Enumerate()
    {
        var found = new List<MtkVcomPort>();
        var seenPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_" + MediaTekVid + "%'");
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                string deviceId = obj["DeviceID"]?.ToString() ?? "";
                var com = System.Text.RegularExpressions.Regex.Match(name, @"(COM\d+)");
                if (!com.Success) continue;                       // the USB node itself, not its serial interface
                var vp = System.Text.RegularExpressions.Regex.Match(
                    deviceId.ToUpperInvariant(), @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})");
                string port = com.Groups[1].Value;
                if (!seenPorts.Add(port)) continue;               // composite devices repeat the same COM under several interfaces
                found.Add(new MtkVcomPort(port,
                    vp.Success ? vp.Groups[1].Value : MediaTekVid,
                    vp.Success ? vp.Groups[2].Value : "????",
                    deviceId, name));
            }
        }
        catch (Exception ex)
        {
            throw new MtkDaProtocolException("Windows could not list the USB devices, so no MediaTek port could be " +
                "resolved safely (WMI: " + ex.Message + ").");
        }
        found.Sort((a, b) => string.CompareOrdinal(a.ComPort, b.ComPort));
        return found;
    }

    /// <summary>
    /// Every MediaTek node Windows reports as working right now, serial face or not. This exists for the moment
    /// after a boot has refused: <see cref="Enumerate"/> deliberately drops the nodes without a COM port, but a
    /// phone that answered once and then re-enumerated onto a HID-only face is precisely the answer the operator
    /// needs, and that phone is invisible to the resolver. Only status-OK nodes count — a ghost of a device that
    /// left the bus would otherwise be reported as "the phone did not move".
    /// </summary>
    internal static IReadOnlyList<MtkNativeBench.MtkBusNode> SnapshotFaces()
    {
        var faces = new List<MtkNativeBench.MtkBusNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, Status FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_" + MediaTekVid + "%'");
        foreach (var obj in searcher.Get())
        {
            if (!string.Equals(obj["Status"]?.ToString(), "OK", StringComparison.OrdinalIgnoreCase)) continue;
            string name = obj["Name"]?.ToString() ?? "";
            string deviceId = obj["DeviceID"]?.ToString() ?? "";
            var vp = System.Text.RegularExpressions.Regex.Match(
                deviceId.ToUpperInvariant(), @"VID_[0-9A-F]{4}&PID_([0-9A-F]{4})");
            string pid = vp.Success ? vp.Groups[1].Value : "????";
            var com = System.Text.RegularExpressions.Regex.Match(name, @"(COM\d+)");
            string port = com.Success ? com.Groups[1].Value : null;
            if (seen.Add(pid + "|" + (port ?? ""))) faces.Add(new MtkNativeBench.MtkBusNode(pid, port));
        }
        return faces;
    }

    /// <summary>
    /// Wait for a MediaTek VCOM to be usable. The phone only enumerates for a moment after the plug-in, so this
    /// polls rather than asking the operator to time a click, and it refuses to choose between two phones. A
    /// stale DA-face port from an aborted session also matches the VID, which is exactly why the caller still has
    /// to run the recipe and let the HW-code check decide what the device really is.
    /// </summary>
    internal static Task<MtkVcomPort> WaitForPortAsync(TimeSpan timeout, Action<string> log, CancellationToken ct) =>
        WaitForAsync(_ => true, "a MediaTek COM port", true,
            "Power the phone off, hold BOTH volume keys and plug it in so it enters BROM; " +
            "also make sure no other client is holding the port.", timeout, log, ct);

    /// <summary>
    /// The face a completed boot was measured leaving the phone on after the BROM jumped: 0E8D:2001, on a COM number
    /// Windows had moved (COM3 for BROM, COM37 for the DA). A handoff waits for that face by name, because "some
    /// MediaTek port" is not a pass — the port of a second phone, or of the same phone having walked back to
    /// Android, would otherwise be written DA frames into.
    /// </summary>
    internal static Task<MtkVcomPort> WaitForDaFaceAsync(TimeSpan timeout, Action<string> log, CancellationToken ct) =>
        WaitForAsync(p => string.Equals(p.Pid, "2001", StringComparison.OrdinalIgnoreCase),
            "the MediaTek DA port (0e8d:2001)", false,
            "The phone did not come back as a DA port. It may have booted Android instead — power it off from the " +
            "menu, wait ~20 s and start the boot again.", timeout, log, ct);

    private static async Task<MtkVcomPort> WaitForAsync(Func<MtkVcomPort, bool> accepts, string waitingFor,
        bool warnAboutStaleDa, string advice, TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        string lastNote = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = Enumerate().Where(p => accepts(p)).ToList();
            if (candidates.Count == 1)
            {
                MtkVcomPort picked = candidates[0];
                string readiness = MtkNativeBench.FaceBootReadiness(picked.Pid);
                log?.Invoke("[+] MTK VCOM " + picked + " (" + readiness + ") — " + MtkNativeBench.DescribeFace(picked.Pid));
                if (readiness == "not-brom" && warnAboutStaleDa)
                    log?.Invoke("[!] " + MtkNativeBench.StaleDaRecovery);
                return picked;
            }
            string note = candidates.Count == 0
                ? "waiting for " + waitingFor
                : "ambiguous: " + string.Join(", ", candidates.Select(c => c.ToString()));
            if (note != lastNote)
            {
                log?.Invoke(" MTK port — " + note);
                lastNote = note;
            }
            if (DateTime.UtcNow >= deadline)
                throw new MtkDaProtocolException("No single " + waitingFor + " appeared within " +
                    (int)timeout.TotalSeconds + " s" + (candidates.Count > 1 ? " (still ambiguous: " + candidates.Count + " ports)" : "") +
                    ". " + advice);
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The <see cref="IMtkTransport"/> that a real boot runs on: the serial object over the MTK VCOM.
///
/// Kept as thin as it can be. Everything with meaning — echo checking, status words, blob framing — lives in
/// <see cref="MtkDaSession"/>, so this file is the only place that touches System.IO.Ports and is deliberately
/// left out of the hardware-free regression project.
///
/// What the A/B run of 2026-10-30 proved about this path: the client that booted the phone to a full GPT print
/// never opened a COM port at all — its own log says <c>TX:fd / USB echo / RX:fd</c>, which is the bulk endpoint
/// pipe, described in <see cref="MtkUsbTransport"/>. USB CDC ignores the baud rate, so 115200 is a formality for
/// the handle, not a protocol parameter; the handshake lines are real control messages, so they are now part of
/// what a run says about itself instead of a constant nobody measured.
/// </summary>
internal sealed class MtkSerialTransport : IMtkTransport
{
    private readonly SerialPort _port;
    private readonly string _description;
    private readonly MtkVcomPort _target;

    /// <summary>Reads never block the boot loop; a burst that has not arrived yet is reported as "nothing waiting".</summary>
    private const int ReadPollTimeoutMs = 50;

    private MtkSerialTransport(SerialPort port, MtkVcomPort target, string description)
    {
        _port = port;
        _target = target;
        _description = description;
    }

    public string Description => _description;

    /// <summary>
    /// Ask Windows — not the wire — whether the phone is still on the face the boot was using. The port object
    /// cannot answer this: a COM port that has been orphaned by a re-enumeration keeps reading and writing like a
    /// live pipe until something actually touches it, which is exactly how RUN 11 looked identical to RUN 10 from
    /// inside the session.
    /// </summary>
    public string BusAfterRefusal() =>
        MtkNativeBench.DescribeBusAfterRefusal(_target.ComPort, _target.Pid, MtkVcom.SnapshotFaces());

    /// <summary>
    /// Open the port. Whatever the face said before we spoke is captured first, then both directions are discarded:
    /// the recipe's first expectation is an echo of the command it is about to send, and stale bytes from a previous
    /// session would be read as that echo. The greeting is kept in the description because it is the only evidence
    /// about what kind of device sits behind the port that cannot be recovered after the first write.
    ///
    /// DTR and RTS default to low. The pipe that demonstrably boots this phone is a raw bulk endpoint, which asserts
    /// no control-line state at all, and no capture has ever measured which serial settings a working MTK client
    /// asks for — so the least-invasive option is the default. RUN 10 and RUN 11 ran with DTR on and were refused;
    /// the lines are now parameters a run chooses and reports instead of constants nobody measured.
    /// </summary>
    internal static MtkSerialTransport Open(MtkVcomPort target, int baudRate = 115200,
        bool dtrEnabled = false, bool rtsEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        var port = new SerialPort(target.ComPort, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = ReadPollTimeoutMs,
            // A write chunk is 2 MiB (the DA's own advertised size). A 5 s write timeout would abort a transfer
            // the device is still draining, mid-partition — the worst place to give up — so the pipe gets 30 s.
            WriteTimeout = 30000,
            DtrEnable = dtrEnabled,
            RtsEnable = rtsEnabled
        };
        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new MtkDaProtocolException(target.ComPort + " is already held by another program (" + ex.Message +
                "). Close the other client — two masters on one MTK VCOM corrupt both sessions.");
        }
        catch (IOException ex)
        {
            port.Dispose();
            throw new MtkDaProtocolException(target.ComPort + " could not be opened (" + ex.Message + ").");
        }

        byte[] greeted = ListenBeforeDiscard(port, 250);
        var transport = new MtkSerialTransport(port, target,
            MtkNativeBench.DescribeSerialOpen(target.ComPort, target.Pid, dtrEnabled, rtsEnabled, greeted));
        transport.DiscardBothDirections();
        return transport;
    }

    /// <summary>Drain a bounded window of unprompted bytes if the face volunteers any; never fails the open.</summary>
    private static byte[] ListenBeforeDiscard(SerialPort port, int windowMs)
    {
        var heard = new List<byte>();
        var scratch = new byte[64];
        var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
        try
        {
            while (DateTime.UtcNow < deadline && heard.Count == 0)
            {
                if (port.BytesToRead > 0)
                    heard.AddRange(scratch.Take(port.Read(scratch, 0, scratch.Length)));
                else
                    Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            // The face walked off mid-listen; that is the boot's problem to report, not the listen's.
        }
        return heard.ToArray();
    }

    public int BytesAvailable
    {
        get
        {
            try { return _port.IsOpen ? _port.BytesToRead : 0; }
            catch (IOException) { return 0; }      // the phone came off the bus mid-poll; the read loop times out and aborts
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        try
        {
            _port.Write(data, offset, count);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            throw new MtkDaProtocolException("Writing " + count + " byte(s) to " + _description + " failed (" + ex.Message +
                "). The device may have left BROM; the boot was stopped with the recipe half-applied.") { TransportFailure = true };
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _port.Read(buffer, offset, count);
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new MtkDaProtocolException("Reading " + _description + " failed (" + ex.Message + ").");
        }
    }

    private void DiscardBothDirections()
    {
        try
        {
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
        }
        catch (IOException) { /* the face is gone; the boot fails on the first echo check */ }
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch (IOException) { /* the cable is already out */ }
        finally { _port.Dispose(); }
    }
}

/// <summary>
/// The serial side of the stage handoff.
///
/// The phone does not keep the port it booted on: the jump into DA1 re-enumerates it, and Windows assigns the new
/// face a different COM number — measured 2026-10-30, where a completed boot left the DA answering on COM37 after
/// starting on COM3. Waiting for "a MediaTek COM port" again is therefore the whole job, and the DA face is allowed
/// by name because a face the recipe was recorded arriving on cannot be treated as a wrong mode.
/// </summary>
internal sealed class MtkSerialStageHandoff : IMtkDaStageHandoff
{
    private readonly Action<string> _log;
    private readonly TimeSpan _timeout;

    internal MtkSerialStageHandoff(Action<string> log, TimeSpan timeout)
    {
        _log = log;
        _timeout = timeout;
    }

    public IMtkTransport OpenDaStageTransport(string bromDescription)
    {
        _log?.Invoke(" MTK port — the BROM jumped, waiting for the phone to come back as a DA port " +
            "(the boot started on " + bromDescription + ")");
        MtkVcomPort port = MtkVcom.WaitForDaFaceAsync(_timeout, _log, CancellationToken.None)
            .GetAwaiter().GetResult();
        return MtkSerialTransport.Open(port);
    }
}

/// <summary>
/// The face proof's second port on the serial pipe. The same rule as <see cref="MtkUsbBromRecovery"/>: a handle
/// that dies between asks is the working client's ordinary retry, so the proof gets a freshly opened port instead
/// of ending the boot. A port that does not come back answers with null.
/// </summary>
internal sealed class MtkSerialBromRecovery : IMtkBromRecovery
{
    private readonly Action<string> _log;

    internal MtkSerialBromRecovery(Action<string> log) => _log = log;

    public IMtkTransport Reopen(int waitMs)
    {
        MtkVcomPort port;
        try
        {
            port = MtkVcom.WaitForPortAsync(TimeSpan.FromMilliseconds(Math.Max(0, waitMs)), _log,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (MtkDaProtocolException)
        {
            return null;
        }

        _log?.Invoke(" MTK port — the BROM pipe was reclaimed on " + port.ComPort + " (0e8d:" + port.Pid + ")");
        return MtkSerialTransport.Open(port);
    }
}

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
    /// Wait for a MediaTek VCOM to be usable. The phone only enumerates for a moment after the plug-in, so this
    /// polls rather than asking the operator to time a click, and it refuses to choose between two phones. A
    /// stale DA-face port from an aborted session also matches the VID, which is exactly why the caller still has
    /// to run the recipe and let the HW-code check decide what the device really is.
    /// </summary>
    internal static async Task<MtkVcomPort> WaitForPortAsync(TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        string lastNote = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = Enumerate();
            if (candidates.Count == 1)
            {
                log?.Invoke("[+] MTK VCOM " + candidates[0]);
                return candidates[0];
            }
            string note = candidates.Count == 0
                ? "waiting for a MediaTek COM port"
                : "ambiguous: " + string.Join(", ", candidates.Select(c => c.ToString()));
            if (note != lastNote)
            {
                log?.Invoke(" MTK port — " + note);
                lastNote = note;
            }
            if (DateTime.UtcNow >= deadline)
                throw new MtkDaProtocolException("No single MediaTek COM port appeared within " + (int)timeout.TotalSeconds +
                    " s" + (candidates.Count > 1 ? " (still ambiguous: " + candidates.Count + " ports)" : "") +
                    ". Power the phone off, hold Volume Down and plug it in so it enters BROM; " +
                    "also make sure no other client is holding the port.");
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
/// Untested-by-device note: USB CDC ignores the baud rate, so 115200 is a formality for the handle, not a
/// protocol parameter. The captures prove the bytes, not the port settings a working client asked for; a
/// real-device run has to confirm the handshake lines before this path is trusted over mtkclient.
/// </summary>
internal sealed class MtkSerialTransport : IMtkTransport
{
    private readonly SerialPort _port;
    private readonly string _description;

    /// <summary>Reads never block the boot loop; a burst that has not arrived yet is reported as "nothing waiting".</summary>
    private const int ReadPollTimeoutMs = 50;

    private MtkSerialTransport(SerialPort port, string description)
    {
        _port = port;
        _description = description;
    }

    public string Description => _description;

    /// <summary>
    /// Open the port. Any bytes the face left behind are discarded: the recipe's first expectation is an echo of
    /// the command it is about to send, and stale bytes from a previous session would be read as that echo.
    /// </summary>
    internal static MtkSerialTransport Open(MtkVcomPort target, int baudRate = 115200)
    {
        ArgumentNullException.ThrowIfNull(target);
        var port = new SerialPort(target.ComPort, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = ReadPollTimeoutMs,
            // A write chunk is 2 MiB (the DA's own advertised size). A 5 s write timeout would abort a transfer
            // the device is still draining, mid-partition — the worst place to give up — so the pipe gets 30 s.
            WriteTimeout = 30000,
            DtrEnable = true,
            RtsEnable = false
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
        var transport = new MtkSerialTransport(port, target.ComPort + " " + target.Identity);
        transport.DiscardBothDirections();
        return transport;
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
                "). The device may have left BROM; the boot was stopped with the recipe half-applied.");
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

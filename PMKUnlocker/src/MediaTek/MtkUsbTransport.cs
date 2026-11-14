#nullable disable
using System.Runtime.InteropServices;
using MonoLibUsb;
using MonoLibUsb.Descriptors;
using MonoLibUsb.Profile;

namespace PMKUnlocker.MediaTek;

/// <summary>
/// One MediaTek device node as the raw-USB side sees it: the face number (PID) and whatever name the bus
/// reports for it. There is no COM port in this record on purpose — the A/B boot of 2026-10-30 proved the
/// working client never asks for one.
/// </summary>
internal sealed record MtkUsbFace(int Vid, int Pid, string Name)
{
    internal string Identity => Vid.ToString("X4") + ":" + Pid.ToString("X4");

    internal string PidText => Pid.ToString("X4");

    public override string ToString() => Identity + " (" + (string.IsNullOrWhiteSpace(Name) ? "no name" : Name) + ")";
}

/// <summary>
/// The USB-level choices a raw-USB open can be told to make before and during its first bytes. All three are off by
/// default, so a run with an unedited note does exactly what RUN 27/28 did; all three are read from
/// <c>pacing.txt</c> per run (<see cref="MtkNativeBench.ReadKnobs"/>) so a trial does not cost a rebuild.
///
/// The first two exist because of B4.20: running the working client under <c>LIBUSB_DEBUG=4</c> (control boot
/// 2026-11-01 08:28:17, exit 0) showed its open as <c>get_configuration</c> → <c>set_configuration("configuration 1")</c>
/// → <c>claim_interface(0)</c> → <c>claim_interface(1)</c> — none of which its own command log can show. Both were
/// then put on the wire (RUN 29, RUN 30) and step 65 still refused, so the open is closed as an explanation.
///
/// The third exists because of RUN 32 (2026-11-01 10:19, our own client under the same <c>LIBUSB_DEBUG=4</c>): the
/// two traces are not comparable at the level of a single wait. Ours arms a bulk-IN URB for 50 ms and aborts it when
/// nothing arrives — 45 <c>errcode 995 → detected operation aborted</c> completions in one boot — while the working
/// client arms one URB at 1000 ms and aborts twice in the whole 9366-transfer boot. Same bytes, different USB.
/// </summary>
internal readonly struct MtkUsbOpenOptions
{
    internal static readonly MtkUsbOpenOptions Default = new(false, false);

    internal MtkUsbOpenOptions(bool setConfiguration, bool claimAllInterfaces, int readArmMs = 0)
    {
        SetConfiguration = setConfiguration;
        ClaimAllInterfaces = claimAllInterfaces;
        ReadArmMs = readArmMs > 0 ? readArmMs : 0;
    }

    /// <summary>Select the face's own configuration value before claiming it, as the working client does.</summary>
    internal bool SetConfiguration { get; }

    /// <summary>Claim every interface the face presents, not only the one carrying the bulk pair.</summary>
    internal bool ClaimAllInterfaces { get; }

    /// <summary>
    /// How long a read's bulk transfer is armed before the host gives up on it, in milliseconds. 0 keeps the
    /// transport's own poll (50 ms, cancel, re-submit). A non-zero value makes every wait one blocking transfer
    /// armed for that long — which is what the working client does: RUN 32 measured 45 host-side aborts
    /// (<c>errcode 995 → detected operation aborted</c>) in one of our boots against its 2 in 9366 transfers.
    /// </summary>
    internal int ReadArmMs { get; }

    internal bool IsPlain => !SetConfiguration && !ClaimAllInterfaces && ReadArmMs == 0;

    public override string ToString() => IsPlain ? "plain" :
        (SetConfiguration ? "SET_CONFIGURATION" : "no set_configuration") + ", " +
        (ClaimAllInterfaces ? "claim every interface" : "claim the data interface only") + ", " +
        (ReadArmMs > 0 ? "read armed " + ReadArmMs + " ms" : "read polled at 50 ms");
}

/// <summary>
/// The pipe mtkclient actually boots this phone through: the bulk endpoints of the USB device itself, claimed
/// from userspace (UsbDk), with no serial driver in between.
///
/// Why this exists. The recorded recipe was captured from a client whose own debug log says
/// <c>TX:fd / USB echo: length(0x1) / RX:fd</c> — it addresses endpoints, not a port. Our first two hardware
/// attempts (RUN 10, RUN 11) sent the same byte through the COM face Windows showed on the same phone and got
/// <c>0xfe</c> back both times, and a third run refused identically while the face sat still on the bus. Two
/// clients, one device, one command, two answers: the difference is the pipe.
///
/// Why UsbDk rather than plain libusb-1.0 (B4.10, from RUN 17 of 2026-10-31). RUN 17 opened <c>0E8D:0003</c>
/// through libusb-1.0 and then failed the claim with <c>libusb -3 ACCESS</c> — because the bundled 1.0.22's
/// default Windows backend is WinUSB, and WinUSB cannot take an interface a kernel driver owns (here
/// <c>usbser</c>, holding COM3, on the same phone the working client talks to). A probe on the same bundled DLL
/// measured the route that does: <c>libusb_set_option(ctx, LIBUSB_OPTION_USE_USBDK=1)</c> returns 0, and the
/// claim of a keyboard-driver-owned HID interface that followed succeeded. So the session is created first, the
/// option is applied to it before the first enumeration, and only then is anything listed or claimed.
///
/// What is deliberately not here: no endpoint numbers invented, no reset issued, and no byte written until the
/// recipe's first frame. The interface and endpoints are read from the device's own descriptor and chosen by
/// <see cref="MtkNativeBench.ChooseBulkPair"/>; a device that does not advertise a bulk pair is refused instead of
/// being poked at. Selecting a configuration and claiming the other interfaces are <see cref="MtkUsbOpenOptions"/>
/// rather than built-in behaviour, because B4.8 removed the configuration on a premise that B4.20 measured to be
/// wrong — the working client does send it — but the removal is only one run away from being right again, so the
/// default stays off until a hardware run says which way it goes.
/// </summary>
internal sealed class MtkUsbTransport : IMtkTransport, IMtkControlTransport
{
    internal const int MediaTekVid = 0x0E8D;

    /// <summary>The BROM face a boot may start on. RUN 6's capture and every VCOM sighting here say 0x0003.</summary>
    internal static readonly int[] BromPids = { 0x0003 };

    private const int ReadPollTimeoutMs = 50;
    private const int WriteChunkBytes = 16 * 1024;
    private const int ReadQueueBytes = 64 * 1024;
    private const int WriteTimeoutMs = 30000;

    /// <summary>
    /// <c>LIBUSB_OPTION_USE_USBDK</c> in the public libusb header for the 1.0.22 line this app ships. The value is
    /// the header's, not a guess: 1.0.22's option enum starts with LOG_LEVEL = 0, so USE_USBDK = 1 (libusb 1.0.24+,
    /// which mtkclient bundles, moved LOG_LEVEL to 2 and kept USE_USBDK at 1 — both agree on this number).
    /// </summary>
    private const int LibUsbOptionUseUsbDk = 1;

    private const string LibUsbDll = "libusb-1.0";

    /// <summary>
    /// The one native call LibUsbDotNet does not expose: choosing the Windows backend on a live context. Without
    /// it the context is WinUSB, which is RUN 17's <c>ErrorAccess</c>; there is no managed API for it in 2.2.0.
    /// </summary>
    [DllImport(LibUsbDll, ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libusb_set_option(IntPtr ctx, int option);

    private readonly MonoUsbDeviceHandle _handle;
    private readonly int[] _claimedInterfaces;
    private readonly int _writeEndpoint;
    private readonly int _readEndpoint;
    private readonly IntPtr _readBuffer;
    private readonly MtkUsbFace _face;

    /// <summary>
    /// How long one wait for bytes arms its bulk transfer. 0 means the transport polls (<see cref="ReadPollTimeoutMs"/>);
    /// any other value comes from the run's <c>arm=</c> note and makes the wait a single blocking transfer, the way the
    /// working client reads. See <see cref="MtkUsbOpenOptions.ReadArmMs"/>.
    /// </summary>
    private readonly int _readArmMs;

    // Not readonly: the face is greeted once on open, and what it said before we spoke belongs in the description
    // the boot log carries.
    private string _description;

    // Bytes already taken off the wire but not yet asked for. A bulk read returns whole transfers, so anything
    // left here belongs to the reply the session is still waiting for.
    private readonly byte[] _queue = new byte[ReadQueueBytes];
    private int _queued;
    private bool _disposed;

    private MtkUsbTransport(MonoUsbDeviceHandle handle, int[] claimedInterfaces, int writeEndpoint, int readEndpoint,
        MtkUsbFace face, string description, int readArmMs = 0)
    {
        _handle = handle;
        _claimedInterfaces = claimedInterfaces ?? Array.Empty<int>();
        _writeEndpoint = writeEndpoint;
        _readEndpoint = readEndpoint;
        _readBuffer = Marshal.AllocHGlobal(ReadQueueBytes);
        _face = face;
        _description = description;
        _readArmMs = readArmMs > 0 ? readArmMs : 0;
    }

    /// <summary>The wait a run asks the bus for: the armed single transfer when the note names one, else the 50 ms poll.</summary>
    private int ReadWaitMs => _readArmMs > 0 ? _readArmMs : ReadPollTimeoutMs;

    // ---------------------------------------------------------------------------------------------------------
    // Session: one libusb-1.0 context for the whole process, switched to UsbDk before the first enumeration.
    // ---------------------------------------------------------------------------------------------------------

    private static readonly object LibUsbGate = new object();
    private static MonoUsbSessionHandle _session;
    private static MonoUsbProfileList _profiles;
    private static DateTime _libUsbRefreshedUtc = DateTime.MinValue;
    private static int _usbDkRc = int.MinValue;

    // Only a session that was actually started is stopped at shutdown: a user who never opened a raw-USB face must
    // not have a libusb context torn down underneath whatever else is running.
    private static bool _libUsbStarted;

    /// <summary>
    /// The libusb-1.0 context, created once and switched to UsbDk. B4.9 called <c>MonoUsbDevice.Init()</c> on every
    /// refresh, which builds a fresh context each time; RUN 17's log shows what that costs — after ~25 attempts the
    /// phone had dropped out of the list altogether and every later claim could only say "not listed". One context
    /// refreshed in place has none of that, and it is also the only way a backend option can be applied at all,
    /// because <c>libusb_set_option(NULL, …)</c> is unusable on 1.0.22 (it returns -5 for USE_USBDK and crashes for
    /// LOG_LEVEL — measured).
    /// </summary>
    private static bool EnsureSessionLocked()
    {
        if (_session != null && !_session.IsInvalid && _profiles != null) return true;
        ReleaseLocked();

        _session = new MonoUsbSessionHandle();
        if (_session.IsInvalid)
        {
            int rc = (int)MonoUsbSessionHandle.LastErrorCode;
            ReleaseLocked();
            _usbDkRc = int.MinValue;
            throw new MtkDaProtocolException("libusb-1.0 could not create a session (" + rc + " " +
                MtkNativeBench.UsbErrorText(rc) + ": " + MonoUsbSessionHandle.LastErrorString + "). Raw USB needs " +
                "the libusb-1.0.dll that ships beside the tool; nothing was sent to any phone.");
        }

        _usbDkRc = libusb_set_option(_session.DangerousGetHandle(), LibUsbOptionUseUsbDk);
        _profiles = new MonoUsbProfileList();
        _libUsbRefreshedUtc = DateTime.MinValue;
        _libUsbStarted = true;
        return true;
    }

    /// <summary>
    /// The device list, rebuilt at most every 400 ms. Polling faster cannot learn anything new — a USB enumeration
    /// takes longer than the gap — and each rebuild is a full bus walk. The caller must hold <see cref="LibUsbGate"/>.
    /// </summary>
    private static MonoUsbProfileList ProfilesLocked()
    {
        if (!EnsureSessionLocked()) return null;
        if (DateTime.UtcNow - _libUsbRefreshedUtc >= TimeSpan.FromMilliseconds(400))
        {
            try { _profiles.Refresh(_session); }
            catch { _profiles.Close(); _profiles = new MonoUsbProfileList(); }
            _libUsbRefreshedUtc = DateTime.UtcNow;
        }
        return _profiles;
    }

    private static void ReleaseLocked()
    {
        try { _profiles?.Close(); } catch { /* the list is already gone */ }
        _profiles = null;
        try { _session?.Dispose(); } catch { /* libusb_exit already ran, or never init'ed */ }
        _session = null;
        _libUsbRefreshedUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Every MediaTek node on the bus right now, whether or not Windows gave it a COM port.
    ///
    /// Only the UsbDk-switched libusb-1.0 list is asked. RUN 16 measured the legacy libusb-win32 path as a dead end
    /// (it opens <c>0E8D:0003</c> and then fails every claim of its CDC data interface with
    /// <c>DeviceIoControl 0x222054 → ERROR_BAD_COMMAND</c>), and RUN 17 measured that libusb-1.0 alone — default
    /// WinUSB backend — cannot claim the same interface while <c>usbser</c> holds it. Merging views would also
    /// double-count one phone and turn it into a false "ambiguous" refusal.
    /// </summary>
    internal static IReadOnlyList<MtkUsbFace> EnumerateFaces()
    {
        try
        {
            lock (LibUsbGate)
            {
                MonoUsbProfileList profiles = ProfilesLocked();
                if (profiles == null) return Array.Empty<MtkUsbFace>();

                var found = new List<MtkUsbFace>();
                for (int i = 0; i < profiles.Count; i++)
                {
                    if (!TryIdentity(profiles[i], out ushort vid, out ushort pid) || vid != MediaTekVid) continue;
                    found.Add(new MtkUsbFace(MediaTekVid, pid, ""));
                }
                found.Sort((a, b) => string.CompareOrdinal(a.PidText, b.PidText));
                return found;
            }
        }
        catch
        {
            // A diagnostic list is never allowed to replace a real refusal with a fake one: "nothing listed" is the
            // honest answer when the session itself cannot be read.
            return Array.Empty<MtkUsbFace>();
        }
    }

    /// <summary>
    /// The VID/PID of one libusb-1.0 profile, read from its own descriptor. A device that leaves the bus between the
    /// list being built and this read answers with an exception instead of an identity — that is a "not here", not
    /// an error worth stopping a boot for.
    /// </summary>
    private static bool TryIdentity(MonoUsbProfile profile, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        try
        {
            MonoUsbDeviceDescriptor descriptor = profile?.DeviceDescriptor;
            if (descriptor == null) return false;
            vid = (ushort)descriptor.VendorID;
            pid = (ushort)descriptor.ProductID;
            return vid != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Wait for a face the stage is allowed to start on. Ambiguity is refused, not guessed, exactly as on the
    /// serial side: two phones on the bus means two DAs, and writing to the wrong one is the brick this whole
    /// design exists to avoid.
    /// </summary>
    internal static async Task<MtkUsbFace> WaitForFaceAsync(int[] acceptedPids, TimeSpan timeout,
        Action<string> log, CancellationToken ct)
    {
        var accepted = (acceptedPids ?? Array.Empty<int>()).ToHashSet();
        var deadline = DateTime.UtcNow.Add(timeout);
        string lastNote = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = EnumerateFaces().Where(f => accepted.Contains(f.Pid)).ToList();
            if (candidates.Count == 1)
            {
                MtkUsbFace picked = candidates[0];
                log?.Invoke(" [+] MTK USB face " + picked + " — " + MtkNativeBench.FaceBootReadiness(picked.PidText) +
                    " as a boot start (" + MtkNativeBench.DescribeFace(picked.PidText) + ")");
                return picked;
            }

            string note = candidates.Count == 0
                ? "waiting for a MediaTek USB face (" + string.Join("/", accepted.Select(a => a.ToString("X4"))) + ")"
                : "ambiguous: " + string.Join(", ", candidates.Select(c => c.Identity));
            if (note != lastNote)
            {
                log?.Invoke(" MTK USB — " + note);
                lastNote = note;
            }
            if (DateTime.UtcNow >= deadline)
                throw new MtkDaProtocolException("No single MediaTek USB face " +
                    string.Join("/", accepted.Select(a => "0e8d:" + a.ToString("X4"))) + " appeared within " +
                    (int)timeout.TotalSeconds + " s" + (candidates.Count > 1 ? " (still ambiguous: " + candidates.Count + " devices)" : "") +
                    ". Power the phone off, hold BOTH volume keys and plug it in so it enters BROM, and close any " +
                    "other tool that has the device claimed. The list was read through libusb-1.0 with " +
                    MtkNativeBench.UsbBackendText(UsbDkRc));
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The backend switch this process got, read for a refusal line. Thread-safe enough: it is only ever written under the gate.</summary>
    private static int UsbDkRc { get { lock (LibUsbGate) return _usbDkRc; } }

    /// <summary>
    /// How long a face whose data interface cannot be claimed yet is retried, and how long between attempts.
    ///
    /// B4.8 (RUN 15, 2026-10-31): the first open after enumeration lost the claim race, and the old fallback broke
    /// that tie by selecting the configuration — a control transfer that re-initialises the device's endpoints.
    /// Every <c>0xfd</c> sent afterwards came back <c>0xfe</c> (four asks, one every 800 ms). Retrying the open,
    /// which costs the device nothing it has not already seen, is still the default.
    ///
    /// B4.8's stated reason — "the working client never selects a configuration" — was read off its command log and
    /// is <strong>wrong</strong>: that log prints commands, not USB setup. Under <c>LIBUSB_DEBUG=4</c> the same
    /// client (control boot 2026-11-01 08:28:17, exit 0, full GPT) shows <c>libusb_set_configuration</c>
    /// "configuration 1" once, before its first command byte, and two <c>libusb_claim_interface</c> calls. Selecting
    /// the configuration is therefore back on the table as the <c>setconfig</c> knob, and one hardware run with it on
    /// decides whether step 65 was ever about it.
    /// </summary>
    private static readonly TimeSpan ClaimBudget = TimeSpan.FromSeconds(12);
    private const int ClaimRetryDelayMs = 150;

    /// <summary>
    /// Claim the data interface of <paramref name="face"/> and hand back the endpoint pipe.
    ///
    /// By default the claim is the only step: no configuration is selected, no reset is issued, and no byte is
    /// written until the recipe's first frame. A node that has just enumerated can be busy with its own driver for a
    /// moment, so a failed claim closes the handle and retries on a fresh one for <see cref="ClaimBudget"/> — the
    /// phone never sees any of it. When the budget is spent the refusal carries the libusb error, the device's
    /// configuration and the backend that was in use, so the next run can tell a busy node from a driver that is not
    /// mounted at all.
    ///
    /// <paramref name="options"/> adds the two USB-level actions B4.20 measured on the working client — a
    /// <c>SET_CONFIGURATION</c> for the value the device itself reports, and claims for the interfaces that are not
    /// carrying our endpoints. Both are opt-in per run through <c>pacing.txt</c>.
    ///
    /// Listens for a quarter second before returning so the description can say whether the face greeted us —
    /// the one thing that tells a silent BROM from a different protocol answering on the same PID.
    /// </summary>
    internal static MtkUsbTransport Open(MtkUsbFace face, Action<string> log = null) =>
        OpenAsync(face, log, CancellationToken.None, new MtkUsbOpenOptions(false, false)).GetAwaiter().GetResult();

    internal static MtkUsbTransport Open(MtkUsbFace face, Action<string> log, MtkUsbOpenOptions options) =>
        OpenAsync(face, log, CancellationToken.None, options).GetAwaiter().GetResult();

    internal static Task<MtkUsbTransport> OpenAsync(MtkUsbFace face, Action<string> log, CancellationToken ct) =>
        OpenAsync(face, log, ct, new MtkUsbOpenOptions(false, false));

    internal static async Task<MtkUsbTransport> OpenAsync(MtkUsbFace face, Action<string> log, CancellationToken ct,
        MtkUsbOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(face);

        var started = DateTime.UtcNow;
        for (int attempt = 1; ; attempt++)
        {
            MtkUsbTransport transport = TryOpenOnce(face, log, options, out string failure);
            if (transport != null)
            {
                if (attempt > 1)
                    log?.Invoke(" MTK USB — the data interface was taken on claim attempt " + attempt + " of " + face +
                        " after " + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms of closing and " +
                        "re-opening the node (open setup: " + options + "; no command byte had been sent yet)");
                return transport;
            }

            string note = MtkNativeBench.UsbClaimRetryNote(attempt, DateTime.UtcNow - started, ClaimBudget, failure);
            if (note == null)
                throw new MtkDaProtocolException(face + " could not be used over raw USB: " + failure + ". Nothing " +
                    "was sent — no byte of the recipe reached the phone. The pipe is libusb-1.0 claimed from userspace, " +
                    "which is the only route measured to take an interface a kernel driver owns (WINUSB cannot: RUN " +
                    "17's libusb -3 ACCESS is exactly that), and a refusal here therefore means either that libusb-1.0 " +
                    "could not switch to UsbDk on this machine, or that another client still holds the phone — close " +
                    "mtkclient, SP Flash Tool and any Unlock Tool session, then retry.");
            log?.Invoke(note);
            await Task.Delay(ClaimRetryDelayMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One attempt: find the profile, open a handle, read the bulk pair out of the descriptor, select the device's
    /// own configuration when the run asks for it, then claim. Nothing of the recipe is sent here. Returns null with
    /// the measured reason when the node is not claimable yet (the caller retries); a face that cannot work at all
    /// throws instead, because retrying it would only delay a refusal that is already certain.
    /// </summary>
    private static MtkUsbTransport TryOpenOnce(MtkUsbFace face, Action<string> log, MtkUsbOpenOptions options,
        out string failure)
    {
        failure = null;
        MonoUsbDeviceHandle handle = null;
        MtkNativeBench.MtkBulkPair pair;
        List<int> claimedInterfaces;
        string how;
        try
        {
            lock (LibUsbGate)
            {
                MonoUsbProfileList profiles = ProfilesLocked();
                if (profiles == null)
                {
                    failure = "the libusb-1.0 session could not be read";
                    return null;
                }

                MonoUsbProfile profile = null;
                for (int i = 0; i < profiles.Count; i++)
                {
                    if (!TryIdentity(profiles[i], out ushort vid, out ushort pid)) continue;
                    if (vid == face.Vid && pid == face.Pid) { profile = profiles[i]; break; }
                }
                if (profile == null)
                {
                    failure = "libusb-1.0 (with " + MtkNativeBench.UsbBackendText(_usbDkRc) + ") did not list " + face +
                              " — another client may hold it, or its driver is still binding";
                    return null;
                }

                how = "the libusb-1.0 session, " + MtkNativeBench.UsbBackendText(_usbDkRc);

                handle = profile.OpenDeviceHandle();
                if (handle == null || handle.IsInvalid)
                {
                    int openRc = (int)MonoUsbSessionHandle.LastErrorCode;
                    failure = "libusb-1.0 listed " + face + " but refused to open it (libusb " + openRc + " " +
                              MtkNativeBench.UsbErrorText(openRc) + "; backend " + BackendNameLocked() + ")";
                    try { handle?.Dispose(); } catch { /* never opened */ }
                    return null;
                }

                MonoUsbProfileHandle profileHandle = MonoUsbApi.GetDevice(handle);
                if (profileHandle == null)
                {
                    failure = "libusb-1.0 opened " + face + " but would not hand back its device profile, so its " +
                              "descriptor cannot be read (backend " + BackendNameLocked() + ")";
                    handle.Dispose();
                    return null;
                }
                int cfgRc = MonoUsbApi.GetActiveConfigDescriptor(profileHandle, out var configHandle);
                if (cfgRc != 0)
                {
                    failure = "the active configuration of " + face + " could not be read (libusb " + cfgRc + " " +
                              MtkNativeBench.UsbErrorText(cfgRc) + "; backend " + BackendNameLocked() + ")";
                    handle.Dispose();
                    return null;
                }

                var candidates = new List<MtkNativeBench.MtkBulkPair>();
                var presentInterfaces = new List<int>();
                var config = new MonoUsbConfigDescriptor(configHandle);
                foreach (MonoUsbInterface itf in config.InterfaceList)
                {
                    foreach (MonoUsbAltInterfaceDescriptor alt in itf.AltInterfaceList)
                    {
                        // Alternate settings are alternative wirings of the same interface; only the one the device
                        // currently presents (alt 0) has endpoints that exist on the wire right now.
                        if (alt.bAlternateSetting != 0) continue;
                        int number = alt.bInterfaceNumber;
                        if (!presentInterfaces.Contains(number)) presentInterfaces.Add(number);
                        int inEp = 0, outEp = 0;
                        foreach (MonoUsbEndpointDescriptor endpoint in alt.EndpointList)
                        {
                            // bmAttributes bits 0..1 are the transfer type; 0x02 is bulk. Interrupt (0x03) is the CDC
                            // notification endpoint on the communication interface and carries no data at all.
                            if ((endpoint.bmAttributes & 0x03) != 0x02) continue;
                            byte address = endpoint.bEndpointAddress;
                            if ((address & 0x80) != 0) { if (inEp == 0) inEp = address; }
                            else if (outEp == 0) outEp = address;
                        }
                        if (inEp != 0 && outEp != 0)
                            candidates.Add(new MtkNativeBench.MtkBulkPair(alt.bInterfaceNumber,
                                (byte)alt.bInterfaceClass, outEp, inEp));
                    }
                }

                MtkNativeBench.MtkBulkPair? chosen = MtkNativeBench.ChooseBulkPair(candidates);
                if (chosen == null)
                {
                    handle.Dispose();
                    throw new MtkDaProtocolException(face + " advertises no interface with both a bulk IN and a bulk " +
                        "OUT endpoint (" + candidates.Count + " candidate(s)). Nothing was sent: an endpoint address " +
                        "guessed at is an endpoint address that can be written to.");
                }
                pair = chosen.Value;

                // B4.20 — the one difference the working client's own API trace shows below its command log. It
                // reads the configuration, selects one, and claims both of the face's interfaces before its first
                // command byte. The value sent is always one the device reported (its active configuration, or the
                // bConfigurationValue of the descriptor libusb just handed back), never a number picked here.
                if (options.SetConfiguration)
                {
                    int active = 0;
                    int getRc = MonoUsbApi.GetConfiguration(handle, ref active);
                    if (getRc != 0)
                    {
                        failure = "the active configuration of " + face + " could not be read for the setconfig run " +
                                  "(libusb " + getRc + " " + MtkNativeBench.UsbErrorText(getRc) + "; backend " +
                                  BackendNameLocked() + ")";
                        handle.Dispose();
                        return null;
                    }

                    int select = MtkNativeBench.ConfigValueToSelect(true, active, config.bConfigurationValue);
                    if (select <= 0)
                    {
                        handle.Dispose();
                        throw new MtkDaProtocolException(face + " was run with setconfig=on but offered no " +
                            "configuration to select: it reported active configuration " + active + " and a descriptor " +
                            "configuration value of " + config.bConfigurationValue + ". A number invented here would be " +
                            "a control request sent to a phone in download mode, so the run is refused instead. " +
                            "Nothing was sent to the phone.");
                    }

                    int setRc = MonoUsbApi.SetConfiguration(handle, select);
                    if (setRc != 0)
                    {
                        failure = "SET_CONFIGURATION(" + select + ") on " + face + " was refused (libusb " + setRc +
                                  " " + MtkNativeBench.UsbErrorText(setRc) + "; the device reported active " + active +
                                  ", backend " + BackendNameLocked() + ")";
                        handle.Dispose();
                        return null;
                    }
                    how += ", SET_CONFIGURATION(" + select + ") sent on the control endpoint (the device reported " +
                           active + " active, descriptor value " + config.bConfigurationValue + ")";
                }

                var toClaim = MtkNativeBench.InterfacesToClaim(pair.InterfaceNumber, presentInterfaces,
                    options.ClaimAllInterfaces);
                var claimRcs = new List<string>();
                claimedInterfaces = new List<int>();
                foreach (int number in toClaim)
                {
                    bool carriesData = number == pair.InterfaceNumber;
                    int claimRc = MonoUsbApi.ClaimInterface(handle, number);
                    if (claimRc == 0) claimedInterfaces.Add(number);
                    claimRcs.Add("iface " + number + (carriesData ? " (data)" : " (extra)") + " rc " + claimRc);

                    // Only the interface holding the bulk pair can refuse the boot; the others are the working
                    // client's own extra claim, and a driver that keeps one is not a reason to stop a session that
                    // has the endpoints it needs.
                    if (carriesData && claimRc != 0)
                    {
                        int configuration = 0;
                        int readCfgRc = MonoUsbApi.GetConfiguration(handle, ref configuration);
                        failure = "the data interface " + pair.InterfaceNumber + " is still held (libusb " + claimRc + " " +
                                  MtkNativeBench.UsbErrorText(claimRc) + "; device configuration " +
                                  (readCfgRc == 0 ? configuration.ToString() : "unknown") + ", backend " +
                                  BackendNameLocked() + ")";
                        // Whatever the extra claims already took is given back before the handle closes: the retry
                        // opens the same node, and a claim left behind by a refused attempt is one more reason the
                        // next attempt could fail for a cause that is ours.
                        foreach (int given in claimedInterfaces)
                        {
                            try { MonoUsbApi.ReleaseInterface(handle, given); } catch { /* the node is leaving the bus */ }
                        }
                        handle.Dispose();
                        return null;
                    }
                }
                if (options.ClaimAllInterfaces)
                    how += ", claims: " + string.Join("; ", claimRcs);
            }
        }
        catch (MtkDaProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try { handle?.Dispose(); } catch { /* never opened, or already gone */ }
            failure = "the libusb-1.0 session threw (" + ex.GetType().Name + ": " + ex.Message + ")";
            return null;
        }

        log?.Invoke(MtkNativeBench.UsbOpenVia(face.ToString(), how));

        var transport = new MtkUsbTransport(handle, claimedInterfaces.ToArray(), pair.WriteEndpoint, pair.ReadEndpoint,
            face, MtkNativeBench.DescribeUsbOpen(face.Vid, face.Pid, pair, Array.Empty<byte>()), options.ReadArmMs);

        // What does this face say before we ask it anything? A BROM is silent; anything else that answers is
        // speaking on its own terms and the log line has to carry that evidence into the next run.
        byte[] greeted;
        try
        {
            greeted = transport.Listen(250);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
        transport._description = MtkNativeBench.DescribeUsbOpen(face.Vid, face.Pid, pair, greeted);
        return transport;
    }

    private static string BackendNameLocked() => _usbDkRc == 0 ? "UsbDk" : "WinUSB";

    /// <summary>
    /// Stop the libusb-1.0 session. Its event loop is a foreground thread, so a process that has ever opened this
    /// backend never exits on its own (measured on a probe: the window closes and the process stays). Called once
    /// from the application's shutdown path; a later open simply re-initialises the session.
    /// </summary>
    internal static void ReleaseLibUsbSession()
    {
        if (!_libUsbStarted) return;
        lock (LibUsbGate)
        {
            ReleaseLocked();
            _libUsbStarted = false;
            _usbDkRc = int.MinValue;
        }
    }

    public string Description => _description;

    /// <summary>
    /// Ask the bus — not the wire — which MediaTek faces exist now. Must never throw: a diagnostic that replaces a
    /// real refusal with a fake one is worse than no diagnostic.
    /// </summary>
    public string BusAfterRefusal()
    {
        try
        {
            return MtkNativeBench.DescribeBusAfterRefusalUsb(_face.PidText,
                EnumerateFaces().Select(f => f.PidText).ToList());
        }
        catch (Exception ex)
        {
            return "the USB bus could not be re-read (" + ex.GetType().Name + ": " + ex.Message + ")";
        }
    }

    /// <summary>
    /// On a plain run this is non-blocking by contract: it may only ever return what a short wait already produced.
    /// With <c>arm=</c> in the note the wait is the point — one bulk transfer, armed for that many milliseconds and
    /// left alive until the device answers, which is how the working client reads instead of polling and aborting
    /// (RUN 32: our 45 <c>errcode 995</c> cancellations against its 2 in 9366 transfers).
    /// </summary>
    public int BytesAvailable
    {
        get
        {
            if (_queued > 0) return _queued;
            PullOnce(ReadWaitMs);
            return _queued;
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || count < 0 || offset + count > data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "The write extent " + offset + "+" + count +
                " does not fit a " + data.Length + "-byte buffer.");

        int sent = 0;
        while (sent < count)
        {
            int piece = Math.Min(WriteChunkBytes, count - sent);
            GCHandle pin = default;
            try
            {
                pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                IntPtr at = Marshal.UnsafeAddrOfPinnedArrayElement(data, offset + sent);
                int rc = MonoUsbApi.BulkTransfer(_handle, (byte)_writeEndpoint, at, piece, out int written,
                    WriteTimeoutMs);
                if (rc != 0 || written != piece)
                    throw WriteFailed(count, "libusb " + rc + " " + MtkNativeBench.UsbErrorText(rc) + ", " + written +
                        "/" + piece + " byte(s) accepted");
                sent += written;
            }
            catch (Exception ex) when (ex is not MtkDaProtocolException)
            {
                throw WriteFailed(count, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (pin.IsAllocated) pin.Free();
            }
        }
    }

    private MtkDaProtocolException WriteFailed(int count, string detail) =>
        new("Writing " + count + " byte(s) to " + _description + " failed (" + detail +
            "). The device may have left BROM; the boot was stopped with the recipe half-applied.") { TransportFailure = true };

    /// <summary>
    /// One endpoint-zero control transfer, the pipe RUN 38 (2026-11-01 15:36) showed the working client speaking
    /// on before every pre-download jump: a CDC SET_LINE_CODING the device accepts and a vendor descriptor read it
    /// stalls. Setup and data stage go out as the single atomic call the capture recorded, and the libusb contract
    /// is kept: the return is bytes transferred, or a negative error code — a stall the device gave back included.
    /// </summary>
    public int ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data, int timeoutMs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MtkUsbTransport));
        GCHandle pin = default;
        try
        {
            IntPtr buffer = IntPtr.Zero;
            if (data is { Length: > 0 })
            {
                pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                buffer = Marshal.UnsafeAddrOfPinnedArrayElement(data, 0);
            }
            return MonoUsbApi.ControlTransfer(_handle, bmRequestType, bRequest, (short)wValue, (short)wIndex,
                buffer, (short)(data?.Length ?? 0), timeoutMs);
        }
        finally
        {
            if (pin.IsAllocated) pin.Free();
        }
    }

    /// <summary>
    /// Drop a stalled endpoint pair. <c>libusb -9</c> is <c>EPIPE</c> — the transfer ended in a stall — and a fresh
    /// handle does not clear that state on the device; only the standard CLEAR_FEATURE does, and without it every
    /// later write on a re-opened node fails the same way. Returns the two libusb codes so a retry line can carry
    /// them; on a face that is simply gone they are both an error, which is also the honest answer.
    /// </summary>
    internal (int Out, int In) ClearHalt()
    {
        if (_disposed) return (-4, -4);
        int outRc = int.MinValue, inRc = int.MinValue;
        try { outRc = MonoUsbApi.ClearHalt(_handle, (byte)_writeEndpoint); } catch { /* the node is already gone */ }
        try { inRc = MonoUsbApi.ClearHalt(_handle, (byte)_readEndpoint); } catch { /* the node is already gone */ }
        return (outRc, inRc);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;
        if (_queued == 0) PullOnce(ReadWaitMs);
        if (_queued == 0) return 0;

        int take = Math.Min(count, _queued);
        Buffer.BlockCopy(_queue, 0, buffer, offset, take);
        _queued -= take;
        if (_queued > 0) Buffer.BlockCopy(_queue, take, _queue, 0, _queued);
        return take;
    }

    /// <summary>
    /// One bounded bulk read appended to the queue. Never throws: the caller is a poll loop.
    ///
    /// A timeout is the normal answer for a silent BROM, so it is not an error; any other code is treated the same
    /// way because a poll cannot act on it — the boot's own read timeout is what refuses the run, and its bus note
    /// says whether the phone is still there.
    /// </summary>
    private void PullOnce(int timeoutMs)
    {
        if (_disposed || _queued >= _queue.Length) return;
        try
        {
            int rc = MonoUsbApi.BulkTransfer(_handle, (byte)_readEndpoint, _readBuffer, _queue.Length - _queued,
                out int got, timeoutMs);
            if (rc == 0 && got > 0)
            {
                Marshal.Copy(_readBuffer, _queue, _queued, got);
                _queued += got;
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            _queued = 0;    // the face is gone; the read loop times out and the boot refuses with the bus note
        }
    }

    /// <summary>
    /// Drain whatever the face says unprompted for the given window. The bytes are removed from the queue on
    /// purpose: they are evidence about what kind of device is answering, not the reply the recipe's first
    /// expectation is waiting for, and leaving them in line would have the echo check read a greeting as an answer.
    ///
    /// This keeps the fixed 50 ms poll even when the note sets <c>arm=</c>: the window is a bounded listen at open,
    /// before a single byte has been sent, and letting one armed transfer swallow it would change the pace of the
    /// open as well as the pace of the boot — RUN 33 is one variable, not two.
    /// </summary>
    private byte[] Listen(int windowMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
        while (DateTime.UtcNow < deadline && _queued == 0) PullOnce(ReadPollTimeoutMs);
        if (_queued == 0) return Array.Empty<byte>();

        var heard = new byte[_queued];
        Buffer.BlockCopy(_queue, 0, heard, 0, _queued);
        _queued = 0;
        return heard;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Release in the order the claims went out. A face that left the bus needs nothing: the kernel drops its
        // claims with the devnode, so every release here is best-effort and its error is not worth a log line.
        foreach (int claimed in _claimedInterfaces)
        {
            try { MonoUsbApi.ReleaseInterface(_handle, claimed); } catch { /* the node is already off the bus */ }
        }
        try { _handle?.Dispose(); } catch { /* the cable is already out */ }
        if (_readBuffer != IntPtr.Zero) { try { Marshal.FreeHGlobal(_readBuffer); } catch { /* nothing to free */ } }
    }
}

/// <summary>
/// The face proof's second pipe.
///
/// RUN 18 (2026-10-31 10:57) proofed the transport and then died on it: six one-byte asks answered <c>0xFE</c>, the
/// seventh write came back <c>libusb -9 pipe error</c>, and the boot was refused with "the recipe half-applied" even
/// though nothing but the one read-only byte had ever been sent. The working client is judged by the same phone
/// minutes later: it reports <c>Handshake failed, retrying...</c>, closes the node, re-detects it, and only then gets
/// <c>TX:fd / RX:fd / RX:07660000</c>. So a dead handle between asks is the working client's ordinary retry, not the
/// end of a boot — this class re-claims the same face so the proof can spend another attempt on it.
/// </summary>
internal sealed class MtkUsbBromRecovery : IMtkBromRecovery
{
    private readonly int _pid;
    private readonly Action<string> _log;
    private readonly MtkUsbOpenOptions _options;

    internal MtkUsbBromRecovery(int pid, Action<string> log) : this(pid, log, new MtkUsbOpenOptions(false, false))
    {
    }

    internal MtkUsbBromRecovery(int pid, Action<string> log, MtkUsbOpenOptions options)
    {
        _pid = pid;
        _log = log;
        _options = options;
    }

    /// <summary>
    /// Wait for the face again, claim it, and clear the endpoint halt a stall leaves behind. A face that does not
    /// come back answers with null; a face that returns but cannot be claimed throws the claim's own message, which
    /// already names the libusb error, the configuration and the backend.
    /// </summary>
    public IMtkTransport Reopen(int waitMs)
    {
        MtkUsbFace face;
        try
        {
            face = MtkUsbTransport.WaitForFaceAsync(new[] { _pid }, TimeSpan.FromMilliseconds(Math.Max(0, waitMs)),
                _log, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (MtkDaProtocolException)
        {
            return null;
        }

        MtkUsbTransport next = MtkUsbTransport.Open(face, _log, _options);
        var (outRc, inRc) = next.ClearHalt();
        _log?.Invoke(" MTK USB — the BROM pipe was reclaimed on " + face + " and its endpoint halt cleared " +
            "(OUT rc " + outRc + ", IN rc " + inRc + "; 0 means the endpoint was not stalled; open setup: " +
            _options + ")");
        return next;
    }
}

#nullable disable

namespace PMKUnlocker.MediaTek;

/// <summary>
/// The byte pipe a <see cref="MtkDaSession"/> talks through. Kept tiny and interface-shaped on purpose:
/// the boot recipe has to be assertable byte-for-byte against a recorded capture in the regression run,
/// because a phone is not available in CI and a wrong boot is a bricked phone rather than a clean error.
/// </summary>
internal interface IMtkTransport : IDisposable
{
    /// <summary>What this pipe is, for the log ("COM7 0E8D:0003").</summary>
    string Description { get; }

    int BytesAvailable { get; }

    void Write(byte[] data, int offset, int count);

    /// <summary>Non-blocking read; returns 0 when nothing is waiting.</summary>
    int Read(byte[] buffer, int offset, int count);
}

/// <summary>
/// A persistent MediaTek DA session — ROADMAP Phase 1.75 "Path B".
///
/// B2 is the boot half: prove the BROM face, replay the recorded bypass, upload DA1, jump into it, bring
/// the DA up through EMI settings, DA2 and the DA extension, and stop the moment anything deviates from
/// what a working boot did. The read/write/erase half is B3 and sits on top of the primitives here.
///
/// The whole design rests on one rule from the captures: no foreign DA can be attached to (RUN 5), so
/// this class owns the entire boot and must never assume a device is already in DA mode. A phone that
/// enumerates on the DA face but was not walked into BROM lies about what it is, which is why the
/// HW-code reply is verified against the recipe instead of being logged and ignored.
/// </summary>
internal sealed partial class MtkDaSession : IDisposable
{
    private readonly IMtkTransport _transport;
    private readonly MtkBootProfile _profile;
    private readonly Action<string> _log;
    private readonly int _bromTimeoutMs;
    private readonly int _daTimeoutMs;
    private readonly int _settleTimeoutMs;

    // Set when DA1 answers 0xc0 after the jump. Kept separate from DaIsReady: an alive DA1 is not a DA that
    // serves extents, and the DA stage only begins once the first one has been proven to be running.
    private bool _da1Alive;

    internal ushort DeviceHwCode { get; private set; }
    internal bool DaIsReady { get; private set; }
    internal string TransportDescription => _transport.Description;

    private MtkDaSession(IMtkTransport transport, MtkBootProfile profile, Action<string> log,
        int bromTimeoutMs, int daTimeoutMs, int settleTimeoutMs)
    {
        _transport = transport;
        _profile = profile;
        _log = log;
        _bromTimeoutMs = bromTimeoutMs;
        _daTimeoutMs = daTimeoutMs;
        _settleTimeoutMs = settleTimeoutMs;
    }

    /// <summary>
    /// Boot a device from BROM to a ready DA by replaying the recorded recipe. Nothing is sent before the
    /// recipe and its blobs have been verified (<see cref="MtkBootProfile.Load"/>), and any frame that
    /// arrives differently than recorded aborts the boot.
    /// </summary>
    internal static async Task<MtkDaSession> BootAsync(IMtkTransport transport, MtkBootProfile profile,
        Action<string> log = null, CancellationToken ct = default,
        int bromTimeoutMs = 1500, int daTimeoutMs = 8000, int settleTimeoutMs = 20000)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(profile);
        var session = new MtkDaSession(transport, profile, log, bromTimeoutMs, daTimeoutMs, settleTimeoutMs);
        try
        {
            await session.BootAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        DaIsReady = false;
        _da1Alive = false;
        try { _transport.Dispose(); } catch { /* the port is already gone; the boot is over either way */ }
    }

    // ---- the boot -------------------------------------------------------------------------------

    private async Task BootAsync(CancellationToken ct)
    {
        Say(" BROM: " + _transport.Description + " — recipe " + _profile.Chipset +
            " (" + _profile.BromSteps.Count + " BROM steps, " + _profile.DaSteps.Count + " DA steps)");

        await RunStageAsync(_profile.BromSteps, isDa: false, ct).ConfigureAwait(false);
        if (!_da1Alive)
            throw new MtkDaProtocolException("The BROM walk finished without the DA1 alive signal (0xc0). " +
                "The device is not in the state this recipe was recorded from, so the boot was stopped.");

        await RunStageAsync(_profile.DaSteps, isDa: true, ct).ConfigureAwait(false);
        DaIsReady = true;
        Say("[+] MTK DA ready — " + _profile.Chipset + " (HW " + DeviceHwCode.ToString("X4") + "), boot replayed from recipe.");
    }

    private async Task RunStageAsync(IReadOnlyList<MtkStep> steps, bool isDa, CancellationToken ct)
    {
        byte[] lastTx = Array.Empty<byte>();
        int timeoutMs = isDa ? _daTimeoutMs : _bromTimeoutMs;

        for (int index = 0; index < steps.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            MtkStep step = steps[index];

            switch (step.Kind)
            {
                case MtkStepKind.Tx:
                    _transport.Write(step.Payload, 0, step.Payload.Length);
                    lastTx = step.Payload;
                    break;

                case MtkStepKind.Rx:
                    await ExpectAsync(step.Expect, steps, index, lastTx, timeoutMs, ct).ConfigureAwait(false);
                    timeoutMs = isDa ? _daTimeoutMs : _bromTimeoutMs;
                    break;

                case MtkStepKind.Blob:
                    StreamBlob(step, ct);
                    // The reply after an upload arrives once the target has run what it just received;
                    // the capture shows this taking seconds for DA1, so the next expectation waits longer.
                    timeoutMs = _settleTimeoutMs;
                    break;
            }
        }
    }

    private void StreamBlob(MtkStep step, CancellationToken ct)
    {
        byte[] image = _profile.GetBlob(step.BlobFile);
        if (image.Length == 0)
            throw new MtkDaProtocolException("Blob " + step.BlobFile + " is empty; refusing to upload it.");
        for (int offset = 0; offset < image.Length; offset += step.ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(step.ChunkSize, image.Length - offset);
            _transport.Write(image, offset, count);
        }
        Say("    streamed " + step.BlobFile + " (" + image.Length + " bytes)");
    }

    private async Task ExpectAsync(MtkReply expect, IReadOnlyList<MtkStep> steps, int index,
        byte[] lastTx, int timeoutMs, CancellationToken ct)
    {
        if (expect == null)
            throw new MtkDaProtocolException("Boot recipe step " + index + " has no reply to check.");

        byte[] reply = await ReadAsync(expect.Bytes, timeoutMs, index, expect.Kind, ct).ConfigureAwait(false);

        switch (expect.Kind)
        {
            case MtkReplyKind.Echo:
                if (!reply.AsSpan().SequenceEqual(lastTx))
                    throw new MtkDaProtocolException("Step " + index + ": the device did not repeat what was sent (" +
                        Hex(lastTx) + " -> " + Hex(reply) + "). The wire is not carrying a MTK BROM session, so the boot was stopped.");
                break;

            case MtkReplyKind.Zero:
                if (reply.Any(b => b != 0))
                    throw new MtkDaProtocolException("Step " + index + ": the device answered with a non-zero status " +
                        Hex(reply) + ". The recorded boot was accepted here and this one was not, so the boot was stopped.");
                break;

            case MtkReplyKind.LengthOnly:
                // Register, RAM and identity content. Length already verified by ReadAsync; the bytes are
                // device state, and a working boot accepted whatever they were.
                break;

            case MtkReplyKind.HwCode:
                DeviceHwCode = MtkBrom.ParseHwCode(reply);
                if (DeviceHwCode != _profile.HwCode)
                    throw new MtkDaProtocolException("Device reports HW code 0x" + DeviceHwCode.ToString("X4") +
                        " but recipe " + _profile.Chipset + " was recorded on 0x" + _profile.HwCode.ToString("X4") +
                        ". Replaying a bypass spray on the wrong chipset is how phones get bricked, so the boot was stopped.");
                Say("[+] BROM face proven: HW 0x" + DeviceHwCode.ToString("X4") + " (" + _profile.Chipset + ").");
                break;

            case MtkReplyKind.DaAlive:
                if (reply[0] != 0xC0)
                    throw new MtkDaProtocolException("Step " + index + ": expected the DA1 alive byte 0xC0, got " + Hex(reply) + ".");
                _da1Alive = true;
                Say("[+] DA1 jumped and answered 0xc0.");
                break;

            case MtkReplyKind.Sync:
                if (!reply.AsSpan().SequenceEqual(MtkDaCommand.Sync))
                    throw new MtkDaProtocolException("Step " + index + ": expected the DA's SYNC frame, got " + Hex(reply) +
                        ". Without it there is no way to tell a live DA from a stale one, so the boot was stopped.");
                Say("[+] DA SYNC received — the loader is answering.");
                break;

            case MtkReplyKind.Header:
                if (!MtkDaFrame.TryParseHeader(reply, out uint declared))
                    throw new MtkDaProtocolException("Step " + index + ": expected an MTK DA header, got " + Hex(reply) + ".");
                int next = index + 1;
                if (next >= steps.Count || steps[next].Kind != MtkStepKind.Rx)
                    throw new MtkDaProtocolException("Step " + index + ": a DA header declaring " + declared +
                        " bytes is not followed by the frame it announces.");
                if (steps[next].Expect == null || steps[next].Expect.Bytes != (int)declared)
                    throw new MtkDaProtocolException("Step " + index + ": DA header declares " + declared + " bytes but the recipe " +
                        "expects " + steps[next].Expect?.Bytes + ". The wire and the recipe disagree, so the boot was stopped.");
                break;
        }
    }

    private async Task<byte[]> ReadAsync(int count, int timeoutMs, int index, MtkReplyKind kind, CancellationToken ct) =>
        await ReadCoreAsync(count, timeoutMs, "Step " + index + " (" + kind + ")", ct).ConfigureAwait(false);

    /// <summary>The same read with an operation name instead of a recipe step, so B3 failures say what was being tried.</summary>
    private async Task<byte[]> ReadAsync(int count, int timeoutMs, string what, CancellationToken ct) =>
        await ReadCoreAsync(count, timeoutMs, what, ct).ConfigureAwait(false);

    private async Task<byte[]> ReadCoreAsync(int count, int timeoutMs, string what, CancellationToken ct)
    {
        var buffer = new byte[count];
        int got = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (got < count)
        {
            ct.ThrowIfCancellationRequested();
            int available = _transport.BytesAvailable;
            if (available > 0)
            {
                int read = _transport.Read(buffer, got, Math.Min(count - got, available));
                if (read > 0) got += read;
            }
            if (got >= count) return buffer;
            if (DateTime.UtcNow >= deadline)
                break;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
        throw new MtkDaProtocolException(what + ": timed out after " + timeoutMs +
            " ms waiting for " + count + " byte(s); got " + got + (got > 0 ? " (" + Hex(buffer[..got]) + ")" : "") + ".");
    }

    // ---- DA frame primitives for the B3 command layer ----------------------------------------------
    //
    // Deliberately small. The captures prove the frame shape (header and payload are separate writes, and
    // every reply arrives as a header plus exactly its declared payload), but they do not yet prove a
    // generic "send command, get answer" transaction for every command group. B3 builds READ/WRITE/ERASE
    // from these primitives and from the sequences it measures, not from a guessed transaction wrapper.

    /// <summary>The size the working client's large frames left the host in: 2 MiB chunks arrived as 32 packets of 64 KiB.</summary>
    private const int TransportWritePieceSize = 64 * 1024;

    /// <summary>Write one DA frame the way the wire shows it: a 12-byte header, then the payload.</summary>
    internal void WriteFrame(ReadOnlySpan<byte> payload)
    {
        RequireReady();
        byte[] header = MtkDaFrame.Header((uint)payload.Length);
        _transport.Write(header, 0, header.Length);
        if (!payload.IsEmpty)
        {
            byte[] body = payload.ToArray();
            _transport.Write(body, 0, body.Length);
        }
    }

    /// <summary>
    /// A data-bearing frame (an image chunk, a DA upload). The header declares the whole length, but the body
    /// goes out in 64 KiB pieces — that is what the captures show the working client doing (pkts=32 maxpkt=65536
    /// for a 2 MiB chunk), and it keeps a stalled device from turning into one giant blocked port write.
    /// </summary>
    internal void WriteDataFrame(byte[] payload, int offset, int count)
    {
        RequireReady();
        byte[] header = MtkDaFrame.Header((uint)count);
        _transport.Write(header, 0, header.Length);
        for (int sent = 0; sent < count; sent += TransportWritePieceSize)
            _transport.Write(payload, offset + sent, Math.Min(TransportWritePieceSize, count - sent));
    }

    /// <summary>Write a bare command id frame (u32le id, no argument).</summary>
    internal void WriteCommand(uint id)
    {
        RequireReady();
        byte[] frame = MtkDaFrame.Command(id);
        _transport.Write(frame, 0, frame.Length);
    }

    /// <summary>Read one DA frame: the 12-byte header and exactly the payload it declares.</summary>
    internal Task<DaReply> ReadFrameAsync(CancellationToken ct) => ReadFrameAsync("DA frame", ct);

    internal async Task<DaReply> ReadFrameAsync(string what, CancellationToken ct)
    {
        RequireReady();
        byte[] header = await ReadAsync(MtkDaFrame.HeaderSize, _daTimeoutMs, what + " header", ct).ConfigureAwait(false);
        if (!MtkDaFrame.TryParseHeader(header, out uint length))
            throw new MtkDaProtocolException(what + ": device sent a frame that is not an MTK DA header: " + Hex(header) + ".");
        byte[] payload = length == 0
            ? Array.Empty<byte>()
            : await ReadAsync((int)length, _settleTimeoutMs, what + " payload (" + length + " B)", ct).ConfigureAwait(false);
        return new DaReply(header, payload);
    }

    /// <summary>Read a frame and require that its payload is all zero — the DA's accept/refusal word.</summary>
    internal async Task ReadStatusAsync(string what, CancellationToken ct)
    {
        DaReply frame = await ReadFrameAsync(what, ct).ConfigureAwait(false);
        RequireZero(frame.Payload, what);
    }

    /// <summary>Read a frame whose whole meaning is one u32le word (the erase report vocabulary).</summary>
    internal async Task<uint> ReadWordAsync(string what, CancellationToken ct)
    {
        DaReply frame = await ReadFrameAsync(what, ct).ConfigureAwait(false);
        if (frame.Payload.Length != 4)
            throw new MtkDaProtocolException(what + ": expected a 4-byte word frame, got " + frame.Payload.Length + " byte(s).");
        return MtkDaFrame.ReadUInt32Little(frame.Payload);
    }

    /// <summary>The host ack that follows every DA data frame (a u32le zero, sent as its own frame).</summary>
    internal void WriteAck()
    {
        RequireReady();
        byte[] ack = MtkDaFrame.Ack();
        _transport.Write(ack, 0, ack.Length);
    }

    internal readonly record struct DaReply(byte[] Header, byte[] Payload)
    {
        internal uint DeclaredLength => MtkDaFrame.TryParseHeader(Header, out uint length) ? length : 0u;
    }

    private void RequireReady()
    {
        if (!DaIsReady)
            throw new InvalidOperationException("The DA is not ready; nothing may be sent to the device.");
    }

    private static void RequireZero(byte[] payload, string what)
    {
        if (payload.Length == 0)
            throw new MtkDaProtocolException(what + ": the DA answered with an empty frame.");
        if (payload.Any(b => b != 0))
            throw new MtkDaProtocolException(what + ": the DA refused (status " + Hex(payload) + ").");
    }

    private void Say(string message) => _log?.Invoke(message);

    private static string Hex(byte[] data) => data == null || data.Length == 0
        ? "(empty)"
        : "0x" + Convert.ToHexString(data[..Math.Min(data.Length, 16)]) + (data.Length > 16 ? "… (" + data.Length + " B)" : "");
}

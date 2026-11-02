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

    /// <summary>
    /// One sentence about whether the phone behind this pipe is still on the bus, asked only after the boot has
    /// already failed. It must never throw and never change the failure: a refusal that hides the real error
    /// behind a broken diagnostic is worse than no diagnostic.
    /// </summary>
    string BusAfterRefusal();

    int BytesAvailable { get; }

    void Write(byte[] data, int offset, int count);

    /// <summary>Non-blocking read; returns 0 when nothing is waiting.</summary>
    int Read(byte[] buffer, int offset, int count);
}

/// <summary>
/// Hands the session the pipe its second stage runs on.
///
/// The boot is not one connection. The BROM walk finishes by jumping into DA1, the SoC re-enumerates (measured
/// 2026-10-30: a boot that started on 0E8D:0003 left the DA answering on 0E8D:2001, and Windows moved the COM
/// number with it — COM3 became COM37) and only then does the DA protocol begin. A client that keeps talking into
/// the handle it opened at the start is writing into a device record that no longer exists, which is how a
/// half-applied recipe happens.
/// </summary>
internal interface IMtkDaStageHandoff
{
    /// <summary>
    /// Open the transport the DA stage must use: wait for the face to appear, refuse rather than fall back to the
    /// BROM pipe, and never leave the previous transport quietly open behind the new one.
    /// </summary>
    IMtkTransport OpenDaStageTransport(string bromDescription);
}

/// <summary>
/// The face proof's second pipe, used only while the boot is still one read-only byte old.
///
/// RUN 18 (2026-10-31 10:57) is the measurement behind it: six asks answered <c>0xFE</c>, the seventh write died with
/// <c>libusb -9 pipe error</c>, and the boot was refused as if the recipe had been half applied — although nothing
/// but the one byte had ever been sent. The working client met by the same phone minutes later prints
/// <c>Handshake failed, retrying...</c>, closes the node, re-detects it and then gets the echo. So a handle that dies
/// between asks is that client's ordinary retry, and the proof is allowed to reclaim the same face and spend another
/// attempt on it instead of ending the boot.
/// </summary>
internal interface IMtkBromRecovery
{
    /// <summary>
    /// Re-open the face the proof started on: wait for it to come back for at most <paramref name="waitMs"/>, claim
    /// it again, and hand over a pipe ready to carry one byte. Null when the face did not come back; a face that
    /// returns but cannot be claimed throws the claim's own refusal, which already names the reason.
    /// </summary>
    IMtkTransport Reopen(int waitMs);
}

/// <summary>
/// The pipe-level primitive a control-transfer step of the recipe is replayed through.
///
/// RUN 38's passive capture (2026-11-01 15:36) named the traffic no bulk log could show: before every
/// pre-download jump the accepted boot speaks on endpoint zero — a CDC SET_LINE_CODING the device accepts, then
/// a vendor descriptor read the device stalls. A serial COM port and a recorded byte stream cannot carry those,
/// so only a transport that implements this interface may replay a recipe that contains control steps.
/// </summary>
internal interface IMtkControlTransport
{
    /// <summary>
    /// One endpoint-zero control transfer, setup and data stage as the single atomic call the capture recorded.
    /// <paramref name="data"/> is the data stage: its payload for a host-to-device request, a receive buffer for
    /// a device-to-host one (its length is the setup's wLength in both cases). The return follows the libusb
    /// contract: bytes transferred, or a negative libusb error code — a stall the device gave back included.
    /// </summary>
    int ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data, int timeoutMs);
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
    private IMtkTransport _transport;
    private readonly MtkBootProfile _profile;
    private readonly Action<string> _log;
    private readonly int _bromTimeoutMs;
    private readonly int _daTimeoutMs;
    private readonly int _settleTimeoutMs;

    /// <summary>
    /// The three timing knobs of a hardware BROM run, in milliseconds and zero by default.
    ///
    /// RUN 21 (2026-10-31 14:40) replayed 64 recorded steps byte for byte, was refused at step 65, and did the whole
    /// 65-command session inside one wall-clock second. The working client sends the same bytes over the same face and
    /// is answered, and it is Python, where every command pays a round trip the C# side does not. The protocol is
    /// therefore not the thing that differs, and the pace is the thing that has never been tried.
    ///
    /// They are knobs rather than a fix because nothing has measured which of the three moments the phone cares
    /// about: <c>startup</c> is the wait before the greet (the face has enumerated, but its USB handler may not be up
    /// yet), <c>settle</c> the wait after the greet (the phone knows us now, but has not been given a command yet),
    /// and <c>step</c> the wait after every BROM step (the phone may need a moment between two register accesses).
    ///
    /// All three are zero on a recorded replay, so the capture's own timing stays exactly as recorded.
    /// </summary>
    private readonly int _bromStartupMs;
    private readonly int _bromSettleMs;
    private readonly int _bromStepMs;

    /// <summary>
    /// Null means the whole recipe runs on the pipe the boot started on — what every regression run does, because a
    /// recorded capture is one stream of bytes. A real phone does not offer that: it re-enumerates when the BROM
    /// jumps into DA1 (measured 2026-10-30 — after a boot that started on 0E8D:0003 the DA answered as 0E8D:2001,
    /// and Windows moved the COM number with it), so the hardware path supplies a handoff and the DA stage runs on
    /// the face the phone came back as.
    /// </summary>
    private readonly IMtkDaStageHandoff _handoff;

    /// <summary>
    /// Re-opens the BROM face when the proof's pipe dies between asks. Null means every ask runs on the pipe the boot
    /// started on — the regression path, where a scripted device never disappears.
    /// </summary>
    private readonly IMtkBromRecovery _recovery;

    // The face proof is the only step that may be asked more than once: the four-byte USB handshake (on a real face)
    // and one read-only byte, then a long patience.
    // Three attempts was measured as too short (RUN 14, 2026-10-31): the same phone that answered 0xFD -> 0xFE to
    // three asks in two seconds was walked all the way into DA1 by mtkclient four minutes later, and mtkclient's own
    // log spends three or four handshake retries ("Handshake failed, retrying...") before it succeeds. A BROM that
    // has just enumerated answers this byte wrongly until its USB handler is ready, so the retry window spans the
    // whole start-up, while still sending one read-only byte per attempt and writing nothing else.
    //
    // Cadence (B4.8, RUN 15): 800 ms between asks spent the whole of RUN 15's window on four asks. If the wrong answer
    // is a start-up state rather than a wrong mode, the asks have to be dense enough to outlast the start-up, so the
    // gap is 400 ms — 25 asks still span 10 s, and no extra byte is sent for it.
    private readonly int _proofAttempts;
    private readonly int _proofRetryDelayMs;

    /// <summary>
    /// How long the whole proof may take, re-opens included. The asks alone span 25 × 400 ms; a pipe that has to be
    /// reclaimed between them costs a wait and a claim each time, so the budget is wall-clock rather than ask count —
    /// a phone that keeps dropping its face must not turn a ten-second handshake into a ten-minute hang.
    /// </summary>
    private readonly int _proofWindowMs;

    /// <summary>
    /// True when the pipe is a real MTK face. A recorded capture does not contain the client's own handshake
    /// exchange, so the replay path leaves this off and the hardware path turns it on — it is what puts the wire
    /// trace and the pacing on the clock. Whether the four-pair greet itself is sent is <see cref="_bromGreet"/>.
    /// </summary>
    private readonly bool _bromHandshake;

    /// <summary>
    /// Whether the four-pair greet is actually sent. It is a knob, not a constant, because the greet is the one
    /// thing our client puts on the wire that the working client does not: its own log has no handshake byte at all
    /// (its first TX is the read-only query), while ours sends A0/0A/50/05 first. RUN 21 replayed 65 identical
    /// commands and was refused at step 65 with 0x1D1A where the working client was answered 0000 — so the greeting
    /// is the first thing a trial has to be able to take out, and a knob is cheaper than a rebuild.
    /// </summary>
    private readonly bool _bromGreet;

    /// <summary>
    /// Whether the pipe the proof is about to use has already completed the handshake. It is per pipe, not per
    /// attempt: a retried query on the same handshaken face must not replay the handshake — nothing measures what a
    /// second handshake does to a BROM that is already listening — while a pipe reclaimed after a failure is new and
    /// has to do it again.
    /// </summary>
    private bool _pipeHandshaken;

    /// <summary>The handshake is reported once, when it first completes. Later attempts on other pipes are covered by
    /// the reclaim line, so the log is not twenty copies of the same four pairs.</summary>
    private bool _handshakeReported;

    /// <summary>
    /// How many BROM steps a hardware run prints the wire for.
    ///
    /// RUN 20 (2026-10-31 14:03) is the reason this exists. The first hardware boot that got past the four-pair
    /// handshake replayed 64 recorded steps and then stopped on a status word — 0x1D1A where the recording has 0000 —
    /// while the working client's own log sends byte-identical commands and is answered 0000 on the same phone. Every
    /// byte we SEND was known; nothing we RECEIVED was, and the answer is the only thing left to look at. The trace
    /// prints the first steps of a hardware run so the two sides can be compared line by line.
    ///
    /// The window covers the whole BROM prelude (the queries, the first register accesses, the payload upload and
    /// the payload's re-open — RUN 39's first hardware boot with the control probes reached step 167, so the window
    /// moved past 140) and stops before the register spray, which is measured in thousands of steps.
    /// </summary>
    private const int WireTraceSteps = 220;

    /// <summary>Set while a hardware run replays the BROM stage, where the trace belongs. A recorded replay has no
    /// wire of its own to show.</summary>
    private bool _traceOn;

    // Set when DA1 answers 0xc0 after the jump. Kept separate from DaIsReady: an alive DA1 is not a DA that
    // serves extents, and the DA stage only begins once the first one has been proven to be running.
    private bool _da1Alive;

    internal ushort DeviceHwCode { get; private set; }
    internal bool DaIsReady { get; private set; }
    internal string TransportDescription => _transport.Description;

    private MtkDaSession(IMtkTransport transport, MtkBootProfile profile, Action<string> log,
        int bromTimeoutMs, int daTimeoutMs, int settleTimeoutMs, IMtkDaStageHandoff handoff = null,
        int proofAttempts = 25, int proofRetryDelayMs = 400, IMtkBromRecovery recovery = null,
        int proofWindowMs = 60000, bool bromHandshake = false, bool bromGreet = true,
        int bromStartupMs = 0, int bromSettleMs = 0, int bromStepMs = 0)
    {
        _transport = transport;
        _profile = profile;
        _log = log;
        _bromTimeoutMs = bromTimeoutMs;
        _daTimeoutMs = daTimeoutMs;
        _settleTimeoutMs = settleTimeoutMs;
        _handoff = handoff;
        _recovery = recovery;
        _proofAttempts = proofAttempts < 1 ? 1 : proofAttempts;
        _proofRetryDelayMs = proofRetryDelayMs < 0 ? 0 : proofRetryDelayMs;
        _proofWindowMs = proofWindowMs < _proofRetryDelayMs ? _proofRetryDelayMs : proofWindowMs;
        _bromHandshake = bromHandshake;
        _bromGreet = bromGreet;
        _bromStartupMs = bromStartupMs < 0 ? 0 : bromStartupMs;
        _bromSettleMs = bromSettleMs < 0 ? 0 : bromSettleMs;
        _bromStepMs = bromStepMs < 0 ? 0 : bromStepMs;
    }

    /// <summary>
    /// Boot a device from BROM to a ready DA by replaying the recorded recipe. Nothing is sent before the
    /// recipe and its blobs have been verified (<see cref="MtkBootProfile.Load"/>), and any frame that
    /// arrives differently than recorded aborts the boot. <paramref name="bromHandshake"/> marks a real face
    /// (a recorded stream begins after the handshake); <paramref name="bromGreet"/> decides whether the
    /// four-pair greet is actually sent on it.
    /// </summary>
    internal static async Task<MtkDaSession> BootAsync(IMtkTransport transport, MtkBootProfile profile,
        Action<string> log = null, CancellationToken ct = default,
        int bromTimeoutMs = 1500, int daTimeoutMs = 8000, int settleTimeoutMs = 20000,
        IMtkDaStageHandoff handoff = null, int proofAttempts = 25, int proofRetryDelayMs = 400,
        IMtkBromRecovery recovery = null, int proofWindowMs = 60000, bool bromHandshake = false,
        bool bromGreet = true, int bromStartupMs = 0, int bromSettleMs = 0, int bromStepMs = 0)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(profile);
        var session = new MtkDaSession(transport, profile, log, bromTimeoutMs, daTimeoutMs, settleTimeoutMs, handoff,
            proofAttempts, proofRetryDelayMs, recovery, proofWindowMs, bromHandshake, bromGreet,
            bromStartupMs, bromSettleMs, bromStepMs);
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

        // The pacing line is printed even when it is all zeroes, because a run that was meant to be slow and was
        // not is the one that explains itself wrongly.
        Say("[*] BROM pacing: " + (_bromGreet
                ? _bromStartupMs + " ms before the greet, " + _bromSettleMs + " ms after it, " + _bromStepMs + " ms after every BROM step"
                : _bromStartupMs + " ms before the first query, " + _bromSettleMs + " ms more before it, " + _bromStepMs + " ms after every BROM step") +
            (_bromHandshake ? "" : " (not applied — this is a recorded replay)"));
        if (_bromHandshake)
            Say("[*] BROM greet: " + (_bromGreet
                ? "on — the four pairs go out before the recipe's first byte"
                : "off — the recipe's first byte is the first byte on the wire, as the working client has it"));

        if (_bromHandshake && _bromStartupMs > 0)
        {
            Say("[*] BROM pacing: waiting " + _bromStartupMs + " ms before the greet — the face has enumerated, " +
                "but its USB handler may not be listening yet");
            await Task.Delay(_bromStartupMs, ct).ConfigureAwait(false);
        }

        await RunStageAsync(_profile.BromSteps, isDa: false, ct).ConfigureAwait(false);
        if (!_da1Alive)
            throw new MtkDaProtocolException("The BROM walk finished without the DA1 alive signal (0xc0). " +
                "The device is not in the state this recipe was recorded from, so the boot was stopped.");

        // The jump into DA1 re-enumerates the phone, so from here on the bytes belong to a different device node.
        if (_handoff != null) SwitchToDaStage();

        await RunStageAsync(_profile.DaSteps, isDa: true, ct).ConfigureAwait(false);
        DaIsReady = true;
        Say("[+] MTK DA ready — " + _profile.Chipset + " (HW " + DeviceHwCode.ToString("X4") + "), boot replayed from recipe.");
    }

    /// <summary>
    /// Move the session onto the face the phone came back as once the BROM jumped.
    ///
    /// The old pipe is only released after the new one exists: if the phone never returns, the failure the operator
    /// reads is the handoff's own message, and <see cref="BootAsync"/>'s caller disposes what is still open. Nothing
    /// here falls back to the BROM pipe, because writing DA frames into a handle the phone abandoned is exactly the
    /// half-applied recipe this class exists to avoid.
    /// </summary>
    private void SwitchToDaStage()
    {
        string from = _transport.Description;
        Say(" DA stage: the BROM jumped — waiting for the phone to come back as a DA (it started on " + from + ")");
        IMtkTransport next;
        try
        {
            next = _handoff.OpenDaStageTransport(from);
        }
        catch (MtkDaProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MtkDaProtocolException("The BROM stage completed and the DA face could not be opened (" +
                ex.GetType().Name + ": " + ex.Message + "). The boot was stopped before any DA frame was sent, " +
                "so the phone is still in the state the jump left it in.");
        }
        if (next == null)
            throw new MtkDaProtocolException("The stage handoff returned no transport. Nothing is sent into a pipe " +
                "that does not exist, so the boot was stopped.");

        try { _transport.Dispose(); } catch { /* the BROM node is already off the bus — that is the expected outcome */ }
        _transport = next;
        Say("[+] DA face is live on " + next.Description + " — the rest of the recipe runs there.");
    }

    private async Task RunStageAsync(IReadOnlyList<MtkStep> steps, bool isDa, CancellationToken ct)
    {
        byte[] lastTx = Array.Empty<byte>();
        int timeoutMs = isDa ? _daTimeoutMs : _bromTimeoutMs;

        // Only a hardware run has a wire worth printing; a recorded replay would print the recording back at itself.
        _traceOn = !isDa && _bromHandshake;

        for (int index = 0; index < steps.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            MtkStep step = steps[index];

            switch (step.Kind)
            {
                case MtkStepKind.Tx:
                    lastTx = step.Payload;
                    // The BROM stage's very first byte belongs to the proof, not to the loop: it is the one write
                    // that may have to be repeated on a re-opened pipe (RUN 18 died on exactly this byte's seventh
                    // send), so ProveFaceAsync performs it for every attempt instead of the loop doing it once.
                    if (!isDa && index == 0) break;
                    _transport.Write(step.Payload, 0, step.Payload.Length);
                    TraceTx(index, step.Payload);
                    break;

                case MtkStepKind.Rx:
                    // Step 1 is the recipe's first reply: the echo of the single read-only byte that proves the
                    // face. It is the only handshake where "not awake yet" and "wrong device" look alike, and the
                    // only one that may be asked again.
                    if (!isDa && index == 1)
                        await ProveFaceAsync(step, steps, index, lastTx, timeoutMs, ct).ConfigureAwait(false);
                    else
                        await ExpectAsync(step.Expect, steps, index, lastTx, timeoutMs, ct).ConfigureAwait(false);
                    timeoutMs = isDa ? _daTimeoutMs : _bromTimeoutMs;
                    break;

                case MtkStepKind.Blob:
                    StreamBlob(step, index, ct);
                    // The reply after an upload arrives once the target has run what it just received;
                    // the capture shows this taking seconds for DA1, so the next expectation waits longer.
                    timeoutMs = _settleTimeoutMs;
                    break;

                case MtkStepKind.Ctrl:
                    RunCtrlProbe(step, index);
                    break;
            }

            // The pace between two commands. Only a hardware run pays it: a recorded replay has no phone to wait for.
            if (!isDa && _bromHandshake && _bromStepMs > 0)
                await Task.Delay(_bromStepMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The one handshake that proves this face is a BROM — with a bounded number of second chances.
    ///
    /// RUN 12 (2026-10-30 17:25) sent the same single byte through the same bulk endpoints the working client used
    /// eighty-four minutes earlier and got <c>0xFE</c> back instead of the echo. One byte and then silence is also
    /// exactly what a command that arrived before the BROM's USB handler was awake looks like, and the two cannot be
    /// told apart from a single answer. RUN 14 (2026-10-31 09:53) settled the question of how long "awake" takes: our
    /// three attempts inside two seconds all got <c>0xFE</c>, and mtkclient — started a second later against the same
    /// plug — walked the phone into DA1 with its BROM stats, after spending several handshake retries of its own.
    /// The retry window is therefore the whole start-up rather than a second chance; it stays read-only per attempt.
    ///
    /// B4.11 (RUN 18, 2026-10-31 10:57) added the shape RUN 18 produced: six one-byte answers, then the handle died
    /// under the seventh write (<c>libusb -9</c>) and the boot was refused although the recipe was one byte old. The
    /// working client's own recovery between handshake retries is to close the node and re-detect it, so a failed
    /// pipe is now reclaimed and the ask repeated — and a silent read, which is the same "not answering yet" state,
    /// is retried instead of ending the windows on its first timeout.
    ///
    /// A device that answers with more than the echo is speaking in its own protocol and is still refused on the
    /// spot; whatever the last attempt said is what the operator reads.
    ///
    /// B4.12 (run P, 2026-10-31 13:38) is where the earlier runs were answered: a real face refuses every byte until
    /// the four-pair USB handshake (0xA0/0x0A/0x50/0x05, answered by their complements) has been exchanged, so
    /// <paramref name="lastTx"/> cannot be the first thing a hardware pipe hears. The handshake is therefore the
    /// first step of each attempt on a pipe that has not done it, and its failure is retried exactly like a wrong
    /// echo — it says "not listening yet" just as clearly.
    /// </summary>
    private async Task ProveFaceAsync(MtkStep step, IReadOnlyList<MtkStep> steps, int index, byte[] lastTx,
        int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_proofWindowMs);
        int attempt = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Which stage this attempt is in, so a retry line says what actually failed. The greeting is only the
            // reason for a failure while it is being sent; once it is done (or was never asked for), a failure is
            // about the query itself.
            bool greetPassed = !_bromGreet || _pipeHandshaken || !_bromHandshake;
            try
            {
                if (_bromHandshake && _bromGreet && !_pipeHandshaken)
                {
                    bool greeted = await ExchangeHandshakeAsync(ct).ConfigureAwait(false);
                    _pipeHandshaken = true;
                    greetPassed = true;
                    if (!_handshakeReported)
                    {
                        _handshakeReported = true;
                        Say(greeted
                            ? "[+] BROM handshake complete: " + MtkBrom.HandshakeTranscript() +
                              " — the face will now take a command."
                            : "[+] BROM handshake: the face was already past the greet (0xA0 came back as 0xA0, " +
                              "the echo a greeted BROM gives a command), so the read-only query follows directly — " +
                              "the HW-code answer is still what decides.");
                    }
                }
                // The phone knows us now, but it has not been given a command yet. This is the one place where a
                // pause can be waited out before the read-only query — the only query that may still be re-asked.
                if (_bromHandshake && _bromSettleMs > 0)
                {
                    if (attempt == 1)
                        Say("[*] BROM pacing: settling " + _bromSettleMs + " ms " +
                            (_bromGreet ? "after the greet " : "") + "before the read-only query");
                    await Task.Delay(_bromSettleMs, ct).ConfigureAwait(false);
                }
                _transport.Write(lastTx, 0, lastTx.Length);
                TraceTx(0, lastTx);
                await ExpectAsync(step.Expect, steps, index, lastTx, timeoutMs, ct).ConfigureAwait(false);
                if (attempt > 1)
                    Say("[+] BROM proof: the face repeated " + Hex(lastTx) + " on attempt " + attempt + " of " +
                        _proofAttempts + " — the earlier answer was a device that was not listening yet, not a wrong mode");
                return;
            }
            catch (MtkDaProtocolException ex) when (attempt < _proofAttempts && DateTime.UtcNow < deadline &&
                                                    IsStartupShape(ex))
            {
                Say(" [ ] BROM proof: attempt " + attempt + " of " + _proofAttempts + " " +
                    DescribeFailedAsk(ex, lastTx, greetPassed) +
                    " — asking again in " + _proofRetryDelayMs + " ms" +
                    (_recovery != null ? " on a freshly claimed pipe" : "") +
                    ProofRetryNote());
                await Task.Delay(_proofRetryDelayMs, ct).ConfigureAwait(false);
                if (_recovery != null)
                    await ReclaimProofPipeAsync(ex, lastTx, attempt, deadline, ct).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    /// <summary>
    /// The measured four-pair USB handshake, played byte by byte because each byte has its own answer and nothing
    /// may be pipelined before the previous complement arrived.
    ///
    /// The whole sequence is required, in order: 0xA0 -> 0x5F, 0x0A -> 0xF5, 0x50 -> 0xAF, 0x05 -> 0xFA. Two
    /// different faces fail it and both must be refused rather than written into: a face that is not a MTK BROM at
    /// all (it answers in its own protocol) and a preloader face, which shares the VID/PID and even answers the
    /// first pair with its complement but not the rest. A pair that does not answer its complement ends the
    /// exchange on the spot — every byte beyond the first pair is a byte sent to a face that has not proven it will
    /// take commands.
    ///
    /// RUN 20 (2026-10-31 14:04) measured the one shape that is not a refusal: a BROM that is ALREADY past the
    /// greet echoes the greeting instead of complementing it (<c>A0 -> A0</c>, the same echo a greeted BROM gives a
    /// command). The greet lives in the phone, not in our pipe, so a second Boot DA against a phone that was booted
    /// once reads exactly that — and refusing it, as the first build did, refused a phone that was ready to take
    /// the query. Pair 1 answering with its own byte therefore means "greeted, skip the rest"; the read-only
    /// HW-code query still follows and is still what decides, so a stale DA or an alien face that happens to echo
    /// this byte is refused one step later rather than accepted here.
    ///
    /// Note what is NOT done here: no command byte is sent. These four bytes are the USB-level hello, and the
    /// read-only HW-code query still follows as the recipe's own step, so a broken handshake leaves the phone
    /// with nothing but four greeting bytes on its wire.
    /// </summary>
    /// <returns><c>true</c> when the greeting was exchanged now; <c>false</c> when the face already had it.</returns>
    private async Task<bool> ExchangeHandshakeAsync(CancellationToken ct)
    {
        for (int i = 0; i < MtkBrom.HandshakeSequence.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            byte sent = MtkBrom.HandshakeSequence[i];
            byte expect = MtkBrom.HandshakeReply(sent);
            _transport.Write(new[] { sent }, 0, 1);
            byte[] reply = await ReadCoreAsync(1, _bromTimeoutMs, "BROM handshake pair " + (i + 1) + " of 4", ct)
                .ConfigureAwait(false);
            if (i == 0 && reply[0] == sent)
                return false;
            if (reply[0] != expect)
                throw new MtkDaProtocolException("The BROM handshake broke at pair " + (i + 1) + " of 4: sent 0x" +
                    sent.ToString("X2") + ", expected its complement 0x" + expect.ToString("X2") + ", got 0x" +
                    reply[0].ToString("X2") + " on " + _transport.Description + ". " +
                    "A face that answers any other way is not in the MTK BROM handshake — a preloader face and a " +
                    "BROM whose USB handler is not up yet look the same here — so no command was sent to it. " +
                    "The full exchange is " + MtkBrom.HandshakeTranscript() + ".")
                { Observed = reply };
        }
        return true;
    }

    /// <summary>
    /// The two shapes a phone that is not listening yet can have: it answered one byte that is not the echo, or it
    /// said nothing at all. RUN 18 also produced a third — the handle died mid-ask with <c>libusb -9</c> — which is
    /// neither a wrong device nor a finished handshake, so it is retried the same way. A device that answers with
    /// more than the echo is speaking in its own protocol and is still refused on the spot.
    /// </summary>
    private static bool IsStartupShape(MtkDaProtocolException ex) =>
        ex.TransportFailure || ex.Observed == null || ex.Observed.Length <= 1;

    private static string DescribeFailedAsk(MtkDaProtocolException ex, byte[] lastTx, bool greetPassed)
    {
        if (!greetPassed)
        {
            // The attempt never reached the recipe's byte: the face refused, or never finished, the USB handshake.
            if (ex.TransportFailure)
                return "could not deliver the BROM handshake " + MtkNativeBench.TransportFailureBrief(ex.Message);
            if (ex.Observed == null)
                return "sent the BROM handshake and the face went silent";
            return "was refused during the BROM handshake (" + Hex(ex.Observed) + " where a complement was due)";
        }
        if (ex.TransportFailure)
            return "could not even deliver the byte " + MtkNativeBench.TransportFailureBrief(ex.Message);
        if (ex.Observed == null)
            return "answered nothing instead of repeating " + Hex(lastTx);
        return "answered " + Hex(ex.Observed) + " instead of repeating " + Hex(lastTx);
    }

    /// <summary>What a retry costs, said in the same line as the reason — a greeting pipe sends four more bytes than a replay.</summary>
    private string ProofRetryNote()
    {
        if (!_bromHandshake)
            return " (one read-only byte per attempt; nothing has been written to the phone)";
        return _bromGreet
            ? " (the handshake and the one read-only query are re-sent; no command has run on the phone)"
            : " (the one read-only query is re-sent; no command has run on the phone)";
    }

    /// <summary>
    /// Replace a pipe that failed mid-proof with a freshly opened one on the same face, the way the working client
    /// recovers between its own handshake retries. When the face does not come back the boot is refused with the
    /// pipe's own error plus what the proof did, so the operator reads why rather than a generic stop.
    /// </summary>
    private async Task ReclaimProofPipeAsync(MtkDaProtocolException cause, byte[] lastTx, int attempt,
        DateTime deadline, CancellationToken ct)
    {
        int wait = (int)Math.Max(0, Math.Min(20000, (deadline - DateTime.UtcNow).TotalMilliseconds));
        string dead = _transport.Description;
        try { _transport.Dispose(); } catch { /* the handle is already gone; the phone is what matters */ }

        IMtkTransport next = null;
        string failure = null;
        try
        {
            next = await Task.Run(() => _recovery.Reopen(wait), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
        }

        if (next == null)
            throw new MtkDaProtocolException(cause.Message + " The proof wrote the single read-only byte " + Hex(lastTx) +
                " " + attempt + " time(s); then " + dead + " was closed and the face was waited on for " + wait +
                " ms, but it did not come back" + (failure == null ? "" : " (" + failure + ")") +
                ". Nothing else was written, so the phone is exactly where it was left: power it off from its menu, " +
                "hold BOTH volume keys and plug it in again.");

        _transport = next;
        // A reclaimed pipe is a new face and has to be greeted again, even when the old one had already handshaken.
        _pipeHandshaken = false;
        Say(" [ ] BROM proof: the face is back on " + next.Description + " — asking again there");
    }

    private void StreamBlob(MtkStep step, int index, CancellationToken ct)
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
        TraceTx(index, image);
        Say("    streamed " + step.BlobFile + " (" + image.Length + " bytes)");
    }

    /// <summary>How long one recorded endpoint-zero probe waits before the boot refuses. RUN 38 shows every probe
    /// completing within ~3 ms of its submit; the oracle arms its transfers at 1000 ms.</summary>
    private const int CtrlProbeTimeoutMs = 1000;

    /// <summary>How many asks an accepted-transfer control step gets when the device stalls it. RUN 40 (2026-11-01
    /// 17:19) is the measurement: the re-open's first SET_LINE_CODING came back libusb -9 within a millisecond of
    /// the payload magic — the payload was still re-initialising the USB stack that the accepted boot, at its
    /// slower pace, had already finished with. A stall on a control endpoint clears itself at the next SETUP, so
    /// the step is re-asked 5 ms apart inside this budget instead of ending the boot; a step whose recorded answer
    /// is a refusal is never re-asked.</summary>
    private const int CtrlProbeRetries = 20;
    private const int CtrlProbeRetryMs = 5;

    /// <summary>
    /// One recorded endpoint-zero control transfer, replayed as the single atomic call it was captured as.
    ///
    /// The accepted boot sends six of these before every pre-download <c>da</c> (RUN 38 frames f232–255, f319–341,
    /// f405–427): a SET_LINE_CODING whose data stage is in the recipe, then a vendor descriptor read the device
    /// stalls. The stall is the recorded answer, not a failure — but the probe refuses anything else, because a
    /// device that hands back descriptor bytes is not the BROM the recipe was measured on.
    ///
    /// An accepted step that the device stalls is a different state: RUN 40 caught the re-open's first
    /// SET_LINE_CODING refused with libusb -9 while the payload was still re-initialising the USB stack, so the
    /// step is re-asked inside the <see cref="CtrlProbeRetries"/> budget before the boot gives up.
    /// </summary>
    private void RunCtrlProbe(MtkStep step, int index)
    {
        byte[] frame = step.Payload;
        byte bmRequestType = frame[0];
        byte bRequest = frame[1];
        ushort wValue = (ushort)(frame[2] | frame[3] << 8);
        ushort wIndex = (ushort)(frame[4] | frame[5] << 8);
        ushort wLength = (ushort)(frame[6] | frame[7] << 8);
        byte[] data = new byte[wLength];
        if ((bmRequestType & 0x80) == 0)
            frame.AsSpan(8, wLength).CopyTo(data);

        if (_transport is not IMtkControlTransport control)
            throw new MtkDaProtocolException("Step " + index + " is a recorded endpoint-zero control transfer, but " +
                _transport.Description + " cannot send one — only a raw-USB pipe replays control steps. The recipe " +
                "cannot run on this pipe, so the boot was stopped before anything was sent.");

        int rc;
        int attempts = 1;
        try
        {
            rc = control.ControlTransfer(bmRequestType, bRequest, wValue, wIndex, data, CtrlProbeTimeoutMs);
            while (rc < 0 && attempts < CtrlProbeRetries && step.Expect.Kind == MtkReplyKind.CtrlAck)
            {
                Thread.Sleep(CtrlProbeRetryMs);
                rc = control.ControlTransfer(bmRequestType, bRequest, wValue, wIndex, data, CtrlProbeTimeoutMs);
                attempts++;
            }
        }
        catch (Exception ex) when (ex is not MtkDaProtocolException)
        {
            throw new MtkDaProtocolException("Step " + index + ": the control transfer " + Hex(frame) +
                " could not be delivered (" + ex.GetType().Name + ": " + ex.Message + ").") { TransportFailure = true };
        }
        TraceCtrl(index, frame, rc, attempts);

        switch (step.Expect.Kind)
        {
            case MtkReplyKind.CtrlAck:
                if (rc != wLength)
                    throw new MtkDaProtocolException("Step " + index + ": the control transfer " + Hex(frame) +
                        " was not accepted the way the recorded boot had it (" + CtrlResult(rc) + " instead of " +
                        wLength + " byte(s) accepted) on " + _transport.Description + ". Refused " + attempts +
                        " ask(s) in a row. The boot was stopped.");
                break;

            case MtkReplyKind.CtrlStall:
                if (rc >= 0)
                    throw new MtkDaProtocolException("Step " + index + ": the descriptor read " + Hex(frame) +
                        " is recorded as refused, but the device returned " + CtrlResult(rc) + ". A device that answers " +
                        "this read is not in the state the recipe was measured on, so the boot was stopped.");
                break;

            default:
                throw new MtkDaProtocolException("Step " + index + ": reply kind " + step.Expect.Kind +
                    " is not verifiable on a control step.");
        }
    }

    private static string CtrlResult(int rc) =>
        rc < 0 ? "libusb " + rc + " (" + MtkNativeBench.UsbErrorText(rc) + ")" : rc + " byte(s)";

    private void TraceCtrl(int index, byte[] frame, int rc, int attempts = 1)
    {
        if (!_traceOn || index > WireTraceSteps) return;
        Say(" [i] BROM wire " + index + ": ctrl " + Hex(frame) + " -> " + CtrlResult(rc) +
            (attempts > 1 ? " after " + attempts + " asks" : ""));
    }

    /// <summary>
    /// Prints one step of the wire on a hardware run.
    ///
    /// The point is comparison: the recorded boot is a known list of bytes and answers, so the trace is what makes
    /// "the phone answered something else here" a statement about the phone instead of a guess. Replies longer than
    /// eight bytes are not printed — the SOC id and the device hash in that range are chip-unique and have no
    /// business in a log file that outlives the run.
    /// </summary>
    private void TraceTx(int index, byte[] payload)
    {
        if (!_traceOn || index > WireTraceSteps) return;
        Say(" [i] BROM wire " + index + ": tx " + Hex(payload));
    }

    private void TraceRx(int index, byte[] reply)
    {
        if (!_traceOn || index > WireTraceSteps) return;
        Say(" [i] BROM wire " + index + ": rx " +
            (reply.Length <= 8 ? Hex(reply) : reply.Length + " bytes (content not logged)"));
    }

    private async Task ExpectAsync(MtkReply expect, IReadOnlyList<MtkStep> steps, int index,
        byte[] lastTx, int timeoutMs, CancellationToken ct)
    {
        if (expect == null)
            throw new MtkDaProtocolException("Boot recipe step " + index + " has no reply to check.");

        byte[] reply = await ReadAsync(expect.Bytes, timeoutMs, index, expect.Kind, ct).ConfigureAwait(false);
        TraceRx(index, reply);

        switch (expect.Kind)
        {
            case MtkReplyKind.Echo:
                if (!reply.AsSpan().SequenceEqual(lastTx))
                {
                    var (tail, tailText) = await WireTailBytesAsync(ct).ConfigureAwait(false);
                    throw new MtkDaProtocolException("Step " + index + ": the device did not repeat what was sent (" +
                        Hex(lastTx) + " -> " + Hex(reply) + ") on " + _transport.Description +
                        ". Still on the wire: " + tailText +
                        ". Still on the bus: " + BusNote() +
                        ". The wire is not carrying a MTK BROM session, so the boot was stopped. " +
                        MtkNativeBench.StaleDaRecovery) { Observed = reply.Concat(tail).ToArray() };
                }
                break;

            case MtkReplyKind.Complement:
                // The four-pair handshake's answers are the bitwise complements of the sent bytes (A0->5F
                // 0A->F5 50->AF 05->FA), and RUN 38's capture (f455–f470) shows the working client replaying
                // the whole greet after the kamakiri payload — the payload re-inits the BROM's USB stack, so
                // the serial session is re-opened before the next command. The loader pins this kind to one
                // byte; lastTx is the greet byte the previous step sent.
                if (lastTx.Length != 1 || reply.Length != 1 || reply[0] != (byte)~lastTx[0])
                {
                    var (tailC, tailTextC) = await WireTailBytesAsync(ct).ConfigureAwait(false);
                    throw new MtkDaProtocolException("Step " + index + ": expected the handshake complement of " +
                        Hex(lastTx) + (lastTx.Length == 1 ? " (" + Hex(new[] { (byte)~lastTx[0] }) + ")" : "") +
                        ", got " + Hex(reply) + " on " + _transport.Description + ". Still on the wire: " + tailTextC +
                        ". Still on the bus: " + BusNote() + ". The payload's re-open did not complete, so the boot was stopped.")
                    { Observed = reply.Concat(tailC).ToArray() };
                }
                break;

            case MtkReplyKind.Zero:
                if (reply.Any(b => b != 0))
                {
                    // What is still on the wire separates "the device said no" from "we read the wrong bytes": a
                    // phone that answered and went quiet left nothing behind, while a phone that answered and kept
                    // talking is still mid-answer and the bytes behind this one say what it meant.
                    //
                    // The bus is asked for the same reason. Every native boot since RUN 20 has refused at step 65
                    // with the same word, 0x1D1A, and the open question is whether the phone answered that as a
                    // BROM or as a DA it had already jumped into. Only one thing can tell those two apart, and it is
                    // not the bytes: a face that is still on the bus as 0E8D:0003 is the BROM that echoed our
                    // previous command, while a face that has become 0E8D:2001 has jumped. This costs the phone
                    // nothing — it is a Windows question, sent after the boot has already stopped, and BusNote
                    // cannot throw.
                    var (tail, tailText) = await WireTailBytesAsync(ct).ConfigureAwait(false);
                    throw new MtkDaProtocolException("Step " + index + ": the device answered with a non-zero status " +
                        Hex(reply) + ". The recorded boot was accepted here and this one was not, so the boot was stopped." +
                        " Still on the wire: " + tailText + ". Still on the bus: " + BusNote() + ".")
                    { Observed = reply.Concat(tail).ToArray() };
                }
                break;

            case MtkReplyKind.LengthOnly:
                // Register, RAM and identity content. Length already verified by ReadAsync; the bytes are
                // device state, and a working boot accepted whatever they were.
                //
                // RUN 20 (2026-10-31 14:03) is why the small ones are now printed: the first hardware boot that
                // got past the handshake replayed 64 steps and then stopped on a status word, and the only way
                // to tell a phone whose security state differs from the recorded one apart from a protocol
                // problem is to read what these steps answered. Target config, BROM version and the hardware
                // words are small; the 16- and 32-byte answers are chip-unique ids and stay out of the log.
                if (_bromHandshake && reply.Length <= 8)
                    Say(" [i] BROM reply " + index + " (" + reply.Length + " byte(s), content not checked): " +
                        Hex(reply));
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
            " ms waiting for " + count + " byte(s); got " + got + (got > 0 ? " (" + Hex(buffer[..got]) + ")" : "") +
            ". Still on the wire: " + await WireTailAsync(ct).ConfigureAwait(false) +
            ". Still on the bus: " + BusNote() + ".");
    }

    /// <summary>
    /// Whether the phone is still where the boot left it, asked of Windows rather than of the wire. RUN 10 and
    /// RUN 11 refused identically from inside the session — one byte, then silence — and the difference between
    /// "the phone is sitting on this face refusing us" and "the phone walked off this face two seconds ago" is
    /// the difference between a wrong serial setting and a phone that never entered BROM. Nothing on the wire can
    /// tell those apart; the bus can. A diagnostic that throws would replace a real error with a fake one, so it
    /// swallows everything and reports that it could not look.
    /// </summary>
    private string BusNote()
    {
        try
        {
            string note = _transport.BusAfterRefusal();
            return string.IsNullOrWhiteSpace(note) ? "the bus reported nothing" : note.Trim();
        }
        catch (Exception ex)
        {
            return "unknown — Windows could not be re-read (" + ex.GetType().Name + ": " + ex.Message + ")";
        }
    }

    /// <summary>
    /// Whatever the device sends next, for people who have to diagnose a boot from a log line. A refusal that
    /// stops at "the echo was wrong" cannot tell a phone in the wrong mode from a port whose settings do not
    /// match the wire — the bytes that follow the wrong one can. This only reads; a frame that has already
    /// failed the boot ends it, so draining the rest of the reply costs nothing.
    /// </summary>
    private async Task<string> WireTailAsync(CancellationToken ct) =>
        (await WireTailBytesAsync(ct).ConfigureAwait(false)).Text;

    private async Task<(byte[] Bytes, string Text)> WireTailBytesAsync(CancellationToken ct)
    {
        const int MaxTailBytes = 32;
        var tail = new List<byte>(MaxTailBytes);
        var deadline = DateTime.UtcNow.AddMilliseconds(250);
        while (tail.Count < MaxTailBytes && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            int available = _transport.BytesAvailable;
            if (available <= 0)
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                continue;
            }
            var chunk = new byte[Math.Min(available, MaxTailBytes - tail.Count)];
            int read = _transport.Read(chunk, 0, chunk.Length);
            if (read <= 0) break;
            for (int i = 0; i < read; i++) tail.Add(chunk[i]);
            deadline = DateTime.UtcNow.AddMilliseconds(250);   // bytes keep coming, so keep the window open while they do
        }
        byte[] bytes = tail.ToArray();
        return (bytes, bytes.Length == 0 ? "nothing" : Hex(bytes) + " (" + bytes.Length + " byte(s))");
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

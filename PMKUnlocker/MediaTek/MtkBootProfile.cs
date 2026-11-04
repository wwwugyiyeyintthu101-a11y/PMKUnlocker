#nullable disable
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace PMKUnlocker.MediaTek;

// Replay recipe for the native MTK boot — ROADMAP Phase 1.75 "Path B", stage B2.
//
// WHAT THIS FILE IS. A boot that actually worked on a Redmi 9A (MT6765) was recorded byte for byte
// (RUN 6) and compiled into a JSON recipe plus five binary images. The recipe is not a theory of the
// protocol: it is the ordered list of frames that the working boot put on the wire, with every reply
// classified by how it may safely be verified. Nothing here invents a value — a field that was not
// observed is absent, and an absent field makes the boot stop instead of guess.
//
// WHY A FLAT STEP LIST AND NOT "COMMAND + ARGS + REPLIES". The capture shows a status word arriving
// BETWEEN two argument writes (d4 -> addr -> echo -> value -> echo -> status -> next value). Grouping
// by command would destroy that interleaving and force the client to invent a rule about when to read.
// An ordered list of TX/RX/BLOB steps cannot lie about order.
//
// WHY REPLAY INSTEAD OF RE-IMPLEMENT. The MT6765 bypass is a long spray of BROM register writes whose
// individual meaning was never derived. Re-implementing it would mean writing down rules we do not
// have. Replaying it means the only things asserted are the things that were measured: every echo,
// every status word, every reply length, and the two frames that carry the meaning of success (0xc0
// from the jumped DA1, and the DA's own SYNC).
//
// CLEAN ROOM. mtkclient is GPL-3.0 and none of its sources were read for this file. The DA images are
// the post-patch bytes taken from that tool's own TX dumps — observing a program's output is not
// copying its source. Provenance: %TEMP%\mtk-da-spike\capture\framing-spec.md.
//
// REPLY KINDS.
//   echo  - the target repeated exactly what the host just sent. Compared byte for byte.
//   zero  - a status word recorded as zero. Non-zero stops the boot.
//   len   - reply DATA (register content, RAM content, chip ids, version words). Length is verified;
//           content is deliberately NOT, because it legitimately differs between units.
//   token - the value IS the meaning: "hw-code" proves the BROM face is real, "da-alive" (0xc0) proves
//           DA1 jumped, "sync" proves the DA is listening rather than merely present.
//   hdr   - a 12-byte EF EE EE FE DA header; magic and type verified, declared length must match the
//           payload frame that follows it.
//   ctrl-ack / ctrl-stall - an endpoint-zero control transfer, recorded as its whole setup packet plus its
//           data stage. RUN 38 (2026-11-01 15:36, passive USBPcap) measured six of them before every
//           pre-download jump: a CDC SET_LINE_CODING the device accepts (ctrl-ack) followed by a vendor
//           descriptor read the device stalls (ctrl-stall). A bulk log cannot see this traffic, which is
//           why the first 35 boots were byte-identical for 65 steps and were still refused.
//
// OPTIONAL STEPS ("opt": true, control steps only). The B6 capture (2026-11-03 13:27, MT6877) measured the phone
// taking 1,004 ms to accept the post-payload SET_LINE_CODING and answering the greet 1 ms after that: the request is
// recorded, it is worth replaying, and nothing downstream of it reads the line setting it configures. RUN B6-1 showed
// our own stack cannot always carry that one ask — the URB sat unfinished and the boot stopped on it. A step marked
// optional is therefore asked exactly like a strict one and, if the pipe cannot carry it, logged and stepped over on
// that pipe: no face reclaim (RUN B6-3 measured 64 open attempts and 12 s on a node the pinned transfer would not
// release) and no dispose, because the greet after it needs the live pipe. The next bulk step proves the boot. No
// recipe marks anything optional except those two handoff probes.

internal enum MtkReplyKind { Echo, Zero, LengthOnly, HwCode, DaAlive, Sync, Header, CtrlAck, CtrlStall, Complement }

internal enum MtkStepKind { Tx, Rx, Blob, Ctrl }

/// <summary>An expected reply, as recorded: how to verify it and how long it is.</summary>
internal sealed record MtkReply(MtkReplyKind Kind, int Bytes);

/// <summary>One frame of the recorded boot: bytes to send, bytes to expect, or an image to stream.
/// Optional marks a control step the recording saw accepted that the boot does not depend on — see the header.</summary>
internal sealed record MtkStep(MtkStepKind Kind, byte[] Payload, MtkReply Expect, string BlobFile, int ChunkSize,
    bool Optional = false);

internal sealed record MtkBlobRef(string File, int Bytes, string Sha256);

/// <summary>
/// A loaded, verified boot recipe. Loading is the fail-closed gate: a recipe whose blobs are missing,
/// truncated, swapped, or whose upload headers disagree with the images they declare is refused before
/// a single byte reaches a phone.
/// </summary>
internal sealed class MtkBootProfile
{
    private const int MaxSteps = 20000;
    private const int MaxBlobBytes = 64 * 1024 * 1024;
    private static readonly byte[] KnownBromCommands =
        { 0xfd, 0xd4, 0xd8, 0xfe, 0xff, 0xfc, 0xe1, 0xe7, 0xda, 0xd1, 0xd7, 0xd5, 0xa0, 0x0a, 0x50, 0x05 };

    private readonly string _blobDirectory;
    private readonly Dictionary<string, byte[]> _blobCache = new(StringComparer.OrdinalIgnoreCase);

    internal string Chipset { get; }
    internal ushort HwCode { get; }
    internal string Source { get; }
    internal string Note { get; }
    internal IReadOnlyList<MtkBlobRef> Blobs { get; }
    internal IReadOnlyList<MtkStep> BromSteps { get; }
    internal IReadOnlyList<MtkStep> DaSteps { get; }

    private MtkBootProfile(string blobDirectory, string chipset, ushort hwCode, string source, string note,
        IReadOnlyList<MtkBlobRef> blobs, IReadOnlyList<MtkStep> bromSteps, IReadOnlyList<MtkStep> daSteps)
    {
        _blobDirectory = blobDirectory;
        Chipset = chipset;
        HwCode = hwCode;
        Source = source;
        Note = note;
        Blobs = blobs;
        BromSteps = bromSteps;
        DaSteps = daSteps;
    }

    /// <summary>
    /// Where recipes live in an installed tool. They are not shipped yet: the DA images come from
    /// unlicensed packs, and that licensing call is PMK's, so an empty directory is a supported state
    /// — the boot then fails closed with a message naming exactly what is missing.
    /// </summary>
    internal static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "mtk", "boot-profiles");

    internal static IReadOnlyList<string> FindProfiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    /// <summary>Parse a recipe, then verify every blob and every framed upload it describes.</summary>
    internal static MtkBootProfile Load(string profilePath, string blobDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
            throw new FileNotFoundException("MTK boot recipe not found: " + profilePath);

        blobDirectory ??= Path.GetDirectoryName(Path.GetFullPath(profilePath));

        List<MtkBlobRef> blobs;
        List<MtkStep> bromSteps;
        List<MtkStep> daSteps;
        string chipset;
        ushort hwCode;
        string source;
        string note;

        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(profilePath)))
        {
            JsonElement root = doc.RootElement;
            chipset = ReadString(root, "chipset");
            hwCode = ParseU16(ReadString(root, "hwCode"), ChipsetLabel(chipset, "hwCode"));
            source = ReadString(root, "source");
            note = ReadOptionalString(root, "note");

            blobs = new List<MtkBlobRef>();
            foreach (JsonElement b in Enumerate(root, "blobs"))
                blobs.Add(new MtkBlobRef(ReadString(b, "file"), ReadInt(b, "bytes"),
                    ReadString(b, "sha256").ToLowerInvariant()));
            if (blobs.Count == 0)
                throw new InvalidDataException(ChipsetLabel(chipset) + " carries no blob registry.");
            var byName = blobs.ToDictionary(b => b.File, b => b, StringComparer.OrdinalIgnoreCase);

            bromSteps = ReadSteps(root, "bromSteps", byName, chipset, blobDirectory, isDa: false);
            daSteps = ReadSteps(root, "daSteps", byName, chipset, blobDirectory, isDa: true);
        }

        // The walk is only trusted if it starts the way a real BROM session starts.
        if (bromSteps.Count < 3 ||
            bromSteps[0].Kind != MtkStepKind.Tx || bromSteps[0].Payload.Length != 1 || bromSteps[0].Payload[0] != MtkBrom.GetHwCode ||
            bromSteps[1].Expect?.Kind != MtkReplyKind.Echo ||
            bromSteps[2].Expect?.Kind != MtkReplyKind.HwCode)
        {
            throw new InvalidDataException(ChipsetLabel(chipset) + " does not open with the HW-code query and its reply, " +
                "so this recipe never proves the device is really in BROM. Boot refused.");
        }
        if (daSteps.All(s => s.Expect?.Kind != MtkReplyKind.Sync))
            throw new InvalidDataException(ChipsetLabel(chipset) + " never waits for the DA's own SYNC frame, " +
                "so it cannot tell a live DA from a stale one. Boot refused.");

        return new MtkBootProfile(blobDirectory, chipset, hwCode, source, note, blobs, bromSteps, daSteps);
    }

    /// <summary>The image bytes for a registry entry, re-verified against the recorded hash on first use.</summary>
    internal byte[] GetBlob(string name)
    {
        if (!_blobCache.TryGetValue(name, out byte[] data))
        {
            MtkBlobRef reference = Blobs.FirstOrDefault(b => string.Equals(b.File, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(ChipsetLabel(Chipset) + " does not describe blob " + name + ".");
            data = VerifyBlobFile(_blobDirectory, reference, Chipset);
            _blobCache[name] = data;
        }
        return data;
    }

    // ---- step lists --------------------------------------------------------------------------------

    private static List<MtkStep> ReadSteps(JsonElement root, string arrayName, Dictionary<string, MtkBlobRef> byName,
        string chipset, string blobDirectory, bool isDa)
    {
        var steps = new List<MtkStep>();
        foreach (JsonElement s in Enumerate(root, arrayName))
        {
            string kind = ReadString(s, "t");
            switch (kind)
            {
                case "tx":
                {
                    byte[] payload = ParseHex(ReadString(s, "hex"), ChipsetLabel(chipset, arrayName + " tx"));
                    if (!isDa && payload.Length == 1 && Array.IndexOf(KnownBromCommands, payload[0]) < 0)
                        throw new InvalidDataException(ChipsetLabel(chipset) + " BROM recipe uses command 0x" +
                            payload[0].ToString("X2") + ", which was never observed on this wire.");
                    steps.Add(new MtkStep(MtkStepKind.Tx, payload, null, null, 0));
                    break;
                }
                case "rx":
                    steps.Add(new MtkStep(MtkStepKind.Rx, null, ReadReply(s, chipset, arrayName), null, 0));
                    break;
                case "blob":
                {
                    string file = ReadString(s, "file");
                    if (!byName.TryGetValue(file, out MtkBlobRef reference))
                        throw new InvalidDataException(ChipsetLabel(chipset) + " uploads " + file + ", which its blob registry does not describe.");
                    int bytes = ReadInt(s, "bytes");
                    int chunk = ReadInt(s, "chunk");
                    if (bytes != reference.Bytes)
                        throw new InvalidDataException(ChipsetLabel(chipset) + " disagrees with itself about the size of " + file + ".");
                    if (chunk <= 0 || chunk > MaxBlobBytes)
                        throw new InvalidDataException(ChipsetLabel(chipset) + " declares an implausible upload chunk of " + chunk + " bytes.");
                    bool framed = s.TryGetProperty("framed", out JsonElement f) && f.ValueKind == JsonValueKind.True;
                    // A DA-stage upload is preceded by a header frame declaring the image length. If that
                    // header disagrees with the file on disk, the phone would be fed a different DA than
                    // the one the recipe was measured with — refuse, do not send.
                    if (framed)
                    {
                        if (steps.Count == 0 || steps[^1].Kind != MtkStepKind.Tx || steps[^1].Payload.Length != MtkDaFrame.HeaderSize)
                            throw new InvalidDataException(ChipsetLabel(chipset) + " frames a blob upload with no DA header before it.");
                        if (!MtkDaFrame.TryParseHeader(steps[^1].Payload, out uint declared))
                            throw new InvalidDataException(ChipsetLabel(chipset) + " frames " + file + " with a header that is not an MTK DA header.");
                        if (declared != (uint)bytes)
                            throw new InvalidDataException(ChipsetLabel(chipset) + " declares " + file + " as " + declared +
                                " bytes in its header but the recorded image is " + bytes + " bytes.");
                    }
                    VerifyBlobFile(blobDirectory, reference, chipset);
                    steps.Add(new MtkStep(MtkStepKind.Blob, null, null, file, chunk));
                    break;
                }
                case "ctrl":
                {
                    // An endpoint-zero control transfer, recorded whole: the 8-byte setup packet followed by
                    // its data stage when the request is host-to-device. RUN 38 measured the two prelude shapes
                    // and RUN 39 added the data-less SET_CONTROL_LINE_STATE the payload's re-open uses — the
                    // loader accepts exactly those, anything else means the recipe was edited by hand.
                    byte[] frame = ParseHex(ReadString(s, "hex"), ChipsetLabel(chipset, arrayName + " ctrl"));
                    if (frame.Length < 8)
                        throw new InvalidDataException(ChipsetLabel(chipset) + " records a control step shorter than " +
                            "its 8-byte setup packet, which is not a transfer the wire could have carried.");
                    ushort wLength = (ushort)(frame[6] | frame[7] << 8);
                    MtkReply expect = ReadReply(s, chipset, arrayName);
                    if (expect.Kind == MtkReplyKind.CtrlAck)
                    {
                        // wLength 0 is legal (RUN 39 measured SET_CONTROL_LINE_STATE accepted with no data stage);
                        // the shape that is not negotiable is host-to-device with the whole data stage in the frame.
                        if ((frame[0] & 0x80) != 0 || frame.Length != 8 + wLength)
                            throw new InvalidDataException(ChipsetLabel(chipset) + " records an accepted control transfer " +
                                "that is not the measured shape (a host-to-device request whose whole data stage is in the frame). " +
                                "The boot was refused before anything was loaded.");
                    }
                    else if (expect.Kind == MtkReplyKind.CtrlStall)
                    {
                        if ((frame[0] & 0x80) == 0 || frame.Length != 8 || wLength == 0)
                            throw new InvalidDataException(ChipsetLabel(chipset) + " records a refused control read that is not " +
                                "the measured shape (a device-to-host request carrying no data, refused by the device itself). " +
                                "The boot was refused before anything was loaded.");
                    }
                    else
                        throw new InvalidDataException(ChipsetLabel(chipset) + " records a control step whose reply kind " +
                            expect.Kind + " a control transfer cannot carry.");
                    if (expect.Bytes != wLength)
                        throw new InvalidDataException(ChipsetLabel(chipset) + " disagrees with itself about a control " +
                            "transfer's length (" + expect.Bytes + " expected vs " + wLength + " in the setup packet).");
                    bool optional = s.TryGetProperty("opt", out JsonElement o) && o.ValueKind == JsonValueKind.True;
                    steps.Add(new MtkStep(MtkStepKind.Ctrl, frame, expect, null, 0, optional));
                    break;
                }
                default:
                    throw new InvalidDataException(ChipsetLabel(chipset) + " uses an unknown step kind '" + kind + "'.");
            }
            if (steps.Count > MaxSteps)
                throw new InvalidDataException(ChipsetLabel(chipset) + " has more than " + MaxSteps + " steps, which is not a usable recipe.");
        }
        if (steps.Count == 0)
            throw new InvalidDataException(ChipsetLabel(chipset) + " has no '" + arrayName + "' steps, so there is nothing to replay.");
        return steps;
    }

    private static MtkReply ReadReply(JsonElement step, string chipset, string arrayName)
    {
        string kind = ReadString(step, "k");
        int bytes = ReadInt(step, "bytes");
        // Zero is legal only for a data-less control write (SET_CONTROL_LINE_STATE, RUN 39): the transfer is
        // accepted with no data stage, so there is no reply to read and no frame length to carry.
        if (bytes < 0 || bytes > MaxBlobBytes || (bytes == 0 && kind != "ctrl-ack"))
            throw new InvalidDataException(ChipsetLabel(chipset) + " expects a reply of " + bytes + " bytes, which is not a frame length.");
        if (kind == "token" || kind is "sync" or "hw-code" or "da-alive")
        {
            // The capture script writes the BROM tokens as {"k":"token","what":...} and the DA's SYNC frame as the
            // bare kind {"k":"sync"}. Both spellings mean the same thing, so both are accepted — but a bare token
            // still has to carry exactly the byte count that was measured on the wire, which is what the switch
            // below enforces.
            string what = kind == "token" ? ReadString(step, "what") : kind;
            return what switch
            {
                "hw-code" when bytes == 4 => new MtkReply(MtkReplyKind.HwCode, 4),
                "da-alive" when bytes == 1 => new MtkReply(MtkReplyKind.DaAlive, 1),
                "sync" when bytes == 4 => new MtkReply(MtkReplyKind.Sync, 4),
                _ => throw new InvalidDataException(ChipsetLabel(chipset) + " records an unsupported token '" + what +
                    "' (" + bytes + " bytes) in " + arrayName + ".")
            };
        }
        if (kind is "ctrl-ack" or "ctrl-stall")
            return new MtkReply(kind == "ctrl-ack" ? MtkReplyKind.CtrlAck : MtkReplyKind.CtrlStall, bytes);
        return kind switch
        {
            "echo" => new MtkReply(MtkReplyKind.Echo, bytes),
            "zero" => new MtkReply(MtkReplyKind.Zero, bytes),
            "len" => new MtkReply(MtkReplyKind.LengthOnly, bytes),
            "complement" when bytes == 1 => new MtkReply(MtkReplyKind.Complement, 1),
            "complement" => throw new InvalidDataException(ChipsetLabel(chipset) + " records a handshake complement of " + bytes +
                " byte(s); the greet's answer is exactly one byte, so the step was refused."),
            "hdr" => new MtkReply(MtkReplyKind.Header, MtkDaFrame.HeaderSize),
            _ => throw new InvalidDataException(ChipsetLabel(chipset) + " uses an unknown reply kind '" + kind + "' in " + arrayName + ".")
        };
    }

    // ---- blob verification -------------------------------------------------------------------------

    private static byte[] VerifyBlobFile(string directory, MtkBlobRef reference, string chipset)
    {
        string path = Path.Combine(directory ?? "", reference.File);
        if (!File.Exists(path))
            throw new FileNotFoundException("Boot recipe needs " + reference.File + " (" + reference.Bytes + " bytes) and it is not in " +
                (directory ?? "(no directory)") + ".");

        byte[] data = File.ReadAllBytes(path);
        if (data.Length != reference.Bytes)
            throw new InvalidDataException(reference.File + " is " + data.Length + " bytes but " + ChipsetLabel(chipset) +
                " recorded " + reference.Bytes + ". A truncated DA image bricks phones, so the boot was refused.");
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!string.Equals(hash, reference.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException(reference.File + " does not match the recipe (sha256 " + hash[..16] +
                "..., expected " + reference.Sha256[..16] + "...). A swapped DA image is not a supported state, so the boot was refused.");
        return data;
    }

    // ---- JSON reading, tolerant of the two shapes a single element can take ------------------------

    private static IEnumerable<JsonElement> Enumerate(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) yield break;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) yield return item;
            yield break;
        }
        if (value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString()))
            yield break;
        yield return value;   // a single object written without its array brackets
    }

    private static string ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Boot recipe is missing the required field '" + name + "'.");
        return value.GetString();
    }

    private static string ReadOptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";

    private static int ReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException("Boot recipe is missing the required numeric field '" + name + "'.");
        return value.GetInt32();
    }

    internal static ushort ParseU16(string text, string what)
    {
        string body = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!ushort.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
            throw new InvalidDataException(what + " is not a 16-bit hex value: '" + text + "'.");
        return value;
    }

    internal static uint ParseU32(string text, string what)
    {
        string body = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!uint.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            throw new InvalidDataException(what + " is not a 32-bit hex value: '" + text + "'.");
        return value;
    }

    private static byte[] ParseHex(string hex, string what)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit))
            throw new InvalidDataException(what + " carries a malformed hex frame: '" + (hex ?? "") + "'.");
        var data = new byte[hex.Length / 2];
        for (int i = 0; i < data.Length; i++)
            data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return data;
    }

    private static string ChipsetLabel(string chipset) => "MTK boot recipe " + chipset;
    private static string ChipsetLabel(string chipset, string field) => "MTK boot recipe " + chipset + " field '" + field + "'";
}

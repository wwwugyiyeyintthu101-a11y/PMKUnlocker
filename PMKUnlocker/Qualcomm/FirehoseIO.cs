using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PMKUnlocker.Qualcomm;

internal sealed record FirehoseResponse(string Xml, string Value, string? RawMode)
{
    internal bool IsAck => Value == "ACK";
}

internal sealed class FirehoseIO
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly byte[] EnvelopeEnd = Encoding.ASCII.GetBytes("</data>");
    private readonly Func<byte[], int, int, int> _readAvailable;
    private readonly byte[] _buffer = new byte[65536];
    private int _start;
    private int _end;

    internal FirehoseIO(Func<byte[], int, int, int> readAvailable) => _readAvailable = readAvailable;

    internal void DiscardBuffered() => _start = _end = 0;

    private async Task FillAsync(long deadline, CancellationToken ct, Func<bool>? stopped)
    {
        CheckStopped(ct, stopped);
        if (_start < _end) return;
        while (Environment.TickCount64 < deadline)
        {
            CheckStopped(ct, stopped);
            int count = _readAvailable(_buffer, 0, _buffer.Length);
            if (count > 0)
            {
                _start = 0;
                _end = count;
                return;
            }
            await Task.Delay(20, ct);
        }
        throw new TimeoutException("Firehose receive timeout.");
    }

    private static void CheckStopped(CancellationToken ct, Func<bool>? stopped)
    {
        ct.ThrowIfCancellationRequested();
        if (stopped?.Invoke() == true) throw new OperationCanceledException("STOP");
    }

    internal async Task<FirehoseResponse> ReadResponseAsync(int timeoutMs, CancellationToken ct,
        Func<bool>? stopped = null)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        using var envelope = new MemoryStream();
        var xmlText = new StringBuilder();
        int total = 0;
        while (true)
        {
            await FillAsync(deadline, ct, stopped);
            while (_start < _end)
            {
                CheckStopped(ct, stopped);
                if (++total > MaxResponseBytes)
                    throw new InvalidDataException("Firehose XML response exceeds 64 KiB.");
                envelope.WriteByte(_buffer[_start++]);
                int length = (int)envelope.Length;
                if (length < EnvelopeEnd.Length ||
                    !envelope.GetBuffer().AsSpan(length - EnvelopeEnd.Length, EnvelopeEnd.Length).SequenceEqual(EnvelopeEnd))
                    continue;

                string xml = Encoding.UTF8.GetString(envelope.GetBuffer(), 0, length).TrimStart(' ', '\t', '\r', '\n');
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxResponseBytes
                });
                var document = XDocument.Load(reader);
                if (document.Root?.Name != "data")
                    throw new InvalidDataException("Expected a Firehose data envelope.");
                var responses = document.Root.Elements("response").ToList();
                if (responses.Count > 1)
                    throw new InvalidDataException("Ambiguous Firehose response.");
                xmlText.Append(xml);
                if (responses.Count == 1)
                {
                    var response = responses[0];
                    return new FirehoseResponse(xmlText.ToString(), (string?)response.Attribute("value") ?? "",
                        (string?)response.Attribute("rawmode"));
                }
                if (!document.Root.Elements("log").Any())
                    throw new InvalidDataException("Firehose envelope contains no response or log.");
                envelope.SetLength(0);
                envelope.Position = 0;
            }
            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException("Firehose response timeout.");
        }
    }

    internal async Task ReadPayloadAsync(long total, Stream sink, Action<int>? progress,
        CancellationToken ct, Func<bool>? stopped = null, int timeoutMs = 30000)
    {
        if (total <= 0) throw new ArgumentOutOfRangeException(nameof(total));
        var initial = await ReadResponseAsync(timeoutMs, ct, stopped);
        if (!initial.IsAck || initial.RawMode != "true")
            throw new IOException("Firehose read did not enter raw mode with ACK.");

        long received = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (received < total)
        {
            await FillAsync(deadline, ct, stopped);
            int count = (int)Math.Min(total - received, _end - _start);
            await sink.WriteAsync(_buffer.AsMemory(_start, count), ct);
            _start += count;
            received += count;
            deadline = Environment.TickCount64 + timeoutMs;
            progress?.Invoke((int)Math.Min(99, received * 100 / total));
        }

        // Only the XML reader consumes the terminal ACK; raw bytes are never trimmed or searched.
        var final = await ReadResponseAsync(timeoutMs, ct, stopped);
        if (!final.IsAck || final.RawMode != "false")
            throw new IOException("Firehose read did not finish with ACK and rawmode=false.");
        await sink.FlushAsync(ct);
        progress?.Invoke(100);
    }
}

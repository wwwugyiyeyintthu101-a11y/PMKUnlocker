using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PMKUnlocker
{
    /// <summary>One observed outcome of an exploit entry on one boot.</summary>
    public sealed class BootLedgerEntry
    {
        public string Serial { get; set; } = "";
        public string BootId { get; set; } = "";
        public string Entry { get; set; } = "";

        /// <summary>"rooted", "panic" or "failed-alive" (PanicProbe.Key).</summary>
        public string Verdict { get; set; } = "";

        /// <summary>UTC ISO-8601, for the log line and the newest-first cap.</summary>
        public string At { get; set; } = "";
    }

    /// <summary>
    /// Per-boot memory of what already happened, so a second run on the same boot cannot repeat a
    /// profile that just panicked or a profile that already produced root. Pure logic: the caller
    /// owns the file, which keeps every rule assertable without a phone.
    /// </summary>
    public sealed class BootLedger
    {
        /// <summary>Small enough to stay readable, big enough for a day of shop runs.</summary>
        public const int MaxEntries = 40;

        public List<BootLedgerEntry> Entries { get; } = new List<BootLedgerEntry>();

        /// <summary>Reads the on-disk shape; a missing, empty or corrupt file is an empty ledger.</summary>
        public static BootLedger FromJson(string? json)
        {
            var ledger = new BootLedger();
            if (string.IsNullOrWhiteSpace(json)) return ledger;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json!);
                if (!doc.RootElement.TryGetProperty("entries", out JsonElement rows) ||
                    rows.ValueKind != JsonValueKind.Array)
                    return ledger;
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    string Read(string name) =>
                        row.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                            ? (value.GetString() ?? "") : "";
                    var entry = new BootLedgerEntry
                    {
                        Serial = Read("serial"),
                        BootId = Read("bootId"),
                        Entry = Read("entry"),
                        Verdict = Read("verdict"),
                        At = Read("at"),
                    };
                    if (entry.BootId.Length == 0 || entry.Entry.Length == 0 || entry.Verdict.Length == 0)
                        continue;
                    ledger.Entries.Add(entry);
                }
            }
            catch (JsonException) { return new BootLedger(); }
            return ledger;
        }

        public string ToJson() => JsonSerializer.Serialize(
            new
            {
                version = 1,
                entries = Entries.Select(e => new
                {
                    serial = e.Serial, bootId = e.BootId, entry = e.Entry, verdict = e.Verdict, at = e.At,
                }),
            },
            new JsonSerializerOptions { WriteIndented = true });

        /// <summary>
        /// Records one verdict, replacing an older row for the same (serial, boot, entry) and
        /// dropping the oldest rows past <see cref="MaxEntries"/>. An unknown verdict is not recorded.
        /// </summary>
        public void Record(string serial, string bootId, string entry, BootVerdict verdict, DateTime utc)
        {
            if (string.IsNullOrWhiteSpace(bootId) || string.IsNullOrWhiteSpace(entry)) return;
            string key = PanicProbe.Key(verdict);
            if (key.Length == 0) return;
            serial = serial ?? "";
            Entries.RemoveAll(e => e.Serial == serial && e.BootId == bootId && e.Entry == entry);
            Entries.Add(new BootLedgerEntry
            {
                Serial = serial, BootId = bootId, Entry = entry, Verdict = key,
                At = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            });
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        }

        public IReadOnlyList<BootLedgerEntry> ForBoot(string serial, string bootId)
            => Entries.Where(e => e.Serial == (serial ?? "") && e.BootId == bootId).ToList();

        public BootLedgerEntry? Last(string serial, string bootId, string entry)
            => Entries.LastOrDefault(e => e.Serial == (serial ?? "") && e.BootId == bootId && e.Entry == entry);

        /// <summary>
        /// Why this entry must not run again on this boot, or null when a retry is allowed.
        /// Root is terminal; a panic says the kernel does not fit this profile and a reboot is required.
        /// </summary>
        public string? BlockReason(string serial, string bootId, string entry)
        {
            BootLedgerEntry? last = Last(serial, bootId, entry);
            if (last == null) return null;
            return last.Verdict == "rooted" || last.Verdict == "panic" ? last.Verdict : null;
        }

        /// <summary>
        /// Drops rows for other boots of the same phone once the current boot is known: a panic or a
        /// root from an older boot can never block or excuse this one.
        /// </summary>
        public int PruneOtherBoots(string serial, string bootId)
        {
            if (string.IsNullOrWhiteSpace(bootId)) return 0;
            return Entries.RemoveAll(e => e.Serial == (serial ?? "") && e.BootId != bootId);
        }
    }
}

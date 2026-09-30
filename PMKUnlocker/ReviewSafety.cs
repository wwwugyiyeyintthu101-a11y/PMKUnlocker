using System.Diagnostics;
using System.Xml.Linq;

namespace PMKUnlocker;

internal static class ReviewSafety
{
    internal static bool ShouldReboot(bool enabled, bool didRun, bool failed, bool stopped, bool dryRun) =>
        enabled && didRun && !failed && !stopped && !dryRun;

    internal static async Task<bool> ExecuteUnlessDryRunAsync(bool dryRun, Func<Task<bool>> command) =>
        !dryRun && await command();

    internal static List<string> ExtractImages(string archive, string directory)
    {
        var files = new List<string>();
        using var stream = File.OpenRead(archive);
        using var reader = new System.Formats.Tar.TarReader(stream);
        System.Formats.Tar.TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (entry.EntryType != System.Formats.Tar.TarEntryType.RegularFile &&
                entry.EntryType != System.Formats.Tar.TarEntryType.V7RegularFile) continue;
            string name = Path.GetFileName(entry.Name);
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext != ".img" && ext != ".bin") continue;
            string destination = Path.Combine(directory, name);
            entry.ExtractToFile(destination, overwrite: true);
            files.Add(destination);
        }
        return files;
    }

    internal static void FilterUserdata(string source, string destination)
    {
        var doc = XDocument.Load(source);
        if (doc.Root?.Name != "data" || !doc.Descendants("program").Any())
            throw new InvalidDataException("Unrecognized rawprogram XML; flash cancelled.");
        doc.Descendants("program").Where(x =>
            ((string?)x.Attribute("label") ?? "").Contains("userdata", StringComparison.OrdinalIgnoreCase) ||
            ((string?)x.Attribute("filename") ?? "").Contains("userdata", StringComparison.OrdinalIgnoreCase)).Remove();
        if (!doc.Descendants("program").Any())
            throw new InvalidDataException("No partitions remain after skipping userdata.");
        doc.Save(destination);
    }

    internal static long ParseSize(string value)
    {
        var text = value.Trim();
        long size = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(text[2..], 16)
            : long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        if (size <= 0) throw new InvalidDataException("Partition size is missing or invalid.");
        return size;
    }

    internal static bool HasBackup(string path, long expectedBytes) =>
        expectedBytes > 0 && File.Exists(path) && new FileInfo(path).Length == expectedBytes;

    internal static async Task<int> RunSequenceAsync(int count, Func<bool> stopped, Func<int, Task<bool>> step)
    {
        int done = 0;
        while (done < count && !stopped())
        {
            if (!await step(done)) break;
            done++;
        }
        return done;
    }

    internal static Dictionary<string, long> PrepareEfsBackup(string source, string directory)
    {
        var doc = XDocument.Load(source);
        string[] names = { "persist", "modemst1", "modemst2" };
        var entries = names.Select(name =>
        {
            var matches = doc.Descendants("program").Where(x =>
                string.Equals((string?)x.Attribute("label"), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || !long.TryParse((string?)matches[0].Attribute("num_partition_sectors"), out var sectors) || sectors <= 0)
                throw new InvalidDataException("Missing or ambiguous EFS layout: " + name);
            var entry = new XElement(matches[0]);
            entry.SetAttributeValue("filename", name + ".bin");
            entry.SetAttributeValue("file_sector_offset", "0");
            entry.SetAttributeValue("sparse", "false");
            return entry;
        }).ToArray();
        Directory.CreateDirectory(directory);
        new XDocument(new XElement("data", entries)).Save(Path.Combine(directory, "efs_read.xml"));
        return entries.ToDictionary(e => Path.Combine(directory, (string)e.Attribute("filename")!),
            e => checked(ParseSize((string?)e.Attribute("num_partition_sectors") ?? "0") *
                         ParseSize((string?)e.Attribute("SECTOR_SIZE_IN_BYTES") ?? "0")));
    }

    internal static async Task<string> RunQuickAsync(string fileName, string arguments, int timeoutMs = 15000,
        bool returnOutputOnFailure = false)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "";
            }
            if (process.ExitCode != 0 && !returnOutputOnFailure) return "";
            return (string.IsNullOrWhiteSpace(stdout.Result) ? stderr.Result : stdout.Result).Trim();
        }
        catch { return ""; }
    }
}

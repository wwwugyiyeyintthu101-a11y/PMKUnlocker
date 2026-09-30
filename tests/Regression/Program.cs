using PMKUnlocker;
using System.Diagnostics;
using System.Formats.Tar;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

if (args.Length > 0)
{
    if (args[0] == "flood")
    {
        Console.Error.Write(new string('e', 200000));
        Console.Out.Write("finished");
    }
    if (args[0] == "sleep") await Task.Delay(30000);
    if (args[0] == "fail") { Console.Error.Write("device error"); Environment.ExitCode = 1; }
    return;
}
int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
    passed++;
}
string dir = Path.Combine(Path.GetTempPath(), "pmk-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var session = new QcLoaderSession { LoaderPath = "old.elf", Authenticated = true, Port = "COM7" };
    session.LoaderPath = "new.elf";
    Check(!session.Authenticated && session.Port == "" && session.LoaderPath == "new.elf", "Changing loader invalidates previous auth on the same port");
    session.Authenticated = true; session.Port = "COM7";
    session.LoaderPath = "";
    Check(!session.Authenticated && session.Port == "" && session.LoaderPath == "", "Auto Detect clears manual loader and authentication");
    session.LoaderPath = "saved.elf";
    Check(session.LoaderPath == "saved.elf" && !session.Authenticated, "Saved loader does not restore a stale authenticated session");
    string multiple = "List of devices attached\none\tdevice\ntwo\tdevice\nblocked\tunauthorized\n";
    Check(ShopServices.DeviceSerials(multiple, true).Length == 2, "Unauthorized ADB devices cannot be selected");
    Check(ShopServices.SelectSerial(multiple, true, "two") == "two", "Explicit serial wins with multiple phones");
    bool multiRejected = false;
    try { ShopServices.SelectSerial(multiple, true, ""); } catch (InvalidOperationException) { multiRejected = true; }
    Check(multiRejected, "Multiple devices require explicit selection");
    multiRejected = false;
    try { ShopServices.SelectSerial("other\tdevice", true, "missing"); } catch (InvalidOperationException) { multiRejected = true; }
    Check(multiRejected, "Disconnected selection never switches to another phone");
    Check(ShopServices.SelectSerial("abc\tfastboot", false, "") == "abc", "Single fastboot device can be pinned");
    Check(ShopServices.ResolvePreferred("List of devices attached\nnew\tdevice\n", true, "old") == "",
        "Stale ADB pin clears when a single phone remains (device swap)");
    Check(ShopServices.ResolvePreferred("List of devices attached\na\tdevice\nb\tdevice\n", true, "old") == "old",
        "Stale ADB pin kept when multiple phones (no silent switch)");
    Check(ShopServices.ResolvePreferred("List of devices attached\nold\tdevice\n", true, "old") == "old",
        "Connected preferred pin is kept");
    Check(ShopServices.ResolvePreferred("List of devices attached\n", true, "old") == "old",
        "No phones — stale pin kept so SelectSerial rejects");

    string backupDir = Path.Combine(dir, "verified-backup"); Directory.CreateDirectory(backupDir);
    var layout = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["nvram"] = 16, ["boot"] = 4096 };
    File.WriteAllBytes(Path.Combine(backupDir, "nvram.bin"), new byte[16]);
    ShopServices.WriteBackup(backupDir, "MTK", layout, new[] { "nvram" }, "test");
    var verified = ShopServices.VerifyBackup(backupDir, layout);
    Check(verified.Files.Count == 1 && verified.DeviceIdentity.StartsWith("Unknown"), "Manifest verifies backup without inventing device identity");
    var changedLayout = new Dictionary<string, long>(layout) { ["nvram"] = 32 };
    bool mismatch = false;
    try { ShopServices.VerifyBackup(backupDir, changedLayout); } catch (InvalidDataException) { mismatch = true; }
    Check(mismatch, "Restore rejects a mismatched target GPT");
    File.WriteAllBytes(Path.Combine(backupDir, "nvram.bin"), Enumerable.Repeat((byte)1, 16).ToArray());
    mismatch = false;
    try { ShopServices.VerifyBackup(backupDir); } catch (InvalidDataException) { mismatch = true; }
    Check(mismatch, "Checksum detects same-size backup corruption");
    mismatch = false;
    try { ShopServices.SafeChild(backupDir, "../outside.bin"); } catch (InvalidDataException) { mismatch = true; }
    Check(mismatch, "Manifest cannot reference paths outside the backup");
    mismatch = false;
    try { ShopServices.WriteBackup(backupDir, "MTK", changedLayout, new[] { "nvram" }, "test"); } catch (InvalidDataException) { mismatch = true; }
    Check(mismatch, "Incomplete files cannot receive a valid manifest");

    string firmwareDir = Path.Combine(dir, "firmware"); Directory.CreateDirectory(firmwareDir);
    File.WriteAllBytes(Path.Combine(firmwareDir, "boot.img"), new byte[32]);
    Check(ShopServices.CheckImages(firmwareDir, layout, false).Count == 0, "Raw image fitting the device GPT passes size preflight");
    File.WriteAllBytes(Path.Combine(firmwareDir, "unknown.img"), new byte[1]);
    Check(ShopServices.CheckImages(firmwareDir, layout, false).Count == 1, "Unknown partition blocks preflight");
    File.Delete(Path.Combine(firmwareDir, "unknown.img"));
    using (var sparse = new BinaryWriter(File.Create(Path.Combine(firmwareDir, "boot.img"))))
    {
        sparse.Write(0xed26ff3aU); sparse.Write((ushort)1); sparse.Write((ushort)0);
        sparse.Write((ushort)28); sparse.Write((ushort)12); sparse.Write(4096U); sparse.Write(8U); sparse.Write(0U); sparse.Write(0U);
    }
    Check(ShopServices.ExpandedImageSize(Path.Combine(firmwareDir, "boot.img")) == 32768, "Sparse preflight uses expanded size, not file length");
    Check(ShopServices.CheckImages(firmwareDir, layout, false).Count == 1, "Oversized sparse image blocks preflight");
    int launched = 0;
    bool dryResult = await ReviewSafety.ExecuteUnlessDryRunAsync(true, () => { launched++; return Task.FromResult(true); });
    Check(!dryResult && launched == 0, "Dry-run never invokes the device command");
    bool liveResult = await ReviewSafety.ExecuteUnlessDryRunAsync(false, () => { launched++; return Task.FromResult(true); });
    Check(liveResult && launched == 1, "Live command runs once and returns its result");
    Check(!await ReviewSafety.ExecuteUnlessDryRunAsync(false, () => Task.FromResult(false)), "Command failure is preserved");
    Check(ReviewSafety.ShouldReboot(true, true, false, false, false), "Successful operation permits enabled auto-reboot");
    Check(!ReviewSafety.ShouldReboot(true, true, true, false, false), "Failed operation cannot auto-reboot");
    Check(!ReviewSafety.ShouldReboot(true, false, false, false, false), "Cancelled dialog without a command cannot auto-reboot");
    Check(!ReviewSafety.ShouldReboot(true, true, false, true, false), "STOP prevents auto-reboot");
    Check(!ReviewSafety.ShouldReboot(true, true, false, false, true), "Dry-run prevents auto-reboot");
    Check(!ReviewSafety.ShouldReboot(false, true, false, false, false), "Disabled auto-reboot is respected");
    string source = Path.Combine(dir, "rawprogram.xml"), target = Path.Combine(dir, "filtered.xml");
    File.WriteAllText(source, "<data><program label='boot' filename='boot.img'/><program label='USERDATA' filename='data.img'/></data>");
    ReviewSafety.FilterUserdata(source, target);
    Check(XDocument.Load(target).Descendants("program").Count() == 1 && File.ReadAllText(source).Contains("USERDATA"), "Skip userdata preserves original XML and boot entry");
    File.WriteAllText(source, "<unexpected/>");
    bool rejected = false;
    try { ReviewSafety.FilterUserdata(source, target); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Unknown XML is rejected");
    File.WriteAllText(source, "<data><program label='userdata'/></data>");
    rejected = false;
    try { ReviewSafety.FilterUserdata(source, target); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Empty flash plan is rejected");
    File.WriteAllText(source, "<data><program label='persist' num_partition_sectors='8'/></data>");
    rejected = false;
    try { ReviewSafety.PrepareEfsBackup(source, dir); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Incomplete EFS layout is rejected");
    File.WriteAllText(source, "<data>" + string.Join("", new[] { "persist", "modemst1", "modemst2" }.Select(n => $"<program label='{n}' num_partition_sectors='8' SECTOR_SIZE_IN_BYTES='512' start_sector='16' physical_partition_number='0'/>")) + "</data>");
    var backups = ReviewSafety.PrepareEfsBackup(source, dir);
    Check(backups.Count == 3 && backups.Values.All(size => size == 4096) && XDocument.Load(Path.Combine(dir, "efs_read.xml")).Descendants("program").All(e => (string?)e.Attribute("sparse") == "false"), "EFS plan computes expected byte sizes");
    string backup = backups.Keys.First();
    File.WriteAllBytes(backup, Array.Empty<byte>());
    Check(!ReviewSafety.HasBackup(backup, 4096) && !ReviewSafety.HasBackup(backups.Keys.Last(), 4096), "Missing and empty backups are rejected");
    File.WriteAllBytes(backup, new byte[1]);
    Check(!ReviewSafety.HasBackup(backup, 4096), "One-byte truncated backup is rejected");
    File.WriteAllBytes(backup, new byte[4097]);
    Check(!ReviewSafety.HasBackup(backup, 4096), "Oversized backup is rejected");
    File.WriteAllBytes(backup, new byte[4096]);
    Check(ReviewSafety.HasBackup(backup, 4096) && !ReviewSafety.HasBackup(backup, 0), "Only exact positive backup size is accepted");
    Check(ReviewSafety.ParseSize("0x1000") == 4096 && ReviewSafety.ParseSize("4096") == 4096, "Hex and decimal partition sizes agree");
    File.WriteAllText(source, File.ReadAllText(source).Replace(" SECTOR_SIZE_IN_BYTES='512'", ""));
    rejected = false;
    try { ReviewSafety.PrepareEfsBackup(source, dir); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Unknown sector size prevents backup verification");
    int calls = 0;
    int completed = await ReviewSafety.RunSequenceAsync(3, () => false, i => { calls++; return Task.FromResult(i != 1); });
    Check(completed == 1 && calls == 2, "Failure stops the sequence before the third partition");
    bool stopped = false;
    calls = 0;
    completed = await ReviewSafety.RunSequenceAsync(2, () => stopped, i => { calls++; stopped = true; return Task.FromResult(true); });
    Check(completed == 1 && calls == 1, "STOP between backups prevents the next command");
    calls = 0;
    completed = await ReviewSafety.RunSequenceAsync(2, () => true, i => { calls++; return Task.FromResult(true); });
    Check(completed == 0 && calls == 0, "STOP before sequence starts prevents all commands");
    string archive = Path.Combine(dir, "firmware.tar.md5");
    using (var fs = File.Create(archive))
    using (var writer = new TarWriter(fs, TarEntryFormat.Ustar))
    {
        var entry = new UstarTarEntry(TarEntryType.RegularFile, "boot.img") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("firmware payload")) };
        writer.WriteEntry(entry);
    }
    using (var fs = new FileStream(archive, FileMode.Append)) fs.Write(Encoding.ASCII.GetBytes(new string('a', 32) + "\n"));
    var images = ReviewSafety.ExtractImages(archive, dir);
    Check(images.Count == 1 && File.ReadAllText(images[0]) == "firmware payload", "Tar with appended checksum extracts correctly by streaming");
    string host = Environment.ProcessPath!;
    string prefix = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? "\"" + Assembly.GetExecutingAssembly().Location + "\" " : "";
    Check(await ReviewSafety.RunQuickAsync(host, prefix + "flood") == "finished", "Large stderr cannot deadlock stdout reading");
    Check(await ReviewSafety.RunQuickAsync(host, prefix + "fail") == "", "Failed process output cannot become a device identity");
    var sw = Stopwatch.StartNew();
    Check(await ReviewSafety.RunQuickAsync(host, prefix + "sleep", 300) == "" && sw.Elapsed.TotalSeconds < 5, "Hung process is stopped at timeout");
    // Kernel exploit (ART-style) — manifest match rules
    var emptyMani = new ExploitManifest();
    var anyFp = new DeviceFingerprint { Android = "16", Kernel = "6.12.10-android16", Brand = "Xiaomi", Model = "25060PN58G" };
    Check(KernelExploit.FindMatch(emptyMani, anyFp, out string st0) == null && st0.Contains("empty"),
        "Empty exploit manifest reports no match");
    var mani = new ExploitManifest
    {
        Exploits =
        {
            new ExploitEntry { Id = "off", Enabled = false, Android = "16", KernelPrefix = new[] { "6.12" }, Payload = "x.bin" },
            new ExploitEntry
            {
                Id = "art-a16", Name = "A16", Enabled = true, Android = "16",
                KernelPrefix = new[] { "6.12" }, Brands = new[] { "Xiaomi" }, Payload = "payloads/a.bin"
            }
        }
    };
    var hit = KernelExploit.FindMatch(mani, anyFp, out string st1);
    Check(hit != null && hit.Id == "art-a16" && st1.Contains("art-a16"),
        "Disabled entry is skipped and Android 16 + kernel 6.12 matches");
    var wrongKernel = new DeviceFingerprint { Android = "16", Kernel = "6.1.0-android16", Brand = "Xiaomi" };
    Check(KernelExploit.FindMatch(mani, wrongKernel, out string st2) == null && st2.Contains("kernel"),
        "Kernel prefix mismatch rejects exploit");
    var wrongAndroid = new DeviceFingerprint { Android = "15", Kernel = "6.12.10", Brand = "Xiaomi" };
    Check(KernelExploit.FindMatch(mani, wrongAndroid, out _) == null, "Android 15 does not match Android 16 rule");
    var wrongBrand = new DeviceFingerprint { Android = "16", Kernel = "6.12.10", Brand = "Samsung" };
    Check(KernelExploit.FindMatch(mani, wrongBrand, out _) == null, "Brand filter rejects non-listed brand");
    var unknownBrand = new DeviceFingerprint { Android = "16", Kernel = "6.12.10", Brand = "" };
    Check(KernelExploit.FindMatch(mani, unknownBrand, out _)?.Id == "art-a16",
        "Empty brand (props fail) still matches when other rules pass");
    Check(KernelExploit.AndroidMatchesPublic("16", "16.0.0") && !KernelExploit.AndroidMatchesPublic("16", "15.0"),
        "Android major version matches 16.0 but not 15");
    string root = Path.Combine(dir, "exploits");
    Directory.CreateDirectory(root);
    string mpath = Path.Combine(root, "manifest.json");
    File.WriteAllText(mpath, "{ \"exploits\": [ { \"id\": \"file\", \"enabled\": true, \"android\": \"16\", \"kernelPrefix\": [\"6.12\"], \"payload\": \"payloads/p.bin\" } ] }");
    var loaded = KernelExploit.LoadManifest(mpath);
    Check(loaded.Exploits.Count == 1 && KernelExploit.FindMatch(loaded, anyFp, out _)?.Id == "file",
        "manifest.json loads and matches from disk");
    var e0 = loaded.Exploits[0];
    string pl = KernelExploit.PayloadFullPath(root, e0);
    Check(pl.EndsWith(Path.Combine("payloads", "p.bin"), StringComparison.Ordinal),
        "Payload path is resolved under exploits root");
    // Companions — GhostLock-style multi-file push
    var eComp = new ExploitEntry
    {
        Id = "gl", Enabled = true, Android = "15",
        KernelPrefix = new[] { "6.1.118-android14-11-ga3b9c44908dd" },
        Brands = new[] { "Redmi" }, Codenames = new[] { "amethyst" },
        Payload = "payloads/ghostlock-a61-118",
        Companions = new[] { "payloads/ksud", "payloads/kernelsu.ko" },
        SuccessPattern = "uid=0|KernelSU ready"
    };
    var glFp = new DeviceFingerprint
    {
        Android = "15", Kernel = "6.1.118-android14-11-ga3b9c44908dd-ab13320413",
        Brand = "Redmi", Model = "Redmi Note 14 Pro+ 5G", Codename = "amethyst",
        Patch = "2025-11-01"
    };
    Check(KernelExploit.FindMatch(new ExploitManifest { Exploits = { eComp } }, glFp, out string glSt)?.Id == "gl" &&
          glSt.Contains("gl"),
        "GhostLock exact kernel prefix + codename amethyst matches");
    Check(KernelExploit.CompanionFullPath(root, eComp, "payloads/ksud")
              .EndsWith(Path.Combine("payloads", "ksud"), StringComparison.Ordinal),
        "Companion path resolves under exploits root");
    Check(eComp.Companions.Length == 2 && eComp.SuccessPattern.Contains("KernelSU ready"),
        "Companions array and multi-pattern successPattern load from entry");
    var glWrongKernel = new DeviceFingerprint
    {
        Android = "15", Kernel = "6.6.0-android15", Brand = "Redmi", Codename = "amethyst"
    };
    Check(KernelExploit.FindMatch(new ExploitManifest { Exploits = { eComp } }, glWrongKernel, out string glBad) == null &&
          glBad.Contains("kernel"),
        "GhostLock rejects non-matching kernel");

    Console.WriteLine($"{passed} regression checks passed.");
}
finally { Directory.Delete(dir, recursive: true); }

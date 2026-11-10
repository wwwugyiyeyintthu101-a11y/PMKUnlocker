using PMKUnlocker;
using PMKUnlocker.MediaTek;
using PMKUnlocker.MtkFirmware;
using PMKUnlocker.Qualcomm;
using PMKUnlocker.Spreadtrum;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.Reflection;
using System.Security.Cryptography;
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
// A checkout without the (untracked) bundled tools can still run the byte-level suite in CI. The wired-catalog
// checks stay mandatory by default; only PMK_CI_ALLOW_MISSING_BUNDLED_TOOLS=1 turns their absence into a SKIP.
int skipped = 0;
void Skip(string description)
{
    Console.WriteLine("SKIP: " + description);
    skipped++;
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
    Check(ShopServices.ResolvePreferred("List of devices attached\nnew\tdevice\n", true, "old") == "old",
        "Missing selected phone remains pinned when another phone is connected");
    Check(ShopServices.ResolvePreferred("List of devices attached\na\tdevice\nb\tdevice\n", true, "old") == "old",
        "Stale ADB pin kept when multiple phones (no silent switch)");
    Check(ShopServices.ResolvePreferred("List of devices attached\nold\tdevice\n", true, "old") == "old",
        "Connected preferred pin is kept");
    Check(ShopServices.ResolvePreferred("List of devices attached\n", true, "old") == "old",
        "No phones — stale pin kept so SelectSerial rejects");

    // ---- MTK DA catalog (drop-in folder scan + path resolve) ----
    string mtkCat = Path.Combine(dir, "mtk-catalog"); Directory.CreateDirectory(mtkCat);
    string mtkJson = Path.Combine(mtkCat, "pmk_mtk_devices.json");
    string mtkLoaders = Path.Combine(mtkCat, "mtk", "loaders"); Directory.CreateDirectory(mtkLoaders);
    File.WriteAllText(mtkJson, "{ \"Xiaomi\": { \"Redmi 9\": { \"da\": \"\", \"auth\": \"\", \"preloader\": \"\" } } }");
    var catEmpty = MtkDaCatalog.Load(mtkJson, mtkLoaders);
    Check(catEmpty.Count == 1 && catEmpty["Xiaomi"].ContainsKey("Redmi 9") &&
          catEmpty["Xiaomi"]["Redmi 9"].Da == "", "Catalog loads JSON seed with empty DA paths");
    string redmi9 = Path.Combine(mtkLoaders, "Xiaomi", "Redmi 9"); Directory.CreateDirectory(redmi9);
    File.WriteAllBytes(Path.Combine(redmi9, "da.bin"), new byte[8]);
    File.WriteAllBytes(Path.Combine(redmi9, "auth.auth"), new byte[4]);
    File.WriteAllBytes(Path.Combine(redmi9, "preloader.bin"), new byte[4]);
    var catFilled = MtkDaCatalog.Load(mtkJson, mtkLoaders);
    var r9 = catFilled["Xiaomi"]["Redmi 9"];
    Check(File.Exists(r9.Da) && File.Exists(r9.Auth) && File.Exists(r9.Preloader), "Drop-in folder auto-fills DA/auth/preloader paths");
    Check(r9.Da.EndsWith("da.bin", StringComparison.OrdinalIgnoreCase) &&
          r9.Auth.EndsWith("auth.auth", StringComparison.OrdinalIgnoreCase) &&
          r9.Preloader.EndsWith("preloader.bin", StringComparison.OrdinalIgnoreCase), "Conventional filenames resolve to the right roles");
    string tecno = Path.Combine(mtkLoaders, "Tecno", "Spark 7"); Directory.CreateDirectory(tecno);
    File.WriteAllBytes(Path.Combine(tecno, "MTK_AllInOne_DA_6765.bin"), new byte[8]);
    var catSubstr = MtkDaCatalog.Load(mtkJson, mtkLoaders);
    Check(catSubstr.ContainsKey("Tecno") && catSubstr["Tecno"].ContainsKey("Spark 7") &&
          catSubstr["Tecno"]["Spark 7"].Da.EndsWith("MTK_AllInOne_DA_6765.bin", StringComparison.OrdinalIgnoreCase),
        "Role substring matches DA files not named da.bin, and scan adds new brands");
    string baseDir = Path.Combine(mtkCat, "mtk");
    Check(MtkDaCatalog.Resolve("loaders/Xiaomi/Redmi 9/da.bin", baseDir) == Path.Combine(baseDir, "loaders", "Xiaomi", "Redmi 9", "da.bin"),
        "Relative catalog path resolves under the mtk base");
    Check(MtkDaCatalog.Resolve(Path.Combine(baseDir, "loaders", "Xiaomi", "da.bin"), baseDir) == Path.Combine(baseDir, "loaders", "Xiaomi", "da.bin"),
        "Absolute catalog path passes through unchanged");
    Check(MtkDaCatalog.Resolve("", baseDir) == "", "Empty path resolves to empty");
    var catNoJson = MtkDaCatalog.Load(Path.Combine(mtkCat, "missing.json"), mtkLoaders);
    Check(catNoJson.Count == 2 && catNoJson["Xiaomi"].ContainsKey("Redmi 9"),
        "Folder scan alone builds the catalog when JSON is missing");

    // ---- Wired umbrage catalog (real bundled-tools/mtk/loaders + pmk_mtk_devices.json) ----
    string? probe = AppContext.BaseDirectory, projRoot = "";
    while (probe != null && !(Directory.Exists(Path.Combine(probe, "PMKUnlocker")) && Directory.Exists(Path.Combine(probe, "tests"))))
        probe = Path.GetDirectoryName(probe);
    if (probe != null) projRoot = probe;
    Check(!string.IsNullOrEmpty(projRoot), "Project root resolved for wired catalog check");
    bool wiredTools = Directory.Exists(Path.Combine(projRoot, "PMKUnlocker", "bundled-tools", "mtk", "loaders"));
    if (!wiredTools && Environment.GetEnvironmentVariable("PMK_CI_ALLOW_MISSING_BUNDLED_TOOLS") == "1")
    {
        Skip("wired MTK catalog checks (bundled-tools/mtk/loaders is not in the checkout)");
    }
    else
    {
        var wired = MtkDaCatalog.Load(Path.Combine(projRoot, "PMKUnlocker", "pmk_mtk_devices.json"),
            Path.Combine(projRoot, "PMKUnlocker", "bundled-tools", "mtk", "loaders"));
        Check(wired.ContainsKey("Lenovo") && wired.ContainsKey("Motorola") && wired.ContainsKey("Nothing") &&
              wired.ContainsKey("Honor") && wired.ContainsKey("Doogee") && wired.ContainsKey("Itel"),
            "Wired catalog adds Lenovo/Motorola/Nothing/Honor/Doogee/Itel brands");
        Check(File.Exists(wired["Xiaomi"]["Redmi 9"].Da) && File.Exists(wired["Xiaomi"]["Redmi 9"].Auth) &&
              File.Exists(wired["Xiaomi"]["Redmi 9"].Preloader), "Wired Redmi 9 has DA/auth/preloader files");
        Check(wired["Motorola"]["G13+G23"].Da.EndsWith("da.bin", StringComparison.OrdinalIgnoreCase) &&
              File.Exists(wired["Motorola"]["G13+G23"].Da), "Wired Motorola G13+G23 DA resolves to a file");
        Check(wired["Lenovo"]["IdeaTab A7600-H"].Da.Length > 0 && wired["Lenovo"]["IdeaTab A7600-H"].Preloader.Length > 0 &&
              wired["Lenovo"]["IdeaTab A7600-H"].Auth.Length == 0, "Wired Lenovo has DA+preloader but no auth");
        Check(File.Exists(wired["Honor"]["Honor 200 Lite"].Da), "Wired Honor 200 Lite DA resolves to a file");
        Check(File.Exists(wired["Vivo"]["iQOO Neo 7"].Da) && File.Exists(wired["Vivo"]["iQOO Neo 7"].Auth),
            "Wired iQOO Neo 7 has DA+auth files");
        int wiredModels = 0;
        foreach (var b in wired.Values) foreach (var m in b.Values) if (m.Da.Length > 0) wiredModels++;
        Check(wiredModels >= 51, "Wired catalog fills DA for all 51 umbrage+mtk-escape models");
        Check(new[] { "Hot 11", "Hot 12", "Note 10", "Note 11" }.All(m => File.Exists(wired["Infinix"][m].Da)) &&
              new[] { "Camon 17", "Camon 18" }.All(m => File.Exists(wired["Tecno"][m].Da)),
            "Wired MT6769 models resolve a DA (Infinix Hot 11/12, Note 10/11, Tecno Camon 17/18)");
        Check(File.ReadAllBytes(wired["Infinix"]["Hot 11"].Da)
                  .SequenceEqual(File.ReadAllBytes(wired["Motorola"]["G13+G23"].Da)),
            "MT6769 model DAs reuse the chipset-level MT6769 download agent");
        Check(File.Exists(wired["Samsung"]["A155F_BIT_5"].Auth) && File.Exists(wired["Samsung"]["SM-X115"].Auth) &&
              File.Exists(wired["Lenovo"]["TB351FU"].Auth),
            "Samsung/Lenovo MTK auth files resolve through the catalog");
        Check(!wired["Xiaomi"].ContainsKey("Redmi Note 8") && !wired["Xiaomi"].ContainsKey("Redmi Note 10") &&
              !wired["Xiaomi"].ContainsKey("Redmi Note 11") && !wired["Xiaomi"].ContainsKey("Poco X3") &&
              !wired["Xiaomi"].ContainsKey("Poco M3") && !wired["Oppo"].ContainsKey("A74") &&
              !wired["Oppo"].ContainsKey("F17") && !wired["Oppo"].ContainsKey("F19") &&
              !wired["Samsung"].ContainsKey("Galaxy A02s") && !wired["Samsung"].ContainsKey("Galaxy A03"),
            "QC/SPD stubs are no longer offered by the MTK catalog");
        Check(new[] { "Spark 8", "Spark 9" }.All(m => File.Exists(wired["Tecno"][m].Da)) &&
              new[] { "Y21", "Y30" }.All(m => File.Exists(wired["Vivo"][m].Da)) &&
              File.Exists(wired["Samsung"]["Galaxy A12"].Da) && File.Exists(wired["Tecno"]["Spark 7"].Da),
            "Wired MT6765/MT6761 models resolve a DA (Tecno Spark 7/8/9, Vivo Y21/Y30, Galaxy A12)");
        Check(File.ReadAllBytes(wired["Vivo"]["Y21"].Da).SequenceEqual(File.ReadAllBytes(wired["Infinix"]["MT6765_2"].Da)),
            "MT6765 model DAs reuse the chipset-level MediaTek AllInOne DA");
        Check(!wired["Vivo"].ContainsKey("Y20"), "Vivo Y20 (Snapdragon 460) is no longer offered by the MTK catalog");
        Check(File.Exists(wired["Vivo"]["Y12"].Da) && File.Exists(wired["Vivo"]["Y12"].Auth) &&
              File.Exists(wired["Vivo"]["Y15"].Da) && File.Exists(wired["Vivo"]["Y15"].Auth),
            "Wired MT6762 models (Vivo Y12/Y15) resolve DA+auth");
        Check(File.ReadAllBytes(wired["Vivo"]["Y12"].Da).SequenceEqual(File.ReadAllBytes(wired["Infinix"]["MT6761_2"].Da)),
            "MT6762 model DAs reuse the 0x6761 AllInOne download agent (upstream mtkclient maps MT6762 to dacode 0x6761)");

        // ---- MTK loader auto-detect (chipset reported by BROM/Preloader → catalog DA/auth) ----
        string mtkBase = Path.Combine(projRoot, "PMKUnlocker", "bundled-tools", "mtk");
        Check(MtkDaCatalog.PrimaryChipset("MT6765/MT8768t(Helio P35/G35)") == "MT6765",
            "Chipset token is the first MT#### of the mtkclient CPU line");
        Check(MtkDaCatalog.PrimaryChipset("Helio G35 · eMMC CJTD4R") == "",
            "Marketing-only text yields no chipset token");
        Check(MtkDaCatalog.ChipsetFromPayloadLines(new[] { "Preloader - boot rom code", "PLTools - Successfully sent payload: C:\\mtkclient\\payloads\\mt6765_payload.bin" }) == "MT6765",
            "Accepted payload filename is the fallback chipset signal");
        Check(MtkDaCatalog.PrimaryChipset("MT6765") == "MT6765" && MtkDaCatalog.PrimaryChipset("mt6765") == "MT6765" &&
              MtkDaCatalog.PrimaryChipset("MediaTek MT6873") == "MT6873",
            "ADB ro.soc.model / ro.board.platform tokens resolve the same way as the BROM CPU line");
        Check(MtkDaCatalog.PrimaryChipset("Qualcomm SM6125") == "" && MtkDaCatalog.PrimaryChipset("Helio G85") == "",
            "Non-MediaTek and marketing-only prop text yields no chipset token for the DA catalog");
        var fam62 = MtkDaCatalog.ChipsetCandidates("mt6762");
        Check(fam62.Count == 2 && fam62[0] == "MT6762" && fam62[1] == "MT6761",
            "Chipset candidates list the device chip first, then its proven shared-DA family");
        Check(MtkDaCatalog.ReadDaChipCode(wired["Vivo"]["Y12"].Da) == 0x6761,
            "DA blob chip code is read from the MTK_DOWNLOAD_AGENT header (LE u16 at 0x6E)");
        Check(MtkDaCatalog.ReadDaChipCode(Path.Combine(dir, "no-such-da.bin")) == -1,
            "Missing or non-DA file yields no chip code");
        var auto6765 = MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6765");
        Check(auto6765 != null && File.Exists(MtkDaCatalog.Resolve(auto6765.Info.Da, mtkBase)),
            "MT6765 auto-resolves to a catalog loader that exists on disk");
        Check(auto6765 != null && (MtkDaCatalog.PrimaryChipset(auto6765.Model) == "MT6765" ||
              MtkDaCatalog.ReadDaChipCode(MtkDaCatalog.Resolve(auto6765.Info.Da, mtkBase)) == 0x6765),
            "The MT6765 loader match is justified by the folder token or the DA's own chip code");
        Check(auto6765 != null && MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6765")!.Brand == auto6765.Brand &&
              MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6765")!.Model == auto6765.Model,
            "Chipset resolution is deterministic across runs");
        Check(auto6765 != null && MtkDaCatalog.PrimaryChipset(auto6765.Model) == "MT6765",
            "Chipset-keyed loader folders outrank model-named folders carrying the same DA code");
        Check(MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6762") != null,
            "MT6762 resolves through the 0x6761 family alias");
        Check(MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6769") != null,
            "MT6769 resolves through the 0x6768 family alias");
        Check(MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6893") != null &&
              MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6991") != null,
            "Chipsets whose loaders are only folder-named (DAv5/AllInOne v6) still resolve");
        Check(MtkDaCatalog.FindByChipset(wired, mtkBase, "MT9999") == null &&
              MtkDaCatalog.FindByChipset(wired, mtkBase, "") == null &&
              MtkDaCatalog.FindByChipset(null, mtkBase, "MT6765") == null,
            "Unknown, empty or missing catalog input yields no loader match instead of a guess");
        var auto6991 = MtkDaCatalog.FindByChipset(wired, mtkBase, "MT6991");
        Check(auto6991 != null && !MtkDaCatalog.DaMatchesChipset(auto6991.Model, MtkDaCatalog.Resolve(auto6991.Info.Da, mtkBase), "MT6765"),
            "A wrong-chipset manual loader is detected as a mismatch");
        Check(auto6765 != null && MtkDaCatalog.DaMatchesChipset(auto6765.Model, MtkDaCatalog.Resolve(auto6765.Info.Da, mtkBase), "MT6765"),
            "The resolved loader passes the manual-override sanity check for its own chipset");
    }

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
    Check(ShopServices.CheckImages(firmwareDir, layout, false, out var sk1).Count == 0, "Raw image fitting the device GPT passes size preflight");
    File.WriteAllBytes(Path.Combine(firmwareDir, "unknown.img"), new byte[1]);
    Check(ShopServices.CheckImages(firmwareDir, layout, false, out var sk2).Count == 0 && sk2.Contains("unknown.img"), "Unknown partition is skipped with a note (wl skips it too)");
    File.Delete(Path.Combine(firmwareDir, "unknown.img"));
    using (var sparse = new BinaryWriter(File.Create(Path.Combine(firmwareDir, "boot.img"))))
    {
        sparse.Write(0xed26ff3aU); sparse.Write((ushort)1); sparse.Write((ushort)0);
        sparse.Write((ushort)28); sparse.Write((ushort)12); sparse.Write(4096U); sparse.Write(8U); sparse.Write(0U); sparse.Write(0U);
    }
    Check(ShopServices.ExpandedImageSize(Path.Combine(firmwareDir, "boot.img")) == 32768, "Sparse preflight uses expanded size, not file length");
    Check(ShopServices.CheckImages(firmwareDir, layout, false, out var sk3).Count == 1, "Oversized sparse image blocks preflight");
    Check(ReviewSafety.ShouldReboot(true, true, false, false), "Successful operation permits enabled auto-reboot");
    Check(!ReviewSafety.ShouldReboot(true, true, true, false), "Failed operation cannot auto-reboot");
    Check(!ReviewSafety.ShouldReboot(true, false, false, false), "Cancelled dialog without a command cannot auto-reboot");
    Check(!ReviewSafety.ShouldReboot(true, true, false, true), "STOP prevents auto-reboot");
    Check(!ReviewSafety.ShouldReboot(false, true, false, false), "Disabled auto-reboot is respected");
    string source = Path.Combine(dir, "rawprogram.xml"), target = Path.Combine(dir, "filtered.xml");
    File.WriteAllText(source, "<data><program label='boot' filename='boot.img'/><program label='USERDATA' filename='data.img'/></data>");
    ReviewSafety.FilterUserdata(source, target);
    Check(XDocument.Load(target).Descendants("program").Count() == 1 && File.ReadAllText(source).Contains("USERDATA"), "Skip userdata preserves original XML and boot entry");
    // Split: file ကြီးတွေကို အပိုင်းခွဲရာမှာ entry order/filename/start_sector မပျက်စေရ (system ပျက်ပြီး bootloop ဖြစ်ခဲ့သော bug)
    string splitDir = Path.Combine(dir, "splitfw");
    Directory.CreateDirectory(splitDir);
    using (var fs = new FileStream(Path.Combine(splitDir, "system.img"), FileMode.Create))
    {
        byte[] block = new byte[4096];
        for (int b = 0; b < 25; b++)
        {
            for (int i = 0; i < block.Length; i++) block[i] = (byte)b;
            fs.Write(block, 0, block.Length);
        }
    }
    string splitSrc = Path.Combine(splitDir, "rawprogram0.xml");
    File.WriteAllText(splitSrc,
        "<?xml version=\"1.0\" ?><data><program SECTOR_SIZE_IN_BYTES=\"512\" file_sector_offset=\"0\" filename=\"system.img\" label=\"SYSTEM\" num_partition_sectors=\"200\" size_in_KB=\"100\" start_byte_hex=\"0xC800\" start_sector=\"100\"/></data>");
    string splitDst = Path.Combine(splitDir, "rawprogram0_pmk.xml");
    int splitAdded = ReviewSafety.SplitOversizedImages(splitSrc, splitDir, splitDst, null, null, 40 * 1024);
    var splitParts = XDocument.Load(splitDst).Descendants("program").ToList();
    Check(splitAdded == 3 && splitParts.Count == 3, "Split produces one program entry per part");
    Check((string?)splitParts[0].Attribute("filename") == "system__pmkpart1.img" &&
          (string?)splitParts[1].Attribute("filename") == "system__pmkpart2.img" &&
          (string?)splitParts[2].Attribute("filename") == "system__pmkpart3.img", "Split keeps part order (first part never lost)");
    Check((string?)splitParts[0].Attribute("start_sector") == "100" &&
          (string?)splitParts[1].Attribute("start_sector") == "180" &&
          (string?)splitParts[2].Attribute("start_sector") == "260", "Split start_sector advances by written bytes");
    Check((string?)splitParts[0].Attribute("num_partition_sectors") == "80" &&
          (string?)splitParts[1].Attribute("num_partition_sectors") == "80" &&
          (string?)splitParts[2].Attribute("num_partition_sectors") == "40" &&
          (string?)splitParts[0].Attribute("start_byte_hex") == "0xc800" &&
          (string?)splitParts[2].Attribute("start_byte_hex") == "0x20800", "Split sectors and byte offsets match part sizes");
    using (var p1 = File.OpenRead(Path.Combine(splitDir, "system__pmkpart1.img")))
    using (var p2 = File.OpenRead(Path.Combine(splitDir, "system__pmkpart2.img")))
    using (var p3 = File.OpenRead(Path.Combine(splitDir, "system__pmkpart3.img")))
        Check(p1.Length == 40960 && p2.Length == 40960 && p3.Length == 20480 &&
              p1.ReadByte() == 0 && p2.ReadByte() == 10 && p3.ReadByte() == 20, "Split part files contain the right data range");
    Check(File.ReadAllText(splitSrc).Contains("filename=\"system.img\""), "Split leaves source XML untouched");
    int splitAgain = ReviewSafety.SplitOversizedImages(splitSrc, splitDir, splitDst, null, null, 40 * 1024);
    var splitParts2 = XDocument.Load(splitDst).Descendants("program").ToList();
    Check(splitAgain == 3 && splitParts2.Count == 3 &&
          (string?)splitParts2[0].Attribute("filename") == "system__pmkpart1.img" &&
          (string?)splitParts2[1].Attribute("start_sector") == "180" &&
          (string?)splitParts2[2].Attribute("start_sector") == "260", "Split rebuilds parts with the same correct entries");

    File.WriteAllBytes(Path.Combine(splitDir, "system__pmkpart1.img"), new byte[40960]);
    ReviewSafety.SplitOversizedImages(splitSrc, splitDir, splitDst, null, null, 40 * 1024);
    Check(File.ReadAllBytes(Path.Combine(splitDir, "system__pmkpart1.img"))[4096] == 1,
        "Split rebuilds same-size corrupted cached part");
    string ranged = Path.Combine(splitDir, "range.xml");
    File.WriteAllText(ranged, "<data><program filename='system.img' SECTOR_SIZE_IN_BYTES='512' start_sector='200' file_sector_offset='8' num_partition_sectors='80'/></data>");
    ReviewSafety.SplitOversizedImages(ranged, splitDir, splitDst, null, null, 40 * 1024);
    var rangedEntry = XDocument.Load(splitDst).Descendants("program").Single();
    Check(File.ReadAllBytes(Path.Combine(splitDir, (string)rangedEntry.Attribute("filename")!))[0] == 1 &&
        (string?)rangedEntry.Attribute("start_sector") == "200" &&
        (string?)rangedEntry.Attribute("file_sector_offset") == "0" &&
        (string?)rangedEntry.Attribute("num_partition_sectors") == "80",
        "Split preserves nonzero source offset and entry length");

    // ---- Sparse image expansion (fh_loader 15.06 container-as-raw → bootloop) ----
    string spDir = Path.Combine(Path.GetTempPath(), "pmk_reg_sparse" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(spDir);
    try
    {
        // header 28B + raw(1 block, 0..255) + fill(2 blocks, AA BB CC DD) + DCA(3 blocks) → 6 blocks × 4096
        string spSrc = Path.Combine(spDir, "tiny.img");
        using (var fs = File.Create(spSrc))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(0xED26FF3Au); bw.Write((ushort)1); bw.Write((ushort)0);
            bw.Write((ushort)28); bw.Write((ushort)12);
            bw.Write(4096u); bw.Write(6u); bw.Write(3u); bw.Write(0u);
            bw.Write((ushort)0xCAC1); bw.Write((ushort)0); bw.Write(1u); bw.Write(12u + 4096u);
            for (int i = 0; i < 4096; i++) bw.Write((byte)(i % 256));
            bw.Write((ushort)0xCAC2); bw.Write((ushort)0); bw.Write(2u); bw.Write(16u);
            bw.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
            bw.Write((ushort)0xCAC3); bw.Write((ushort)0); bw.Write(3u); bw.Write(12u);
        }
        string spXml = Path.Combine(spDir, "rawprogram0.xml");
        File.WriteAllText(spXml,
            "<?xml version=\"1.0\" ?>\n<data>" +
            "<program SECTOR_SIZE_IN_BYTES=\"512\" file_sector_offset=\"0\" filename=\"tiny.img\" label=\"x\" num_partition_sectors=\"12\" physical_partition_number=\"0\" size_in_KB=\"4.0\" sparse=\"true\" start_byte_hex=\"0x1000\" start_sector=\"8\"/>" +
            "<program SECTOR_SIZE_IN_BYTES=\"512\" file_sector_offset=\"0\" filename=\"rawish.bin\" label=\"y\" num_partition_sectors=\"2\" physical_partition_number=\"0\" size_in_KB=\"1.0\" sparse=\"false\" start_byte_hex=\"0x2000\" start_sector=\"16\"/>" +
            "</data>");
        File.WriteAllBytes(Path.Combine(spDir, "rawish.bin"), new byte[] { 1, 2, 3, 4 });

        int conv = ReviewSafety.ConvertSparseImages(spXml, spDir, null);
        string rawOut = Path.Combine(spDir, "tiny_pmk_raw.img");
        Check(conv == 1 && File.Exists(rawOut), "Sparse expand returns 1 and creates raw file");
        byte[] ob = File.ReadAllBytes(rawOut);
        Check(ob.Length == 6 * 4096, "Sparse expand length = blocks*blkSize");
        bool rawOk = true;
        for (int i = 0; i < 4096 && rawOk; i++) if (ob[i] != (byte)(i % 256)) rawOk = false;
        Check(rawOk, "Sparse raw chunk content preserved");
        bool fillOk = true;
        for (int i = 4096; i < 4096 + 8192 && fillOk; i++)
            if (ob[i] != new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }[(i - 4096) % 4]) fillOk = false;
        Check(fillOk, "Sparse fill chunk expanded with pattern");
        bool dcaOk = true;
        for (int i = 4096 + 8192; i < ob.Length && dcaOk; i++) if (ob[i] != 0) dcaOk = false;
        Check(dcaOk, "Sparse don't-care chunk reads as zeros");

        string xmlNow = File.ReadAllText(spXml);
        Check(xmlNow.Contains("tiny_pmk_raw.img") && xmlNow.Contains("sparse=\"false\""),
              "Sparse XML: filename renamed and sparse flag cleared");
        Check(xmlNow.Contains("filename=\"rawish.bin\""), "Sparse XML: non-sparse entry untouched");

        var xmlRegen = XDocument.Load(spXml);
        xmlRegen.Descendants("program").First(p => (string?)p.Attribute("label") == "x")
                .Attribute("filename")!.Value = "tiny.img";
        xmlRegen.Save(spXml);
        File.WriteAllBytes(rawOut, new byte[ob.Length]); // same size, newer timestamp, wrong content
        int conv2 = ReviewSafety.ConvertSparseImages(spXml, spDir, null);
        Check(conv2 == 1 && File.ReadAllBytes(rawOut).SequenceEqual(ob),
              "Second run rebuilds a same-size corrupted raw image");
        string badCrc = Path.Combine(spDir, "bad-crc.img");
        byte[] invalidSparse = File.ReadAllBytes(spSrc);
        BitConverter.GetBytes(1U).CopyTo(invalidSparse, 24);
        File.WriteAllBytes(badCrc, invalidSparse);
        bool crcBlocked = false;
        try { ReviewSafety.ExpandSparseImage(badCrc, Path.Combine(spDir, "bad.raw")); }
        catch (InvalidDataException) { crcBlocked = true; }
        Check(crcBlocked && !File.Exists(Path.Combine(spDir, "bad.raw")), "Invalid sparse CRC rejects output");
        string pipelineXml = Path.Combine(spDir, "pipeline.xml");
        File.WriteAllText(pipelineXml, "<data><program filename='tiny.img' SECTOR_SIZE_IN_BYTES='512' start_sector='100' file_sector_offset='0' num_partition_sectors='48' sparse='true'/></data>");
        ReviewSafety.ConvertSparseImages(pipelineXml, spDir);
        string pipelineOut = Path.Combine(spDir, "pipeline-final.xml");
        ReviewSafety.SplitOversizedImages(pipelineXml, spDir, pipelineOut, null, null, 8192);
        var pipelineParts = XDocument.Load(pipelineOut).Descendants("program").ToList();
        Check(pipelineParts.Count == 3 && pipelineParts.All(p =>
            new FileInfo(Path.Combine(spDir, (string)p.Attribute("filename")!)).Length == 8192),
            "Small sparse container expands then splits below the loader limit");
        Check(new FileInfo(spSrc).Length == 28 + 12 + 4096 + 16 + 12, "Source sparse file untouched");
    }
    finally { try { Directory.Delete(spDir, true); } catch { } }
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

    // GhostLock fix floor (CVE-2026-43499) + profile provenance
    string noteLow = KernelExploit.GhostLockFixNote("5.15.194-android13-8-00038-g853eb7b6b965-ab15936612", "2026-08-01") ?? "";
    Check(noteLow.Contains("below the fixed 5.15.212") && noteLow.Contains("vendor backport cannot be ruled out"),
        "Fix note marks a below-floor 5.15 build as possibly backported");
    string noteNoDate = KernelExploit.GhostLockFixNote("5.15.194-x", "") ?? "";
    Check(noteNoDate.Contains("patch date unknown"),
        "Fix note says patch date unknown when the build has none");
    string noteFixed = KernelExploit.GhostLockFixNote("6.6.140-android15", "2026-06-01") ?? "";
    Check(noteFixed.Contains("at/above the fixed 6.6.140"),
        "Fix note reports a build at the fix floor as patched");
    string noteUnknown = KernelExploit.GhostLockFixNote("4.19.191-x", "") ?? "";
    Check(noteUnknown.Contains("patch status unknown") && KernelExploit.GhostLockFixNote("", "") == null,
        "Fix note is honest for an unbranched kernel and silent for an empty one");

    string provRoot = Path.Combine(dir, "prov-exploits");
    Directory.CreateDirectory(Path.Combine(provRoot, "profiles"));
    File.WriteAllText(Path.Combine(provRoot, "profiles", "verified.txt"),
        "# test\n5.15.206-android13-8-00056-x\n");
    Check(KernelExploit.ProfileProvenance(provRoot, "5.15.206-android13-8-00056-x") == "upstream-verified",
        "Profile provenance reads the upstream verified list");
    Check(KernelExploit.ProfileProvenance(provRoot, "5.15.194-android13-8-00038-x").StartsWith("local candidate"),
        "Profile provenance flags an unlisted release as a local candidate");
    Check(KernelExploit.ProfileProvenance(Path.Combine(dir, "prov-none"), "5.15.1").Contains("unrecorded"),
        "Profile provenance is unrecorded without verified.txt");

    // Measured-patched firmware — GhostLock cannot write once the kernel clears the stale waiter
    string pkRoot = Path.Combine(dir, "patched-exploits");
    Directory.CreateDirectory(pkRoot);
    File.WriteAllText(Path.Combine(pkRoot, "patched-kernels.txt"),
        "# header\n\n5.15.194-android13-8-00038-x | remove_waiter() has the GKI fix\n5.15.189-android13-8-00004-y\n");
    Check(KernelExploit.PatchedFirmwareNote(pkRoot, " 5.15.194-android13-8-00038-x ") == "remove_waiter() has the GKI fix",
        "Patched-firmware note returns the per-release reason");
    Check((KernelExploit.PatchedFirmwareNote(pkRoot, "5.15.189-android13-8-00004-y") ?? "").Contains("remove_waiter"),
        "Patched-firmware note falls back to the default reason without '|'");
    Check(KernelExploit.PatchedFirmwareNote(pkRoot, "5.15.189-android13-8-00004") == null &&
          KernelExploit.PatchedFirmwareNote(pkRoot, "") == null &&
          KernelExploit.PatchedFirmwareNote(Path.Combine(dir, "pk-none"), "5.15.1") == null,
        "Patched-firmware note is null for another release, an empty kernel and a missing list");
    string shippedExploits = Path.Combine(projRoot, "PMKUnlocker", "bundled-tools", "exploits");
    if (!File.Exists(Path.Combine(shippedExploits, "patched-kernels.txt")) &&
        Environment.GetEnvironmentVariable("PMK_CI_ALLOW_MISSING_BUNDLED_TOOLS") == "1")
    {
        Skip("shipped patched-kernels.txt check (bundled-tools is not in the checkout)");
    }
    else
    {
        Check(File.Exists(Path.Combine(shippedExploits, "patched-kernels.txt")) &&
              (KernelExploit.PatchedFirmwareNote(shippedExploits,
                  "5.15.194-android13-8-00038-g853eb7b6b965-ab15936612") ?? "").Contains("remove_waiter"),
            "Shipped patched-kernels.txt flags the bench firmware 5.15.194 with a remove_waiter() reason");
    }

    // Route success claim — separates a refused route from a write the route acknowledged but never landed
    Check(KernelExploit.RouteClaimedWrite("[route] route_done status=0 clean=1/1 step=0 errno=0 calls=1 success=1") &&
          KernelExploit.RouteClaimedWrite("multicast route status=0 clean=1/1 step=0 errno=0"),
        "Route success claim reads route_done success=1 and route status=0");
    Check(!KernelExploit.RouteClaimedWrite("[route] route_done status=-1 clean=0/1 step=2 errno=35 calls=1 success=0") &&
          !KernelExploit.RouteClaimedWrite("PI route did not produce a verified write") &&
          !KernelExploit.RouteClaimedWrite(null),
        "Route success claim is false for a refused route and for empty output");

    // Leak summary — counters must come from ONE summary line of THIS run, never a stale device log
    string runA = "[*] attempt=1/4\n[!] slide tracefs raw summary pages=9 bytes=36864 events=214 callers=204 parse_fail=0 candidates=0 cpu_files=8\n";
    string staleB = "[!] slide tracefs raw summary pages=9 bytes=36864 events=214 callers=348 parse_fail=0 candidates=0 cpu_files=8";
    string leakLine = KernelExploit.LastLeakSummaryLine(runA + staleB, out int leakN) ?? "";
    var lc = KernelExploit.LeakCounters(leakLine);
    Check(leakN == 2 && lc?.Callers == 348 && lc?.Candidates == 0,
        "Leak counters take the last summary line whole (no 204/348 mix)");
    var lcOne = KernelExploit.LeakCounters(KernelExploit.LastLeakSummaryLine(runA, out int leakOne));
    Check(leakOne == 1 && lcOne?.Callers == 204 && lcOne?.ParseFail == 0 && lcOne?.Candidates == 0,
        "Leak counters keep all six fields from a single summary line");
    Check(KernelExploit.LastLeakSummaryLine("callers=204 parse_fail=0 candidates=0", out int leakNone) == null &&
          leakNone == 0 && KernelExploit.LeakCounters(null) == null,
        "A summary-shaped fragment without the summary line yields no counters");
    var lcPartial = KernelExploit.LeakCounters("[!] slide tracefs raw summary pages=9 callers=204");
    Check(lcPartial == null && KernelExploit.LeakCounters("") == null,
        "An incomplete summary line is refused instead of half-parsed");

    // --- MI FLASH (Xiaomi fastboot flash_all.bat parser) ---
    string romBase = Path.Combine(dir, "ROM Air MIUI");
    var parsed = MiFlash.ParseLines(new[]
    {
        "@echo off",
        "color 07",
        "rem fastboot flash fake boot.img",
        ":: fastboot flash commented vendor.img",
        "set FASTBOOT=",
        "fastboot %FASTBOOT% flash logo \"%~dp0images\\logo.bin\"",
        "fastboot %FASTBOOT% flash super %~dp0images\\super image.img",
        "fastboot %FASTBOOT% erase userdata",
        "fastboot %FASTBOOT% set_active b",
        "fastboot reboot",
        "timeout /t 2 /nobreak >nul",
        "echo Flashing done",
        "\"%~dp0tools\\fastboot.exe\" flash dtbo %~dp0images\\dtbo.img"
    }, romBase);
    Check(parsed.Count == 6, "flash_all.bat: comment/echo/timeout lines skipped, 6 real ops kept");
    Check(parsed[0].Kind == "flash" && parsed[0].Partition == "logo" &&
          parsed[0].File == Path.Combine(romBase, "images", "logo.bin"),
        "flash_all.bat: quoted %~dp0 path resolves to absolute image path");
    Check(parsed[1].Kind == "flash" && parsed[1].File == Path.Combine(romBase, "images", "super image.img"),
        "flash_all.bat: unquoted path with spaces joins remaining tokens");
    Check(parsed[2].Kind == "erase" && parsed[2].Partition == "userdata",
        "flash_all.bat: erase command parsed");
    Check(parsed[3].Kind == "set_active" && parsed[3].Raw.StartsWith("set_active b"),
        "flash_all.bat: set_active parsed as own op");
    Check(parsed[4].Kind == "reboot", "flash_all.bat: reboot captured for Auto Reboot handling");
    Check(parsed[5].Kind == "flash" && parsed[5].Partition == "dtbo" &&
          parsed[5].File == Path.Combine(romBase, "images", "dtbo.img"),
        "flash_all.bat: bundled \"fastboot.exe\" path form parsed");
    var slotOps = MiFlash.ParseLines(new[]
        { "set FASTBOOT=--slot=all", "fastboot %FASTBOOT% flash boot x.img" }, romBase);
    Check(slotOps.Count == 1 && slotOps[0].Kind == "flash" && slotOps[0].Partition == "boot" &&
          slotOps[0].File.EndsWith("x.img", StringComparison.Ordinal),
        "flash_all.bat: %FASTBOOT% option var skipped when locating flash verb");
    var starOps = MiFlash.ParseLines(new[]
    {
        "fastboot %* flash boot %~dp0images\\boot.img || @echo \"Flash boot error\" && exit /B 1",
        "fastboot %* getvar product 2>&1 | findstr /r /c:\"^product: *air\" || echo Missmatching image and device",
        "fastboot %* erase expdb    || @echo \"Erase expdb error\" && exit /B 1",
        "fastboot %* oem cdms",
        "fastboot %* set_active a  || @echo \"set_active a error\" && exit /B 1",
        "fastboot %* reboot || @echo \"Reboot error\" && exit /B 1"
    }, romBase);
    Check(starOps.Count == 5, "flash_all.bat: %* skipped, product guard retained as plan metadata, 5 real ops kept");
    Check(starOps[0].Kind == "flash" && starOps[0].File == Path.Combine(romBase, "images", "boot.img") &&
          !starOps[0].File.Contains("||"),
        "flash_all.bat: flash file path free of || @echo tail");
    Check(starOps[1].Kind == "erase" && starOps[1].Partition == "expdb",
        "flash_all.bat: %* erase parsed");
    Check(starOps[2].Kind == "raw" && starOps[2].Raw == "oem cdms",
        "flash_all.bat: oem cdms kept as raw op");
    Check(starOps[3].Kind == "set_active" && starOps[3].Raw.StartsWith("set_active a") &&
          !starOps[3].Raw.Contains("||"),
        "flash_all.bat: set_active raw tail stripped");
    Check(starOps[4].Kind == "reboot" && starOps[4].Raw == "reboot",
        "flash_all.bat: reboot op raw = reboot");
    var condOps = MiFlash.ParseLines(new[]
    {
        "fastboot %* getvar partition-type:opcust 2>&1 | findstr /r /c:\"^partition-type:opcust: raw\"",
        "if %errorlevel% equ 0 (",
        "fastboot %* erase opcust || @echo \"Erase opcust error\" && exit /B 1",
        ")",
        "fastboot %* erase metadata || @echo \"Erase metadata error\" && exit /B 1",
        "fastboot %* getvar crc 2>&1 | findstr /r /c:\"^crc: 1\" && if %errorlevel% equ 0 (",
        "fastboot %* flash crclist %~dp0images\\crclist.txt || @echo err",
        ")"
    }, romBase);
    Check(condOps.Count == 3, "flash_all.bat: getvar guard lines dropped, 3 ops kept");
    Check(condOps[0].Kind == "erase" && condOps[0].Partition == "opcust" &&
          condOps[0].Cond == "partition-type:opcust",
        "flash_all.bat: partition-type guard attached to erase op");
    Check(condOps[1].Kind == "erase" && condOps[1].Cond == "",
        "flash_all.bat: ')' block end clears guard — metadata erase unconditional");
    Check(condOps[2].Kind == "flash" && condOps[2].Cond == "crc" &&
          condOps[2].File == Path.Combine(romBase, "images", "crclist.txt"),
        "flash_all.bat: crc guard attached to crclist flash");
    string batPath = Path.Combine(dir, "flash_all_lock.bat");
    File.WriteAllText(batPath, "fastboot flash boot boot.img");
    Check(MiFlash.FindFlashAllBat(dir) == Path.GetFullPath(batPath), "FindFlashAllBat prefers flash_all*.bat by name");
    Check(MiFlash.EraseFirst.Contains("boot_a") && MiFlash.EraseFirst.Contains("metadata") && MiFlash.EraseFirst.Length == 6,
        "MST erase-first list has 6 partitions incl. boot_a + metadata");

    Check(starOps.All(o => o.ExpectedProduct == "air"), "Product guard protects the entire plan");
    bool wrongModel = false;
    try { MiFlash.ValidateDevice(starOps, "(bootloader) product: other"); } catch (InvalidDataException) { wrongModel = true; }
    Check(wrongModel, "Wrong model is rejected before any write");
    MiFlash.ValidateDevice(starOps, "(bootloader) product: air\n(bootloader) anti: 1");
    Check(slotOps[0].Options == "--slot=all", "Slot option is preserved for command execution");
    bool controlFlowRejected = false;
    try { MiFlash.ParseLines(new[] { "if %ANTI% GTR 1 exit /b 1", "fastboot flash boot boot.img" }, romBase); }
    catch (InvalidDataException) { controlFlowRejected = true; }
    Check(controlFlowRejected, "Unknown rollback control flow is rejected instead of stripped");
    bool noGuard = false;
    try { MiFlash.ValidateDevice(parsed, "product: air"); } catch (InvalidDataException) { noGuard = true; }
    Check(noGuard, "Missing product guard blocks flashing");

    Check(!MiFlash.GuardMatches("crc", "crc: 11") && MiFlash.GuardMatches("crc", "(bootloader) crc: 1"),
        "Conditional CRC guard matches the exact value");
    bool chained = false;
    try { MiFlash.ParseLines(new[] { "fastboot flash boot boot.img && fastboot flashing lock" }, romBase); }
    catch (InvalidDataException) { chained = true; }
    Check(chained, "Unsupported chained BAT commands are rejected");
    string leasedImage = Path.Combine(dir, "leased.img");
    File.WriteAllBytes(leasedImage, new byte[512]);
    using (var lease = new MiFlash.ImageLease(new[] { new MiFlashOp { Kind = "flash", File = leasedImage } }))
    {
        bool imageLocked = false;
        try { using var writer = new FileStream(leasedImage, FileMode.Open, FileAccess.Write); }
        catch (IOException) { imageLocked = true; }
        Check(imageLocked, "Firmware image cannot be changed while the flash plan holds its lease");
    }
    bool missingImage = false;
    try { using var lease = new MiFlash.ImageLease(new[] { new MiFlashOp { Kind = "flash", File = leasedImage }, new MiFlashOp { Kind = "flash", File = Path.Combine(dir, "missing.img") } }); }
    catch (FileNotFoundException) { missingImage = true; }
    Check(missingImage, "Whole-plan image preflight rejects a missing later image before erase");
    using (var unlocked = new FileStream(leasedImage, FileMode.Open, FileAccess.Write))
        Check(unlocked.CanWrite, "Failed image preflight releases earlier file handles");

    string integrityPath = Path.Combine(AppContext.BaseDirectory, "integrity.json");
    byte[]? originalIntegrity = File.Exists(integrityPath) ? File.ReadAllBytes(integrityPath) : null;
    string probeName = "integrity-probe-" + Guid.NewGuid().ToString("N") + ".bin";
    string probeFile = Path.Combine(AppContext.BaseDirectory, probeName);
    try
    {
        File.WriteAllBytes(probeFile, new byte[] { 1, 2, 3 });
        File.WriteAllText(integrityPath, System.Text.Json.JsonSerializer.Serialize(new[] { new { k = probeName, h = ShopServices.Hash(probeFile) } }));
        ToolIntegrity.Verify(probeFile);
        File.WriteAllBytes(probeFile, new byte[] { 3, 2, 1 });
        bool tampered = false;
        try { ToolIntegrity.Verify(probeFile); } catch (InvalidDataException) { tampered = true; }
        Check(tampered, "Bundled tool changed after first verification is rejected");
        File.Delete(integrityPath);
        bool noManifest = false;
        try { ToolIntegrity.Verify(probeFile); } catch (InvalidDataException) { noManifest = true; }
        Check(noManifest, "Missing bundled integrity manifest fails closed");
    }
    finally
    {
        File.Delete(probeFile);
        if (originalIntegrity != null) File.WriteAllBytes(integrityPath, originalIntegrity);
        else if (File.Exists(integrityPath)) File.Delete(integrityPath);
    }

    // tools.json is user-writable; the elevated app must refuse paths outside protected roots.
    Check(ShopServices.ConfiguredToolProblem("adb.exe", Path.Combine(AppContext.BaseDirectory, "adb.exe")) == null,
        "Configured tool inside the install folder is accepted");
    Check(ShopServices.ConfiguredToolProblem("adb.exe", Path.Combine(Path.GetTempPath(), "adb.exe")) != null,
        "Configured tool in a user-writable folder is refused");
    Check(ShopServices.ConfiguredToolProblem("QSaharaServer.exe", Path.Combine(AppContext.BaseDirectory, "cmd.exe")) != null,
        "Configured tool must keep the requested file name");
    string programFilesForTools = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    if (!string.IsNullOrWhiteSpace(programFilesForTools))
        Check(ShopServices.ConfiguredToolProblem("adb.exe", Path.Combine(programFilesForTools, "PMK", "adb.exe")) == null,
            "Configured tool under Program Files is accepted");
    bool externalScriptRefused = false;
    try { ShopServices.VerifyToolFile(Path.Combine(Path.GetTempPath(), "edl.py")); }
    catch (InvalidDataException) { externalScriptRefused = true; }
    Check(externalScriptRefused, "EDL script outside the install folder is refused");

    // ===== MtkFirmware layer: scatter parser / risk / preflight / backend / redactor =====
    string fwDir = Path.Combine(dir, "fw"); Directory.CreateDirectory(fwDir);
    string GoodScatter(string blocks) =>
        "MTK_SCATTER_V11_LIO\r\n- general:\r\n  - platform: MT6765\r\n  - project: dandelion\r\n" +
        "  - config_version: V1.1.5\r\n  - storage: EMMC\r\n" + blocks;
    string Blk(string name, string file, string start, string size,
        string region = "EMMC_USER", bool dl = true, string op = "FLASH") =>
        "- partition_index: SYS\r\n" +
        "  - partition_name: " + name + "\r\n" +
        "  - file_name: " + file + "\r\n" +
        "  - is_download: " + (dl ? "true" : "false") + "\r\n" +
        "  - linear_start_addr: " + start + "\r\n" +
        "  - partition_size: " + size + "\r\n" +
        "  - region: " + region + "\r\n" +
        "  - storage: HW_STORAGE_EMMC\r\n" +
        "  - operation_type: " + op + "\r\n";
    void MkImg(string name, int bytes) => File.WriteAllBytes(Path.Combine(fwDir, name), new byte[bytes]);
    void WriteSparse(string path, uint blkSz, uint blocks)
    {
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((uint)0xED26FF3A);
        bw.Write((ushort)1); bw.Write((ushort)0);
        bw.Write((ushort)28); bw.Write((ushort)12);
        bw.Write(blkSz); bw.Write(blocks);
        bw.Write((uint)0); bw.Write((uint)0);
    }

    MkImg("preloader_x.bin", 1024);
    MkImg("boot.img", 2048);
    MkImg("super.img", 4096);
    MkImg("vendor.img", 4096);
    string sGood = Path.Combine(fwDir, "good_scatter.txt");
    File.WriteAllText(sGood, GoodScatter(
        Blk("preloader", "preloader_x.bin", "0x0", "0x40000") +
        Blk("boot", "boot.img", "0x1000000", "0x2000000") +
        Blk("super", "super.img", "0x5d000000", "0x120000000") +
        Blk("pgpt", "NONE", "0x0", "0x8000", dl: false)));
    var good = ScatterParser.Load(sGood);
    Check(good.FormatSupported && good.Platform == "MT6765" && good.ConfigVersion == "V1.1.5",
        "scatter: well-formed parses with platform/config");
    Check(good.Entries.Count == 4 && good.FlashableEntries.Count() == 3,
        "scatter: 4 rows parsed, 3 flashable (is_download=false skipped)");
    Check(good.Find("preloader") is { NumbersValid: true, Selectable: true },
        "scatter: preloader start 0x0 parses as valid number");
    Check(!good.HasBlockingErrors, "scatter: clean package has no blocking errors");

    string sBad = Path.Combine(fwDir, "bad_scatter.txt");
    File.WriteAllText(sBad, "just some text\r\nno blocks here\r\n");
    var bad = ScatterParser.Load(sBad);
    Check(!bad.FormatSupported && bad.Issues.Any(i => i.Code == "unsupported-format"),
        "scatter: non-scatter text rejected as unsupported (no guessing)");

    string sHex = Path.Combine(fwDir, "hex_scatter.txt");
    File.WriteAllText(sHex, GoodScatter(Blk("boot", "boot.img", "0x1000", "0xZZZ")));
    var badHex = ScatterParser.Load(sHex);
    Check(badHex.Find("boot") is { LoadError: not null, Selectable: false } &&
          badHex.Issues.Any(i => i.Code == "invalid-number"),
        "scatter: invalid hex size flagged and excluded from flash map");

    string sDup = Path.Combine(fwDir, "dup_scatter.txt");
    File.WriteAllText(sDup, GoodScatter(
        Blk("boot", "boot.img", "0x1000", "0x2000") +
        Blk("boot", "vendor.img", "0x5000", "0x2000")));
    var dup = ScatterParser.Load(sDup);
    Check(dup.Entries.Count == 2 && dup.Entries[1].LoadError != null &&
          dup.Issues.Any(i => i.Code == "duplicate-name"),
        "scatter: duplicate partition name flagged");

    string sTrav = Path.Combine(fwDir, "trav_scatter.txt");
    File.WriteAllText(sTrav, GoodScatter(Blk("boot", "..\\evil.img", "0x1000", "0x2000")));
    var trav = ScatterParser.Load(sTrav);
    Check(trav.Find("boot") is { LoadError: not null, ResolvedPath: null } &&
          trav.Issues.Any(i => i.Code == "path-traversal"),
        "scatter: file_name escaping firmware folder blocked");
    Check(ScatterParser.ResolveInside(fwDir, "boot.img") != null &&
          ScatterParser.ResolveInside(fwDir, "..\\evil.img") == null,
        "path: in-folder resolves, parent-dir blocked");

    string sOv = Path.Combine(fwDir, "ov_scatter.txt");
    File.WriteAllText(sOv, GoodScatter(
        Blk("boot", "boot.img", "0x1000", "0x2000") +
        Blk("vendor", "vendor.img", "0x2000", "0x3000")));
    var ov = ScatterParser.Load(sOv);
    Check(ov.Entries.All(e => e.LoadError != null) &&
          ov.Issues.Any(i => i.Code == "address-overlap" && i.Severity == IssueSeverity.Error),
        "overlap: two flashable entries overlapping both excluded with error");

    string sRes = Path.Combine(fwDir, "reserved_scatter.txt");
    File.WriteAllText(sRes, GoodScatter(
        Blk("super", "super.img", "0x100000000", "0x40000000") +
        Blk("sgpt", "NONE", "0x110000000", "0x4200", dl: false)));
    var resv = ScatterParser.Load(sRes);
    Check(resv.Find("super") is { Selectable: true } &&
          resv.Issues.Any(i => i.Code == "address-overlap" && i.Severity == IssueSeverity.Warning) &&
          !resv.Issues.Any(i => i.Code == "address-overlap" && i.Severity == IssueSeverity.Error),
        "overlap: reserved (non-download) entry inside super stays warning — vendor layout tolerated");

    string sMiss = Path.Combine(fwDir, "miss_scatter.txt");
    File.WriteAllText(sMiss, GoodScatter(Blk("odin", "nope.img", "0x1000", "0x2000")));
    var miss = ScatterParser.Load(sMiss);
    Check(miss.Find("odin") is { ImageExists: false, Selectable: true } &&
          miss.Issues.Any(i => i.Code == "missing-image" && i.Severity == IssueSeverity.Warning),
        "scatter: missing image → warning, entry still listed for dialog");

    string sSparse = Path.Combine(fwDir, "sparse_scatter.txt");
    File.WriteAllText(sSparse, GoodScatter(Blk("vendor2", "sparse.img", "0x100000000", "0x1000000")));
    WriteSparse(Path.Combine(fwDir, "sparse.img"), 4096, 0x10000);
    var sp = ScatterParser.Load(sSparse);
    Check(sp.Find("vendor2")!.EffectiveImageSize == 268435456L,
        "sparse: expanded size computed from 28-byte header");
    Check(sp.Issues.Any(i => i.Code == "image-larger-than-partition" && i.Partition == "vendor2"),
        "sparse: image larger than partition flagged");
    Check(sp.Find("vendor2") is { Selectable: true },
        "sparse: oversize entry still listed (preflight blocks when selected)");

    Check(FlashRisk.Classify(good.Find("preloader")!) == RiskLevel.HighRisk &&
          !FlashRisk.DefaultSelected(good.Find("preloader")!),
        "risk: preloader is high-risk and default-unselected");
    Check(FlashRisk.Classify(good.Find("boot")!) == RiskLevel.Normal &&
          FlashRisk.DefaultSelected(good.Find("boot")!),
        "risk: normal partition default-selected");
    Check(!FlashRisk.DefaultSelected(good.Find("pgpt")!),
        "risk: non-download table entry never default-selected");
    string sData = Path.Combine(fwDir, "data_scatter.txt");
    File.WriteAllText(sData, GoodScatter(
        Blk("userdata", "userdata.img", "0x140000000", "0x200000000") +
        Blk("nvram", "nvram.img", "0x1000000", "0x200000") +
        Blk("mystery", "mystery.img", "0x2000000", "0x1000", op: "PROTECTED")));
    var dataPkg = ScatterParser.Load(sData);
    Check(FlashRisk.Classify(dataPkg.Find("userdata")!) == RiskLevel.HighRisk &&
          FlashRisk.Classify(dataPkg.Find("nvram")!) == RiskLevel.HighRisk &&
          FlashRisk.Classify(dataPkg.Find("mystery")!) == RiskLevel.HighRisk,
        "risk: userdata + nvram + PROTECTED op all high-risk");

    Check(sp.Find("vendor2")!.ImageIsSparse, "sparse: the parser marks a sparse-magic image as sparse");
    Check(!good.Find("boot")!.ImageIsSparse && !good.Find("preloader")!.ImageIsSparse,
        "sparse: a raw image is not flagged sparse");

    Check(FlashRow.StatusText(good.Find("boot")!, true) == "Ready · raw",
        "row: normal raw image reads \"Ready · raw\"");
    Check(FlashRow.StatusText(good.Find("preloader")!, true) == "High risk · raw",
        "row: high-risk raw image reads \"High risk · raw\"");
    Check(FlashRow.StatusText(sp.Find("vendor2")!, true) == "Ready · sparse",
        "row: sparse image reads \"Ready · sparse\" — the route differs before the click");
    Check(FlashRow.StatusText(dataPkg.Find("userdata")!, true) == "High risk · no file",
        "row: a named-but-absent image says the file is missing, not \"raw\"");
    Check(FlashRow.StatusText(good.Find("pgpt")!, false) == "Unavailable",
        "row: a non-flashable row says nothing about routes it cannot take");

    Check(FlashRow.StatusTip(good.Find("preloader")!, true).StartsWith("bootloader — wrong image can prevent boot.") &&
          FlashRow.StatusTip(good.Find("preloader")!, true).Contains("Raw image"),
        "row tip: high risk names WHY before the route");
    Check(FlashRow.StatusTip(good.Find("boot")!, true).StartsWith("Raw image:") &&
          !FlashRow.StatusTip(good.Find("boot")!, true).Contains("risk"),
        "row tip: a normal row carries no risk sentence");
    Check(FlashRow.StatusTip(sp.Find("vendor2")!, true).Contains("native Flash Selected expands and validates") &&
          FlashRow.StatusTip(sp.Find("vendor2")!, true).Contains("temporary disk space"),
        "row tip: sparse native flashing names host preparation and temporary space");
    Check(FlashRow.StatusTip(dataPkg.Find("userdata")!, true).Contains("user data erasure") &&
          FlashRow.StatusTip(dataPkg.Find("userdata")!, true).Contains("not on disk"),
        "row tip: risk reason and missing file both reach the tip");
    Check(FlashRow.StatusTip(good.Find("pgpt")!, false).Contains("Nothing is written to it"),
        "row tip: non-flashable says plainly that nothing is written");

    Check(FlashRow.SummaryText(0, 0, true) == "0 selected · nothing will be written",
        "summary: zero selection says nothing will be written");
    Check(FlashRow.SummaryText(3, 5L * 1024 * 1024, false).Contains("3 selected") &&
          FlashRow.SummaryText(3, 5L * 1024 * 1024, false).Contains("5.00 MB to write") &&
          FlashRow.SummaryText(3, 5L * 1024 * 1024, false).Contains("route: mtkclient") &&
          FlashRow.SummaryText(3, 5L * 1024 * 1024, false).Contains("rebuilds the GPT"),
        "summary: mtkclient route names bytes and GPT rebuild");
    Check(FlashRow.SummaryText(2, 1L * 1024 * 1024 * 1024, true).Contains("1.00 GB to write") &&
          FlashRow.SummaryText(2, 1L * 1024 * 1024 * 1024, true).Contains("route: native DA session") &&
          FlashRow.SummaryText(2, 1L * 1024 * 1024 * 1024, true).Contains("sparse expanded on PC") &&
          FlashRow.SummaryText(2, 1L * 1024 * 1024 * 1024, true).Contains("GPT is not rebuilt"),
        "summary: native route names host sparse expansion and no GPT rebuild");
    Check(FlashRow.Bytes(900) == "900 B" && FlashRow.Bytes(2048) == "2.00 KB",
        "summary: byte formatting keeps both small units");

    // The scatter file's own facts, shown on the line above the selection summary.
    Check(FlashRow.PackageText(null).StartsWith("No scatter file"),
        "package facts: no scatter says so instead of inventing a table");
    Check(FlashRow.PackageText(bad) == "Not a scatter file this tool can read — nothing will be written from it",
        "package facts: an unreadable file says nothing will be written");
    string factsGood = FlashRow.PackageText(good);
    Check(factsGood == "MT6765 · dandelion · V1.1.5 · EMMC · 4 rows · 3 flashable · 3 images on disk · " +
          "7.00 KB · 0 errors, 1 warning",
        "package facts: header + row/flashable/image counts + total bytes + issue counts, singular warning");
    string factsData = FlashRow.PackageText(dataPkg);
    Check(factsData.Contains("3 rows") && factsData.Contains("0 images on disk"),
        "package facts: images counted only when they are actually on disk");
    Check(FlashRow.PackageText(good).Length < 140, "package facts: one 856px label line still fits");

    var capsOk = new BackendCapabilities { Name = "test-backend", SupportsFlash = true };
    var capsNo = new BackendCapabilities { Name = "test-no-flash", SupportsFlash = false };

    var rep1 = FirmwarePreflight.Run(good, new[] { "boot" }, null, capsOk);
    Check(rep1.Overall == VerifyState.UnableToVerify && rep1.CanFlash,
        "preflight: no device → unable-to-verify overall, flash allowed");
    Check(rep1.Checks.Single(c => c.Name == "Scatter structure").State == VerifyState.VerifiedCompatible,
        "preflight: clean scatter structure verified");
    Check(rep1.Checks.Single(c => c.Name == "Selected image files").State == VerifyState.VerifiedCompatible,
        "preflight: selected images present + fit");
    Check(rep1.SummaryNote.Contains("not a full compatibility proof"),
        "preflight: summary warns platform match alone is not proof");

    var repBad = FirmwarePreflight.Run(badHex, new[] { "boot" }, null, capsOk);
    Check(repBad.Overall == VerifyState.Mismatch && !repBad.CanFlash,
        "preflight: structural scatter error blocks flash");

    var repMiss = FirmwarePreflight.Run(miss, new[] { "odin" }, null, capsOk);
    Check(repMiss.Overall == VerifyState.Mismatch && !repMiss.CanFlash,
        "preflight: selected missing image blocks flash");

    var repEmpty = FirmwarePreflight.Run(good, Array.Empty<string>(), null, capsOk);
    Check(repEmpty.Overall == VerifyState.UnableToVerify && repEmpty.CanFlash,
        "preflight: empty selection unverifiable, not a mismatch");

    var repStruct = FirmwarePreflight.Run(sp, Array.Empty<string>(), null, capsOk);
    Check(repStruct.Checks.Single(c => c.Name == "Scatter structure").State == VerifyState.VerifiedCompatible,
        "preflight: image-size issue does not poison structure check");
    var repOversel = FirmwarePreflight.Run(sp, new[] { "vendor2" }, null, capsOk);
    Check(repOversel.Overall == VerifyState.Mismatch && !repOversel.CanFlash,
        "preflight: oversize image selected → blocked");

    var probeMatch = new DeviceProbeResult
    {
        Connected = true, Transport = "brom", Chipset = "MT6765",
        StorageType = "eMMC", CapacityBytes = 58240000000L
    };
    var repMatch = FirmwarePreflight.Run(good, new[] { "boot" }, probeMatch, capsOk);
    Check(repMatch.Checks.Single(c => c.Name == "Chipset platform").State == VerifyState.VerifiedCompatible,
        "preflight: matching chipset verified");
    Check(repMatch.Overall == VerifyState.UnableToVerify && repMatch.CanFlash,
        "preflight: auth stays unverifiable even with device connected");
    var probeBad = new DeviceProbeResult
    {
        Connected = true, Transport = "brom", Chipset = "MT6768",
        StorageType = "eMMC", CapacityBytes = 58240000000L
    };
    Check(!FirmwarePreflight.Run(good, new[] { "boot" }, probeBad, capsOk).CanFlash,
        "preflight: mismatched chipset blocks flash");
    var probeUfs = new DeviceProbeResult
    {
        Connected = true, Transport = "brom", Chipset = "MT6765",
        StorageType = "UFS", CapacityBytes = 128000000000L
    };
    Check(!FirmwarePreflight.Run(good, new[] { "boot" }, probeUfs, capsOk).CanFlash,
        "preflight: UFS device vs EMMC scatter blocks flash");
    Check(!FirmwarePreflight.Run(good, new[] { "boot" }, null, capsNo).CanFlash,
        "preflight: backend without flash support blocks flash");

    var st = FlashOperationState.Idle;
    Check(FlashStateMachine.TryTransition(ref st, FlashOperationState.Connecting) && st == FlashOperationState.Connecting,
        "state: Idle→Connecting allowed");
    Check(FlashStateMachine.CanTransition(FlashOperationState.Connecting, FlashOperationState.Writing) &&
          FlashStateMachine.CanTransition(FlashOperationState.Writing, FlashOperationState.Completed) &&
          FlashStateMachine.CanTransition(FlashOperationState.Completed, FlashOperationState.Idle),
        "state: happy path transitions allowed");
    Check(FlashStateMachine.CanTransition(FlashOperationState.Failed, FlashOperationState.Connecting) &&
          FlashStateMachine.CanTransition(FlashOperationState.Interrupted, FlashOperationState.Connecting) &&
          FlashStateMachine.CanTransition(FlashOperationState.Interrupted, FlashOperationState.Idle) &&
          FlashStateMachine.CanTransition(FlashOperationState.Interrupted, FlashOperationState.Failed),
        "state: Interrupted only → Connecting/Idle/Failed");
    Check(!FlashStateMachine.CanTransition(FlashOperationState.Interrupted, FlashOperationState.Writing) &&
          !FlashStateMachine.CanTransition(FlashOperationState.Writing, FlashOperationState.Idle) &&
          !FlashStateMachine.CanTransition(FlashOperationState.Idle, FlashOperationState.Writing) &&
          !FlashStateMachine.CanTransition(FlashOperationState.Writing, FlashOperationState.Writing),
        "state: unsafe transitions refused (incl. self-loop)");

    var mplan = new FlashPlan();
    mplan.Items.Add(new FlashPlanItem { Partition = "a", Phase = "BROM", ImagePath = "a.img", SizeBytes = 10 });
    mplan.Items.Add(new FlashPlanItem { Partition = "b", Phase = "Fastboot", ImagePath = "b.img", SizeBytes = 20 });
    mplan.Items.Add(new FlashPlanItem { Partition = "c", Phase = "Fastboot", ImagePath = "c.img", SizeBytes = 30 });
    Check(mplan.BromItems.Count() == 1 && mplan.FastbootItems.Count() == 2 && mplan.TotalBytes == 60,
        "plan: phase split + total bytes");

    var sync = new SyncProgress<FlashProgress>();
    var mock = new MockMtkBackend();
    var mres = await mock.FlashAsync(mplan, sync, CancellationToken.None);
    Check(mres.Success && mres.Written.Count == 3,
        "mock backend: happy path writes all partitions");
    Check(mres.Verification == VerificationState.NotPerformed,
        "mock: verification honestly reported as not performed");
    Check(mock.Capabilities.IsMock && mock.Capabilities.Name.Contains("MOCK") &&
          mres.BackendNote.Contains("MOCK"),
        "mock: backend labeled mock/test-only");
    Check(sync.V.Count == 3 && sync.V.All(p => p.Percent == null),
        "mock: progress carries no fabricated percent");
    Check(mock.LastPlan.Items.Count == 3,
        "mock: observed plan recorded for assertions");

    var mfail = new MockMtkBackend(fail: true);
    var fres = await mfail.FlashAsync(mplan, null, CancellationToken.None);
    Check(!fres.Success && fres.Failed.Count == 1 && fres.Failed[0].Category == ErrorCategory.Write,
        "mock: failure surfaces with error category");
    Check(fres.Skipped.Count > 0 && fres.Verification == VerificationState.NotPerformed,
        "mock: remaining partitions skipped, no fake verification");

    var mcan = new MockMtkBackend(cancelMidway: true);
    var cres = await mcan.FlashAsync(mplan, null, CancellationToken.None);
    Check(cres.Cancelled && cres.Written.Count == 1 && cres.Skipped.Count == 2,
        "mock: cancel midway → partial write + explicit skips");
    Check(cres.NeedsRedetection, "mock: cancelled run flags device redetection");

    using (var cts = new CancellationTokenSource())
    {
        cts.Cancel();
        var pres = await new MockMtkBackend().FlashAsync(mplan, null, cts.Token);
        Check(pres.Cancelled && pres.Written.Count == 0,
            "mock: pre-cancelled token writes nothing");
    }

    bool bromCalled = false, fbCalled = false;
    var adapterOk = new MtkBackendAdapter(
        (fp, pr, ct) => { bromCalled = true; var o = new PhaseOutcome(); o.Written.AddRange(fp.BromItems.Select(i => i.Partition)); return Task.FromResult(o); },
        (fp, pr, ct) => { fbCalled = true; var o = new PhaseOutcome(); o.Written.AddRange(fp.FastbootItems.Select(i => i.Partition)); return Task.FromResult(o); });
    var ares = await adapterOk.FlashAsync(mplan, null, CancellationToken.None);
    Check(ares.Success && ares.Written.Count == 3 && bromCalled && fbCalled,
        "adapter: real-interface path runs both phases");
    Check(ares.Verification == VerificationState.NotPerformed && ares.BackendNote.Contains("readback"),
        "adapter: result states no post-write readback");
    Check(!adapterOk.Capabilities.IsMock && adapterOk.Capabilities.SupportsFlash &&
          !adapterOk.Capabilities.SupportsReadbackHash && !adapterOk.Capabilities.SafeCancelDuringWrite,
        "adapter: real caps honest (no readback hash, no safe cancel)");

    bool fbAfterStop = false;
    var adapterStop = new MtkBackendAdapter(
        (fp, pr, ct) =>
        {
            var o = new PhaseOutcome();
            o.Failed.Add(("preloader", ErrorCategory.Connection, "usb gone"));
            o.StopFurtherPhases = true;
            o.AbortCategory = ErrorCategory.Connection;
            return Task.FromResult(o);
        },
        (fp, pr, ct) => { fbAfterStop = true; return Task.FromResult(new PhaseOutcome()); });
    var sres = await adapterStop.FlashAsync(mplan, null, CancellationToken.None);
    Check(!sres.Success && !fbAfterStop, "adapter: phase1 stop skips fastboot phase");
    Check(sres.NeedsRedetection && sres.Failed[0].Category == ErrorCategory.Connection,
        "adapter: connection failure flags redetection");

    var adapterThrow = new MtkBackendAdapter(
        (fp, pr, ct) => throw new InvalidOperationException("fastboot USB connect timeout"),
        (fp, pr, ct) => Task.FromResult(new PhaseOutcome()));
    var tres = await adapterThrow.FlashAsync(mplan, null, CancellationToken.None);
    Check(!tres.Success && tres.Failed.Count == 1 && tres.Failed[0].Category == ErrorCategory.Connection,
        "adapter: exception classified as connection error");
    Check(tres.NeedsRedetection && tres.Verification == VerificationState.NotPerformed,
        "adapter: exception → redetection, no fake verification");

    var adapterOce = new MtkBackendAdapter(
        (fp, pr, ct) => throw new OperationCanceledException(),
        (fp, pr, ct) => Task.FromResult(new PhaseOutcome()));
    var ores = await adapterOce.FlashAsync(mplan, null, CancellationToken.None);
    Check(ores.Cancelled && !ores.Success && ores.NeedsRedetection,
        "adapter: OperationCanceledException → cancelled result");

    bool bromHandoff = false;
    var adapterFbOnly = new MtkBackendAdapter(
        (fp, pr, ct) => { bromHandoff = true; return Task.FromResult(new PhaseOutcome()); },
        (fp, pr, ct) => { var o = new PhaseOutcome(); o.Written.Add("super"); return Task.FromResult(o); });
    var fonly = new FlashPlan();
    fonly.Items.Add(new FlashPlanItem { Partition = "super", Phase = "Fastboot" });
    var fbOnlyRes = await adapterFbOnly.FlashAsync(fonly, null, CancellationToken.None);
    Check(bromHandoff && fbOnlyRes.Success,
        "adapter: fastboot-only plan still runs BROM handoff phase");

    string hex40 = new string('a', 40);
    string r1 = LogRedactor.Redact("IMEI: 123456789012345\r\nother line");
    Check(!r1.Contains("123456789012345") && r1.Contains("other line"),
        "redact: IMEI line redacted, other lines kept");
    string r2 = LogRedactor.Redact("da " + hex40 + " end");
    Check(!r2.Contains(hex40) && r2.Contains("[REDACTED-HEX]"),
        "redact: 40-char hex id removed");
    string r3 = LogRedactor.Redact("fastboot -s \"5TUW8T75JJ5PGURC\" flash boot");
    Check(!r3.Contains("5TUW8T75JJ5PGURC") && r3.Contains("-s \"[REDACTED]\""),
        "redact: fastboot serial argument removed");
    string r4 = LogRedactor.Redact("device id 866754012345678 done");
    Check(!r4.Contains("866754012345678") && r4.Contains("done"),
        "redact: long digit identifier removed");
    string r5 = LogRedactor.Redact("flash boot.img in 12.3s, 45 files");
    Check(r5 == "flash boot.img in 12.3s, 45 files",
        "redact: normal log text untouched");

    var hr = new FlashResult();
    hr.Written.Add("boot");
    hr.Failed.Add(("super", ErrorCategory.Write, "timeout"));
    hr.Skipped.Add(("userdata", "cancelled by user"));
    string sum = hr.BuildHonestSummary();
    Check(sum.Contains("FAILED") && sum.Contains("Verification: not performed"),
        "summary: honest result states verification not performed");
    Check(sum.Contains("boot") && sum.Contains("super") && sum.Contains("userdata"),
        "summary: written/failed/skipped all listed");
    var hs = new FlashResult { Success = true };
    hs.Written.Add("boot");
        hs.BackendNote = "Backend: MOCK (test only) — no device was touched.";
    string hsum = hs.BuildHonestSummary();
    Check(hsum.Contains("SUCCESS") && hsum.Contains("MOCK"),
        "summary: success line includes backend note");

    var mtkGpt = MtkClientEngine.ParseGpt(new MtkCommandResult(0, new[] {
        "boot: Offset 0x1000, Length 0x2000, Flags 0x00000000" }));
    Check(mtkGpt.Count == 1 && mtkGpt[0].Offset == 4096 && mtkGpt[0].Length == 8192,
        "new MTK engine: GPT byte ranges parsed correctly");
    bool RejectMtk(Action check) { try { check(); return false; } catch (Exception ex) when (ex is IOException or InvalidDataException) { return true; } }
    Check(RejectMtk(() => MtkClientEngine.ParseGpt(new(0, new[] { "GPT Table:" }))),
        "new MTK engine: empty GPT never authorizes operations");
    Check(RejectMtk(() => MtkClientEngine.ParseGpt(new(0, new[] {
        "boot: Offset 0x1000, Length 0x2000", "BOOT: Offset 0x3000, Length 0x2000" }))),
        "new MTK engine: duplicate partition names rejected");
    Check(RejectMtk(() => MtkClientEngine.ParseGpt(new(0, new[] { "boot: Offset 0x1000, Length 0x0" }))),
        "new MTK engine: zero-size partition rejected");
    MtkClientEngine.RequireWriteEvidence(new(0, new[] { @"Wrote C:\ROM folder\tee.img to sector 10 with sector count 20." }), "tee1", @"C:\ROM folder\tee.img");
    Check(true, "new MTK engine: completion matches exact image path even when partition name differs");
    Check(RejectMtk(() => MtkClientEngine.RequireWriteEvidence(new(0, new[] { "100%" }), "boot", "boot.img")),
        "new MTK engine: exit zero and progress are not write evidence");
    Check(RejectMtk(() => MtkClientEngine.RequireWriteEvidence(new(0, new[] { "Wrote other.img to sector 10" }), "boot", "boot.img")),
        "new MTK engine: another image cannot satisfy completion");
    Check(RejectMtk(() => MtkClientEngine.RequireWriteEvidence(new(0, new[] { "Wrote boot.img to sector 10", "Failed to write" }), "boot", "boot.img")),
        "new MTK engine: error output overrides completion evidence");
    Check(RejectMtk(() => MtkClientEngine.RequireWriteEvidence(new(1, new[] { "Wrote boot.img to sector 10" }), "boot", "boot.img")),
        "new MTK engine: failed process overrides completion evidence");
    Check(new MtkCommandResult(0, new[] { "DaHandler - Please disconnect, start mtkclient and reconnect." }).HasError,
        "new MTK engine: reconnect-demand output is an error even with exit zero");
    Check(MtkClientEngine.HasWriteEvidence(new(0, new[] { @"Wrote C:\t\seccfg_out.bin to sector 4" }), @"C:\t\seccfg_out.bin"),
        "new MTK engine: HasWriteEvidence accepts exact completion line");
    Check(!MtkClientEngine.HasWriteEvidence(new(0, new[] { "Wrote other.bin to sector 4" }), @"C:\t\seccfg_out.bin"),
        "new MTK engine: HasWriteEvidence rejects another image path");
    Check(new MtkCommandResult(0, new[] { "Reset command was sent. Rebooting device to system - unplug the USB cable." }).HasError == false,
        "new MTK engine: reset confirmation is not an error");

    Check(MtkClientEngine.LuhnValid("490154203237518") && MtkClientEngine.LuhnValid("356938035643809"),
        "IMEI write: valid 15-digit Luhn IMEIs accepted");
    Check(!MtkClientEngine.LuhnValid("490154203237519") && !MtkClientEngine.LuhnValid("4901542032375") && !MtkClientEngine.LuhnValid("49015420323a518"),
        "IMEI write: bad check digit, short, and non-digit IMEIs rejected");
    Check(MtkClientEngine.IsImeiWriteListArg(new[] { "da", "imei", "490154203237518,356938035643809" }, 2),
        "new MTK engine: da imei positional IMEI list is the comma carve-out slot");
    Check(!MtkClientEngine.IsImeiWriteListArg(new[] { "da", "imei", "a,b" }, 1) &&
          !MtkClientEngine.IsImeiWriteListArg(new[] { "w", "boot", "a,b.img" }, 2) &&
          !MtkClientEngine.IsImeiWriteListArg(new[] { "multi", "e a,b" }, 1),
        "new MTK engine: comma guard still rejects commas outside the da imei slot");
    Check(MtkClientEngine.BaseFlagInsertIndex(new[] { "w", "boot", "boot.img" }) == 1 &&
          MtkClientEngine.BaseFlagInsertIndex(new[] { "reset" }) == 1,
        "new MTK engine: single-level commands get base flags after the subcommand");
    Check(MtkClientEngine.BaseFlagInsertIndex(new[] { "da", "imei", "a,b", "--write" }) == 2,
        "new MTK engine: two-level da subcommand gets base flags after the subcommand token");
    Check(MtkClientEngine.BaseFlagInsertIndex(new[] { "multi", "da seccfg unlock;reset" }) == -1,
        "new MTK engine: multi script keeps top-level base flags only");
    Check(MtkClientEngine.IsImeiWriteSuccess(new(0, new[] { "IMEIs were written successfully" })) &&
          !MtkClientEngine.IsImeiWriteFailed(new(0, new[] { "IMEIs were written successfully" })),
        "IMEI write: success line detected without error");
    Check(MtkClientEngine.IsImeiWriteFailed(new(0, new[] { "Failed to write IMEIs." })) &&
          !MtkClientEngine.IsImeiWriteSuccess(new(0, new[] { "Failed to write IMEIs." })),
        "IMEI write: explicit failure line detected even with exit zero");
    Check(MtkClientEngine.IsImeiWriteFailed(new(1, new[] { "something broke" })),
        "IMEI write: nonzero exit counts as failure");
    Check(!MtkClientEngine.IsImeiWriteSuccess(new(0, new[] { "done" })) &&
          !MtkClientEngine.IsImeiWriteFailed(new(0, new[] { "done" })),
        "IMEI write: unrelated output is neither success nor failure");

    Check(ReviewSafety.QcEfsPartitions.Length == 5 &&
          ReviewSafety.QcEfsPartitions.SequenceEqual(new[] { "persist", "modemst1", "modemst2", "fsg", "fsc" }),
        "QC EFS: partition set covers persist/modemst1/modemst2/fsg/fsc");

    string efsDir = Path.Combine(dir, "efs"); Directory.CreateDirectory(efsDir);
    foreach (var (name, len) in new[] { ("persist", 100L), ("modemst1", 200L), ("modemst2", 300L), ("fsg", 400L), ("fsc", 500L) })
        File.WriteAllBytes(Path.Combine(efsDir, name + ".bin"), new byte[len]);
    var efsLive = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    { ["persist"] = 100, ["modemst1"] = 200, ["modemst2"] = 300, ["fsg"] = 400, ["fsc"] = 999 };
    var (efsPlan, efsSkipped) = ReviewSafety.BuildQcEfsRestorePlan(efsDir, ReviewSafety.QcEfsPartitions, efsLive);
    Check(efsPlan.Count == 4 && efsSkipped.Count == 1 && efsSkipped[0].Contains("fsc"),
        "QC EFS: size mismatch skips only the mismatched partition");

    string efsMissingDir = Path.Combine(dir, "efs-partial"); Directory.CreateDirectory(efsMissingDir);
    File.WriteAllBytes(Path.Combine(efsMissingDir, "persist.bin"), new byte[100]);
    var (missingPlan, missingSkipped) = ReviewSafety.BuildQcEfsRestorePlan(efsMissingDir, ReviewSafety.QcEfsPartitions,
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
    Check(missingPlan.Count == 1 && missingPlan[0].Name == "persist" && missingSkipped.Count == 4 &&
          missingSkipped.All(s => s.Contains("no ") && s.Contains(".bin")),
        "QC EFS: missing .bin files are skipped with a note");

    var (noLivePlan, noLiveSkipped) = ReviewSafety.BuildQcEfsRestorePlan(efsDir, ReviewSafety.QcEfsPartitions,
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
    Check(noLivePlan.Count == 5 && noLiveSkipped.Count == 0,
        "QC EFS: without a live GPT, all present files are planned (no size check)");

    // ===== Native QC firehose: GPT parser + rawprogram XML + sector math =====
    byte[] GptHeader(long entryLba, int entryCount, int entrySize, string sig = "EFI PART")
    {
        var h = new byte[512];
        Encoding.ASCII.GetBytes(sig).CopyTo(h, 0);
        BitConverter.GetBytes(entryLba).CopyTo(h, 72);
        BitConverter.GetBytes(entryCount).CopyTo(h, 80);
        BitConverter.GetBytes(entrySize).CopyTo(h, 84);
        return h;
    }
    byte[] GptEntry(string name, long firstLba, long lastLba)
    {
        var e = new byte[128];
        Guid.NewGuid().ToByteArray().CopyTo(e, 0);
        BitConverter.GetBytes(firstLba).CopyTo(e, 32);
        BitConverter.GetBytes(lastLba).CopyTo(e, 40);
        Encoding.Unicode.GetBytes(name).CopyTo(e, 56);
        return e;
    }

    Check(GptParser.HasSignature(GptHeader(2, 4, 128)) && !GptParser.HasSignature(new byte[512]),
        "GPT: EFI PART signature detected, empty header rejected");
    var (ec, es, elba) = GptParser.HeaderInfo(GptHeader(2, 4, 128));
    Check(ec == 4 && es == 128 && elba == 2, "GPT: header entry count/size/LBA parsed");
    bool badHdr = false;
    try { GptParser.HeaderInfo(new byte[512]); } catch (InvalidDataException) { badHdr = true; }
    Check(badHdr, "GPT: header without signature is rejected");
    bool shortHdr = false;
    try { GptParser.HeaderInfo(new byte[64]); } catch (InvalidDataException) { shortHdr = true; }
    Check(shortHdr, "GPT: undersized header is rejected");

    var hdr = GptHeader(2, 2, 128);
    var entries = new byte[2 * 128];
    GptEntry("boot", 34, 66).CopyTo(entries, 0);
    GptEntry("system", 67, 500).CopyTo(entries, 128);
    var parts = GptParser.ParseEntries(hdr, entries);
    Check(parts.Count == 2 && parts[0].Name == "boot" && parts[0].FirstLba == 34 && parts[0].LastLba == 66,
        "GPT: entries parsed with name + LBA range");
    Check(parts[0].SectorCount == 33 && parts[0].ByteOffset(512) == 34L * 512 && parts[0].ByteLength(512) == 33L * 512,
        "GPT: sector count and byte offset/length math");
    Check(parts[1].Name == "system" && parts[1].ByteLength(4096) == (500 - 67 + 1L) * 4096,
        "GPT: 4096-byte sector math for UFS devices");

    var entriesZero = new byte[2 * 128];
    GptEntry("boot", 34, 66).CopyTo(entriesZero, 128);
    var partsOne = GptParser.ParseEntries(hdr, entriesZero);
    Check(partsOne.Count == 1 && partsOne[0].Name == "boot", "GPT: zero (unused) entries are skipped");

    var xml = GptParser.BuildRawProgramXml(parts, 512);
    var progs = XDocument.Parse(xml).Descendants("program").ToList();
    Check(progs.Count == 2, "rawprogram: one program entry per partition");
    Check((string?)progs[0].Attribute("label") == "boot" &&
          (string?)progs[0].Attribute("filename") == "boot.bin" &&
          (string?)progs[0].Attribute("start_sector") == "34" &&
          (string?)progs[0].Attribute("num_partition_sectors") == "33" &&
          (string?)progs[0].Attribute("start_byte_hex") == "0x4400" &&
          (string?)progs[0].Attribute("SECTOR_SIZE_IN_BYTES") == "512",
        "rawprogram: boot entry attributes + byte offset (34*512=0x4400)");
    Check((string?)progs[1].Attribute("start_sector") == "67" &&
          (string?)progs[1].Attribute("num_partition_sectors") == "434" &&
          (string?)progs[1].Attribute("start_byte_hex") == "0x8600",
        "rawprogram: system entry LBA range + byte offset (67*512=0x8600)");

    var escHdr = GptHeader(2, 1, 128);
    var escEntries = new byte[128];
    GptEntry("a&b", 10, 20).CopyTo(escEntries, 0);
    var escXml = GptParser.BuildRawProgramXml(GptParser.ParseEntries(escHdr, escEntries), 512);
    Check(escXml.Contains("label=\"a&amp;b\"") && escXml.Contains("filename=\"a&amp;b.bin\""),
        "rawprogram: partition names are XML-escaped");

    string FirehoseEnvelope(string value, string? rawMode = null, string logs = "") =>
        "<?xml version=\"1.0\"?><data>" + logs + "<response value=\"" + value + "\"" +
        (rawMode == null ? "" : " rawmode=\"" + rawMode + "\"") + "/></data>";
    byte[] FirehoseBytes(string xml) => Encoding.UTF8.GetBytes(xml);
    FirehoseIO FirehoseFixture(byte[] wire, int maxChunk = 65536, Action? onEmpty = null)
    {
        int offset = 0;
        return new FirehoseIO((buffer, start, count) =>
        {
            int take = Math.Min(wire.Length - offset, Math.Min(count, maxChunk));
            if (take == 0) { onEmpty?.Invoke(); return 0; }
            Array.Copy(wire, offset, buffer, start, take);
            offset += take;
            return take;
        });
    }
    async Task<(byte[] Bytes, Exception? Error, List<int> Progress)> FirehoseReadFixture(
        FirehoseIO io, int length, CancellationToken ct = default, Func<bool>? stopped = null, int timeoutMs = 1000)
    {
        using var sink = new MemoryStream();
        var progress = new List<int>();
        try
        {
            await io.ReadPayloadAsync(length, sink, pct => progress.Add(pct), ct, stopped, timeoutMs);
            return (sink.ToArray(), null, progress);
        }
        catch (Exception ex) { return (sink.ToArray(), ex, progress); }
    }
    var firehoseStart = FirehoseBytes(FirehoseEnvelope("ACK", "true"));
    var firehoseEnd = FirehoseBytes(FirehoseEnvelope("ACK", "false"));
    var firehosePayload = new byte[512];
    for (int i = 0; i < firehosePayload.Length; i++) firehosePayload[i] = (byte)i;
    new byte[] { 0x0a, 0x0d, 0x20, 0x09, 0x00 }.CopyTo(firehosePayload, 0);
    firehoseEnd.CopyTo(firehosePayload, 64);
    var firehoseWire = firehoseStart.Concat(firehosePayload).Concat(firehoseEnd).ToArray();
    foreach (int chunk in new[] { 1, 2, 7, 64, 4096 })
    {
        var result = await FirehoseReadFixture(FirehoseFixture(firehoseWire, chunk), firehosePayload.Length);
        Check(result.Error == null && result.Bytes.SequenceEqual(firehosePayload) && result.Progress.Last() == 100,
            "Firehose framing: exact raw payload across " + chunk + "-byte fragments, including leading whitespace and embedded XML");
    }
    var firehoseLarge = Enumerable.Range(0, 131073).Select(i => (byte)i).ToArray();
    var firehoseLargeResult = await FirehoseReadFixture(
        FirehoseFixture(firehoseStart.Concat(firehoseLarge).Concat(firehoseEnd).ToArray()), firehoseLarge.Length);
    Check(firehoseLargeResult.Error == null && firehoseLargeResult.Bytes.SequenceEqual(firehoseLarge),
        "Firehose framing: payload streams beyond the pending-buffer size without XML contamination");
    var firehoseNext = FirehoseBytes(FirehoseEnvelope("NAK"));
    var firehoseQueued = FirehoseFixture(firehoseWire.Concat(firehoseNext).ToArray());
    var firehoseQueuedRead = await FirehoseReadFixture(firehoseQueued, firehosePayload.Length);
    var firehoseQueuedResponse = await firehoseQueued.ReadResponseAsync(1000, CancellationToken.None);
    Check(firehoseQueuedRead.Error == null && firehoseQueuedRead.Bytes.Length == 512 && !firehoseQueuedResponse.IsAck,
        "Firehose framing: terminal ACK consumed and following response preserved, not mistaken for read success");
    var firehoseTwoReads = FirehoseFixture(firehoseWire.Concat(firehoseWire).ToArray());
    var firehoseFirstRead = await FirehoseReadFixture(firehoseTwoReads, 512);
    var firehoseSecondRead = await FirehoseReadFixture(firehoseTwoReads, 512);
    Check(firehoseFirstRead.Error == null && firehoseSecondRead.Error == null &&
          firehoseFirstRead.Bytes.SequenceEqual(firehoseSecondRead.Bytes),
        "Firehose framing: consecutive GPT-style reads do not consume stale acknowledgements");
    string firehoseLog = "<?xml version=\"1.0\"?><data><log value=\"NAK ERROR text is not response status\"/></data>\n";
    var firehoseLogged = await FirehoseReadFixture(FirehoseFixture(
        FirehoseBytes(firehoseLog).Concat(firehoseWire).ToArray(), 7), 512);
    Check(firehoseLogged.Error == null && firehoseLogged.Bytes.SequenceEqual(firehosePayload),
        "Firehose framing: separate log envelope and XML whitespace do not alter raw data or ACK status");
    var firehoseFullClose = FirehoseBytes("<data><response value='ACK' rawmode='true'></response></data>");
    var firehoseFullCloseResult = await FirehoseReadFixture(FirehoseFixture(
        firehoseFullClose.Concat(firehosePayload).Concat(firehoseEnd).ToArray(), 1), 512);
    Check(firehoseFullCloseResult.Error == null && firehoseFullCloseResult.Bytes.SequenceEqual(firehosePayload),
        "Firehose framing: non-self-closing response consumes the complete data envelope");
    var firehoseMisleading = FirehoseFixture(FirehoseBytes(FirehoseEnvelope("NAK", logs: "<log value='ACK'/>")));
    Check(!(await firehoseMisleading.ReadResponseAsync(1000, CancellationToken.None)).IsAck,
        "Firehose framing: ACK in log text cannot override the response value");
    var firehosePartialAck = FirehoseFixture(FirehoseBytes(FirehoseEnvelope("BACKUP_ACK")));
    Check(!(await firehosePartialAck.ReadResponseAsync(1000, CancellationToken.None)).IsAck,
        "Firehose framing: response value must be exactly ACK");
    foreach (string start in new[] { FirehoseEnvelope("NAK", "true"), FirehoseEnvelope("ACK", "false"), FirehoseEnvelope("ACK") })
    {
        var result = await FirehoseReadFixture(FirehoseFixture(FirehoseBytes(start).Concat(firehosePayload).ToArray()), 512);
        Check(result.Error is IOException && result.Bytes.Length == 0,
            "Firehose framing: initial non-ACK or absent/wrong raw mode writes no payload");
    }
    foreach (string end in new[] { FirehoseEnvelope("NAK", "false"), FirehoseEnvelope("ACK", "true"), FirehoseEnvelope("ACK") })
    {
        var result = await FirehoseReadFixture(FirehoseFixture(firehoseStart.Concat(firehosePayload).Concat(FirehoseBytes(end)).ToArray()), 512);
        Check(result.Error is IOException && result.Bytes.SequenceEqual(firehosePayload),
            "Firehose framing: final non-ACK or absent/wrong raw mode cannot report completion");
    }
    var firehoseMissingEnd = await FirehoseReadFixture(FirehoseFixture(firehoseStart.Concat(firehosePayload).ToArray()), 512, timeoutMs: 40);
    Check(firehoseMissingEnd.Error is TimeoutException && firehoseMissingEnd.Bytes.SequenceEqual(firehosePayload) &&
          !firehoseMissingEnd.Progress.Contains(100),
        "Firehose framing: missing terminal ACK times out instead of accepting a backup");
    var firehoseShortRead = await FirehoseReadFixture(FirehoseFixture(firehoseStart.Concat(firehosePayload[..20]).ToArray()), 512, timeoutMs: 40);
    Check(firehoseShortRead.Error is TimeoutException && firehoseShortRead.Bytes.Length == 20,
        "Firehose framing: stalled raw payload has an idle timeout");
    var firehoseIncompleteXml = await FirehoseReadFixture(FirehoseFixture(FirehoseBytes("<data><response value='ACK' rawmode='true'/>")), 512, timeoutMs: 40);
    Check(firehoseIncompleteXml.Error is TimeoutException && firehoseIncompleteXml.Bytes.Length == 0,
        "Firehose framing: response tag without closing data envelope never starts raw mode");
    var firehoseOversizedXml = await FirehoseReadFixture(FirehoseFixture(FirehoseBytes("<data>" + new string(' ', 65536))), 512);
    Check(firehoseOversizedXml.Error is InvalidDataException && firehoseOversizedXml.Bytes.Length == 0,
        "Firehose framing: XML accumulation is bounded");
    var firehoseDtd = await FirehoseReadFixture(FirehoseFixture(FirehoseBytes(
        "<!DOCTYPE data [<!ENTITY x 'ACK'>]><data><response value='&x;' rawmode='true'/></data>")), 512);
    Check(firehoseDtd.Error is System.Xml.XmlException && firehoseDtd.Bytes.Length == 0,
        "Firehose framing: DTD and entity processing are prohibited");
    var firehoseAmbiguous = await FirehoseReadFixture(FirehoseFixture(FirehoseBytes(
        "<data><response value='ACK' rawmode='true'/><response value='NAK'/></data>")), 512);
    Check(firehoseAmbiguous.Error is InvalidDataException && firehoseAmbiguous.Bytes.Length == 0,
        "Firehose framing: multiple response elements are refused");
    using (var firehoseCancel = new CancellationTokenSource())
    {
        firehoseCancel.Cancel();
        var result = await FirehoseReadFixture(FirehoseFixture(firehoseWire), 512, firehoseCancel.Token);
        Check(result.Error is OperationCanceledException && result.Bytes.Length == 0,
            "Firehose framing: cancellation refuses already-buffered data");
    }
    using (var firehoseCancel = new CancellationTokenSource())
    {
        var io = FirehoseFixture(firehoseStart.Concat(firehosePayload[..20]).ToArray(), onEmpty: () => firehoseCancel.Cancel());
        var result = await FirehoseReadFixture(io, 512, firehoseCancel.Token);
        Check(result.Error is OperationCanceledException && result.Bytes.Length == 20,
            "Firehose framing: cancellation interrupts a stalled raw read");
    }
    var firehoseStopped = await FirehoseReadFixture(FirehoseFixture(firehoseWire), 512, stopped: () => true);
    Check(firehoseStopped.Error is OperationCanceledException && firehoseStopped.Bytes.Length == 0,
        "Firehose framing: STOP is honored even when the full reply is already available");
    bool firehoseStopAtEnd = false;
    var firehoseStopEndReader = FirehoseFixture(firehoseStart.Concat(firehosePayload).ToArray(), onEmpty: () => firehoseStopAtEnd = true);
    var firehoseStopEnd = await FirehoseReadFixture(firehoseStopEndReader, 512, stopped: () => firehoseStopAtEnd);
    Check(firehoseStopEnd.Error is OperationCanceledException && firehoseStopEnd.Bytes.SequenceEqual(firehosePayload),
        "Firehose framing: STOP interrupts the wait for final acknowledgement");

    async Task<(List<string> Calls, Exception? Error)> RunQcResetFixture(
        IReadOnlyList<GptPartition> layout, string failAt = "", string cancelAt = "", int sectorSize = 512,
        long lastUsableLba = 4095, Func<GptLayout>? readLayout = null)
    {
        var calls = new List<string>();
        using var cancel = new CancellationTokenSource();
        if (cancelAt == "before") cancel.Cancel();
        try
        {
            var resetLayout = readLayout?.Invoke() ?? new GptLayout(34, lastUsableLba, layout);
            await ReviewSafety.RunQcUserdataResetAsync(resetLayout, sectorSize, (p, token) =>
            {
                token.ThrowIfCancellationRequested();
                calls.Add("erase:" + p.Name);
                if (failAt == p.Name) throw new IOException("Erase refused.");
                if (cancelAt == p.Name) cancel.Cancel();
                return Task.CompletedTask;
            }, token =>
            {
                token.ThrowIfCancellationRequested();
                calls.Add("reset");
                if (failAt == "reset") throw new IOException("Reset refused.");
                return Task.CompletedTask;
            }, cancel.Token);
            return (calls, null);
        }
        catch (Exception ex) { return (calls, ex); }
    }

    var qcUserdata = new GptPartition("userdata", 1000, 1999, "");
    var qcMetadata = new GptPartition("metadata", 100, 199, "");
    var qcResetLayout = new[]
    {
        qcMetadata, new GptPartition("boot", 200, 299, ""),
        new GptPartition("modemst1", 300, 399, ""), new GptPartition("frp", 400, 499, ""), qcUserdata
    };
    var qcResetBoth = await RunQcResetFixture(qcResetLayout);
    Check(qcResetBoth.Error == null && qcResetBoth.Calls.SequenceEqual(new[] { "erase:userdata", "erase:metadata", "reset" }),
        "QC userdata reset: data then metadata then one reset; EFS, boot and FRP excluded");
    var qcResetLegacy = await RunQcResetFixture(new[] { qcUserdata });
    Check(qcResetLegacy.Error == null && qcResetLegacy.Calls.SequenceEqual(new[] { "erase:userdata", "reset" }),
        "QC userdata reset: absent metadata is optional, not a failed second session");
    var qcResetCase = await RunQcResetFixture(new[] { qcUserdata with { Name = "USERDATA" }, qcMetadata with { Name = "METADATA" } });
    Check(qcResetCase.Error == null && qcResetCase.Calls.SequenceEqual(new[] { "erase:USERDATA", "erase:METADATA", "reset" }),
        "QC userdata reset: GPT names match case-insensitively");
    var qcReset4096 = await RunQcResetFixture(qcResetLayout, sectorSize: 4096);
    Check(qcReset4096.Error == null && qcReset4096.Calls.Count == 3,
        "QC userdata reset: planner accepts 4096-byte sector arithmetic without claiming UFS transport support");
    var qcResetMissing = await RunQcResetFixture(new[] { qcMetadata });
    Check(qcResetMissing.Error is InvalidDataException && qcResetMissing.Calls.Count == 0,
        "QC userdata reset: missing userdata refuses before metadata erase or reset");
    var qcResetEmpty = await RunQcResetFixture(Array.Empty<GptPartition>());
    Check(qcResetEmpty.Error is InvalidDataException && qcResetEmpty.Calls.Count == 0,
        "QC userdata reset: empty GPT cannot report success");
    var qcResetDuplicateData = await RunQcResetFixture(new[] { qcUserdata, qcUserdata with { Name = "USERDATA" } });
    Check(qcResetDuplicateData.Error is InvalidDataException && qcResetDuplicateData.Calls.Count == 0,
        "QC userdata reset: duplicate userdata refuses before erase");
    var qcResetDuplicateMeta = await RunQcResetFixture(new[] { qcUserdata, qcMetadata, qcMetadata with { Name = "METADATA" } });
    Check(qcResetDuplicateMeta.Error is InvalidDataException && qcResetDuplicateMeta.Calls.Count == 0,
        "QC userdata reset: ambiguous optional metadata refuses the whole plan");
    var qcResetBadMeta = await RunQcResetFixture(new[] { qcUserdata, qcMetadata with { FirstLba = 200, LastLba = 199 } });
    Check(qcResetBadMeta.Error is InvalidDataException && qcResetBadMeta.Calls.Count == 0,
        "QC userdata reset: invalid metadata is detected before userdata erase");
    var qcResetHeader = await RunQcResetFixture(new[] { qcUserdata with { FirstLba = 1 } });
    Check(qcResetHeader.Error is InvalidDataException && qcResetHeader.Calls.Count == 0,
        "QC userdata reset: target covering the GPT header refuses before erase");
    var qcResetOverlap = await RunQcResetFixture(new[] { qcUserdata, new GptPartition("modemst1", 1500, 1600, "") });
    Check(qcResetOverlap.Error is InvalidDataException && qcResetOverlap.Calls.Count == 0,
        "QC userdata reset: target overlapping EFS refuses before erase");
    var qcResetTargetOverlap = await RunQcResetFixture(new[] { qcUserdata, qcMetadata with { LastLba = 1100 } });
    Check(qcResetTargetOverlap.Error is InvalidDataException && qcResetTargetOverlap.Calls.Count == 0,
        "QC userdata reset: overlapping erase targets refuse before erase");
    var qcResetOverflow = await RunQcResetFixture(new[] { qcUserdata with { LastLba = long.MaxValue } }, lastUsableLba: long.MaxValue);
    Check(qcResetOverflow.Error is OverflowException && qcResetOverflow.Calls.Count == 0,
        "QC userdata reset: overflowing byte range refuses before erase");
    var qcResetReserved = await RunQcResetFixture(new[] { qcUserdata with { FirstLba = 2, LastLba = 33 } });
    Check(qcResetReserved.Error is InvalidDataException && qcResetReserved.Calls.Count == 0,
        "QC userdata reset: primary GPT entries are outside usable LBAs and never erased");
    var qcResetPastEnd = await RunQcResetFixture(new[] { qcUserdata with { LastLba = 4096 } });
    Check(qcResetPastEnd.Error is InvalidDataException && qcResetPastEnd.Calls.Count == 0,
        "QC userdata reset: target beyond last usable LBA cannot erase backup GPT");

    byte[] QcResetHeader()
    {
        var header = GptHeader(2, 4, 128);
        BitConverter.GetBytes(1L).CopyTo(header, 24);
        BitConverter.GetBytes(4096L).CopyTo(header, 32);
        BitConverter.GetBytes(34L).CopyTo(header, 40);
        BitConverter.GetBytes(4000L).CopyTo(header, 48);
        return header;
    }
    var qcRawEntries = new byte[512];
    GptEntry("userdata", 1000, 1999).CopyTo(qcRawEntries, 0);
    GptEntry("metadata", 100, 199).CopyTo(qcRawEntries, 128);
    var qcRawReset = await RunQcResetFixture(Array.Empty<GptPartition>(),
        readLayout: () => GptParser.ParseLayout(QcResetHeader(), qcRawEntries, 512));
    Check(qcRawReset.Error == null && qcRawReset.Calls.SequenceEqual(new[] { "erase:userdata", "erase:metadata", "reset" }),
        "QC userdata reset: raw GPT parsing preserves usable bounds and feeds the reset plan");
    var qcMalformedEntries = (byte[])qcRawEntries.Clone();
    GptEntry("metadata", 200, 199).CopyTo(qcMalformedEntries, 128);
    var qcMalformedReset = await RunQcResetFixture(Array.Empty<GptPartition>(),
        readLayout: () => GptParser.ParseLayout(QcResetHeader(), qcMalformedEntries, 512));
    Check(qcMalformedReset.Error is InvalidDataException && qcMalformedReset.Calls.Count == 0,
        "QC userdata reset: malformed raw metadata cannot disappear as an optional absent partition");
    var qcMalformedDuplicate = (byte[])qcRawEntries.Clone();
    GptEntry("metadata", 300, 299).CopyTo(qcMalformedDuplicate, 256);
    var qcMalformedDuplicateReset = await RunQcResetFixture(Array.Empty<GptPartition>(),
        readLayout: () => GptParser.ParseLayout(QcResetHeader(), qcMalformedDuplicate, 512));
    Check(qcMalformedDuplicateReset.Error is InvalidDataException && qcMalformedDuplicateReset.Calls.Count == 0,
        "QC userdata reset: malformed duplicate metadata cannot bypass raw GPT validation");
    var qcTruncatedReset = await RunQcResetFixture(Array.Empty<GptPartition>(),
        readLayout: () => GptParser.ParseLayout(QcResetHeader(), qcRawEntries[..256], 512));
    Check(qcTruncatedReset.Error is InvalidDataException && qcTruncatedReset.Calls.Count == 0,
        "QC userdata reset: truncated raw GPT refuses rather than planning from partial entries");
    var qcBadBounds = QcResetHeader();
    BitConverter.GetBytes(2L).CopyTo(qcBadBounds, 40);
    var qcBadBoundsReset = await RunQcResetFixture(Array.Empty<GptPartition>(),
        readLayout: () => GptParser.ParseLayout(qcBadBounds, qcRawEntries, 512));
    Check(qcBadBoundsReset.Error is InvalidDataException && qcBadBoundsReset.Calls.Count == 0,
        "QC userdata reset: usable range cannot include the primary GPT array");
    var qcBadBackupBounds = QcResetHeader();
    BitConverter.GetBytes(4095L).CopyTo(qcBadBackupBounds, 48);
    var qcBadBackupReset = await RunQcResetFixture(Array.Empty<GptPartition>(),
        readLayout: () => GptParser.ParseLayout(qcBadBackupBounds, qcRawEntries, 512));
    Check(qcBadBackupReset.Error is InvalidDataException && qcBadBackupReset.Calls.Count == 0,
        "QC userdata reset: usable range cannot include the backup GPT array");
    var qcRawOutsideEntries = (byte[])qcRawEntries.Clone();
    GptEntry("userdata", 2, 33).CopyTo(qcRawOutsideEntries, 0);
    var qcRawOutsideReset = await RunQcResetFixture(Array.Empty<GptPartition>(),
        readLayout: () => GptParser.ParseLayout(QcResetHeader(), qcRawOutsideEntries, 512));
    Check(qcRawOutsideReset.Error is InvalidDataException && qcRawOutsideReset.Calls.Count == 0,
        "QC userdata reset: raw partition outside usable LBAs refuses before planning");

    var qcResetBadSector = await RunQcResetFixture(qcResetLayout, sectorSize: 0);
    Check(qcResetBadSector.Error is InvalidDataException && qcResetBadSector.Calls.Count == 0,
        "QC userdata reset: invalid sector size refuses before erase");
    var qcResetDataFail = await RunQcResetFixture(qcResetLayout, failAt: "userdata");
    Check(qcResetDataFail.Error is IOException && qcResetDataFail.Calls.SequenceEqual(new[] { "erase:userdata" }),
        "QC userdata reset: userdata failure never advances to metadata or reset");
    var qcResetMetaFail = await RunQcResetFixture(qcResetLayout, failAt: "metadata");
    Check(qcResetMetaFail.Error is IOException && qcResetMetaFail.Calls.SequenceEqual(new[] { "erase:userdata", "erase:metadata" }),
        "QC userdata reset: metadata failure prevents reset");
    var qcResetResetFail = await RunQcResetFixture(qcResetLayout, failAt: "reset");
    Check(qcResetResetFail.Error is IOException && qcResetResetFail.Calls.Count == 3,
        "QC userdata reset: reset failure propagates rather than reporting completion");
    var qcResetStopped = await RunQcResetFixture(qcResetLayout, cancelAt: "before");
    Check(qcResetStopped.Error is OperationCanceledException && qcResetStopped.Calls.Count == 0,
        "QC userdata reset: an already-cancelled run sends nothing");
    var qcResetStopData = await RunQcResetFixture(qcResetLayout, cancelAt: "userdata");
    Check(qcResetStopData.Error is OperationCanceledException && qcResetStopData.Calls.SequenceEqual(new[] { "erase:userdata" }),
        "QC userdata reset: STOP after userdata skips metadata and reset");
    var qcResetStopMeta = await RunQcResetFixture(qcResetLayout, cancelAt: "metadata");
    Check(qcResetStopMeta.Error is OperationCanceledException && qcResetStopMeta.Calls.SequenceEqual(new[] { "erase:userdata", "erase:metadata" }),
        "QC userdata reset: STOP after metadata prevents reset");

    // ================= native MTK DA wire layer (ROADMAP Phase 1.75 "Path B", stage B2) ============
    // BROM speaks big-endian, the DA stage speaks little-endian, and every value asserted below is pinned to
    // bytes a working boot actually put on the wire (RUN 6/7/9, Redmi 9A / MT6765). The boot machine runs on a
    // scripted fake transport, so a wrong frame order, an invented padding byte or a swallowed status word
    // fails here instead of on a phone.

    string LcHex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    // ---- B2.2: wire codecs --------------------------------------------------------------------------
    Check(LcHex(MtkBrom.Be(0x11002000u)) == "11002000", "MTK BROM: register args are big-endian (UART base 0x11002000)");
    Check(LcHex(MtkBrom.Be((ushort)0x0766)) == "0766", "MTK BROM: HW code 0x0766 encodes big-endian");
    Check(MtkBrom.ParseHwCode(Convert.FromHexString("07660000")) == 0x0766, "MTK BROM: HW-code reply splits code from status");
    bool hwStatusRefused = false;
    try { MtkBrom.ParseHwCode(Convert.FromHexString("07660001")); } catch (MtkDaProtocolException) { hwStatusRefused = true; }
    Check(hwStatusRefused, "MTK BROM: a refused HW-code query stops the boot instead of logging a chipset nobody verified");
    bool hwShortRefused = false;
    try { MtkBrom.ParseHwCode(new byte[3]); } catch (MtkDaProtocolException) { hwShortRefused = true; }
    Check(hwShortRefused, "MTK BROM: an undersized HW-code reply is refused, not read past its end");
    Check(MtkBrom.ParseTargetConfig(Convert.FromHexString("000000e70000")) == 0xE7, "MTK BROM: target config reads the BE u32 out of the 6-byte reply");
    bool cfgStatusRefused = false;
    try { MtkBrom.ParseTargetConfig(Convert.FromHexString("000000e70003")); } catch (MtkDaProtocolException) { cfgStatusRefused = true; }
    Check(cfgStatusRefused, "MTK BROM: a non-zero status on the config query aborts the boot");
    bool cfgLenRefused = false;
    try { MtkBrom.ParseTargetConfig(Convert.FromHexString("000000e7")); } catch (MtkDaProtocolException) { cfgLenRefused = true; }
    Check(cfgLenRefused, "MTK BROM: a 4-byte config reply is not accepted as 6");
    Check(MtkBrom.DescribeConfig(0xE7) ==
            "SBC=True SLA=True DAA=True SWJTAG=True EPP_PARAM=False RootCert=False MemReadAuth=True MemWriteAuth=True CmdC8Blocked=True",
        "MTK BROM: config 0xE7 decodes to exactly the nine flag lines the working client printed");
    Check(MtkBrom.DescribeConfig(0) ==
            "SBC=False SLA=False DAA=False SWJTAG=True EPP_PARAM=False RootCert=True MemReadAuth=False MemWriteAuth=False CmdC8Blocked=True",
        "MTK BROM: an all-zero config word keeps the three inverted flags honest (no false 'unlocked' comfort)");

    Check(LcHex(MtkDaFrame.Header(0x14)) == "efeeeefe0100000014000000", "MTK DA: header is magic + type 1 + LE length, byte-identical to the capture");
    Check(LcHex(MtkDaFrame.Word(MtkDaCommand.Read)) == "05000100",
        "MTK DA: a bare command is its u32le id on its own — RUN B6-4 (2026-11-03 16:03) is what split the frame in two: " +
        "the MT6877 DA answered the recipe's header-then-id writes seventeen times and said nothing at all to the same " +
        "0x10009 sent as one merged 16-byte transfer");
    Check(LcHex(MtkDaFrame.Word(0)) == "00000000", "MTK DA: the host ack is a zero u32le, and it is the payload half of its own frame");
    Check(MtkDaFrame.TryParseHeader(Convert.FromHexString("efeeeefe010000000c000000"), out uint declaredLen) && declaredLen == 12,
        "MTK DA: the declared length is read little-endian (0x0c000000 would be 201 MB)");
    Check(!MtkDaFrame.TryParseHeader(Convert.FromHexString("efeeeefe0200000004000000"), out _), "MTK DA: a frame whose type is not 1 is not a DA header");
    Check(!MtkDaFrame.TryParseHeader(Convert.FromHexString("efeeef0e0100000004000000"), out _), "MTK DA: wrong magic is refused");
    Check(!MtkDaFrame.TryParseHeader(new byte[11], out _), "MTK DA: an 11-byte header is refused");
    Check(MtkDaFrame.Header(0).Length == MtkDaFrame.HeaderSize && MtkDaFrame.Word(0).Length == 4,
        "MTK DA: a frame is built as two pieces — a 12-byte header and its payload — and there is no merged-frame codec left to call");

    byte[] readExtent = MtkOpStruct.Create(0, 0x400);
    Check(readExtent.Length == 56 &&
          LcHex(readExtent) == "0100000008000000" + "0000000000000000" + "0004000000000000" + new string('0', 64),
        "MTK DA: extent struct = region 1, field 8, LE offset, LE length, 32 zero reserved bytes");
    Check(MtkOpStruct.TryParse(readExtent, out uint extRegion, out uint extField4, out ulong extOffset, out ulong extLength) &&
          extRegion == 1 && extField4 == 8 && extOffset == 0 && extLength == 0x400, "MTK DA: extent struct round-trips");
    var tamperedExtent = (byte[])readExtent.Clone(); tamperedExtent[30] = 0xFF;
    Check(!MtkOpStruct.TryParse(tamperedExtent, out _, out _, out _, out _), "MTK DA: non-zero reserved bytes make an extent struct unparseable");
    Check(!MtkOpStruct.TryParse(readExtent[..55], out _, out _, out _, out _), "MTK DA: a short extent struct is refused");
    Check(MtkOpStruct.Create(0ul, 3ul * 1024 * 1024 * 1024).Length == 56,
        "MTK DA: a 3 GiB extent is representable in the u64 length field — a super partition really is that big");
    bool zeroExtent = false;
    try { MtkOpStruct.Create(0x5b280000ul, 0ul); } catch (ArgumentOutOfRangeException) { zeroExtent = true; }
    Check(zeroExtent, "MTK DA: a zero-length extent is refused — it was never observed and a no-op op would still open the transfer");
    bool absurdExtent = false;
    try { MtkOpStruct.Create(0ul, 2ul * 1024 * 1024 * 1024 * 1024); } catch (ArgumentOutOfRangeException) { absurdExtent = true; }
    Check(absurdExtent, "MTK DA: an extent past the sane width is refused rather than sent as a wrap-around");
    bool wrappingExtent = false;
    try { MtkOpStruct.Create(ulong.MaxValue - 100, 4096); } catch (ArgumentOutOfRangeException) { wrappingExtent = true; }
    Check(wrappingExtent, "MTK DA: an offset+length that wraps the address space is refused");
    Check(MtkOpStruct.Create(0x1000_0000ul, 0x20ul).Length == 56, "MTK DA: offsets above 4 GiB fit the u64 field (UFS partitions are that big)");

    var limits = MtkLimits.Parse(Convert.FromHexString("0000200000000100"));
    Check(limits.WriteChunkSize == 0x200000 && limits.TransferPacketSize == 0x10000,
        "MTK DA: chunk sizes come from the DA itself (2 MiB write chunk, 64 KiB transfer packet)");
    bool zeroLimitRefused = false;
    try { MtkLimits.Parse(Convert.FromHexString("0000000000000100")); } catch (MtkDaProtocolException) { zeroLimitRefused = true; }
    Check(zeroLimitRefused, "MTK DA: a zero write-chunk limit aborts before anything loops on it");
    bool shortLimitsRefused = false;
    try { MtkLimits.Parse(new byte[7]); } catch (MtkDaProtocolException) { shortLimitsRefused = true; }
    Check(shortLimitsRefused, "MTK DA: an undersized limits reply aborts");
    bool sillyLimitsRefused = false;
    try { MtkLimits.Parse(Convert.FromHexString("0000000800000100")); } catch (MtkDaProtocolException) { sillyLimitsRefused = true; }
    Check(sillyLimitsRefused, "MTK DA: a 128 MiB chunk limit is implausible and is refused");

    Check(MtkChecksum.ChunkByteSum(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }) == 15, "MTK DA: the write-path word is a plain byte sum, not a CRC");
    Check(MtkChecksum.ChunkByteSum(Enumerable.Repeat((byte)0xFF, 100).ToArray()) == 25500, "MTK DA: the byte sum keeps counting past 255 instead of wrapping per byte");
    Check(MtkChecksum.ChunkByteSum(Enumerable.Repeat((byte)0xFF, 66048).ToArray()) == 65024, "MTK DA: the byte sum is truncated to its low 16 bits");
    Check(MtkChecksum.ChunkByteSum(new byte[] { 0, 1, 2, 3, 4, 5 }, 2, 3) == 9, "MTK DA: a chunk sums only the frame being sent");
    Check(MtkDaCommand.Read == 0x00010005 && MtkDaCommand.Write == 0x00010004 && MtkDaCommand.Erase == 0x00010003,
        "MTK DA: read/write/erase ids are the three each capture proved");
    Check(MtkDaCommand.WriteCommit == 0x00080005 && MtkDaCommand.Preamble == 0x00010009 && MtkDaCommand.Limits == 0x00040007,
        "MTK DA: commit, preamble and limits ids pinned to the WRITE capture");
    Check(LcHex(MtkDaCommand.Sync) == "53594e43", "MTK DA: SYNC is ASCII 'SYNC' — the only signal that separates a live DA from a stale one");
    Check(MtkDaCommand.EraseReportStart == 0x40040004 && MtkDaCommand.EraseReportDone == 0x40040005, "MTK DA: erase report words pinned to the ERASE capture");
    Check(MtkBrom.GetHwCode == 0xfd && MtkBrom.JumpDa1 == 0xd5 && MtkBrom.SendDa1 == 0xd7 && MtkBrom.WriteReg == 0xd4,
        "MTK BROM: the four command bytes the boot machine is allowed to replay");

    // ---- B2.3: the boot recipe loader is the fail-closed gate ---------------------------------------
    string bootFix = Path.Combine(dir, "mtk-boot"); Directory.CreateDirectory(bootFix);
    byte[] kamakiri = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();
    byte[] da1 = Enumerable.Range(0, 8).Select(i => (byte)(0x10 + i)).ToArray();
    byte[] da2 = { 0xA0, 0xA1, 0xA2, 0xA3 };
    string Sha256Of(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    string BlobEntry(string file, byte[] data) =>
        "{\"file\":\"" + file + "\",\"bytes\":" + data.Length + ",\"sha256\":\"" + Sha256Of(data) + "\"}";
    string GoodBlobs() => "[" + BlobEntry("kamakiri.bin", kamakiri) + "," + BlobEntry("da1.bin", da1) + "," + BlobEntry("da2.bin", da2) + "]";
    string Recipe(string brom, string da, string? blobs = null) =>
        "{\"chipset\":\"MT6765-TEST\",\"hwCode\":\"0x0766\",\"source\":\"regression fixture\",\"note\":\"synthetic\"," +
        "\"blobs\":" + (blobs ?? GoodBlobs()) + ",\"bromSteps\":" + brom + ",\"daSteps\":" + da + "}";
    // The recorded BROM walk, shrunk to one example of every step kind the machine has to honour:
    // echoed commands, a BE status word, a register spray, an unframed payload upload, and the 0xc0 jump ack.
    string GoodBrom = """
        [{"t":"tx","hex":"fd"},{"t":"rx","k":"echo","bytes":1},{"t":"rx","k":"token","what":"hw-code","bytes":4},
         {"t":"tx","hex":"d8"},{"t":"rx","k":"echo","bytes":1},{"t":"rx","k":"len","bytes":6},
         {"t":"tx","hex":"d4"},{"t":"rx","k":"echo","bytes":1},
         {"t":"tx","hex":"00201000"},{"t":"rx","k":"echo","bytes":4},
         {"t":"tx","hex":"00000000"},{"t":"rx","k":"echo","bytes":4},{"t":"rx","k":"zero","bytes":2},
         {"t":"blob","file":"kamakiri.bin","bytes":8,"chunk":4,"framed":false},
         {"t":"tx","hex":"d7"},{"t":"rx","k":"echo","bytes":1},
         {"t":"tx","hex":"00200000"},{"t":"rx","k":"echo","bytes":4},
         {"t":"tx","hex":"00000008"},{"t":"rx","k":"echo","bytes":4},
         {"t":"tx","hex":"00000100"},{"t":"rx","k":"echo","bytes":4},{"t":"rx","k":"zero","bytes":2},
         {"t":"blob","file":"da1.bin","bytes":8,"chunk":4,"framed":false},{"t":"rx","k":"len","bytes":4},
         {"t":"tx","hex":"d5"},{"t":"rx","k":"echo","bytes":1},
         {"t":"tx","hex":"00200000"},{"t":"rx","k":"echo","bytes":4},{"t":"rx","k":"zero","bytes":2},
         {"t":"rx","k":"token","what":"da-alive","bytes":1}]
        """;
    // The recorded DA bring-up: host SYNC, the two setup commands and their 20-byte arg, the EMI settings,
    // the framed DA2 upload, then the DA's own SYNC — the frame that proves it is alive and not a leftover.
    string GoodDa = """
        [{"t":"tx","hex":"efeeeefe0100000004000000"},{"t":"tx","hex":"53594e43"},
         {"t":"tx","hex":"efeeeefe0100000004000000"},{"t":"tx","hex":"00010100"},
         {"t":"tx","hex":"efeeeefe0100000014000000"},{"t":"tx","hex":"0200000001000000010000000000000000000000"},
         {"t":"rx","k":"hdr","bytes":12},{"t":"rx","k":"zero","bytes":4},
         {"t":"tx","hex":"efeeeefe0100000004000000"},{"t":"tx","hex":"0a000100"},
         {"t":"tx","hex":"efeeeefe0100000004000000"},{"t":"tx","hex":"04000000"},
         {"t":"tx","hex":"efeeeefe0100000004000000"},
         {"t":"blob","file":"da2.bin","bytes":4,"chunk":4,"framed":true},
         {"t":"rx","k":"hdr","bytes":12},{"t":"rx","k":"zero","bytes":4},
         {"t":"tx","hex":"efeeeefe0100000004000000"},{"t":"tx","hex":"09000100"},
         {"t":"rx","k":"hdr","bytes":12},{"t":"rx","k":"token","what":"sync","bytes":4}]
        """;
    string FixturePath(string name, string json, params (string File, byte[] Data)[] blobs)
    {
        string folder = Path.Combine(bootFix, name);
        Directory.CreateDirectory(folder);
        foreach (var blob in blobs) File.WriteAllBytes(Path.Combine(folder, blob.File), blob.Data);
        string path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, json);
        return path;
    }
    string LoadFailure(string name, string json, params (string File, byte[] Data)[] blobs)
    {
        blobs = blobs.Length == 0 ? new[] { ("kamakiri.bin", kamakiri), ("da1.bin", da1), ("da2.bin", da2) } : blobs;
        try { MtkBootProfile.Load(FixturePath(name, json, blobs)); return "(loaded, no refusal)"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    var goodProfilePath = FixturePath("good", Recipe(GoodBrom, GoodDa),
        ("kamakiri.bin", kamakiri), ("da1.bin", da1), ("da2.bin", da2));
    var bootProfile = MtkBootProfile.Load(goodProfilePath);
    Check(bootProfile.Chipset == "MT6765-TEST" && bootProfile.HwCode == 0x0766, "MTK recipe: chipset and HW code loaded as recorded");
    Check(bootProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Tx) == 11 &&
          bootProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Rx) == 18 &&
          bootProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Blob) == 2,
        "MTK recipe: the BROM stage keeps its 11 sends, 18 expected replies and 2 image uploads");
    Check(bootProfile.DaSteps.Count == 20 && bootProfile.DaSteps.Count(s => s.Expect?.Kind == MtkReplyKind.Sync) == 1,
        "MTK recipe: the DA stage ends on exactly one SYNC expectation");
    Check(bootProfile.GetBlob("da1.bin").SequenceEqual(da1) && bootProfile.Blobs.Count == 3,
        "MTK recipe: blobs come back byte-identical to the hash-pinned registry");
    Check(MtkBootProfile.FindProfiles(bootFix).Count == 1 &&
          MtkBootProfile.FindProfiles(Path.Combine(dir, "no-such-folder")).Count == 0,
        "MTK recipe: profile discovery lists json files and survives a missing directory");
    Check(MtkBootProfile.DefaultDirectory.EndsWith(Path.Combine("mtk", "boot-profiles")),
        "MTK recipe: recipes live under mtk/boot-profiles next to the exe");
    Check(MtkBootProfile.ParseU16("0x0766", "hw") == 0x0766 && MtkBootProfile.ParseU16("0766", "hw") == 0x0766,
        "MTK recipe: HW code accepts the recorded 0x-prefixed form");
    bool badU32 = false;
    try { MtkBootProfile.ParseU32("not-hex", "addr"); } catch (InvalidDataException) { badU32 = true; }
    Check(badU32, "MTK recipe: a non-hex address is refused instead of becoming 0");
    bool missingRecipe = false;
    try { MtkBootProfile.Load(Path.Combine(bootFix, "absent.json")); } catch (FileNotFoundException) { missingRecipe = true; }
    Check(missingRecipe, "MTK recipe: a recipe file that is not there is an error, not an empty boot");

    Check(LoadFailure("no-hwquery", Recipe(GoodBrom.Replace("{\"t\":\"tx\",\"hex\":\"fd\"}", "{\"t\":\"tx\",\"hex\":\"fe\"}"), GoodDa))
              .Contains("does not open with the HW-code query"),
        "MTK recipe: a walk that never asks for the HW code cannot prove the device is in BROM — refused");
    Check(LoadFailure("no-sync", Recipe(GoodBrom, GoodDa.Replace("{\"t\":\"rx\",\"k\":\"token\",\"what\":\"sync\",\"bytes\":4}",
                                                                  "{\"t\":\"rx\",\"k\":\"zero\",\"bytes\":4}")))
              .Contains("never waits for the DA's own SYNC"),
        "MTK recipe: without a SYNC expectation a stale DA in RAM would look like a live one — refused");
    Check(LoadFailure("bare-sync", Recipe(GoodBrom, GoodDa.Replace("{\"t\":\"rx\",\"k\":\"token\",\"what\":\"sync\",\"bytes\":4}",
                                                                   "{\"t\":\"rx\",\"k\":\"sync\",\"bytes\":4}")))
              .Contains("(loaded, no refusal)"),
        "MTK recipe: the shorthand the capture script actually writes for SYNC is accepted — the recorded MT6765 recipe loads exactly as measured, not a rewritten copy of it");
    Check(LoadFailure("bare-sync-size", Recipe(GoodBrom, GoodDa.Replace("{\"t\":\"rx\",\"k\":\"token\",\"what\":\"sync\",\"bytes\":4}",
                                                                         "{\"t\":\"rx\",\"k\":\"sync\",\"bytes\":5}")))
              .Contains("unsupported token"),
        "MTK recipe: a bare SYNC with a length the wire never showed is still refused — the shorthand loosens the spelling, never the gate");
    Check(LoadFailure("bad-reply", Recipe(GoodBrom.Replace("{\"t\":\"rx\",\"k\":\"len\",\"bytes\":6}", "{\"t\":\"rx\",\"k\":\"magic\",\"bytes\":6}"), GoodDa))
              .Contains("unknown reply kind"), "MTK recipe: an unclassified reply kind is refused");
    Check(LoadFailure("bad-token", Recipe(GoodBrom.Replace("{\"t\":\"rx\",\"k\":\"token\",\"what\":\"da-alive\",\"bytes\":1}",
                                                            "{\"t\":\"rx\",\"k\":\"token\",\"what\":\"totally-alive\",\"bytes\":1}"), GoodDa))
              .Contains("unsupported token"), "MTK recipe: an unknown token is refused");
    Check(LoadFailure("bad-command", Recipe(GoodBrom.Replace("{\"t\":\"tx\",\"hex\":\"d4\"}", "{\"t\":\"tx\",\"hex\":\"c8\"}"), GoodDa))
              .Contains("never observed on this wire"),
        "MTK recipe: a BROM command byte that was never seen on the wire is refused (0xC8 is a blocked command on this SoC)");
    Check(LoadFailure("unframed", Recipe(GoodBrom, """
        [{"t":"tx","hex":"09000100"},
         {"t":"blob","file":"da2.bin","bytes":4,"chunk":4,"framed":true},
         {"t":"rx","k":"hdr","bytes":12},{"t":"rx","k":"token","what":"sync","bytes":4}]
        """)).Contains("frames a blob upload with no DA header"),
        "MTK recipe: a framed upload with no declaring header in front of it is refused");
    Check(LoadFailure("header-lies", Recipe(GoodBrom, """
        [{"t":"tx","hex":"efeeeefe0100000008000000"},
         {"t":"blob","file":"da2.bin","bytes":4,"chunk":4,"framed":true},
         {"t":"rx","k":"hdr","bytes":12},{"t":"rx","k":"token","what":"sync","bytes":4}]
        """)).Contains("in its header but the recorded image is 4 bytes"),
        "MTK recipe: a header declaring 8 bytes over a 4-byte image would feed the phone the wrong DA — refused");
    Check(LoadFailure("unknown-blob", Recipe(GoodBrom.Replace("da1.bin", "da9.bin"), GoodDa))
              .Contains("which its blob registry does not describe"), "MTK recipe: an upload the registry does not describe is refused");
    Check(LoadFailure("size-lie", Recipe(GoodBrom.Replace("{\"t\":\"blob\",\"file\":\"da1.bin\",\"bytes\":8", "{\"t\":\"blob\",\"file\":\"da1.bin\",\"bytes\":9"), GoodDa))
              .Contains("disagrees with itself about the size"), "MTK recipe: a recipe that disagrees with its own registry is refused");
    Check(LoadFailure("chunk-zero", Recipe(GoodBrom.Replace("{\"t\":\"blob\",\"file\":\"kamakiri.bin\",\"bytes\":8,\"chunk\":4",
                                                             "{\"t\":\"blob\",\"file\":\"kamakiri.bin\",\"bytes\":8,\"chunk\":0"), GoodDa))
              .Contains("implausible upload chunk"), "MTK recipe: a zero upload chunk is refused (it would spin the stream loop)");
    Check(LoadFailure("bad-step", Recipe(GoodBrom.Replace("{\"t\":\"blob\",\"file\":\"kamakiri.bin\"", "{\"t\":\"blorb\",\"file\":\"kamakiri.bin\""), GoodDa))
              .Contains("unknown step kind"), "MTK recipe: an unknown step kind is refused");
    Check(LoadFailure("no-field", Recipe(GoodBrom, GoodDa.Replace("{\"t\":\"rx\",\"k\":\"zero\",\"bytes\":4}", "{\"t\":\"rx\",\"k\":\"zero\"}")))
              .Contains("missing the required numeric field"), "MTK recipe: a reply with no length is refused — the reader would block forever");
    Check(LoadFailure("bad-hex", Recipe(GoodBrom.Replace("{\"t\":\"tx\",\"hex\":\"d8\"}", "{\"t\":\"tx\",\"hex\":\"d8x\"}"), GoodDa))
              .Contains("malformed hex frame"), "MTK recipe: malformed frame bytes are refused");
    Check(LoadFailure("empty-da", Recipe(GoodBrom, "[]")).Contains("has no 'daSteps' steps"),
        "MTK recipe: an empty DA stage is refused instead of 'booted' with nothing loaded");
    Check(LoadFailure("no-hwcode-field", Recipe(GoodBrom, GoodDa).Replace("\"hwCode\":\"0x0766\",", "")).Contains("missing the required field 'hwCode'"),
        "MTK recipe: without a recorded HW code the boot has nothing to compare the device against");
    Check(LoadFailure("short-blob", Recipe(GoodBrom, GoodDa), ("kamakiri.bin", kamakiri), ("da1.bin", da1[..7]), ("da2.bin", da2))
              .Contains("A truncated DA image bricks phones"), "MTK recipe: a truncated DA image is refused before the port is even opened");
    Check(LoadFailure("swapped-blob", Recipe(GoodBrom, GoodDa), ("kamakiri.bin", kamakiri), ("da1.bin", da1.Select(b => (byte)(b ^ 1)).ToArray()), ("da2.bin", da2))
              .Contains("A swapped DA image is not a supported state"), "MTK recipe: a DA image that does not match its recorded sha256 is refused");
    Check(LoadFailure("missing-blob-file", Recipe(GoodBrom, GoodDa), ("kamakiri.bin", kamakiri), ("da2.bin", da2))
              .Contains("is not in"), "MTK recipe: a recipe whose image file is absent is refused");

    // ---- B4.32: endpoint-zero control steps (RUN 38, 2026-11-01) -------------------------------------
    // The passive wire capture named the traffic every bulk-only log missed: before each pre-download `da`
    // the accepted boot sends a CDC SET_LINE_CODING the device accepts and a vendor descriptor read it stalls.
    string AckProbe = "{\"t\":\"ctrl\",\"k\":\"ctrl-ack\",\"hex\":\"2120000000000c0000100e000000080091c20000\",\"bytes\":12}";
    string StallProbe = "{\"t\":\"ctrl\",\"k\":\"ctrl-stall\",\"hex\":\"8006ff0200000900\",\"bytes\":9}";
    string BromWithCtrl = GoodBrom.Replace("{\"t\":\"tx\",\"hex\":\"d4\"},{\"t\":\"rx\",\"k\":\"echo\",\"bytes\":1}",
        "{\"t\":\"tx\",\"hex\":\"d4\"},{\"t\":\"rx\",\"k\":\"echo\",\"bytes\":1}," + AckProbe + "," + StallProbe);
    var ctrlProfile = MtkBootProfile.Load(FixturePath("ctrl-good", Recipe(BromWithCtrl, GoodDa),
        ("kamakiri.bin", kamakiri), ("da1.bin", da1), ("da2.bin", da2)));
    Check(ctrlProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Ctrl) == 2 &&
          ctrlProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Ctrl && s.Expect?.Kind == MtkReplyKind.CtrlAck) == 1 &&
          ctrlProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Ctrl && s.Expect?.Kind == MtkReplyKind.CtrlStall) == 1,
        "MTK recipe: endpoint-zero control steps load as their own kind — an accepted SET_LINE_CODING and a stalled descriptor read");
    Check(ctrlProfile.BromSteps.Single(s => s.Kind == MtkStepKind.Ctrl && s.Expect?.Kind == MtkReplyKind.CtrlAck)
              .Payload.SequenceEqual(Convert.FromHexString("2120000000000c0000100e000000080091c20000")),
        "MTK recipe: the accepted probe keeps its whole data stage in the frame, byte for byte as RUN 38 recorded it");
    Check(LoadFailure("ctrl-ack-in", Recipe(BromWithCtrl.Replace(AckProbe, AckProbe.Replace("\"hex\":\"2120", "\"hex\":\"8120")), GoodDa))
              .Contains("not the measured shape"),
        "MTK recipe: an accepted control transfer with a device-to-host request type is not what the wire showed — refused");
    Check(LoadFailure("ctrl-ack-short", Recipe(BromWithCtrl.Replace(AckProbe,
              AckProbe.Replace("\"hex\":\"2120000000000c0000100e000000080091c20000\"", "\"hex\":\"2120000000000c0000100e000000080091c2\"")), GoodDa))
              .Contains("not the measured shape"),
        "MTK recipe: an accepted control transfer whose data stage is shorter than its wLength is refused");
    Check(LoadFailure("ctrl-stall-out", Recipe(BromWithCtrl.Replace(StallProbe, StallProbe.Replace("\"hex\":\"8006", "\"hex\":\"0006")), GoodDa))
              .Contains("not the measured shape"),
        "MTK recipe: a refused control read with a host-to-device request type is refused");
    Check(LoadFailure("ctrl-stall-data", Recipe(BromWithCtrl.Replace(StallProbe,
              "{\"t\":\"ctrl\",\"k\":\"ctrl-stall\",\"hex\":\"8006ff0200000900ff\",\"bytes\":9}"), GoodDa))
              .Contains("not the measured shape"),
        "MTK recipe: a refused control read that carries data bytes is not the recorded shape — refused");
    Check(LoadFailure("ctrl-len-lie", Recipe(BromWithCtrl.Replace(AckProbe, AckProbe.Replace("\"bytes\":12}", "\"bytes\":10}")), GoodDa))
              .Contains("disagrees with itself about a control transfer's length"),
        "MTK recipe: a control step that disagrees with its own setup packet's wLength is refused");

    // ---- B2.3: the boot machine replayed against a scripted device ----------------------------------
    // Reply order mirrors the recipe: 18 BROM replies then 6 DA replies (three headers and their payloads).
    string[] recordedReplies =
    {
        "fd", "07660000", "d8", "000000e70000", "d4", "00201000", "00000000", "0000",
        "d7", "00200000", "00000008", "00000100", "0000", "25200000", "d5", "00200000", "0000", "c0",
        "efeeeefe0100000004000000", "00000000", "efeeeefe0100000004000000", "00000000",
        "efeeeefe0100000004000000", "53594e43"
    };
    var bootLines = new List<string>();
    var device = new FakeMtkTransport(recordedReplies);
    var booted = await MtkDaSession.BootAsync(device, bootProfile, bootLines.Add, default, 300, 300, 300);
    Check(booted.DaIsReady && booted.DeviceHwCode == 0x0766, "MTK boot: the recorded recipe walks BROM to a ready DA with the HW code proven");
    Check(device.Writes.Count == 29 && device.WrittenBytes == 173,
        "MTK boot: exactly the recipe's 29 writes / 173 bytes reach the wire — no invented padding, nothing skipped");
    Check(device.Writes[0].SequenceEqual(new byte[] { 0xfd }), "MTK boot: the first byte sent is the HW-code query, so the face is proven before anything is written");
    Check(device.Writes[5].SequenceEqual(kamakiri[..4]) && device.Writes[6].SequenceEqual(kamakiri[4..]),
        "MTK boot: an unframed payload upload is streamed in the recorded chunk size, not one giant write");
    Check(device.Writes[11].SequenceEqual(da1[..4]) && device.Writes[12].SequenceEqual(da1[4..]),
        "MTK boot: DA1 is streamed after its size and signature words, as recorded");
    Check(device.Writes.Count(w => w.Length == 1 && w[0] == 0xd7) == 1 && device.Writes.Count(w => w.SequenceEqual(new byte[] { 0xd5 })) == 1,
        "MTK boot: DA1 is uploaded once and jumped to once");
    Check(device.Writes[26].SequenceEqual(da2), "MTK boot: the DA-framed image goes out as its own payload after its header frame");
    Check(device.Writes[20].SequenceEqual(Convert.FromHexString("0200000001000000010000000000000000000000")),
        "MTK boot: the 20-byte DA setup argument is sent exactly as recorded");
    Check(device.Writes[28].SequenceEqual(new byte[] { 0x09, 0x00, 0x01, 0x00 }),
        "MTK boot: the DA stage closes on the recorded preamble frame (0x00010009)");
    Check(bootLines.Any(l => l.Contains("DA1 jumped and answered 0xc0")) && bootLines.Any(l => l.Contains("DA SYNC received")),
        "MTK boot: both success oracles — the 0xc0 ack and the DA's own SYNC — are reported to the operator");
    Check(bootLines.Any(l => l.Contains("MTK DA ready") && l.Contains("0766")), "MTK boot: the ready line names the chipset and the proven HW code");
    booted.Dispose();
    bool notReadyRefused = false;
    try { booted.WriteCommand(MtkDaCommand.Read); } catch (InvalidOperationException) { notReadyRefused = true; }
    Check(notReadyRefused, "MTK session: a closed session refuses commands instead of writing into a dead port");

    // The control probes ride the same recorded bulk walk: they add no bulk replies and no bulk writes, so the
    // reply script and the write census are unchanged — only endpoint zero is spoken to, twice.
    var ctrlDevice = new FakeMtkTransport(recordedReplies);
    var ctrlPipe = new FakeCtrlTransport(ctrlDevice);
    var ctrlBooted = await MtkDaSession.BootAsync(ctrlPipe, ctrlProfile, bootLines.Add, default, 300, 300, 300);
    Check(ctrlBooted.DaIsReady, "MTK boot: control probes replay through the pipe and the boot still reaches a ready DA");
    Check(ctrlPipe.ControlFrames.SequenceEqual(new[] { "2120000000000C00", "8006FF0200000900" }) &&
          ctrlPipe.ControlData[0].SequenceEqual(Convert.FromHexString("00100e000000080091c20000")) &&
          ctrlPipe.ControlData[1].Length == 9,
        "MTK boot: the probes go out as whole setup+data frames in the recorded order — a 12-byte line coding and a 9-byte descriptor ask");
    Check(ctrlDevice.Writes.Count == 29 && ctrlDevice.WrittenBytes == 173,
        "MTK boot: the control probes add no bulk writes — the recorded 29 writes / 173 bytes are unchanged");
    ctrlBooted.Dispose();
    var ctrlNoSupport = new FakeMtkTransport(recordedReplies);
    string? noCtrlError = null;
    try
    {
        var noSupportSession = await MtkDaSession.BootAsync(ctrlNoSupport, ctrlProfile, null, default, 300, 300, 300);
        noSupportSession.Dispose();
    }
    catch (Exception ex) { noCtrlError = ex.Message; }
    Check(noCtrlError != null && noCtrlError.Contains("cannot send one"),
        "MTK boot: a pipe that cannot send endpoint-zero transfers refuses a control-step recipe instead of skipping the probes");
    Check(ctrlNoSupport.Writes.Count == 3,
        "MTK boot: that refusal stops after the three recorded writes and nothing more, so the phone keeps its state");

    // ---- B6.6: auto recipe choice (2026-11-04) -------------------------------------------------------
    // PMK has two phones on the bench and the combo made him switch the boot recipe before every single boot.
    // Row 0 is now Auto, and the session picks the recipe from the BROM's own HW-code reply — the first byte of
    // the walk that is chipset-specific — so nothing has to be replayed and nothing chipset-specific is early.
    Check(MtkNativeBench.ProfileChoice(0, 2).Auto && MtkNativeBench.ProfileChoice(0, 2).ProfileIndex < 0 &&
          MtkNativeBench.ProfileChoice(-1, 2).Auto,
        "MTK auto: combo row 0 — and a combo nobody filled — means Auto and names no recipe file");
    Check(!MtkNativeBench.ProfileChoice(1, 2).Auto && MtkNativeBench.ProfileChoice(1, 2).ProfileIndex == 0 &&
          MtkNativeBench.ProfileChoice(2, 2).ProfileIndex == 1,
        "MTK auto: a named row is the installed recipe one place after it — the off-by-one that used to boot the wrong phone");
    Check(!MtkNativeBench.ProfileChoice(3, 2).Auto && MtkNativeBench.ProfileChoice(3, 2).ProfileIndex < 0,
        "MTK auto: a row past the last installed recipe is refused, not clamped onto the last one");
    Check(MtkNativeBench.AutoBootLine(2).Contains("2 installed") && MtkNativeBench.AutoBootLine(2).Contains("HW code picks one"),
        "MTK auto: the boot says the recipe was not chosen by hand and how many are installed, so a later reader knows why it switched");

    string RecipeOn(string chipset, string hw, string brom, string da) =>
        "{\"chipset\":\"" + chipset + "\",\"hwCode\":\"" + hw + "\",\"source\":\"regression fixture\",\"note\":\"synthetic\"," +
        "\"blobs\":" + GoodBlobs() + ",\"bromSteps\":" + brom + ",\"daSteps\":" + da + "}";
    (string, byte[])[] AutoBlobs = { ("kamakiri.bin", kamakiri), ("da1.bin", da1), ("da2.bin", da2) };
    var recipe6765 = MtkBootProfile.Load(FixturePath("auto-6765", RecipeOn("MT6765-TEST", "0x0766", GoodBrom, GoodDa), AutoBlobs));
    var recipe6877 = MtkBootProfile.Load(FixturePath("auto-6877", RecipeOn("MT6877-TEST", "0x0959", GoodBrom, GoodDa), AutoBlobs));
    // The same first three frames, then a different command: the switch may only continue while the prefix the
    // phone already answered is provably what the target recipe would have put there.
    var recipeLate = MtkBootProfile.Load(FixturePath("auto-6877-late", RecipeOn("MT6877-LATE", "0x0959",
        GoodBrom.Replace("{\"t\":\"tx\",\"hex\":\"d8\"}", "{\"t\":\"tx\",\"hex\":\"ff\"}"), GoodDa), AutoBlobs));

    Check(MtkNativeBench.PickByHwCode(new[] { recipe6765, recipe6877 }, p => p.HwCode, 0x0959) == recipe6877 &&
          MtkNativeBench.PickByHwCode(new[] { recipe6765, recipe6877 }, p => p.HwCode, 0x0766) == recipe6765 &&
          MtkNativeBench.PickByHwCode(new[] { recipe6765, recipe6877 }, p => p.HwCode, 0x0bad) == null,
        "MTK auto: the installed recipe recorded on the device's own HW code is the one picked, and a code nobody claims picks nothing");
    Check(MtkBootProfile.SamePrefix(recipe6765.BromSteps, recipe6877.BromSteps, recipe6765.BromSteps.Count),
        "MTK auto: two recipes with the same recorded prelude share their whole prefix, so a switch between them needs no replay");
    Check(MtkBootProfile.SamePrefix(recipe6765.BromSteps, recipeLate.BromSteps, 3) &&
          !MtkBootProfile.SamePrefix(recipe6765.BromSteps, recipeLate.BromSteps, 4),
        "MTK auto: the divergence point is measured step by step — three frames agree, the fourth does not");

    // A recipe file cannot be loaded at all unless it opens with 0xfd / its echo / the HW-code reply, so the only
    // prefix that ever has to be compared is this three-frame opening. The guard is still asserted against a
    // hand-built list: it is what stands between a switch and a byte the phone already answered differently.
    var openQuery = new MtkStep[]
    {
        new(MtkStepKind.Tx, new byte[] { 0xfd }, null, null, 0),
        new(MtkStepKind.Rx, null, new MtkReply(MtkReplyKind.Echo, 1), null, 0),
        new(MtkStepKind.Rx, null, new MtkReply(MtkReplyKind.HwCode, 4), null, 0)
    };
    var differentReply = new MtkStep[]
    {
        openQuery[0], openQuery[1],
        new(MtkStepKind.Rx, null, new MtkReply(MtkReplyKind.HwCode, 2), null, 0)
    };
    Check(MtkBootProfile.SamePrefix(openQuery, differentReply, 2) && !MtkBootProfile.SamePrefix(openQuery, differentReply, 3),
        "MTK auto: the guard walks the frames in order — the query and its echo agree, the HW-code reply it expects does not, so that recipe is not switchable to");
    Check(MtkBootProfile.SamePrefix(openQuery, openQuery, 3),
        "MTK auto: a recipe measured against itself always matches, which is the case every real switch takes");
    Check(!MtkBootProfile.SamePrefix(openQuery, openQuery[..2], 3),
        "MTK auto: a recipe shorter than the frames already on the wire cannot be switched to either");

    string[] NineFiveNineReplies = recordedReplies.Select((r, i) => i == 1 ? "09590000" : r).ToArray();
    var autoLines = new List<string>();
    var autoDevice = new FakeMtkTransport(NineFiveNineReplies);
    var autoBooted = await MtkDaSession.BootAsync(autoDevice, recipe6765, autoLines.Add, default, 300, 300, 300,
        autoProfiles: new[] { recipe6765, recipe6877 });
    Check(autoBooted.DaIsReady && autoBooted.DeviceHwCode == 0x0959 && autoBooted.RecipeChipset == "MT6877-TEST",
        "MTK auto: the boot starts on the first installed recipe and finishes on the one the BROM named — a plugged-in phone, not a dropdown choice");
    Check(autoDevice.Writes.Count == 29 && autoDevice.WrittenBytes == 173,
        "MTK auto: switching costs nothing on the wire — the same 29 writes / 173 bytes a hand-picked recipe puts out, no replay of the prefix");
    Check(autoLines.Any(l => l.Contains("Auto: recipe MT6877-TEST") && l.Contains("0x0959")) &&
          autoLines.Any(l => l.Contains("MTK DA ready") && l.Contains("MT6877-TEST")),
        "MTK auto: the log names the recipe the HW code landed on and the ready line repeats it, so every run says which recipe actually ran");
    autoBooted.Dispose();

    var noMatchDevice = new FakeMtkTransport(recordedReplies.Select((r, i) => i == 1 ? "0bad0000" : r).ToArray());
    string? noMatchError = null;
    try { (await MtkDaSession.BootAsync(noMatchDevice, recipe6765, null, default, 300, 300, 300,
        autoProfiles: new[] { recipe6765, recipe6877 })).Dispose(); }
    catch (Exception ex) { noMatchError = ex.Message; }
    Check(noMatchError != null && noMatchError.Contains("No installed recipe is recorded on HW 0x0BAD") &&
          noMatchError.Contains("MT6765-TEST on 0x0766") && noMatchError.Contains("MT6877-TEST on 0x0959") &&
          noMatchError.Contains("Nothing chipset-specific was sent"),
        "MTK auto: an HW code no installed recipe claims stops the boot, names the CPUs that are installed, and says the phone was not sprayed");
    Check(noMatchDevice.Writes.Count == 1,
        "MTK auto: that refusal is paid for by the one query byte that was already due — nothing else reaches the phone");

    // A recipe that diverges only after the HW-code reply is switchable, and the switch then has to survive the
    // walk itself: here the recorded device answers the recipe the phone was NOT run with, and the very next echo
    // refuses. That is the safety property — auto never lets a wrong spray continue silently.
    var lateSwitch = new FakeMtkTransport(NineFiveNineReplies);
    string? lateSwitchError = null;
    try
    {
        (await MtkDaSession.BootAsync(lateSwitch, recipe6765, null, default, 300, 300, 300,
            autoProfiles: new[] { recipe6765, recipeLate })).Dispose();
    }
    catch (Exception ex) { lateSwitchError = ex.Message; }
    Check(lateSwitchError != null && lateSwitchError.Contains("did not repeat what was sent") &&
          lateSwitchError.Contains("FF") && lateSwitchError.Contains("D8"),
        "MTK auto: a switch onto a recipe that diverges later is stopped by the first reply that disagrees — the walk keeps its own echo guard");
    Check(lateSwitch.Writes.Count == 2 && lateSwitch.Writes[1].SequenceEqual(new byte[] { 0xff }),
        "MTK auto: that stop costs the one divergent byte and nothing after it, so a wrong recipe never reaches the register spray");

    string? handPickedError = null;
    try { (await MtkDaSession.BootAsync(new FakeMtkTransport(NineFiveNineReplies), recipe6765, null, default, 300, 300, 300)).Dispose(); }
    catch (Exception ex) { handPickedError = ex.Message; }
    Check(handPickedError != null && handPickedError.Contains("Replaying a bypass spray") &&
          !handPickedError.Contains("No installed recipe"),
        "MTK auto: choosing a recipe by hand keeps the old hard refusal — auto is the only mode that is allowed to switch");
    Check(MtkNativeBench.ConciseNativeLine("[*] Native DA: starting on recipe mt6765_boot_profile.json (blobs verified)") ==
          "[*] Recipe: mt6765_boot_profile.json (auto)",
        "MTK auto: the simple screen still says which recipe the run started on, and says it is the auto choice");

    // ---- B6.7: auto pipe choice (2026-11-04) ---------------------------------------------------------
    // The same complaint one control further down: the pipe combo made PMK pick raw USB or the COM port before every
    // boot. Row 0 is now Auto and the choice is read off the bus before anything is sent.
    Check(MtkNativeBench.PipeChoice(0).Auto && !MtkNativeBench.PipeChoice(0).RawUsb &&
          MtkNativeBench.PipeChoice(-1).Auto,
        "MTK pipe: combo row 0 — and an unfilled combo — means Auto and opens no pipe until the bus has been read");
    Check(!MtkNativeBench.PipeChoice(1).Auto && MtkNativeBench.PipeChoice(1).RawUsb &&
          !MtkNativeBench.PipeChoice(2).Auto && !MtkNativeBench.PipeChoice(2).RawUsb,
        "MTK pipe: the two named rows still force their own pipe — raw USB is row 1 and the COM port row 2, so a hand-picked A/B run is still possible");
    Check(MtkNativeBench.PipeChoice(3).Auto && MtkNativeBench.PipeChoice(99).Auto,
        "MTK pipe: a row past the last pipe is not an invented pipe — it falls back to Auto rather than booting on a name nobody chose");
    Check(MtkNativeBench.PickRawPipe(true, true) && MtkNativeBench.PickRawPipe(true, false),
        "MTK pipe: the bulk face wins any tie, because every hardware-proven boot came through it and the COM face of the same node was refused in RUN 10/11");
    Check(!MtkNativeBench.PickRawPipe(false, true),
        "MTK pipe: the COM port is picked when it is the only face the phone is showing — a node UsbDk cannot see but Windows has a driver for");
    Check(MtkNativeBench.PickRawPipe(false, false),
        "MTK pipe: nothing listed at all is a phone that is not plugged in yet, so Auto stays on the proven pipe and lets its own wait give the honest refusal");
    // The accepted BROM PID set, mirrored from the transport's own BromPids (that file is not linked into this
    // project because it drags System.IO.Ports and System.Management in). One PID today: 0E8D:0003.
    int[] BromPids = { 0x0003 };
    Check(MtkNativeBench.BulkBromFaceListed(new[] { 0x0003 }, BromPids) &&
          !MtkNativeBench.BulkBromFaceListed(new[] { 0x2001 }, BromPids) &&
          !MtkNativeBench.BulkBromFaceListed(Array.Empty<int>(), BromPids),
        "MTK pipe: the bulk look counts a BROM face by PID only — the DA face of a phone left over from an earlier session is not a boot start");
    Check(MtkNativeBench.ComBromPortListed(new[] { "0003" }, BromPids) &&
          !MtkNativeBench.ComBromPortListed(new[] { "2001", "20FF" }, BromPids) &&
          !MtkNativeBench.ComBromPortListed(Array.Empty<string>(), BromPids),
        "MTK pipe: the serial look counts a COM port only when it sits on a BROM PID — the same VID under the DA's PID would otherwise pull Auto off the raw pipe");
    Check(!MtkNativeBench.ComBromPortListed(new[] { "0003" }, new[] { 0x0766 }),
        "MTK pipe: that PID test uses the accepted BROM set, not whatever the recipe's HW code happens to say");
    string pipeAuto = MtkNativeBench.AutoPipeLine(true, true, false);
    Check(pipeAuto.Contains("pipe = auto (raw USB)") && pipeAuto.Contains("the USB face was listed") &&
          pipeAuto.Contains("the MediaTek COM port was not listed"),
        "MTK pipe: the auto line names the pipe it chose and what each side of the bus said, so the log alone explains the choice later");
    Check(MtkNativeBench.AutoPipeLine(false, false, true).Contains("pipe = auto (COM port)") &&
          MtkNativeBench.AutoPipeLine(false, false, true).Contains("the only pipe the phone can be reached on"),
        "MTK pipe: choosing the serial pipe is said as the exception it is, with the reason on the same line");
    Check(MtkNativeBench.ConciseNativeLine("[*] Native DA: pipe = auto (raw USB) — the USB face was listed and the MediaTek COM port was listed; raw USB is the pipe every recorded boot came through") ==
          "[*] Pipe: auto (raw USB)",
        "MTK pipe: the simple screen shows the auto pipe choice as one short line instead of the whole sentence");
    Check(MtkNativeBench.ConciseNativeLine("[*] Native DA: pipe = raw USB bulk endpoints — power the phone off, hold BOTH volume keys and plug it in") ==
          "[>] Pipe: raw — power off, hold BOTH volume keys, plug in" &&
          MtkNativeBench.ConciseNativeLine("[*] Native DA: pipe = COM port (serial) — power the phone off, hold BOTH volume keys and plug it in") ==
          "[>] Pipe: COM — power off, hold BOTH volume keys, plug in",
        "MTK pipe: a hand-picked pipe still reaches the screen as the plug-in reminder, and says which pipe it is waiting on");

    // ---- B6.10: the walk timing marks --------------------------------------------------------------
    // Every MT6877 boot so far spends 20-22 s between the payload's ack and the DA1 line, and 14 s of that sits
    // after WireTraceSteps, where the log prints nothing. The recorded client moved the same 454,632-byte DA1
    // through the same pipe in 0.088 s, so the phone is not the wait; these two lines are what splits a slow step
    // sequence from a slow upload without needing a capture.
    string phase = MtkNativeBench.WalkPhaseLine(7708, 7717, 14123, 4711, 2776, 220, "mt6877_da1.bin");
    Check(phase.Contains("7708/7717 step(s) in 14123 ms") && phase.Contains("4711 read(s)") &&
          phase.Contains("2776 write(s)") && phase.Contains("220 control(s)") && phase.Contains("mt6877_da1.bin"),
        "MTK walk timing: the phase mark names the step, the elapsed time and what kind of steps produced it, so the quiet window is attributable");
    Check(MtkNativeBench.WalkPhaseLine(0, 4486, 0, 0, 0, 0, "x.bin").Contains("0/4486 step(s) in 0 ms") &&
          MtkNativeBench.WalkPhaseLine(0, 4486, 0, 0, 0, 0, "x.bin").Contains("0 read(s), 0 write(s), 0 control(s)"),
        "MTK walk timing: a mark printed before any step says zero rather than omitting the counters a reader would otherwise have to infer");
    Check(MtkNativeBench.StreamedLine("mt6877_da1.bin", 454632, 88) ==
          "    streamed mt6877_da1.bin (454632 bytes) in 88 ms",
        "MTK walk timing: the blob line keeps its own wording and adds only the upload duration, so a 14 s pre-blob wait cannot be mistaken for a slow upload");
    Check(MtkNativeBench.StreamedLine("mt6765_da1_patched.bin", 237464, 0).EndsWith(" in 0 ms"),
        "MTK walk timing: a sub-millisecond upload is reported as 0 ms instead of being dropped, so the fast path stays visible as a control");

    // ---- B4.33: the payload's re-open (RUN 39, 2026-11-01) --------------------------------------------
    // RUN 39's first hardware boot with the probes answered 0000 + 64281000 and the kamakiri payload ran
    // (magic a1a2a3a4 at step 165), then died on the first post-payload d1 echo. The RUN 38 capture
    // (f450–f471) shows why: after the magic the working client silently re-opens the serial session —
    // a 7-byte SET_LINE_CODING (no vendor extension), a data-less CDC SET_CONTROL_LINE_STATE (DTR on)
    // and the four-pair greet again — because the payload re-inits the BROM's USB stack.
    string LineCoding7 = "{\"t\":\"ctrl\",\"k\":\"ctrl-ack\",\"hex\":\"212000000000070000100e00000008\",\"bytes\":7}";
    string DtrAck = "{\"t\":\"ctrl\",\"k\":\"ctrl-ack\",\"hex\":\"2122020000000000\",\"bytes\":0}";
    string GreetPairs = "{\"t\":\"tx\",\"hex\":\"a0\"},{\"t\":\"rx\",\"k\":\"complement\",\"bytes\":1}," +
                        "{\"t\":\"tx\",\"hex\":\"0a\"},{\"t\":\"rx\",\"k\":\"complement\",\"bytes\":1}," +
                        "{\"t\":\"tx\",\"hex\":\"50\"},{\"t\":\"rx\",\"k\":\"complement\",\"bytes\":1}," +
                        "{\"t\":\"tx\",\"hex\":\"05\"},{\"t\":\"rx\",\"k\":\"complement\",\"bytes\":1}";
    string BromWithReopen = GoodBrom.Replace("{\"t\":\"tx\",\"hex\":\"d4\"},{\"t\":\"rx\",\"k\":\"echo\",\"bytes\":1}",
        "{\"t\":\"tx\",\"hex\":\"d4\"},{\"t\":\"rx\",\"k\":\"echo\",\"bytes\":1}," + LineCoding7 + "," + DtrAck + "," + GreetPairs);
    var reopenProfile = MtkBootProfile.Load(FixturePath("reopen-good", Recipe(BromWithReopen, GoodDa),
        ("kamakiri.bin", kamakiri), ("da1.bin", da1), ("da2.bin", da2)));
    Check(reopenProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Ctrl) == 2 &&
          reopenProfile.BromSteps.Count(s => s.Kind == MtkStepKind.Rx && s.Expect?.Kind == MtkReplyKind.Complement) == 4,
        "MTK recipe: the payload's re-open loads — a 7-byte line coding, a data-less DTR write and four greet pairs");
    Check(reopenProfile.BromSteps.Single(s => s.Expect?.Kind == MtkReplyKind.CtrlAck && s.Payload.Length == 8)
              .Payload.SequenceEqual(Convert.FromHexString("2122020000000000")),
        "MTK recipe: the data-less SET_CONTROL_LINE_STATE (DTR on) is kept byte for byte as the capture recorded it");
    Check(LoadFailure("ctrl-ack-zerodata", Recipe(BromWithReopen.Replace(DtrAck,
              "{\"t\":\"ctrl\",\"k\":\"ctrl-ack\",\"hex\":\"2122020000000000ff\",\"bytes\":0}"), GoodDa))
              .Contains("not the measured shape"),
        "MTK recipe: an accepted control write carrying data bytes it does not declare is not the recorded shape — refused");
    Check(LoadFailure("complement-two", Recipe(BromWithReopen.Replace("{\"t\":\"rx\",\"k\":\"complement\",\"bytes\":1}",
              "{\"t\":\"rx\",\"k\":\"complement\",\"bytes\":2}"), GoodDa))
              .Contains("exactly one byte"),
        "MTK recipe: a handshake complement that is not one byte is refused — the greet answers a single byte");
    string[] reopenReplies = recordedReplies.Take(5).Concat(new[] { "5f", "f5", "af", "fa" })
                                  .Concat(recordedReplies.Skip(5)).ToArray();
    var reopenPipe = new FakeCtrlTransport(new FakeMtkTransport(reopenReplies));
    var reopenBooted = await MtkDaSession.BootAsync(reopenPipe, reopenProfile, bootLines.Add, default, 300, 300, 300);
    Check(reopenBooted.DaIsReady,
        "MTK boot: the re-open replays — line coding, DTR, the greet again — and the boot still reaches a ready DA");
    Check(reopenPipe.ControlFrames.SequenceEqual(new[] { "2120000000000700", "2122020000000000" }) &&
          reopenPipe.ControlData[0].SequenceEqual(Convert.FromHexString("00100e00000008")) &&
          reopenPipe.ControlData[1].Length == 0,
        "MTK boot: the re-open's control pair goes out whole — a 7-byte line coding with no extension and an empty DTR write");
    Check(reopenPipe.Inner.Writes.Count == 33 && reopenPipe.Inner.WrittenBytes == 177,
        "MTK boot: the re-open adds exactly the four greet bytes to the wire — 33 writes / 177 bytes, nothing else");
    string[] wrongComplement = recordedReplies.Take(5).Concat(new[] { "5e", "f5", "af", "fa" })
                                   .Concat(recordedReplies.Skip(5)).ToArray();
    string? greetError = null;
    try
    {
        var wrongGreet = await MtkDaSession.BootAsync(new FakeCtrlTransport(new FakeMtkTransport(wrongComplement)), reopenProfile, null, default, 300, 300, 300);
        wrongGreet.Dispose();
    }
    catch (Exception ex) { greetError = ex.Message; }
    Check(greetError != null && greetError.Contains("handshake complement"),
        "MTK boot: a greet byte that is not the complement of what was sent is refused, not absorbed");

    // RUN 40 replayed on the re-open recipe itself: the device stalls the re-open's first SET_LINE_CODING
    // (libusb -9, the payload still re-initialising its USB stack), the step is re-asked, and the boot moves on.
    var reopenRetry = new FakeCtrlTransport(new FakeMtkTransport(reopenReplies));
    reopenRetry.ControlScript.Enqueue(-9);
    reopenRetry.ControlScript.Enqueue(-9);
    var reopenRetryBooted = await MtkDaSession.BootAsync(reopenRetry, reopenProfile, bootLines.Add, default, 300, 300, 300);
    Check(reopenRetryBooted.DaIsReady &&
          reopenRetry.ControlFrames.Count == 4 &&
          reopenRetry.ControlFrames[0] == "2120000000000700" &&
          reopenRetry.ControlFrames[2] == "2120000000000700" &&
          reopenRetry.ControlData[2].SequenceEqual(Convert.FromHexString("00100e00000008")) &&
          reopenRetry.ControlFrames[3] == "2122020000000000" &&
          reopenRetry.ControlData[3].Length == 0,
        "MTK boot: the re-open's stalled line coding is asked a third time and accepted, then DTR follows — the same frames, the recorded order");
    Check(reopenRetry.Inner.Writes.Count == 33 && reopenRetry.Inner.WrittenBytes == 177,
        "MTK boot: the retries add no bulk writes — the re-open census stays 33 writes / 177 bytes");
    reopenRetryBooted.Dispose();
    string? reopenExhausted = null;
    try
    {
        var exhaustedPipe = new FakeCtrlTransport(new FakeMtkTransport(reopenReplies));
        for (int i = 0; i < 30; i++) exhaustedPipe.ControlScript.Enqueue(-9);
        var exhausted = await MtkDaSession.BootAsync(exhaustedPipe, reopenProfile, null, default, 300, 300, 300);
        exhausted.Dispose();
    }
    catch (Exception ex) { reopenExhausted = ex.Message; }
    Check(reopenExhausted != null && reopenExhausted.Contains("Refused 20 ask(s) in a row"),
        "MTK boot: a re-open the device refuses through the whole budget ends the boot with the ask count named");

    async Task<string> CtrlFailure(int[] controlResults)
    {
        var fake = new FakeCtrlTransport(new FakeMtkTransport(recordedReplies));
        foreach (int rc in controlResults) fake.ControlScript.Enqueue(rc);
        try
        {
            var probeSession = await MtkDaSession.BootAsync(fake, ctrlProfile, null, default, 300, 300, 300);
            probeSession.Dispose();
            return "(booted, no refusal)";
        }
        catch (Exception ex) { return ex.Message; }
    }
    // RUN 40 (2026-11-01 17:19): the device stalled the re-open's first SET_LINE_CODING (libusb -9) while the
    // payload was still re-initialising its USB stack — a stall on a control endpoint clears at the next SETUP,
    // so an accepted-transfer step is re-asked inside a budget. Two stalls are absorbed; a refusal through the
    // whole budget still stops the boot.
    Check((await CtrlFailure(new[] { -9, -9 })) == "(booted, no refusal)",
        "MTK boot: a SET_LINE_CODING the device stalls twice is re-asked and the boot reaches a ready DA — RUN 40's refusal is a race, not a state");
    Check((await CtrlFailure(Enumerable.Repeat(-9, 30).ToArray())).Contains("was not accepted the way the recorded boot had it") &&
          (await CtrlFailure(Enumerable.Repeat(-9, 30).ToArray())).Contains("Refused 20 ask(s) in a row"),
        "MTK boot: a SET_LINE_CODING the device stalls through the whole retry budget stops the boot — a dead CDC function is not absorbed");
    Check((await CtrlFailure(new[] { 12, 9 })).Contains("is recorded as refused"),
        "MTK boot: a descriptor read that comes back with bytes is not the recorded stall — the boot stops rather than trust a foreign answer");

    // ---- B6.4: the ask that never comes back (RUN B6-1, 2026-11-03) --------------------------------------
    // RUN B6-1 replayed the MT6877 recipe byte for byte as far as the payload's a1a2a3a4 (wire 161) and then the
    // post-payload SET_LINE_CODING printed nothing at all: the phone re-initialised its USB stack and left the bus,
    // the driver never completed the transfer, and a call that trusts libusb's timeout sits inside it for the life of
    // the process — a boot the operator can stop only by closing the window. The same recording shows that transfer
    // completing 1,004 ms after its submit, so an arm of exactly one second loses a step the working boot wins. Three
    // measured behaviours: one ask is bounded, a pipe that ran out is not re-asked nineteen more times, and the face
    // is reclaimed once the way the proof reclaims it before the step is refused.
    async Task<(string Message, FakeMtkTransport Device)> SilentAskRun(MtkBootProfile profile, string[] replies,
        IMtkBromRecovery? recovery)
    {
        var device = new FakeMtkTransport(replies);
        var pipe = new FakeCtrlTransport(device);
        pipe.ControlScript.Enqueue(MtkNativeBench.CtrlNoAnswer);
        try
        {
            var session = await MtkDaSession.BootAsync(pipe, profile, null, default, 300, 300, 300,
                recovery: recovery).ConfigureAwait(false);
            session.Dispose();
            return ("(booted, no refusal)", device);
        }
        catch (Exception ex) { return (ex.Message, device); }
    }
    var (silentRefusal, silentDevice) = await SilentAskRun(reopenProfile, reopenReplies, null);
    Check(silentRefusal.Contains("no answer") && silentRefusal.Contains("Refused 1 ask(s) in a row"),
        "MTK boot: an endpoint-zero ask that never completes is reported as silence rather than a stall, and it is asked once — a pipe that cannot answer endpoint zero cannot answer a re-ask either");
    Check(silentDevice.Writes.Count == 3 && silentDevice.WrittenBytes == 3,
        "MTK boot: nothing after the unanswered control step reaches the phone — the refusal stops at the three prelude writes already on the wire");

    var faceNeverBack = new FakeBromRecovery(() => null);
    string lostRefusal = (await SilentAskRun(reopenProfile, reopenReplies, faceNeverBack)).Message;
    Check(lostRefusal.Contains("never completed") && lostRefusal.Contains("did not come back") &&
          lostRefusal.Contains("hold BOTH volume keys"),
        "MTK boot: an unanswered probe whose face does not return ends the boot naming the reclaim it tried and telling the operator what to do");
    Check(faceNeverBack.Calls == 1 && faceNeverBack.LastWaitMs == 5000,
        "MTK boot: the face after a silent probe is waited on once, for the ~1 s the recorded re-open took plus room");

    // The same ask on a face that comes back: one reclaimed pipe, the step asked there, the boot carried on to a
    // ready DA — the recovery the working client runs between its own retries, applied to a control step.
    var carriedDevice = new FakeMtkTransport(reopenReplies);
    var silentPipe = new FakeCtrlTransport(carriedDevice);
    silentPipe.ControlScript.Enqueue(MtkNativeBench.CtrlNoAnswer);
    var reclaimedPipe = new FakeCtrlTransport(carriedDevice);
    var faceCameBack = new FakeBromRecovery(() => reclaimedPipe);
    var reclaimedSession = await MtkDaSession.BootAsync(silentPipe, reopenProfile, null, default, 300, 300, 300,
        recovery: faceCameBack);
    Check(reclaimedSession.DaIsReady && faceCameBack.Calls == 1 &&
          silentPipe.ControlFrames.Count == 1 && reclaimedPipe.ControlFrames.Count == 2 &&
          carriedDevice.Writes.Count == 33,
        "MTK boot: the face that returns carries the step the dead pipe could not — line coding and DTR asked there, the greet replayed, a ready DA");
    reclaimedSession.Dispose();

    Check((await CtrlFailure(new[] { 12, MtkNativeBench.CtrlNoAnswer })).Contains("Silence is not a stall"),
        "MTK boot: a descriptor read recorded as refused is not satisfied by a device that stopped answering — silence is a fourth state, not a stall");

    // The bound itself, with a fake that behaves like RUN B6-1's phone: the transfer does not return at all.
    var hangingPipe = new FakeCtrlTransport(new FakeMtkTransport(reopenReplies)) { BlockMs = 8000 };
    var hangStart = DateTime.UtcNow;
    string hangRefusal;
    try
    {
        var hungSession = await MtkDaSession.BootAsync(hangingPipe, reopenProfile, null, default, 300, 300, 300);
        hungSession.Dispose();
        hangRefusal = "(booted, no refusal)";
    }
    catch (Exception ex) { hangRefusal = ex.Message; }
    int hangMs = (int)(DateTime.UtcNow - hangStart).TotalMilliseconds;
    // A timer can fire a millisecond early, so the lower bound is "the 3 s timeout was actually spent", not an
    // exact 3000 — the failure this check exists for is a transfer that returns at the 300 ms serial timeout or
    // instantly, and either of those is nowhere near 2.9 s.
    Check(hangRefusal.Contains("no answer") && hangMs >= 2900 && hangMs < 7000,
        "MTK boot: a control transfer the driver never completes is given its timeout plus room and then reported — " + hangMs +
        " ms, while the fake device is still inside the call");

    // ---- B6.5: the probe the recipe itself marks optional (the B6 capture, 2026-11-03 13:28) ------------------
    // The recorded bus answers the payload with a1a2a3a4 at 58.841 s, takes 1,004 ms to accept the SET_LINE_CODING
    // that follows 0.8 ms later, then gets SET_CONTROL_LINE_STATE and all four greet pairs inside 1 ms (f3416-f3440).
    // So the two line-setting writes configure a serial face nothing in the boot reads: the greet is the proof. When
    // our own pipe cannot carry that one ask, the MT6877 recipe steps over it out loud and lets the bulk walk decide —
    // and RUN B6-3 (15:26:57) is why the skip may not touch the pipe: the reclaim it used to try spent 64 open attempts
    // and 12 s on a node the pinned transfer would not hand over, and it had already disposed the pipe the greet needs.
    string LineCoding7Optional = LineCoding7.Replace("\"bytes\":7}", "\"bytes\":7,\"opt\":true}");
    var optionalProfile = MtkBootProfile.Load(FixturePath("reopen-optional",
        Recipe(BromWithReopen.Replace(LineCoding7, LineCoding7Optional), GoodDa),
        ("kamakiri.bin", kamakiri), ("da1.bin", da1), ("da2.bin", da2)));
    Check(optionalProfile.BromSteps.Count(s => s.Optional) == 1 &&
          optionalProfile.BromSteps.Single(s => s.Optional).Expect?.Kind == MtkReplyKind.CtrlAck &&
          reopenProfile.BromSteps.All(s => !s.Optional),
        "MTK recipe: 'opt' decodes onto the control step it is written on, and a recipe that does not carry it keeps every step strict");

    async Task<(List<string> Lines, FakeMtkTransport Device, bool Ready, bool PipeWasDisposed)> OptionalAskRun(
        MtkBootProfile profile, string[] replies, IMtkBromRecovery? recovery)
    {
        var walked = new FakeMtkTransport(replies);
        var quiet = new FakeCtrlTransport(walked);
        quiet.ControlScript.Enqueue(MtkNativeBench.CtrlNoAnswer);
        var said = new List<string>();
        try
        {
            var session = await MtkDaSession.BootAsync(quiet, profile, said.Add, default, 300, 300, 300,
                recovery: recovery).ConfigureAwait(false);
            bool ready = session.DaIsReady, disposed = walked.Disposed;
            session.Dispose();
            return (said, walked, ready, disposed);
        }
        catch (Exception ex) { said.Add("REFUSED: " + ex.Message); return (said, walked, false, walked.Disposed); }
    }
    var carriedOn = await OptionalAskRun(optionalProfile, reopenReplies, null);
    Check(carriedOn.Ready && carriedOn.Lines.Any(l => l.Contains("recorded as optional") &&
          l.Contains("never completed and is left in flight on this pipe")),
        "MTK boot: a line-setting probe the phone stopped answering is named and stepped over — the walk reaches the greet and a ready DA");
    Check(carriedOn.Device.Writes.Count == 33,
        "MTK boot: stepping over the optional probe costs no bulk byte — the same 33 writes the answered boot makes still go out");

    var faceGoneButOptional = new FakeBromRecovery(() => null);
    var keptPipe = await OptionalAskRun(optionalProfile, reopenReplies, faceGoneButOptional);
    Check(keptPipe.Ready && faceGoneButOptional.Calls == 0 && !keptPipe.PipeWasDisposed &&
          keptPipe.Lines.Any(l => l.Contains("no reclaim")),
        "MTK boot: the optional probe is stepped over on the pipe in hand — the face is never reclaimed and the pipe " +
        "the greet needs is never disposed, which is the RUN B6-3 ObjectDisposedException refused at the source");

    string[] greetAnsweredWrong = reopenReplies.Select((reply, at) => at == 5 ? "00" : reply).ToArray();
    var notLenient = await OptionalAskRun(optionalProfile, greetAnsweredWrong, null);
    Check(!notLenient.Ready && notLenient.Lines.Any(l => l.Contains("handshake complement")),
        "MTK boot: stepping over the optional probe does not soften the proof after it — a greet that answers anything but the recorded complement still stops the boot");

    // A strict probe keeps the reclaim it always had; the optional flag is the only thing that changes the path.
    var strictFaceAway = new FakeBromRecovery(() => null);
    var strictRun = await OptionalAskRun(reopenProfile, reopenReplies, strictFaceAway);
    Check(!strictRun.Ready && strictFaceAway.Calls == 1 && strictRun.PipeWasDisposed &&
          strictRun.Lines.Any(l => l.Contains("the face is being waited on")),
        "MTK boot: a strict control step still reclaims the face and still refuses when the reclaim cannot carry it — " +
        "the mt6765 boot measured on hardware keeps exactly the path that boot ran");

    async Task<string> BootFailure(FakeMtkTransport fake, IMtkDaStageHandoff? handoff = null,
        int proofAttempts = 25, int proofRetryDelayMs = 800, IMtkBromRecovery? recovery = null,
        bool bromHandshake = false)
    {
        try
        {
            var session = await MtkDaSession.BootAsync(fake, bootProfile, null, default, 300, 300, 300, handoff,
                proofAttempts, proofRetryDelayMs, recovery, bromHandshake: bromHandshake).ConfigureAwait(false);
            session.Dispose();
            return "(booted, no refusal)";
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    var wrongHw = new FakeMtkTransport(recordedReplies) { ReplyOverride = (1, "07670000") };
    Check((await BootFailure(wrongHw)).Contains("wrong chipset"), "MTK boot: a device reporting MT6768 while the recipe is MT6765 is stopped before the spray");
    Check(wrongHw.Writes.Count == 1, "MTK boot: the refusal happens on the very first reply, so nothing was written to the wrong SoC");
    var badEcho = new FakeMtkTransport(recordedReplies) { ReplyOverride = (0, "fe") };
    Check((await BootFailure(badEcho)).Contains("did not repeat"), "MTK boot: a BROM that does not echo the command byte is not a BROM — stopped");
    var wrongEchoButRightCode = new FakeMtkTransport(recordedReplies) { ReplyOverride = (0, "fe07660000") };
    string wrongEchoText = await BootFailure(wrongEchoButRightCode);
    Check(wrongEchoText.Contains("did not repeat") && wrongEchoText.Contains("0x07660000"),
        "MTK boot: a refused echo also reports the bytes still arriving — a phone in the wrong mode and a port with the wrong settings have to be told apart from one log line");
    var silentTail = new FakeMtkTransport(recordedReplies) { HoldBack = recordedReplies.Length };
    Check((await BootFailure(silentTail, null, 3, 10)).Contains("Still on the wire: nothing"),
        "MTK boot: a device that never answers is reported as silence, not as an unexplained refusal");

    // RUN 10 and RUN 11 refused identically from inside the session (one byte, then silence), and the bytes could
    // not tell "the phone is sitting here refusing us" from "the phone left this face two seconds ago". The bus
    // can, so a refusal now asks Windows what is still there — see MtkNativeBench.DescribeBusAfterRefusal.
    var wrongEchoLeftBus = new FakeMtkTransport(recordedReplies)
    {
        ReplyOverride = (0, "fe"),
        BusNote = "COM3 is gone; what is left on the bus has no COM port at all (0e8d:20FF) — the phone re-enumerated"
    };
    string leftBusText = await BootFailure(wrongEchoLeftBus);
    Check(leftBusText.Contains("Still on the bus: COM3 is gone") && leftBusText.Contains("0e8d:20FF"),
        "MTK boot: a refused echo also reports that the phone left the face — a wrong-mode phone and a wrong-settings port are different repairs");
    var timeoutStillThere = new FakeMtkTransport(recordedReplies)
    {
        HoldBack = recordedReplies.Length,
        BusNote = "COM3 is still enumerated as 0e8d:0003 — the phone did not move"
    };
    string stillThereText = await BootFailure(timeoutStillThere);
    Check(stillThereText.Contains("timed out") && stillThereText.Contains("Still on the bus: COM3 is still enumerated"),
        "MTK boot: a timeout carries the same bus answer, so silence from a phone that stayed is not read as a disappearance");
    var busQueryBroken = new FakeMtkTransport(recordedReplies) { ReplyOverride = (0, "fe"), BusThrows = true };
    string brokenBusText = await BootFailure(busQueryBroken);
    Check(brokenBusText.Contains("did not repeat") && brokenBusText.Contains("Still on the bus: unknown"),
        "MTK boot: a bus re-check that throws never replaces the real refusal — it reports that it could not look");
    var busSilent = new FakeMtkTransport(recordedReplies) { ReplyOverride = (0, "fe"), BusNote = "  " };
    Check((await BootFailure(busSilent)).Contains("Still on the bus: the bus reported nothing"),
        "MTK boot: an empty bus answer is admitted as nothing reported, not phrased as if the phone had been seen");
    var refusedStatus = new FakeMtkTransport(recordedReplies) { ReplyOverride = (7, "0001") };
    Check((await BootFailure(refusedStatus)).Contains("recorded boot was accepted"), "MTK boot: a status word that is not zero stops the boot");
    Check((await BootFailure(new FakeMtkTransport(recordedReplies) { ReplyOverride = (7, "0001") }))
              .Contains("Still on the wire:"),
        "MTK B4.14: a refused status says what was still on the wire — a phone that answered and went quiet is a different shape from one that is still talking");
    // RUN 29 (2026-11-01): every native boot refuses at the same status word, 0x1D1A, and the question the panel
    // asks is whether the phone answered that as a BROM or as a DA it had already jumped into. The status refusal
    // was the only failure that did not look at the bus, so the run that decides that question could not answer it.
    var refusedStatusMoved = new FakeMtkTransport(recordedReplies)
    {
        ReplyOverride = (7, "1d1a"),
        BusNote = "0e8d:0003 is gone; the bus now carries 0e8d:2001 — the phone re-enumerated mid-boot rather than waiting in BROM"
    };
    string refusedMovedText = await BootFailure(refusedStatusMoved);
    Check(refusedMovedText.Contains("non-zero status 0x1D1A") && refusedMovedText.Contains("Still on the bus: 0e8d:0003 is gone"),
        "MTK B4.21: a refused status word names the face the phone is on now — BROM-stayed and DA-jumped are different failures and only the bus can tell them");
    var refusedStatusStayed = new FakeMtkTransport(recordedReplies)
    {
        ReplyOverride = (7, "1d1a"),
        BusNote = "the bus still carries 0e8d:0003 — the phone did not move"
    };
    Check((await BootFailure(refusedStatusStayed)).Contains("Still on the bus: the bus still carries 0e8d:0003"),
        "MTK B4.21: a refused status from a phone that stayed on the BROM face says it stayed, so the refusal is read as a refusal and not as a disappearance");
    var noAck = new FakeMtkTransport(recordedReplies) { ReplyOverride = (17, "0000") };
    Check((await BootFailure(noAck)).Contains("DA1 alive byte"), "MTK boot: without the 0xc0 ack the DA stage is never attempted");
    var silent = new FakeMtkTransport(recordedReplies[..18]);
    Check((await BootFailure(silent)).Contains("timed out"), "MTK boot: a device that stops answering is a timeout, not an infinite wait");
    var fakeSync = new FakeMtkTransport(recordedReplies) { ReplyOverride = (23, "00000000") };
    Check((await BootFailure(fakeSync)).Contains("tell a live DA from a stale one"), "MTK boot: anything arriving where SYNC was recorded is a refusal");
    var lyingHeader = new FakeMtkTransport(recordedReplies) { ReplyOverride = (18, "efeeeefe0100000008000000") };
    Check((await BootFailure(lyingHeader)).Contains("The wire and the recipe disagree"),
        "MTK boot: a DA header declaring a length the recipe does not expect is refused before the payload is read");
    var badMagic = new FakeMtkTransport(recordedReplies) { ReplyOverride = (18, "efeeff0e0100000004000000") };
    Check((await BootFailure(badMagic)).Contains("expected an MTK DA header"), "MTK boot: a non-DA frame where a header was recorded is refused");

    // ==== B4.5 — the raw-USB pipe and the BROM→DA face handoff ========================================
    //
    // The same-window A/B (2026-10-30) settled the transport question with evidence instead of theory: the client
    // that demonstrably boots this phone talks to the device's bulk endpoints and never opens a COM port, while our
    // serial pipe was refused 0xfd → 0xfe on a face that B4.4 proved was still enumerated as 0e8d:0003. And a
    // successful boot does not stay on one node — the phone re-enumerates (measured: started on 0E8D:0003, the DA
    // answered as 0E8D:2001 with a different COM number), so the DA stage of the recipe cannot run on the handle the
    // BROM stage opened. Everything assertable about that lives in these pure helpers; the pipes themselves are the
    // un-linked transports.
    var cdcComm = new MtkNativeBench.MtkBulkPair(0, 0x02, 0x00, 0x84);   // the measured CDC communication interface: interrupt endpoint only
    var cdcData = new MtkNativeBench.MtkBulkPair(1, 0x0A, 0x01, 0x81);   // the measured CDC data interface: bulk OUT 0x01 / bulk IN 0x81
    Check(MtkNativeBench.ChooseBulkPair(new[] { cdcComm, cdcData }) == cdcData,
        "MTK raw USB: a CDC composite is claimed on the data interface — writing to the communication interface is a silent dead end");
    var vendorPair = new MtkNativeBench.MtkBulkPair(0, 0xFF, 0x02, 0x82);
    Check(MtkNativeBench.ChooseBulkPair(new[] { vendorPair }) == vendorPair,
        "MTK raw USB: a face that is not a CDC composite still gets its one real bulk pair, because the rule is the endpoints, not the class");
    Check(MtkNativeBench.ChooseBulkPair(new[] { new MtkNativeBench.MtkBulkPair(0, 0x0A, 0x01, 0x00) }) == null &&
          MtkNativeBench.ChooseBulkPair(Array.Empty<MtkNativeBench.MtkBulkPair>()) == null &&
          MtkNativeBench.ChooseBulkPair(null) == null,
        "MTK raw USB: a half-present endpoint pair is refused rather than guessed — a session on 0x00 never answers");

    // A run is only evidence if a later run can tell what differed, so the two knobs no capture has ever measured
    // (DTR/RTS) and the greeting the pipe heard before we spoke have to survive into the log line.
    string serialLine = MtkNativeBench.DescribeSerialOpen("com3", "0E8D:0003", false, false, new byte[] { 0xfe });
    Check(serialLine == "COM3 0e8d:0003 [serial, DTR off/RTS off, pre-write wire: FE]",
        "MTK pipe log: the serial description names the port, the control lines and what the face volunteered first");
    Check(MtkNativeBench.DescribeSerialOpen(null, "0003", true, true, null).Contains("? 0e8d:0003 [serial, DTR on/RTS on, pre-write wire: nothing") &&
          MtkNativeBench.DescribeSerialOpen("COM3", "0003", false, false, new byte[0]).Contains("DTR off/RTS off"),
        "MTK pipe log: a missing port is admitted as '?', and DTR/RTS are reported on both edges — 115200/DTR-on has never been tried on this phone");
    string usbLine = MtkNativeBench.DescribeUsbOpen(0x0E8D, 0x0003, cdcData, null);
    Check(usbLine == "usb 0E8D:0003 [raw bulk, iface 1 class 0x0A, OUT 0x01 / IN 0x81, no COM driver in the way, pre-write wire: nothing]",
        "MTK pipe log: the raw-USB description carries the claimed interface and both endpoint addresses, so a refusal says which endpoint was actually written");

    Check(MtkNativeBench.DescribeBusAfterRefusalUsb("0003", new[] { "0E8D:0003" }).Contains("the bus still carries 0e8d:0003"),
        "MTK raw USB refusal: a phone that stayed in BROM is reported as staying — the answer it gave is the answer it gives");
    Check(MtkNativeBench.DescribeBusAfterRefusalUsb("0003", Array.Empty<string>()).Contains("no MediaTek device is on the bus at all"),
        "MTK raw USB refusal: a bare bus means the phone left download mode, not that our command was wrong");
    string usbMoved = MtkNativeBench.DescribeBusAfterRefusalUsb("0003", new[] { " 0E8D:2001 ", "2001", "20FF" });
    Check(usbMoved.Contains("0e8d:0003 is gone; the bus now carries 0e8d:2001, 0e8d:20FF") && !usbMoved.Contains("2001, 0e8d:2001"),
        "MTK raw USB refusal: re-enumeration is told apart from disappearance, with padding, the 0E8D prefix and duplicate faces normalised away");
    Check(MtkNativeBench.DescribeBusAfterRefusalUsb("0E8D:0003", new[] { " 0003 " }).Contains("the bus still carries 0e8d:0003"),
        "MTK raw USB refusal: a face written with its VID prefix or stray spaces is still recognised as the face we started on");
    Check(MtkNativeBench.DescribeBusAfterRefusalUsb(null, null).Contains("no MediaTek device is on the bus at all"),
        "MTK raw USB refusal: a refusal with no recorded face still produces a readable bus answer instead of throwing");

    // ---- B4.8: a claim race is retried on a fresh handle, and a competing client is refused by name ----------
    //
    // RUN 15 (2026-10-31 10:03) could not claim the node on the first open and the old fallback selected the
    // configuration to break the tie. Every 0xfd after that came back 0xfe, and the working client never selects a
    // configuration at all — so the tie is now broken by closing and re-opening, which sends the phone nothing.
    string claimNote = MtkNativeBench.UsbClaimRetryNote(1, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(12),
        "libusb -12: Resource busy; device configuration 1, driver WinUsb");
    Check(claimNote != null && claimNote.Contains("claim attempt 1") && claimNote.Contains("nothing has been sent") &&
          !claimNote.Contains("SET_CONFIGURATION") && !claimNote.Contains("re-initialis"),
        "MTK raw USB: a claim race is retried with a note that says nothing reached the phone — the configuration is never selected");
    Check(MtkNativeBench.UsbClaimRetryNote(1, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12), "x") == null &&
          MtkNativeBench.UsbClaimRetryNote(3, TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(12), "x") == null,
        "MTK raw USB: the retry budget is bounded — a spent budget hands the refusal back instead of retrying forever");
    bool attemptZeroRefused = false;
    try { MtkNativeBench.UsbClaimRetryNote(0, TimeSpan.Zero, TimeSpan.FromSeconds(12), null); }
    catch (ArgumentOutOfRangeException) { attemptZeroRefused = true; }
    Check(attemptZeroRefused, "MTK raw USB: attempts are numbered from 1, and a caller that forgets cannot silently look like a retry");

    // A second MediaTek client is the mechanism that makes a node unclaimable: it polls for the face and takes it
    // first. RUN 15's takeover is exactly the two-master state this refuses before any byte is sent.
    Check(MtkNativeBench.CompetingClient(new[] { "explorer", "mtk", "notepad" }) == "mtk.exe" &&
          MtkNativeBench.CompetingClient(new[] { "MTKClient.EXE" }) == "mtkclient.exe" &&
          MtkNativeBench.CompetingClient(new[] { "explorer", "PMKUnlocker" }) == null &&
          MtkNativeBench.CompetingClient(null) == null,
        "MTK raw USB: a running mtkclient/SP Flash Tool-style client is named before a native boot, and an unrelated process list never blocks it");

    // ---- B4.9: the face is opened through libusb-1.0, and the log names the backend that did it ------------
    //
    // RUN 16 (2026-10-31 10:14) got as far as "driver LibUsb" and then 28 failed claims of interface 1 —
    // DeviceIoControl 0x222054 → ERROR_BAD_COMMAND — because LibUsbDotNet had opened the node through its legacy
    // libusb-win32 backend, which cannot claim a CDC data interface on this machine. mtkclient claims the same
    // interface through libusb-1.0, so B4.9 moves the open there; these strings are the only part of that decision
    // the offline suite can hold still, and a run that refuses has to say which backend it refused on.
    string viaLibUsb = MtkNativeBench.UsbOpenVia("0E8D:0003 (MediaTek USB Port_V1633)",
        "the libusb-1.0 session, " + MtkNativeBench.UsbBackendText(0));
    Check(viaLibUsb.StartsWith(" MTK USB — opened 0E8D:0003") && viaLibUsb.Contains("libusb-1.0") &&
          viaLibUsb.Contains("UsbDk backend engaged") &&
          !viaLibUsb.Contains("SET_CONFIGURATION") && !viaLibUsb.Contains("re-initialis"),
        "MTK raw USB: a successful open names the face and the libusb-1.0/UsbDk backend that took it — RUN 16's legacy path can be told apart at a glance");
    Check(MtkNativeBench.UsbOpenVia(null, null).Contains("opened the face through an unnamed backend") &&
          MtkNativeBench.UsbOpenVia("  ", "  ").Trim().Length > 0,
        "MTK raw USB: a missing face or backend still yields a readable line instead of throwing");

    // ---- B4.10: the session is switched to UsbDk before it is enumerated, and the switch is reported --------
    //
    // RUN 17 (2026-10-31 10:35) opened 0E8D:0003 through libusb-1.0 and then failed the claim with libusb -3
    // ACCESS: the bundled 1.0.22 defaults to WinUSB, which cannot take an interface usbser already owns (COM3 was
    // on the same phone). A probe on the same DLL measured libusb_set_option(ctx, USE_USBDK) -> 0 followed by a
    // successful claim of a kbdhid-owned interface. The rc is the whole difference between a phone refusal and a
    // backend that never switched, so both the verdict and the error vocabulary are pinned here.
    string usbDkOn = MtkNativeBench.UsbBackendText(0);
    Check(usbDkOn.Contains("UsbDk backend engaged") && usbDkOn.Contains("libusb_set_option -> 0") &&
          usbDkOn.Contains("interface another driver holds"),
        "MTK raw USB: rc 0 from set_option is reported as the UsbDk route that can claim a driver-held interface");
    string usbDkFailed = MtkNativeBench.UsbBackendText(-5);
    Check(usbDkFailed.Contains("WinUSB backend only") && usbDkFailed.Contains("-5") &&
          usbDkFailed.Contains("not the phone"),
        "MTK raw USB: a failed switch is reported as the WinUSB route — RUN 17's ErrorAccess is named as the backend, not the phone");
    Check(MtkNativeBench.UsbBackendText(int.MinValue).Contains("no backend switch was attempted") &&
          MtkNativeBench.UsbBackendText(int.MinValue).Contains("RUN 17"),
        "MTK raw USB: a session that was never switched says so instead of printing a meaningless rc");
    Check(MtkNativeBench.UsbErrorText(0) == "success" &&
          MtkNativeBench.UsbErrorText(-3).Contains("Access denied") &&
          MtkNativeBench.UsbErrorText(-3).Contains("RUN 17") &&
          MtkNativeBench.UsbErrorText(-7).Contains("timeout") &&
          MtkNativeBench.UsbErrorText(-6).Contains("resource busy"),
        "MTK raw USB: libusb's error numbers are translated — RUN 17's -3 ACCESS and the busy/-7 cases a claim race produces are readable without a header file");
    Check(MtkNativeBench.UsbErrorText(-99).Contains("-99"),
        "MTK raw USB: an unmapped libusb code keeps its number instead of being given an invented meaning");

    // The handoff itself: the recipe splits across two faces, and every way it can fail has to leave the phone
    // exactly where the jump put it, with no DA frame ever sent into the BROM handle.
    var bromFace = new FakeMtkTransport(recordedReplies[..18]) { Description = "FAKE-USB 0E8D:0003" };
    var daFace = new FakeMtkTransport(recordedReplies[18..]) { Description = "FAKE-USB 0E8D:2001" };
    int bromStageWrites = 0;
    var splitLines = new List<string>();
    var splitBooted = await MtkDaSession.BootAsync(bromFace, bootProfile, splitLines.Add, default,
        300, 300, 300, new FakeStageHandoff(() => daFace));
    bromStageWrites = bromFace.Writes.Count;
    Check(splitBooted.DaIsReady && splitBooted.DeviceHwCode == 0x0766,
        "MTK boot: the recipe completes when the DA stage runs on the face the phone came back as, not on the BROM pipe");
    Check(bromFace.Disposed && splitBooted.TransportDescription == "FAKE-USB 0E8D:2001",
        "MTK boot: the BROM handle is released once the new face is live, and the session reports the pipe it is now on");
    Check(bromStageWrites > 0 && daFace.Writes.Count == 29 - bromStageWrites &&
          bromFace.Writes.Concat(daFace.Writes).SelectMany(w => w).Count() == 173,
        "MTK boot: split across two faces the recipe is still exactly 29 writes / 173 bytes — the handoff neither skips nor duplicates a step");
    bool IsDaFrame(byte[] w) => w.Length >= 4 && w[0] == 0xEF && w[1] == 0xEE && w[2] == 0xEE && w[3] == 0xFE;
    Check(bromFace.Writes.All(w => !IsDaFrame(w)) && daFace.Writes.Any(IsDaFrame),
        "MTK boot: no DA frame is ever written into the BROM pipe — a half-applied recipe is what this session exists to prevent");
    Check(splitLines.Any(l => l.Contains("the BROM jumped")) &&
          splitLines.Any(l => l.Contains("DA face is live on FAKE-USB 0E8D:2001")),
        "MTK boot: the operator sees both halves of the handoff — that the phone was waited for, and which face answered");
    splitBooted.Dispose();

    var handoffMissing = new FakeMtkTransport(recordedReplies[..18]);
    string faceNeverCame = await BootFailure(handoffMissing,
        new FakeStageHandoff(() => throw new IOException("the DA face never appeared")));
    Check(faceNeverCame.Contains("The BROM stage completed and the DA face could not be opened") &&
          faceNeverCame.Contains("before any DA frame was sent"),
        "MTK boot: a DA face that never arrives stops the boot with the jump state named, and no half-recipe follows it");
    Check(handoffMissing.Writes.Count == bromStageWrites,
        "MTK boot: the failed handoff leaves the BROM pipe with exactly the BROM-stage writes — the refusal does not resend the recipe");
    Check((await BootFailure(new FakeMtkTransport(recordedReplies[..18]), new FakeStageHandoff(() => null)))
              .Contains("returned no transport"),
        "MTK boot: a handoff that answers with no pipe is refused outright instead of being written into");
    string handoffRefused = await BootFailure(new FakeMtkTransport(recordedReplies[..18]),
        new FakeStageHandoff(() => throw new MtkDaProtocolException("No single MediaTek DA face appeared within 30 s")));
    Check(handoffRefused.StartsWith("MtkDaProtocolException") && handoffRefused.Contains("No single MediaTek DA face"),
        "MTK boot: the handoff's own refusal reaches the operator verbatim — a message that already explains itself is never re-wrapped");

    // RUN 12 (2026-10-30 17:25) put the same byte on the same endpoint pair the working client had used and got
    // 0xfe back again. One byte and then silence is also what a command that landed before the BROM was listening
    // looks like, and the two are indistinguishable from one answer — so the read-only proof is asked again, for
    // as long as a start-up takes. RUN 14 (2026-10-31 09:53) measured that window: three asks inside two seconds
    // all drew 0xfe, and mtkclient — started a second later on the same plug — walked the phone into DA1.
    var proofLate = new FakeMtkTransport(new[] { "fe", "" }.Concat(recordedReplies).ToArray());
    var proofLateLines = new List<string>();
    var proofLateBooted = await MtkDaSession.BootAsync(proofLate, bootProfile, proofLateLines.Add, default,
        300, 300, 300, null, 25, 10);
    Check(proofLateBooted.DaIsReady && proofLateBooted.DeviceHwCode == 0x0766,
        "MTK boot: a face that proves itself on the second ask still boots — a BROM that was not listening yet is not a wrong mode");
    Check(proofLate.Writes.Count == 30 && proofLate.WrittenBytes == 174 &&
          proofLate.Writes[0].SequenceEqual(new byte[] { 0xFD }) && proofLate.Writes[1].SequenceEqual(new byte[] { 0xFD }) &&
          proofLate.Writes[2].SequenceEqual(new byte[] { 0xD8 }),
        "MTK boot: the retry re-sends exactly the one proof byte — no other step is ever repeated and nothing else goes out early");
    Check(proofLateLines.Any(l => l.Contains("attempt 1 of 25 answered 0xFE instead of repeating 0xFD") &&
                                  l.Contains("nothing has been written to the phone")) &&
          proofLateLines.Any(l => l.Contains("repeated 0xFD on attempt 2 of 25")),
        "MTK boot: both halves of the retry are in the log — that the first answer was wrong, and which ask proved the face");
    proofLateBooted.Dispose();

    // The window has to cover a start-up, not two seconds: RUN 14's phone answered wrongly for a whole handful of
    // handshakes before mtkclient got through. So success on the twentieth ask must still boot, and it must be
    // reported as such rather than silently accepted.
    var proofSlow = new FakeMtkTransport(
        Enumerable.Repeat(new[] { "fe", "" }, 19).SelectMany(x => x).Concat(recordedReplies).ToArray());
    var proofSlowLines = new List<string>();
    var proofSlowBooted = await MtkDaSession.BootAsync(proofSlow, bootProfile, proofSlowLines.Add, default,
        300, 300, 300, null, 25, 10);
    Check(proofSlowBooted.DaIsReady && proofSlow.Writes.Count == 19 + 29 &&
          proofSlow.Writes.Take(19).All(w => w.SequenceEqual(new byte[] { 0xFD })),
        "MTK boot: a face that only starts echoing on the twentieth ask inside the start-up window still boots");
    Check(proofSlowLines.Any(l => l.Contains("attempt 19 of 25") ) &&
          proofSlowLines.Any(l => l.Contains("repeated 0xFD on attempt 20 of 25")),
        "MTK boot: the long retry admits every wrong ask and names the one that worked — the operator sees how late the BROM woke up");
    proofSlowBooted.Dispose();

    var proofNever = new FakeMtkTransport(
        Enumerable.Repeat(new[] { "fe", "" }, 25).SelectMany(x => x).ToArray());
    string proofNeverText = await BootFailure(proofNever, null, 25, 10);
    Check(proofNeverText.Contains("did not repeat") && proofNever.Writes.Count == 25,
        "MTK boot: a face that never echoes is refused with the recorded message after the whole window, one byte per ask");

    // B4.11: silence is the same "not answering yet" state as a wrong byte — RUN 14's mtkclient also spent a
    // "Handshake failed" before it got through, and a BROM whose USB handler is not up answers nothing at all.
    var proofSilent = new FakeMtkTransport(recordedReplies) { HoldBack = recordedReplies.Length };
    string proofSilentText = await BootFailure(proofSilent, null, 3, 10);
    Check(proofSilent.Writes.Count == 3 && proofSilentText.Contains("timed out"),
        "MTK boot: silence during the proof is asked again inside the window — a silent BROM is a BROM that has not woken up yet");
    var proofSilentThenAwake = new FakeMtkTransport(new[] { "", "", "" }.Concat(recordedReplies).ToArray());
    var proofSilentAwakeLines = new List<string>();
    var proofSilentAwake = await MtkDaSession.BootAsync(proofSilentThenAwake, bootProfile, proofSilentAwakeLines.Add,
        default, 300, 300, 300, null, 25, 10);
    Check(proofSilentAwake.DaIsReady && proofSilentThenAwake.Writes.Count == 31,
        "MTK boot: a face that is silent for three asks and then echoes still boots, and the recipe is not nudged out of order for it");
    Check(proofSilentAwakeLines.Any(l => l.Contains("answered nothing instead of repeating 0xFD")),
        "MTK boot: a silent ask is logged as silence by the proof, not as a wrong answer");
    proofSilentAwake.Dispose();

    // ==== B4.11 — the proof survives a dead pipe (RUN 18) ==============================================
    //
    // RUN 18 (2026-10-31 10:57) answered 0xFE six times and then the seventh write died: `libusb -9 pipe error,
    // 0/1 byte(s) accepted`, and the boot was refused "with the recipe half-applied" although only the one read-only
    // byte had ever been sent. The working client's own recovery is to close the node and re-detect it between
    // handshake retries, so with a recovery seam every failed ask now gets a freshly claimed pipe.
    var pipeDied = new FakeMtkTransport(recordedReplies) { FailWriteAt = 1 };
    var reclaimed = new FakeMtkTransport(recordedReplies) { Description = "FAKE-USB 0E8D:0003 reclaimed" };
    var pipeDiedLines = new List<string>();
    var recovery = new FakeBromRecovery(() => reclaimed);
    var pipeDiedBooted = await MtkDaSession.BootAsync(pipeDied, bootProfile, pipeDiedLines.Add, default,
        300, 300, 300, null, 25, 10, recovery);
    Check(pipeDiedBooted.DaIsReady && pipeDiedBooted.DeviceHwCode == 0x0766 && recovery.Calls == 1 &&
          pipeDied.Disposed && pipeDiedBooted.TransportDescription == "FAKE-USB 0E8D:0003 reclaimed",
        "MTK boot: a pipe that dies under the first proof byte is reclaimed once and the boot continues on it — RUN 18's failure is a retry, not the end");
    Check(pipeDied.Writes.Count == 0 && reclaimed.Writes.Count == 29 &&
          reclaimed.Writes[0].SequenceEqual(new byte[] { 0xFD }),
        "MTK boot: a byte that never left the host is not counted as sent, and the fresh pipe starts at the one read-only byte — no step is skipped or replayed out of order");
    Check(pipeDiedLines.Any(l => l.Contains("could not even deliver the byte") && l.Contains("libusb -9 pipe error")) &&
          pipeDiedLines.Any(l => l.Contains("the face is back on FAKE-USB 0E8D:0003 reclaimed")) &&
          pipeDiedLines.Any(l => l.Contains("repeated 0xFD on attempt 2 of 25")),
        "MTK boot: the operator sees the pipe die, the reclaim succeed, and which ask finally proved the face");
    pipeDiedBooted.Dispose();

    var wrongFirst = new FakeMtkTransport(new[] { "fe", "" });
    var afterReclaim = new FakeMtkTransport(recordedReplies) { Description = "FAKE-USB 0E8D:0003 second claim" };
    var wrongFirstLines = new List<string>();
    var wrongFirstRecovery = new FakeBromRecovery(() => afterReclaim);
    var wrongFirstBooted = await MtkDaSession.BootAsync(wrongFirst, bootProfile, wrongFirstLines.Add, default,
        300, 300, 300, null, 25, 10, wrongFirstRecovery);
    Check(wrongFirstBooted.DaIsReady && wrongFirstRecovery.Calls == 1 && wrongFirst.Writes.Count == 1 &&
          afterReclaim.Writes.Count == 29,
        "MTK boot: a wrong one-byte answer is retried on a freshly claimed pipe, the way the working client closes and re-detects between its own handshake retries");
    Check(wrongFirstLines.Any(l => l.Contains("answered 0xFE instead of repeating 0xFD") &&
                                   l.Contains("on a freshly claimed pipe")),
        "MTK boot: the retry line says the pipe is reclaimed first, so a later reader can tell this from a plain re-write");
    wrongFirstBooted.Dispose();

    var faceStayedAway = new FakeMtkTransport(recordedReplies) { FailWriteAt = 1 };
    string faceStayedAwayText = await BootFailure(faceStayedAway, null, 25, 10, new FakeBromRecovery(() => null));
    Check(faceStayedAwayText.Contains("libusb -9 pipe error") && faceStayedAwayText.Contains("did not come back") &&
          faceStayedAwayText.Contains("Nothing else was written") && faceStayedAwayText.Contains("hold BOTH volume keys"),
        "MTK boot: a face that does not come back after the pipe died stops the boot with the measured cause, what was sent, and the recovery step — not a generic half-recipe warning");
    Check(faceStayedAway.Writes.Count == 0 && faceStayedAway.Disposed,
        "MTK boot: the refusal closes the dead pipe and leaves the recipe at zero delivered bytes");

    var reclaimThrew = new FakeMtkTransport(recordedReplies) { FailWriteAt = 1 };
    string reclaimThrewText = await BootFailure(reclaimThrew, null, 25, 10,
        new FakeBromRecovery(() => throw new MtkDaProtocolException("0E8D:0003 could not be used over raw USB: the data interface 1 is still held")));
    Check(reclaimThrewText.Contains("did not come back") && reclaimThrewText.Contains("still held"),
        "MTK boot: a face that returns but cannot be claimed is refused with the claim's own reason instead of a silent stop");

    Check(MtkNativeBench.TransportFailureBrief(
              "Writing 1 byte(s) to usb 0E8D:0003 [raw bulk] failed (libusb -9 pipe error, 0/1 byte(s) accepted). The device may have left BROM; the boot was stopped with the recipe half-applied.")
          == "(libusb -9 pipe error, 0/1 byte(s) accepted)",
        "MTK B4.11: the retry line quotes only the measured cause of a pipe failure, not the half-recipe warning that does not apply mid-proof");
    Check(MtkNativeBench.TransportFailureBrief("Something went wrong. Nothing was sent.") == "Something went wrong." &&
          MtkNativeBench.TransportFailureBrief("") == "(no detail was recorded)",
        "MTK B4.11: a message with no 'failed (...)' clause is quoted up to its first sentence rather than truncated or invented");

    // ==== B4.12 — the measured BROM USB handshake (run P, 2026-10-31) ================================
    //
    // Run P finally answered why RUN 10..19 all got 0xfd -> 0xfe: a face on 0E8D:0003 that has not exchanged
    // A0/0A/50/05 (each answered by its bitwise complement) replies to EVERY byte with (b + 1) & 0xFF and executes
    // nothing — 0xFD -> 0xFE, 0x00 -> 0x01, 0x7F -> 0x80. mtkclient's 197 "Handshake failed, retrying..." lines were
    // that same state, not a refused command. The handshake is a hardware-pipe step in front of the recipe's own
    // 0xFD query; these checks pin the bytes, their order, and what a face that refuses them costs.
    Check(MtkBrom.HandshakeTranscript() == "A0->5F 0A->F5 50->AF 05->FA" &&
          MtkBrom.HandshakeSequence.SequenceEqual(new byte[] { 0xA0, 0x0A, 0x50, 0x05 }),
        "MTK B4.12: the handshake is exactly the four pairs run P recorded on the Redmi 9A — A0->5F 0A->F5 50->AF 05->FA");
    Check(MtkBrom.HandshakeReply(0x00) == 0xFF && MtkBrom.HandshakeReply(0xFF) == 0x00 &&
          MtkBrom.HandshakeReply(0xFD) == 0x02,
        "MTK B4.12: the expected answer is the bitwise complement for every byte, wrap cases included");

    var handshaken = new FakeMtkTransport(new[] { "5f", "f5", "af", "fa" }.Concat(recordedReplies).ToArray());
    var handshakeLines = new List<string>();
    var handshakeBooted = await MtkDaSession.BootAsync(handshaken, bootProfile, handshakeLines.Add, default,
        300, 300, 300, bromHandshake: true);
    Check(handshakeBooted.DaIsReady && handshakeBooted.DeviceHwCode == 0x0766 && handshaken.Writes.Count == 33 &&
          handshaken.WrittenBytes == 177,
        "MTK boot: a hardware pipe runs the four handshake bytes and then the unchanged 29-write / 173-byte recipe");
    Check(handshaken.Writes.Take(4).Select(w => w[0]).SequenceEqual(new byte[] { 0xA0, 0x0A, 0x50, 0x05 }) &&
          handshaken.Writes[4].SequenceEqual(new byte[] { 0xFD }),
        "MTK boot: the handshake is four separate one-byte writes in the measured order, and the HW-code query follows only after the last complement arrived");
    Check(handshakeLines.Any(l => l.Contains("BROM handshake complete: A0->5F 0A->F5 50->AF 05->FA")),
        "MTK boot: the completed handshake is in the log with its transcript, so a later run can compare the wire byte for byte");
    handshakeBooted.Dispose();

    // A preloader face shares the VID/PID and even answers the first pair; pair 2 is where it stops being a BROM.
    // Every attempt after that must be refused before a command byte — and the refusal has to name the pair.
    var preloaderFace = new FakeMtkTransport(new[] { "5f", "f6", "5f", "f6", "5f", "f6" });
    var preloaderLines = new List<string>();
    string preloaderText;
    try
    {
        var preloaderSession = await MtkDaSession.BootAsync(preloaderFace, bootProfile, preloaderLines.Add, default,
            300, 300, 300, null, 3, 10, null, bromHandshake: true);
        preloaderSession.Dispose();
        preloaderText = "(booted, no refusal)";
    }
    catch (Exception ex) { preloaderText = ex.GetType().Name + ": " + ex.Message; }
    Check(preloaderText.Contains("pair 2 of 4") && preloaderText.Contains("expected its complement 0xF5") &&
          preloaderText.Contains("got 0xF6"),
        "MTK boot: a face that answers 0xA0 with its complement but not 0x0A is not a BROM — refused at pair 2 with the measured expectation named");
    Check(preloaderFace.Writes.Count == 6 && preloaderFace.Writes.All(w => w.Length == 1 && w[0] != 0xFD),
        "MTK boot: a failed handshake never lets the HW-code query out — each attempt stops at the pair that broke it");
    Check(preloaderLines.Any(l => l.Contains("was refused during the BROM handshake (0xF6 where a complement was due)") &&
                                  l.Contains("no command has run on the phone")),
        "MTK boot: the retry line says the face refused the handshake and that no command has run — not that 0xFD was wrong");

    // The handshake belongs to the pipe, not to each attempt: a face that handshakes and then answers 0xFD with the
    // pre-handshake 0xFE must have the query returned, never the four greeting bytes again (nothing measures what a
    // second handshake does to a BROM that is already listening).
    var greetedOnce = new FakeMtkTransport(new[] { "5f", "f5", "af", "fa", "fe", "" }.Concat(recordedReplies).ToArray());
    var greetedOnceLines = new List<string>();
    var greetedOnceBooted = await MtkDaSession.BootAsync(greetedOnce, bootProfile, greetedOnceLines.Add, default,
        300, 300, 300, null, 25, 10, null, bromHandshake: true);
    Check(greetedOnceBooted.DaIsReady && greetedOnce.Writes.Count == 34 && greetedOnce.WrittenBytes == 178 &&
          greetedOnce.Writes[4].SequenceEqual(new byte[] { 0xFD }) && greetedOnce.Writes[5].SequenceEqual(new byte[] { 0xFD }) &&
          greetedOnce.Writes[6].SequenceEqual(new byte[] { 0xD8 }),
        "MTK boot: after one completed handshake the retry re-sends only the 0xFD query — the greeting is per pipe, never per attempt");
    Check(greetedOnceLines.Any(l => l.Contains("repeated 0xFD on attempt 2 of 25")) &&
          greetedOnceLines.Count(l => l.Contains("BROM handshake complete")) == 1,
        "MTK boot: the handshake is reported once even when the query had to be asked twice");
    greetedOnceBooted.Dispose();

    // A pipe reclaimed mid-handshake is a new face: it has to be greeted again, and the boot still has to finish.
    var handshakeDied = new FakeMtkTransport(new[] { "5f" }) { FailWriteAt = 2 };
    var regreeted = new FakeMtkTransport(new[] { "5f", "f5", "af", "fa" }.Concat(recordedReplies).ToArray())
        { Description = "FAKE-USB 0E8D:0003 re-greeted" };
    var regreetLines = new List<string>();
    var regreetBooted = await MtkDaSession.BootAsync(handshakeDied, bootProfile, regreetLines.Add, default,
        300, 300, 300, null, 25, 10, new FakeBromRecovery(() => regreeted), bromHandshake: true);
    Check(regreetBooted.DaIsReady && regreeted.Writes.Count == 33 &&
          regreeted.Writes.Take(4).Select(w => w[0]).SequenceEqual(new byte[] { 0xA0, 0x0A, 0x50, 0x05 }),
        "MTK boot: a pipe that dies mid-handshake is reclaimed and greeted again from the start — the DA still comes up");
    Check(regreetLines.Any(l => l.Contains("could not deliver the BROM handshake (libusb -9 pipe error")) &&
          regreetLines.Any(l => l.Contains("the face is back on FAKE-USB 0E8D:0003 re-greeted")),
        "MTK boot: the operator sees the handshake die on a pipe, the reclaim, and the boot continuing there");
    regreetBooted.Dispose();

    // RUN 20 (2026-10-31 14:04) measured the one handshake shape that is not a refusal: a BROM that is ALREADY
    // past the greet answers the greeting byte with itself (0xA0 -> 0xA0), because that is the echo a greeted BROM
    // gives a command. The greet lives in the phone, not in our pipe, so a second Boot DA against a phone that was
    // booted once sees exactly that — and refusing it refused a phone that was ready to take the query.
    var alreadyGreeted = new FakeMtkTransport(new[] { "a0" }.Concat(recordedReplies).ToArray());
    var greetedLines = new List<string>();
    var greetedBooted = await MtkDaSession.BootAsync(alreadyGreeted, bootProfile, greetedLines.Add, default,
        300, 300, 300, null, 25, 10, null, bromHandshake: true);
    Check(greetedBooted.DaIsReady && alreadyGreeted.Writes.Count == 30 && alreadyGreeted.WrittenBytes == 174 &&
          alreadyGreeted.Writes[0].SequenceEqual(new byte[] { 0xA0 }) &&
          alreadyGreeted.Writes[1].SequenceEqual(new byte[] { 0xFD }),
        "MTK boot: a face that is already past the greet (0xA0 comes back as 0xA0) is not greeted again — the read-only query follows and the boot still finishes");
    Check(greetedLines.Any(l => l.Contains("already past the greet")) &&
          !greetedLines.Any(l => l.Contains("BROM handshake complete")),
        "MTK boot: the already-greeted face is reported as already greeted — the operator is not told a greeting happened that did not");
    greetedBooted.Dispose();

    // Already greeted is not the same as proven: the HW-code answer is still the decider, so a face that echoes the
    // greeting and then reports another chipset is refused instead of being taken for the phone.
    var greetedWrongHw = new FakeMtkTransport(new[] { "a0" }.Concat(recordedReplies).ToArray())
        { ReplyOverride = (2, "07670000") };
    string greetedWrongText;
    try
    {
        var refused = await MtkDaSession.BootAsync(greetedWrongHw, bootProfile, null, default, 300, 300, 300,
            null, 3, 10, null, bromHandshake: true);
        refused.Dispose();
        greetedWrongText = "(booted, no refusal)";
    }
    catch (Exception ex) { greetedWrongText = ex.GetType().Name + ": " + ex.Message; }
    Check(greetedWrongText.Contains("reports HW code 0x0767"),
        "MTK boot: an already-greeted face still has to prove its HW code — the greeting is not a proof by itself");

    // The small BROM replies are the only way to see that a phone's own state matches the recording, so a hardware
    // pipe prints them. A recorded replay stays quiet: that dump belongs to a real phone, not to the fixture.
    var replyLines = new List<string>();
    var replyBooted = await MtkDaSession.BootAsync(
        new FakeMtkTransport(new[] { "5f", "f5", "af", "fa" }.Concat(recordedReplies).ToArray()),
        bootProfile, replyLines.Add, default, 300, 300, 300, bromHandshake: true);
    Check(replyLines.Any(l => l.Contains("BROM reply 5 (6 byte(s), content not checked): 0x000000E70000")),
        "MTK boot: a hardware run prints the small BROM replies (target config, version words) so the next run can be compared with the recording");
    Check(!bootLines.Any(l => l.Contains("BROM reply")),
        "MTK boot: a recorded replay stays quiet — the reply dump belongs to a real phone, not to the fixture");
    replyBooted.Dispose();

    // ==== B4.14 — the wire trace on a hardware run =====================================================
    //
    // RUN 20 (2026-10-31 14:03) stopped 64 steps into the recorded boot on a status word the recording does not
    // have — 0x1D1A where the capture has 0000 — while the working client sends byte-identical commands to the same
    // phone and is answered 0000. Everything we SEND was known; nothing we RECEIVED was, so the answers are the
    // only thing left to look at. The trace prints them for the first steps of a hardware run, which is what makes
    // "the phone answered something else here" a comparison instead of a guess.
    var wireLines = new List<string>();
    var wireBooted = await MtkDaSession.BootAsync(
        new FakeMtkTransport(new[] { "5f", "f5", "af", "fa" }.Concat(recordedReplies).ToArray()),
        bootProfile, wireLines.Add, default, 300, 300, 300, bromHandshake: true);
    Check(wireLines.Any(l => l.Contains("BROM wire ") && l.Contains(": tx 0x")) &&
          wireLines.Any(l => l.Contains("BROM wire ") && l.Contains(": rx 0x")),
        "MTK B4.14: a hardware run prints the wire it used — the bytes it sent and the bytes it got back");
    Check(wireLines.Any(l => l.Contains("BROM wire ") && l.Contains(": rx 0x000000E70000")),
        "MTK B4.14: the trace carries the small answers verbatim, so the next run can be compared with the recording byte for byte");
    Check(!wireLines.Any(l => l.Contains("BROM wire ") && l.Contains("efeeeefe")),
        "MTK B4.14: the DA stage is not traced — the frame stream that follows does not belong in a log file");
    Check(!bootLines.Any(l => l.Contains("BROM wire")),
        "MTK B4.14: a recorded replay stays quiet — the trace belongs to a real phone, not to the fixture");
    wireBooted.Dispose();

    // ==== B4.16 — the pacing knobs (RUN 22) =========================================================
    //
    // RUN 21 (2026-10-31 14:40) replayed 64 recorded steps byte for byte, was refused at step 65, and did the whole
    // session inside one second — while the working client, which is Python and pays a round trip per command, sends
    // the same bytes over the same face and is answered. The protocol is not what differs, so the pace is the half
    // that has never been tried. These three numbers are read from a file per run, so a timing experiment costs a text
    // file and not a rebuild.
    string pacingRoot = Path.Combine(dir, "pacing-root");
    string pacingSession = Path.Combine(pacingRoot, "MediaTekSessions", "native-da");
    Directory.CreateDirectory(pacingSession);
    var noNote = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(noNote.Startup == 0 && noNote.Settle == 0 && noNote.Step == 0 && noNote.Greet,
        "MTK B4.16: a missing pacing note means no pacing — the recorded timing is the default, not a failure");
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"),
        "# operator note" + Environment.NewLine + "startup=120" + Environment.NewLine + "settle= 80 " +
        Environment.NewLine + "step=5" + Environment.NewLine + "unknown=9" + Environment.NewLine + "broken=x");
    var pacingNote = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(pacingNote.Startup == 120 && pacingNote.Settle == 80 && pacingNote.Step == 5,
        "MTK B4.16: the note is read per run — three numbers, no rebuild, unknown keys ignored");
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "startup=-5" + Environment.NewLine + "step=abc");
    var brokenNote = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(brokenNote.Startup == 0 && brokenNote.Settle == 0 && brokenNote.Step == 0 && brokenNote.Greet,
        "MTK B4.16: a note with a negative or unreadable number means no pacing — a broken note never stops a boot");

    var pacedLines = new List<string>();
    var pacedClock = Stopwatch.StartNew();
    var pacedTransport = new FakeMtkTransport(new[] { "5f", "f5", "af", "fa" }.Concat(recordedReplies).ToArray());
    var pacedBooted = await MtkDaSession.BootAsync(
        pacedTransport,
        bootProfile, pacedLines.Add, default, 300, 300, 300, bromHandshake: true,
        bromStartupMs: 120, bromSettleMs: 80, bromStepMs: 5);
    pacedClock.Stop();
    Check(pacedLines.Any(l => l.Contains("BROM pacing: 120 ms before the greet, 80 ms after it, 5 ms after every BROM step")),
        "MTK B4.16: the pacing a run used is in the log — a slow run and a fast run can be told apart afterwards");
    Check(pacedClock.ElapsedMilliseconds >= 180,
        "MTK B4.16: a hardware run really waits — the startup and settle pauses are on the clock, not only in the log");
    pacedBooted.Dispose();

    // The capture keeps the timing it was recorded with: the knobs are for a phone, not for the recording.
    var replayPacedLines = new List<string>();
    var replayPacedClock = Stopwatch.StartNew();
    var replayPaced = await MtkDaSession.BootAsync(new FakeMtkTransport(recordedReplies), bootProfile,
        replayPacedLines.Add, default, 300, 300, 300,
        bromStartupMs: 5000, bromSettleMs: 5000, bromStepMs: 5000);
    replayPacedClock.Stop();
    Check(replayPacedLines.Any(l => l.Contains("(not applied — this is a recorded replay)")) &&
          replayPacedClock.ElapsedMilliseconds < 1000,
        "MTK B4.16: a recorded replay ignores the pacing — the capture keeps its own timing");
    replayPaced.Dispose();

    // ==== B4.17 — the greeting switch (RUN 23) =====================================================
    //
    // The wire is now diffed against the working client command by command: our first 65 wire entries are its
    // first 65, byte for byte, TX and RX alike — until the phone refuses our read of 0xBD80 with 0x1D1A where it
    // answered 0000 and handed back the data. The one thing our client puts on the wire that the working client does
    // not is the four-pair greet: its own log has no handshake byte at all, its first TX is the read-only query.
    // So the greet is the first thing a trial has to be able to take out — and it has to be takeable without a
    // rebuild, because every attempt on the phone is expensive.
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"),
        "startup=1000" + Environment.NewLine + "settle=500" + Environment.NewLine + "step=50" +
        Environment.NewLine + "greet=off");
    var greetless = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(!greetless.Greet && greetless.Startup == 1000 && greetless.Settle == 500 && greetless.Step == 50,
        "MTK B4.17: the note can take the four-pair greet out while keeping the timing — one file, no rebuild");

    var greetlessLines = new List<string>();
    var greetlessTransport = new FakeMtkTransport(recordedReplies);
    var greetlessBooted = await MtkDaSession.BootAsync(greetlessTransport, bootProfile,
        greetlessLines.Add, default, 300, 300, 300, bromHandshake: true, bromGreet: false);
    Check(greetlessBooted.DaIsReady && greetlessBooted.DeviceHwCode == 0x0766 &&
          greetlessTransport.Writes.Count == 29 && greetlessTransport.Writes[0].SequenceEqual(new byte[] { 0xFD }),
        "MTK B4.17: with the greet off the recipe's own query is the first byte on the wire — the working client's shape");
    Check(greetlessTransport.Writes.Count == 29 && pacedTransport.Writes.Count == 33 &&
          pacedTransport.Writes.Take(4).Select(w => w[0]).SequenceEqual(new byte[] { 0xA0, 0x0A, 0x50, 0x05 }),
        "MTK B4.17: the greet is exactly four one-byte writes — off means four fewer bytes on the wire");
    Check(greetlessLines.Any(l => l.Contains("BROM greet: off — the recipe's first byte is the first byte on the wire")) &&
          pacedLines.Any(l => l.Contains("BROM greet: on — the four pairs go out before the recipe's first byte")),
        "MTK B4.17: every run says in its log whether it greeted — a later run can tell the two shapes apart");
    greetlessBooted.Dispose();

    // ==== B4.20 — the two USB-level knobs the working client's API trace shows ============================
    //
    // The wire diff left exactly one divergence: our first 65 entries are the working client's, byte for byte, and
    // step 65 (`da 0 0000bd80 4`) is answered 0x1D1A by the phone where mtk.exe on the same phone, minutes apart,
    // gets 0000 + 64281000 (control 2026-11-01 08:02 and 08:28, both exit 0). So the answer is below the command
    // log. Running mtk.exe with LIBUSB_DEBUG=4 (that 08:28 control boot, stderr 25 MB, 9366 transfers) prints its
    // whole open: libusb_open → libusb_get_configuration ("active config 0") → libusb_set_configuration
    // ("configuration 1") → libusb_claim_interface 0 → libusb_claim_interface 1. Zero libusb_reset_device and zero
    // set_interface_alt_setting, so B4.8's removal of the reset still stands — but B4.8's "the working client never
    // selects a configuration" was read off a log that could not show it, and it is wrong. These knobs are how one
    // hardware run gets to test that without a rebuild, and the checks below are the fail-closed rules that keep a
    // knob from inventing a number or claiming an interface nobody measured.
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "setconfig=on" + Environment.NewLine + "claimall=on");
    var usbKnobs = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(usbKnobs.SetConfig && usbKnobs.ClaimAll && usbKnobs.Greet && usbKnobs.Startup == 0,
        "MTK B4.20: the note can put a USB-level action into a run while leaving the timings and the greet alone — one file, no rebuild");
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "greet=off" + Environment.NewLine + "setconfig=1");
    var unspoken = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(!unspoken.SetConfig && !unspoken.ClaimAll && !unspoken.Greet,
        "MTK B4.20: only an explicit on puts a control request on the wire — a value that is not 'on' or 'off' never does");
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "startup=0" + Environment.NewLine + "settle=0" +
        Environment.NewLine + "step=0" + Environment.NewLine + "greet=on");
    var plainNote = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(MtkNativeBench.DescribeKnobs(plainNote) == null && MtkNativeBench.DescribeKnobs(noNote) == null,
        "MTK B4.20: a note that changes nothing prints nothing — RUN 27/28's shape stays the recognisable default");
    Check(MtkNativeBench.DescribeKnobs(usbKnobs).Contains("SET_CONFIGURATION") &&
          MtkNativeBench.DescribeKnobs(usbKnobs).Contains("every interface"),
        "MTK B4.20: a run that changes the device's USB state says which change in its own log line");

    Check(MtkNativeBench.ConfigValueToSelect(false, 1, 1) == 0,
        "MTK B4.20: with setconfig off no configuration request goes on the wire at all — the plain run is untouched");
    Check(MtkNativeBench.ConfigValueToSelect(true, 1, 0) == 1 && MtkNativeBench.ConfigValueToSelect(true, 2, 1) == 2,
        "MTK B4.20: a face already in a configuration gets that same value back — the request is the working client's, the number is the device's");
    Check(MtkNativeBench.ConfigValueToSelect(true, 0, 1) == 1,
        "MTK B4.20: a face reporting 'active config 0' — what the control boot measured — takes the value off its own descriptor, which is where its 'configuration 1' comes from");
    Check(MtkNativeBench.ConfigValueToSelect(true, 0, 0) == 0,
        "MTK B4.20: a configuration number that would have to be guessed is refused, not invented — 0 means the transport must not send anything");

    Check(MtkNativeBench.InterfacesToClaim(1, new[] { 0, 1 }, false).SequenceEqual(new[] { 1 }),
        "MTK B4.20: with claimall off exactly the data interface is claimed, so the plain open still matches RUN 27/28's");
    Check(MtkNativeBench.InterfacesToClaim(1, new[] { 1, 0 }, true).SequenceEqual(new[] { 0, 1 }),
        "MTK B4.20: with claimall on the claims go out in ascending order — interface 0 then interface 1, the order the API trace shows");
    Check(MtkNativeBench.InterfacesToClaim(0, new[] { 0 }, true).SequenceEqual(new[] { 0 }),
        "MTK B4.20: a one-interface face is not claimed twice — the set is the descriptor's, deduplicated");
    Check(MtkNativeBench.InterfacesToClaim(3, Array.Empty<int>(), true).SequenceEqual(new[] { 3 }),
        "MTK B4.20: a face whose descriptor shows nothing else still claims the interface carrying the bulk pair — the knob never loses the boot");

    // ==== B4.24 — the third knob, from running OUR client under the same API trace =======================
    //
    // RUN 32 (2026-11-01 10:19:50, PMKUnlocker.exe with LIBUSB_DEBUG=4, six boots, step 65 refused again) closed the
    // open as the divergence — get_configuration → set_configuration(1) → claim 0 → claim 1 is now ours too — and
    // showed the reads are not. Our transport asks for bytes on a 50 ms timer, so each empty wait ends in a host-side
    // abort: 45 "errcode 995 → detected operation aborted → actual_length=0" completions in one boot, and a median
    // 26 ms between submissions. The working client arms one bulk transfer at 1000 ms, waits 1.9 ms apart, and aborts
    // twice in 9366 transfers. Same bytes, different USB. arm= is how one run gets to be theirs.
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "greet=on" + Environment.NewLine + "setconfig=on" +
        Environment.NewLine + "claimall=on" + Environment.NewLine + "arm=1000");
    var armed = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(armed.Arm == 1000 && armed.SetConfig && armed.ClaimAll && armed.Greet && armed.Step == 0,
        "MTK B4.24: arm= sets the read wait and nothing else — the USB-level knobs RUN 30 proved stay exactly as they were");
    Check(MtkNativeBench.DescribeKnobs(armed).Contains("armed 1000 ms"),
        "MTK B4.24: a run that changes how it waits on the bus names that wait in its own log line, so tomorrow's diff knows which shape produced today's bytes");
    Check(noNote.Arm == 0 && plainNote.Arm == 0 && MtkNativeBench.DescribeKnobs(noNote) == null,
        "MTK B4.24: no note, or a note that never mentions arm, means the 50 ms poll — RUN 32's shape stays the default");
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "arm=off");
    Check(MtkNativeBench.ReadKnobs(pacingRoot).Arm == 0,
        "MTK B4.24: a value that is not a number cannot arm anything — a typo in the note never changes the transport");
    Check(MtkNativeBench.ReadArmMs(0) == 0 && MtkNativeBench.ReadArmMs(-5) == 0,
        "MTK B4.24: 0 and a negative number both mean 'poll', which is the run that has been recorded every time so far");
    Check(MtkNativeBench.ReadArmMs(1000) == 1000,
        "MTK B4.24: the working client's arm value passes through untouched — the trial is its number, not ours");
    Check(MtkNativeBench.ReadArmMs(900_000) == MtkNativeBench.MaxReadArmMs,
        "MTK B4.24: an arm long enough to outlast a step's own timeout is refused — a wait belongs under the recipe, not above it");
    // MtkUsbOpenOptions itself is not asserted here: it lives in MtkUsbTransport.cs, which this console test does not
    // link (MonoLibUsb needs the Windows-targeted app). What it carries is the number above, and the hardware run
    // names the shape it opened with — which is exactly how RUN 32's trace was read.

    // ==== B4.25 — the fourth knob, and the first one that is not about the wire at all ===================
    //
    // RUN 33 (2026-11-01 10:40:36, raw USB, cold complete greet, arm=1000) made our reads the working client's reads —
    // 84 transfers, 5 of them empty, 79 carrying real bytes, one open, one handle — and step 65 answered 0x1D1A
    // anyway. The same trace then said something plainer: every completion is logged at the timestamp of its own
    // submit ("poll() returned 1", revents already set), so the phone is never waited for, and the 26-45 ms between
    // two wire events sits entirely between libusb_free_transfer and the next libusb_alloc_transfer. That is our own
    // code: one synchronous rtbLog.Invoke plus a ScrollToCaret per line, on the thread driving the phone, while the
    // working client's prelude runs 19x faster. uiwire=off stops paying it for the per-byte chatter.
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "greet=on" + Environment.NewLine + "setconfig=on" +
        Environment.NewLine + "claimall=on" + Environment.NewLine + "arm=1000" + Environment.NewLine + "uiwire=off");
    var batched = MtkNativeBench.ReadKnobs(pacingRoot);
    Check(!batched.UiWire && batched.Arm == 1000 && batched.SetConfig && batched.ClaimAll && batched.Greet,
        "MTK B4.25: uiwire=off takes the per-line UI round-trip out and disturbs no knob RUN 33 was run with — one variable, not six");
    Check(MtkNativeBench.DescribeKnobs(batched).Contains("batched"),
        "MTK B4.25: a run that logs differently says so, so a pace measured tomorrow can be attributed to the shape that produced it");
    Check(armed.UiWire && noNote.UiWire && plainNote.UiWire,
        "MTK B4.25: every note that never mentions uiwire keeps RUN 33's behaviour — one UI line per wire line is the default");
    File.WriteAllText(Path.Combine(pacingSession, "pacing.txt"), "uiwire=maybe");
    Check(MtkNativeBench.ReadKnobs(pacingRoot).UiWire,
        "MTK B4.25: only an explicit on/off counts — a typo in the note cannot change how a live boot reports itself");

    Check(MtkNativeBench.IsWireChatter(" [i] BROM wire 41: tx 0x00000000"),
        "MTK B4.25: the per-byte tx line is the chatter that may wait for the screen — it is 60 of the 65 steps");
    Check(MtkNativeBench.IsWireChatter(" [i] BROM reply 34 (4 byte(s), content not checked): 0x00000020") &&
        MtkNativeBench.IsWireChatter(" [i] DA packet 3: 65536 byte(s)"),
        "MTK B4.25: replies and DA frames are the same kind of line, and the DA stage produces thousands of them per partition");
    Check(!MtkNativeBench.IsWireChatter("[+] BROM face proven: HW 0x0766 (MT6765).") &&
        !MtkNativeBench.IsWireChatter("[FAIL] Native DA boot: Step 65: the device answered with a non-zero status 0x1D1A.") &&
        !MtkNativeBench.IsWireChatter("[*] Native DA knobs — startup 0 ms"),
        "MTK B4.25: a verdict, a refusal or an instruction is never batched — the lines that decide a run reach the screen on their own");
    Check(!MtkNativeBench.IsWireChatter(" [i] BROM pacing: 0 ms before the greet") && !MtkNativeBench.IsWireChatter(""),
        "MTK B4.25: an [i] line that is not about a byte on the wire stays synchronous, and an empty line is never chatter");

    // ==== B3 — the DA command layer on top of the booted session ======================================
    //
    // Same scripted device: walk the recorded recipe, then work the DA exactly the way RUN 6 (read), RUN 7 and
    // RUN 9 (write + erase) and RUN 8 (read-back verify) did. The transfer sizes here are deliberately NOT the
    // captured 2 MiB / 64 KiB — this fake DA advertises 2048 / 1024, so a chunk or packet size taken from a
    // constant would pass a capture-sized test and fail right here.
    const string ZeroWord = "00000000";     // the host ack, and the word before every data frame
    const string PreFrame = "09000100";     // 0x00010009 preamble
    const string StorageCmd = "01000400";   // 0x00040001 storage descriptor
    const string LimitsCmd = "07000400";    // 0x00040007 transfer limits
    const string ReadCmd = "05000100";      // 0x00010005
    const string WriteCmd = "04000100";     // 0x00010004
    const string EraseCmd = "03000100";     // 0x00010003
    const string UploadCmd = "08000100";    // 0x00010008 — the upload the boot's ext image and the reset stub share
    const string CommitCmd = "05000800";    // 0x00080005
    string DaFrame(string payloadHex)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)(payloadHex.Length / 2));
        return "efeeeefe01000000" + Convert.ToHexString(len).ToLowerInvariant() + payloadHex;
    }
    string Filler(byte value, int bytes) => string.Concat(Enumerable.Repeat(value.ToString("x2"), bytes));
    string TruncFrame(string payload) => payload.Length > 32 ? payload[..32] + "…(" + payload.Length / 2 + " B)" : payload;
    string ExtentStruct(ulong offset, ulong length)
    {
        var b = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);                        // eMMC user area
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 8);              // the constant every observed struct carries
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), length);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
    string DescriptorPayload(ulong userBytes, uint blockBytes = 512)
    {
        var d = new byte[104];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 1);                        // "EMMC"
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), blockBytes);     // 0x200 on the capture
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(8), 0x400000);       // boot1
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(16), 0x400000);      // boot2
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(24), 0x400000);      // RPMB
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(64), userBytes);     // user area (0xE8F800000 on the capture)
        // +72 is the eMMC CID. Left zero on purpose: chip identity does not belong in a test fixture.
        return Convert.ToHexString(d).ToLowerInvariant();
    }
    string Status = DaFrame(ZeroWord);
    string LimitsReply = DaFrame("00080000" + "00040000");           // chunk 2048 B, transfer packet 1024 B
    string StorageReply = DaFrame(DescriptorPayload(0xE8F800000ul));
    ulong LogoAt = 0x5b280000;                                       // the measured byte offset of logo
    const ulong LogoLen = 0x1000;                                    // 8 blocks: small enough to check frame by frame
    string LogoStruct = ExtentStruct(LogoAt, LogoLen);

    // RUN B6 (2026-11-03) measured the same queries on the MT6877 behind a UFS disk: 0x40001 answers 104 zero
    // bytes, 0x40004 answers the geometry, and every READ struct carries 0x30 + 3 instead of 1 + 8.
    const string UfsGeomCmd = "04000400";      // 0x00040004 — the geometry reply a UFS unit answers with
    string UfsGeometryPayload(ulong userBytes = 0x3B96000000ul, uint blockBytes = 4096)
    {
        var g = new byte[208];
        BinaryPrimitives.WriteUInt32LittleEndian(g, 0x30);                      // the word its READ structs repeat
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4), blockBytes);      // 0x1000 on the capture
        BinaryPrimitives.WriteUInt64LittleEndian(g.AsSpan(8), 0x400000);        // boot LUN 1
        BinaryPrimitives.WriteUInt64LittleEndian(g.AsSpan(16), 0x400000);       // boot LUN 2
        BinaryPrimitives.WriteUInt64LittleEndian(g.AsSpan(24), userBytes);      // 0x3B96000000 on the capture
        // +32 onward is the disk's own identification (product name, serial, manufacturer id). Deliberately
        // absent: chip identity does not belong in a fixture, and the checks below prove nothing reads it.
        return Convert.ToHexString(g).ToLowerInvariant();
    }
    string UfsExtentStruct(ulong offset, ulong length)
    {
        var b = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), length);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
    string ZeroedDescriptor = DaFrame(new string('0', 208));
    IEnumerable<string> StorageQuery() => new[] { Status, Status, StorageReply, Status };
    IEnumerable<string> LimitsQuery() => new[] { Status, Status, LimitsReply, Status };
    IEnumerable<string> ReadGroup(params string[] dataPayloads)
    {
        // READ accept, extent accepted, the second status both captures put before the first frame, then
        // data + status per frame with nothing trailing.
        var replies = new List<string> { Status, Status, Status };
        foreach (string payload in dataPayloads) { replies.Add(DaFrame(payload)); replies.Add(Status); }
        return replies;
    }
    async Task<(MtkDaSession Session, FakeMtkTransport Device)> BootedDa(List<string>? log, IEnumerable<string> deviceReplies)
    {
        var fake = new FakeMtkTransport(recordedReplies);
        fake.Append(deviceReplies);
        Action<string>? sink = log == null ? null : new Action<string>(log.Add);
        var bootedSession = await MtkDaSession.BootAsync(fake, bootProfile, sink, default, 300, 300, 300).ConfigureAwait(false);
        return (bootedSession, fake);
    }
    async Task<string> DaFailure(MtkDaSession daSession, Func<Task> operation)
    {
        try { await operation(); return "(succeeded, no refusal)"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    List<string> SentFrames(FakeMtkTransport d, int since)
    {
        var all = new MemoryStream();
        for (int i = since; i < d.Writes.Count; i++) all.Write(d.Writes[i], 0, d.Writes[i].Length);
        byte[] wire = all.ToArray();
        var frames = new List<string>();
        int p = 0;
        while (p < wire.Length)
        {
            if (!MtkDaFrame.TryParseHeader(wire.AsSpan(p, Math.Min(MtkDaFrame.HeaderSize, wire.Length - p)), out uint declared) ||
                p + MtkDaFrame.HeaderSize + (int)declared > wire.Length)
                throw new Exception("MTK B3: the bytes this session sent are not a clean run of DA frames (offset " + p + " of " + wire.Length + ").");
            frames.Add(Convert.ToHexString(wire, p + MtkDaFrame.HeaderSize, (int)declared).ToLowerInvariant());
            p += MtkDaFrame.HeaderSize + (int)declared;
        }
        return frames;
    }
    void CheckFrames(List<string> sent, string label, params string[] expected)
    {
        string? detail;
        if (sent.Count != expected.Length) detail = "sent " + sent.Count + " frame(s): " + string.Join(" | ", sent.Select(TruncFrame));
        else
        {
            detail = null;
            for (int i = 0; i < expected.Length; i++)
                if (sent[i] != expected[i]) { detail = "frame " + i + " is " + TruncFrame(sent[i]) + ", expected " + TruncFrame(expected[i]); break; }
        }
        Check(detail == null, label + (detail == null ? " — byte-exact against the capture order" : " — " + detail));
    }

    // The test's own struct math has to agree with the product's and with the capture, or every golden below
    // is just a restatement of the code under test.
    Check(LogoStruct == "0100000008000000" + "0000285b00000000" + "0010000000000000" + new string('0', 64),
        "MTK B3: the logo extent is region 1 + field 8 + LE u64 offset 0x5b280000 + LE u64 length, 32 reserved zero bytes");

    // The descriptor carries the one number that turns an LBA into a byte offset, so its refusals are checked
    // at the codec as well: a truncated or implausible reply must never become a device geometry.
    byte[] goodDescriptor = Convert.FromHexString(DescriptorPayload(0xE8F800000ul));
    var decoded = MtkStorageInfo.Parse(goodDescriptor);
    Check(decoded.StorageType == 1 && decoded.Boot1Size == 0x400000 && decoded.RpmbSize == 0x400000 &&
          decoded.GeneralPurpose1 == 0 && decoded.UserAreaSize == 62537072640ul && decoded.Describe.Contains("58 GiB"),
        "MTK B3: the descriptor's areas decode in the only slot order that reproduces the working client's print block");
    bool shortDescriptor = false;
    try { MtkStorageInfo.Parse(goodDescriptor[..103]); } catch (MtkDaProtocolException) { shortDescriptor = true; }
    Check(shortDescriptor, "MTK B3: a 103-byte descriptor is refused instead of reading past its end for the user size");
    bool oddBlock = false;
    try { MtkStorageInfo.Parse(Convert.FromHexString(DescriptorPayload(0xE8F800000ul, 600))); } catch (MtkDaProtocolException) { oddBlock = true; }
    Check(oddBlock, "MTK B3: a block size that is not a 512..65536 power of two is refused — every extent maths is derived from it");
    bool tinyArea = false;
    try { MtkStorageInfo.Parse(Convert.FromHexString(DescriptorPayload(128))); } catch (MtkDaProtocolException) { tinyArea = true; }
    Check(tinyArea, "MTK B3: a user area smaller than one block is refused rather than accepted as a device with no storage");

    // ---- B6.5: the 0x40004 geometry reply a UFS unit gives instead of the descriptor -------------------
    byte[] ufsGeometry = Convert.FromHexString(UfsGeometryPayload());
    MtkStorageInfo ufs = MtkStorageInfo.ParseUfs(ufsGeometry);
    Check(ufs.StorageType == 0x30 && ufs.BlockSize == 4096 && ufs.Boot1Size == 0x400000 && ufs.Boot2Size == 0x400000 &&
          ufs.UserAreaSize == 0x3B96000000ul && ufs.RpmbSize == 0 && ufs.GeneralPurpose1 == 0 && ufs.IsUfsAddressed &&
          !ufs.IsEmmcUserAddressed && ufs.HasMeasuredOpPair,
        "MTK B6.5 UFS: the 208-byte reply decodes as block 4096, two 4 MiB boot LUNs and a 238 GiB user area, and RPMB " +
        "stays 0 because no slot in this reply was measured for it");
    Check(ufs.OpRegion == 0x30 && ufs.OpField4 == 3 && decoded.OpRegion == 1 && decoded.OpField4 == 8,
        "MTK B6.5 UFS: the extent-struct pair travels with the reply that reported it — 1 + 8 on the MT6765, 0x30 + 3 on the MT6877");
    Check(ufs.Describe.Contains("(UFS)") && ufs.Describe.Contains("block 4096") && ufs.Describe.Contains("238 GiB") &&
          decoded.Describe.Contains("(eMMC)"),
        "MTK B6.5 UFS: the status line names the addressing the device itself reported, so the operator can tell a UFS unit from an eMMC one");
    Check(MtkStorageInfo.IsDescriptorShape(goodDescriptor) && !MtkStorageInfo.IsDescriptorShape(ufsGeometry) &&
          MtkStorageInfo.IsUfsGeometryShape(ufsGeometry) && !MtkStorageInfo.IsUfsGeometryShape(goodDescriptor),
        "MTK B6.5 UFS: the two geometry replies are told apart by their own shape — 104 bytes opening with 1, or 208 opening " +
        "with 0x30 — never by a chipset table, so a new chipset cannot be decoded into a plausible layout");
    byte[] zeroedDescriptorBytes = Convert.FromHexString(new string('0', 208));   // what 0x40001 really answers on the MT6877
    Check(!MtkStorageInfo.IsDescriptorShape(zeroedDescriptorBytes) && !MtkStorageInfo.IsUfsGeometryShape(zeroedDescriptorBytes),
        "MTK B6.5 UFS: the 104 all-zero bytes are neither reply shape — that is the fact that sends the session on to 0x40004");
    Check(Convert.ToHexString(MtkOpStruct.Create(ufs, 0x1000, 0x1000)).ToLowerInvariant() == UfsExtentStruct(0x1000, 0x1000) &&
          Convert.ToHexString(MtkOpStruct.Create(decoded, 0x1000, 0x1000)).ToLowerInvariant() == ExtentStruct(0x1000, 0x1000),
        "MTK B6.5 UFS: the same extent frames as 30000000 03000000 on a UFS unit and 01000000 08000000 on an eMMC one — the pair " +
        "is not a constant, and a READ into the wrong region reads the wrong area while answering happily");
    bool shortGeometry = false;
    try { MtkStorageInfo.ParseUfs(ufsGeometry[..207]); } catch (MtkDaProtocolException) { shortGeometry = true; }
    Check(shortGeometry, "MTK B6.5 UFS: a 207-byte geometry is refused instead of reading one byte past its end for the user area");
    bool wrongRegion = false;
    try { MtkStorageInfo.ParseUfs(Convert.FromHexString("01000000" + UfsGeometryPayload()[8..])); }
    catch (MtkDaProtocolException) { wrongRegion = true; }
    Check(wrongRegion, "MTK B6.5 UFS: a 208-byte reply that does not open with 0x30 is an unmeasured layout and is refused");
    bool ufsOddBlock = false;
    try { MtkStorageInfo.ParseUfs(Convert.FromHexString(UfsGeometryPayload(blockBytes: 600))); }
    catch (MtkDaProtocolException) { ufsOddBlock = true; }
    Check(ufsOddBlock, "MTK B6.5 UFS: the same block-size sanity line guards both decoders — 600 would move every partition by the wrong factor");
    byte[] withIdentity = (byte[])ufsGeometry.Clone();
    "FAKEPROD  FAKEPART0123456"u8.CopyTo(withIdentity.AsSpan(32));
    MtkStorageInfo idUfs = MtkStorageInfo.ParseUfs(withIdentity);
    Check(!idUfs.Describe.Contains("FAKE") && !idUfs.ToString().Contains("FAKE") && idUfs.EmmcPartNumber == null &&
          idUfs.UserAreaSize == ufs.UserAreaSize,
        "MTK B6.5 UFS: the disk's own product name and serial live at +32 and no string this record produces carries them " +
        "(EmmcPartNumber is only ever read from the eMMC layout, where it is a model-wide part number, not a serial)");

    var logoFromLba = new MtkDaExtent("logo", 2987008, 2989055, 512);
    Check(logoFromLba.ByteOffset == LogoAt && logoFromLba.ByteLength == 0x100000,
        "MTK B3: LBA × the DA's own block size gives byte 0x5b280000 + 1 MiB — the exact extent the write capture used");
    var probeTable = new MtkDaPartitionTable(512, new[] { logoFromLba }, GptHeader(2, 1, 128), new byte[128]);
    Check(probeTable.TryFind("Logo", out MtkDaExtent probed) && probed.LastLba == 2989055 && !probeTable.TryFind("bootlog", out _),
        "MTK B3: a partition table matches names case-insensitively and says no to a name it does not hold");

    // ---- B3.1: the two queries the command layer is built on ---------------------------------------
    var qLines = new List<string>();
    var (qSes, qDev) = await BootedDa(qLines, LimitsQuery().Concat(StorageQuery()));
    int afterBoot = qDev.Writes.Count;
    var limitsQ = await qSes.QueryLimitsAsync(default);
    var storageQ = await qSes.QueryStorageAsync(default);
    Check(limitsQ.WriteChunkSize == 2048 && limitsQ.TransferPacketSize == 1024,
        "MTK B3: chunk and packet sizes come from the DA's own 0x40007 answer, never from what one phone reported");
    Check(storageQ.BlockSize == 512 && storageQ.UserAreaSize == 0xE8F800000ul && storageQ.IsEmmcUserAddressed,
        "MTK B3: the 104-byte descriptor decodes into the block size and user area every extent is measured against");
    CheckFrames(SentFrames(qDev, afterBoot), "MTK B3: each query is the recorded 0x10009 preamble then the command id",
        PreFrame, LimitsCmd, PreFrame, StorageCmd);
    Check(LcHex(qDev.Writes[afterBoot]) == "efeeeefe0100000004000000" && LcHex(qDev.Writes[afterBoot + 1]) == PreFrame &&
          LcHex(qDev.Writes[afterBoot + 2]) == "efeeeefe0100000004000000" && LcHex(qDev.Writes[afterBoot + 3]) == LimitsCmd,
        "MTK B6.5: a live command is two transfers — the 12-byte header, then the id — the way every one of the 102 " +
        "command frames in the two boot recordings was captured. The same bytes merged into one 16-byte write are what " +
        "RUN B6-4's MT6877 DA refused to answer, after replaying the split shape seventeen times in one boot");
    int afterQueries = qDev.Writes.Count;
    Check(ReferenceEquals(await qSes.QueryLimitsAsync(default), limitsQ) && ReferenceEquals(await qSes.QueryStorageAsync(default), storageQ) &&
          qDev.Writes.Count == afterQueries,
        "MTK B3: both queries are cached, so the rest of the session costs the port nothing and the DA is not asked twice");
    Check(qLines.Any(l => l.Contains("DA limits: write chunk 0x800, transfer 0x400")),
        "MTK B3: the sizes the device advertised are shown to the operator before anything is transferred");
    var (descSes, descDev) = await BootedDa(null, new[] { Status, Status, DaFrame(DescriptorPayload(0xE8F800000ul)[..206]), Status }
        .Concat(new[] { Status, Status, DaFrame(UfsGeometryPayload()[..206]), Status }));
    int descSince = descDev.Writes.Count;
    Check((await DaFailure(descSes, () => descSes.QueryStorageAsync(default)))
              .Contains("Neither geometry reply this session can read"),
        "MTK B6.5: a reply that is neither of the two measured shapes is refused, not decoded into something plausible");
    Check(descSes.Storage == null, "MTK B3: nothing is cached from a refused descriptor, so the next operation cannot run on a half-read geometry");
    CheckFrames(SentFrames(descDev, descSince), "MTK B6.5: the session asks the two geometry queries the working client asks, in that order",
        PreFrame, StorageCmd, PreFrame, UfsGeomCmd);
    descSes.Dispose();

    // ---- B6.5: the live MT6877 shape — 0x40001 all-zero, 0x40004 the geometry, 0x30+3 in every struct ----
    IEnumerable<string> UfsGeometryQuery() => new[] { Status, Status, DaFrame(UfsGeometryPayload()), Status };
    string UfsLimitsReply = DaFrame("00002000" + "00000100");   // the 0x40007 answer on the capture: 2 MiB chunk, 64 KiB packet
    var ufsLines = new List<string>();
    var (ufsSes, ufsDev) = await BootedDa(ufsLines,
        new[] { Status, Status, ZeroedDescriptor, Status }.Concat(UfsGeometryQuery())
            .Concat(new[] { Status, Status, UfsLimitsReply, Status })
            .Concat(ReadGroup(Filler(0xB6, 4096))));
    int ufsSince = ufsDev.Writes.Count;
    var ufsPercents = new List<int>();
    byte[] ufsDump = await ufsSes.ReadExtentAsync(0x1000, 0x1000, ufsPercents.Add);
    Check(ufsDump.Length == 4096 && ufsDump.All(b => b == 0xB6),
        "MTK B6.5 UFS READ: the one frame that comes back is the extent, in full");
    CheckFrames(SentFrames(ufsDev, ufsSince), "MTK B6.5 UFS READ: descriptor, geometry, limits, then the read framed with 0x30 + 3",
        PreFrame, StorageCmd, PreFrame, UfsGeomCmd, PreFrame, LimitsCmd, ReadCmd, UfsExtentStruct(0x1000, 0x1000), ZeroWord);
    Check(ufsSes.Limits.WriteChunkSize == 0x200000 && ufsSes.Limits.TransferPacketSize == 0x10000,
        "MTK B6.5 UFS: this DA advertises 2 MiB / 64 KiB where the MT6765 DA said 2048 / 1024 — the sizes stay read from the device");
    Check(ufsLines.Any(l => l.Contains("asking 0x40004")) &&
          ufsLines.Any(l => l.Contains("DA storage: type 48 (UFS), block 4096")) &&
          ufsLines.Any(l => l.Contains("READ 0x1000 + 0x1000 — 4096 byte(s) in 1 frame(s)")),
        "MTK B6.5 UFS: the operator sees the fallback happen and the geometry the unit itself reported");
    Check(ufsPercents.Count == 1 && ufsPercents[0] == 100, "MTK B6.5 UFS READ: a single-frame extent still reports a finished read");
    int afterUfsRead = ufsDev.Writes.Count;
    Check(ReferenceEquals(await ufsSes.QueryStorageAsync(default), ufsSes.Storage) && ufsDev.Writes.Count == afterUfsRead,
        "MTK B6.5 UFS: the geometry the fallback found is cached like the descriptor is, so 0x40004 is not re-asked per operation");
    string ufsMisaligned = await DaFailure(ufsSes, () => ufsSes.ReadExtentAsync(0x1000, 512));
    Check(ufsMisaligned.Contains("not a multiple of the device block size 4096") && ufsDev.Writes.Count == afterUfsRead,
        "MTK B6.5 UFS: extents are checked against the block size THIS unit reported — 512 is a whole eMMC block and a fifth of " +
        "a UFS one, and the refusal costs the port nothing");
    ufsSes.Dispose();

    // ---- B3.2: READ is frame-driven ------------------------------------------------------------------
    var rLines = new List<string>();
    var (rSes, rDev) = await BootedDa(rLines, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(Filler(0xA0, 1024), Filler(0xA1, 1024), Filler(0xA2, 1024), Filler(0xA3, 1024))));
    int readStart = rDev.Writes.Count;
    var readPercents = new List<int>();
    byte[] logoDump = await rSes.ReadExtentAsync(LogoAt, LogoLen, readPercents.Add);
    Check(logoDump.Length == 0x1000 && logoDump[0] == 0xA0 && logoDump[1023] == 0xA0 && logoDump[1024] == 0xA1 && logoDump[4095] == 0xA3,
        "MTK B3 READ: the four frames are concatenated in arrival order, so a 128-frame dump reassembles byte-exactly");
    CheckFrames(SentFrames(rDev, readStart), "MTK B3 READ: preamble+limits, READ, the 56-byte extent, then exactly one ack per data frame",
        PreFrame, StorageCmd, PreFrame, LimitsCmd, ReadCmd, LogoStruct, ZeroWord, ZeroWord, ZeroWord, ZeroWord);
    Check(readPercents.SequenceEqual(new[] { 25, 50, 75, 100 }),
        "MTK B3 READ: progress advances per frame rather than per byte, so a long read shows real movement instead of stuttering");
    Check(rLines.Any(l => l.Contains("READ 0x5b280000 + 0x1000 — 4096 byte(s) in 4 frame(s)")),
        "MTK B3 READ: the operator is told the byte count and the frame count that produced it");

    // The read-path probe (2026-11-05, after PMK's "အရမ်းကြာ" on the NV backup). A real read costs 0.41 s per
    // 64 KiB frame while an 8 MiB write commits in 3 s, so the wait is host-side until proved otherwise — and the
    // only way to fix the right half is to have the extent say how many passes it made, how many found nothing,
    // and how many milliseconds went to the transport versus to our own sleep.
    var readProbe = System.Text.RegularExpressions.Regex.Match(
        rLines.Last(l => l.Contains("Read probe")),
        @"(\d+) frame\(s\), (\d+) B — (\d+) read attempt\(s\), (\d+) empty wait\(s\), transport (\d+) ms, our delay (\d+) ms");
    Check(readProbe.Success && readProbe.Groups[1].Value == "4" && readProbe.Groups[2].Value == "4096",
        "MTK B7 READ probe: the probe line carries this extent's own frame and byte count, not a session total");
    Check(readProbe.Success && int.Parse(readProbe.Groups[3].Value) >= 4 && readProbe.Groups[4].Value == "0",
        "MTK B7 READ probe: a scripted device with bytes ready on every pass charges zero empty waits, so a real " +
        "run's waits are genuinely the DA's pacing and not our own polling");
    int afterRead = rDev.Writes.Count;
    rDev.Append(ReadGroup(Filler(0x5A, 1024), Filler(0x5A, 1024), Filler(0x5A, 1024), Filler(0x5A, 1024)));
    byte[] secondRead = await rSes.ReadExtentAsync(LogoAt, LogoLen);
    Check(secondRead.All(b => b == 0x5A), "MTK B3 READ: a second extent on the same session reads the device, not the previous answer");
    Check(System.Text.RegularExpressions.Regex.Match(rLines.Last(l => l.Contains("Read probe")),
            @"(\d+) read attempt\(s\)").Groups[1].Value == readProbe.Groups[3].Value,
        "MTK B7 READ probe: the counters reset per extent — a second identical read reports the same attempt count " +
        "instead of accumulating, so the numbers describe the read the operator is waiting for");
    CheckFrames(SentFrames(rDev, afterRead), "MTK B3 READ: a repeat read sends no preamble and no queries — the cable was touched once",
        ReadCmd, LogoStruct, ZeroWord, ZeroWord, ZeroWord, ZeroWord);
    rSes.Dispose();

    async Task<(string Error, List<string> Frames)> OperationRefusal(string label, IEnumerable<string> replies, Func<MtkDaSession, Task> operation, string expectedInMessage)
    {
        var (ses, dev) = await BootedDa(null, replies);
        int since = dev.Writes.Count;
        string error = await DaFailure(ses, () => operation(ses));
        Check(error.Contains(expectedInMessage), label + (error.Contains(expectedInMessage) ? "" : " — got: " + error));
        return (error, SentFrames(dev, since));
    }

    // ---- B3.3: WRITE (0x10004 + per-chunk words + 0x80005) and the read-back that proves it ----------
    var wLines = new List<string>();
    byte[] writeImage = Enumerable.Repeat((byte)0x01, 2048).Concat(Enumerable.Repeat((byte)0x02, 2048)).ToArray();
    IEnumerable<string> WriteReplies(params string[] verifyFrames) => StorageQuery().Concat(LimitsQuery())
        .Concat(new[] { Status, Status, Status, Status, Status, Status, Status, Status })
        .Concat(ReadGroup(verifyFrames));
    var (wSes, wDev) = await BootedDa(wLines, WriteReplies(Filler(0x01, 1024), Filler(0x01, 1024), Filler(0x02, 1024), Filler(0x02, 1024)));
    int writeStart = wDev.Writes.Count;
    var writePercents = new List<int>();
    await wSes.WriteExtentAsync(LogoAt, writeImage, writePercents.Add);
    List<string> writtenFrames = SentFrames(wDev, writeStart);
    CheckFrames(writtenFrames, "MTK B3 WRITE: queries, WRITE+extent, (0, byte-sum, chunk) per chunk, commit preamble + 0x80005, then the verify read",
        PreFrame, StorageCmd, PreFrame, LimitsCmd, WriteCmd, LogoStruct,
        ZeroWord, "00080000", Filler(0x01, 2048), ZeroWord, "00100000", Filler(0x02, 2048),
        PreFrame, CommitCmd, ReadCmd, LogoStruct, ZeroWord, ZeroWord, ZeroWord, ZeroWord);
    Check(writtenFrames[8].Length / 2 == 2048 && writtenFrames[11].Length / 2 == 2048,
        "MTK B3 WRITE: the data frames are the DA's own 2048-byte chunk, so a 4096-byte image is two chunks and not one guess");
    Check(writtenFrames[7] == "00080000" && writtenFrames[10] == "00100000",
        "MTK B3 WRITE: each frame is preceded by the byte sum of exactly that chunk (2048, then 4096) — the whole image would be 6144 and the DA would reject the second chunk");
    Check(writePercents.Count == 6 && writePercents[1] == 100 && writePercents[5] == 100,
        "MTK B3 WRITE: progress covers the write and then the verify pass, both ending at 100");
    Check(wLines.Any(l => l.Contains("committed")) && wLines.Any(l => l.Contains("VERIFY OK")),
        "MTK B3 WRITE: success is reported as committed-then-verified, never as a status word alone");
    int afterWrite = wDev.Writes.Count;
    wDev.Append(Enumerable.Repeat(Status, 8));   // accept, extent, chunk 1, chunk 2, final, commit preamble, commit, commit close
    await wSes.WriteExtentAsync(LogoAt, writeImage, null, verify: false);
    CheckFrames(SentFrames(wDev, afterWrite), "MTK B3 WRITE: verify:false sends the identical write and stops at the commit — the read-back is the only difference",
        WriteCmd, LogoStruct, ZeroWord, "00080000", Filler(0x01, 2048), ZeroWord, "00100000", Filler(0x02, 2048), PreFrame, CommitCmd);
    wSes.Dispose();

    var (badVSes, badVDev) = await BootedDa(null, WriteReplies(Filler(0x01, 1024), Filler(0x01, 1024),
        "ff" + Filler(0x02, 1023), Filler(0x02, 1023)));
    string verifyFailure = await DaFailure(badVSes, () => badVSes.WriteExtentAsync(LogoAt, writeImage));
    Check(verifyFailure.Contains("MtkDataVerifyException") && verifyFailure.Contains("Verify failed at byte 2048"),
        "MTK B3 VERIFY: a flipped byte at the head of the second chunk is named by offset, not reported as a flash success");
    Check(verifyFailure.Contains("real corruption"), "MTK B3 VERIFY: the operator is told the DA accepted the write and the bytes are still wrong");
    List<string> verifyFailFrames = SentFrames(badVDev, 29);
    Check(verifyFailFrames.Contains(CommitCmd) && verifyFailFrames.Contains(ReadCmd),
        "MTK B3 VERIFY: the refusal lands after the commit, on the read-back — the write itself went exactly as measured");
    badVSes.Dispose();

    var (nvSes, nvDev) = await BootedDa(null, Array.Empty<string>());
    Check((await DaFailure(nvSes, () => nvSes.WriteExtentAsync(LogoAt, Array.Empty<byte>())))
              .Contains("zero-length write"), "MTK B3 WRITE: an empty image is refused before the port is opened for it");
    string shortImage = Path.Combine(dir, "short-logo.bin");
    File.WriteAllBytes(shortImage, writeImage);
    Check((await DaFailure(nvSes, () => nvSes.WriteExtentFromFileAsync(LogoAt, shortImage, 5000)))
              .Contains("from a file that is 4096 long"),
        "MTK B3 WRITE: asking for more bytes than the image holds is refused up front, not padded into the next partition");
    Check((await DaFailure(nvSes, () => nvSes.WriteExtentFromFileAsync(LogoAt, Path.Combine(dir, "no-such-image.bin"))))
              .Contains("Write image is missing"), "MTK B3 WRITE: a missing image is a named file, not a protocol timeout");
    Check(SentFrames(nvDev, 29).Count == 0,
        "MTK B3 WRITE: all three image refusals happened on the host — the DA was never even asked for its geometry");
    nvSes.Dispose();

    // The file paths are the ones the UI will actually use: a partition dump goes to disk, and a scatter-sized
    // image is streamed from a file rather than allocated.
    var fLines = new List<string>();
    var (fSes, fDev) = await BootedDa(fLines, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(Filler(0xA0, 1024), Filler(0xA1, 1024), Filler(0xA2, 1024), Filler(0xA3, 1024)))
        .Concat(Enumerable.Repeat(Status, 8))
        .Concat(ReadGroup(Filler(0x01, 1024), Filler(0x01, 1024), Filler(0x02, 1024), Filler(0x02, 1024))));
    string dumpPath = Path.Combine(dir, "logo-dump.bin");
    var dumpPercents = new List<int>();
    int fileReadStart = fDev.Writes.Count;
    await fSes.ReadExtentToFileAsync(LogoAt, LogoLen, dumpPath, dumpPercents.Add);
    Check(new FileInfo(dumpPath).Length == 0x1000 && File.ReadAllBytes(dumpPath)[1024] == 0xA1,
        "MTK B3 READ: a partition dump lands on disk complete and in the DA's own frame order");
    Check(dumpPercents.SequenceEqual(new[] { 25, 50, 75, 100 }),
        "MTK B3 READ: a file-backed read reports progress from the wire, so the UI bar means real bytes on disk");
    Check(Directory.GetFiles(dir, "logo-dump.bin.*.part").Length == 0,
        "MTK B3 READ: the scratch name is moved away once the extent is complete — no half-dump is left beside the real one");
    CheckFrames(SentFrames(fDev, fileReadStart), "MTK B3 READ: dumping to a file sends the same frames as dumping to memory",
        PreFrame, StorageCmd, PreFrame, LimitsCmd, ReadCmd, LogoStruct, ZeroWord, ZeroWord, ZeroWord, ZeroWord);

    // The image is streamed from the file rather than allocated, which is what lets a scatter-sized super be
    // written without turning it into an out-of-memory crash mid-flash.
    int fileWriteStart = fDev.Writes.Count;
    await fSes.WriteExtentFromFileAsync(LogoAt, shortImage, writeImage.Length);
    CheckFrames(SentFrames(fDev, fileWriteStart), "MTK B3 WRITE: a file-sourced image puts the identical chunk frames on the wire as a buffer write, queries and all",
        WriteCmd, LogoStruct, ZeroWord, "00080000", Filler(0x01, 2048), ZeroWord, "00100000", Filler(0x02, 2048),
        PreFrame, CommitCmd, ReadCmd, LogoStruct, ZeroWord, ZeroWord, ZeroWord, ZeroWord);
    Check(fLines.Any(l => l.Contains("VERIFY OK")) && fLines.Any(l => l.Contains("committed")),
        "MTK B3 WRITE: the file path verifies exactly like the buffer path — the read-back is not skipped because the image came off disk");
    fSes.Dispose();

    // A dump that dies halfway must leave nothing behind: a short logo dump on a bench is how a bad restore starts.
    var (halfSes, halfDev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(Filler(0xA0, 1024))));
    string halfPath = Path.Combine(dir, "half-dump.bin");
    Check((await DaFailure(halfSes, () => halfSes.ReadExtentToFileAsync(LogoAt, LogoLen, halfPath)))
              .Contains("timed out"), "MTK B3 READ: a DA that stops mid-extent is a timeout, not a short file");
    Check(!File.Exists(halfPath) && Directory.GetFiles(dir, "half-dump.bin.*").Length == 0,
        "MTK B3 READ: an interrupted dump leaves no destination and no partial file — the operator never restores from a quarter of a partition");
    halfSes.Dispose();

    // ---- B3.4: ERASE (0x10003) goes bare and answers with report words -------------------------------
    var eLines = new List<string>();
    var (eSes, eDev) = await BootedDa(eLines, StorageQuery().Concat(new[]
    { Status, Status, DaFrame("04000440"), DaFrame("64000000"), DaFrame("05000440") }));
    await eSes.EraseExtentAsync(LogoAt, LogoLen);
    CheckFrames(SentFrames(eDev, 29), "MTK B3 ERASE: the extent goes bare — no preamble, no limits query — and one ack sits between the percent word and the end marker",
        PreFrame, StorageCmd, EraseCmd, LogoStruct, ZeroWord);
    Check(!SentFrames(eDev, 29).Contains(LimitsCmd),
        "MTK B3 ERASE: the measured cycle never asked for transfer limits, so nothing was guessed into the group");
    Check(eLines.Any(l => l.Contains("the DA reported completion (3 word(s)")) &&
          eLines.Any(l => l.Contains("without proving zeroes")),
        "MTK B3 ERASE: completion is reported as the DA's own words, with the honest caveat that an accepted erase is not measured zeros");
    var (e2Ses, e2Dev) = await BootedDa(null, StorageQuery().Concat(new[]
    { Status, Status, DaFrame("04000440"), DaFrame("64000000"), DaFrame("32000000"), DaFrame("05000440") }));
    await e2Ses.EraseExtentAsync(LogoAt, LogoLen);
    Check(SentFrames(e2Dev, 29).Count(f => f == ZeroWord) == 1,
        "MTK B3 ERASE: two progress words, one ack — the measured position, because guessing extra acks could interleave into the next command");
    var (e3Ses, e3Dev) = await BootedDa(null, StorageQuery().Concat(new[]
    { Status, Status, DaFrame("04000440"), DaFrame("00f00000") }));
    string eraseRefusal = await DaFailure(e3Ses, () => e3Ses.EraseExtentAsync(LogoAt, LogoLen));
    Check(eraseRefusal.Contains("unexpected report word 0x0000f000"),
        "MTK B3 ERASE: a report word outside the four measured shapes stops the loop instead of being reinterpreted mid-erase");
    CheckFrames(SentFrames(e3Dev, 29), "MTK B3 ERASE: the refusal happens before any ack, so nothing was invented on the wire",
        PreFrame, StorageCmd, EraseCmd, LogoStruct);
    eSes.Dispose(); e2Ses.Dispose(); e3Ses.Dispose();

    // ---- B4.42: the DA-extension reboot, replayed against the RUN 48 measurement ---------------------
    string stubHex = Convert.ToHexString(MtkDaReset.ToprguStub).ToLowerInvariant();
    Check(MtkDaReset.ExtBase == 0x4FFF0000 && MtkDaReset.ToprguStub.Length == 56,
        "MTK RESET: the stub goes to the scratch address the boot's own ext upload used, and it is exactly the 56 bytes RUN 48 carried");
    Check(Convert.ToHexString(SHA256.HashData(MtkDaReset.ToprguStub)).ToLowerInvariant() == "1a99a3f095fc93fe32ba1929491606867c397e8035e4874f957869f87f285d13",
        "MTK RESET: the stub is byte-identical to the captured payload — a hand-edited watchdog write is a brick, so the hash is the gate");
    Check(Convert.ToHexString(MtkDaReset.UploadArgs(0x4FFF0000, 56)).ToLowerInvariant() == "0000ff4f000000003800000000000000",
        "MTK RESET: the argument frame is {u32le addr, 0, u32le len, 0} — the same upload shape the boot's ext upload carried, zero words included");
    Check(stubHex.AsSpan(88).StartsWith("142080e5") && stubHex.AsSpan(96).StartsWith("4ff07ff5"),
        "MTK RESET: the captured bytes really are the WDT_SWRST store at [r0,#0x14] and the DSB before the spin — the reset meaning sits in the bytes, not the name");

    var rbLines = new List<string>();
    var (rbSes, rbDev) = await BootedDa(rbLines, new[] { Status, Status });
    int rbStart = rbDev.Writes.Count;
    await rbSes.RebootAsync();
    CheckFrames(SentFrames(rbDev, rbStart), "MTK B3 RESET: the measured cycle is bare — 0x10008, the 16-byte args, the 56-byte stub frame — with no preamble and no limits query",
        UploadCmd, "0000ff4f000000003800000000000000", stubHex);
    Check(rbLines.Any(l => l.Contains("Reset command was sent") && l.Contains("status 0")),
        "MTK B3 RESET: the accepted run is reported in the oracle's own words — sent, accepted, phone rebooting");
    Check(!rbSes.DaIsReady, "MTK B3 RESET: a fired reboot spends the session — the phone is on its way down, so no further command can be served");
    Check((await DaFailure(rbSes, () => rbSes.ReadExtentAsync(LogoAt, LogoLen))).Contains("The DA is not ready"),
        "MTK B3 RESET: a rebooted session refuses the next operation outright instead of writing into a phone that is rebooting");
    rbSes.Dispose();

    // The oracle itself was answered status 0 and then got a dead port, and still called the reset sent —
    // silence after the stub is the same shape from our side, not a failure.
    var rb2Lines = new List<string>();
    var (rb2Ses, rb2Dev) = await BootedDa(rb2Lines, new[] { Status });
    int rb2Start = rb2Dev.Writes.Count;
    await rb2Ses.RebootAsync();
    CheckFrames(SentFrames(rb2Dev, rb2Start), "MTK B3 RESET: the silent-phone run still sends the identical three frames and stops — nothing else is guessed onto the wire",
        UploadCmd, "0000ff4f000000003800000000000000", stubHex);
    Check(rb2Lines.Any(l => l.Contains("Reset command was sent") && l.Contains("quiet")),
        "MTK B3 RESET: silence after the stub is logged as the measured success shape, not raised as a timeout");
    Check(!rb2Ses.DaIsReady, "MTK B3 RESET: the silent run spends the session too — the stub was written, whatever the reply said");
    rb2Ses.Dispose();

    // ---- B5.1: the seccfg wire, measured from the RUN 48 oracle session -------------------------------
    // RUN 48 ran "da seccfg unlock;reset" on the already-unlocked Redmi 9A, so the oracle sent the STATE half
    // and nothing of the unlock write (stdout: "Device is already unlocked"). The state half is recorded here,
    // byte for byte, straight from the oracle's own TX lines — every one of them is the 0x10005 read our RUN 6/8
    // fixtures already proved, over extents seccfg actually asks for (all 16 measured structs equal
    // MtkOpStruct.Create(offset, length) exactly):
    //   0x0+0x400 config block, 0x200+0x400, then 512-byte probes stepping offset 0x400→0x1800 at len 0x200,
    //   a full 0x0+0x4200 config read, and 0x46800000+0x800000 — an 8 MiB extent the DA served in
    //   128 x 0x10000 frames.
    // The GPT pass explains the low-offset reads (that 0x0+0x4200 read answers all zeros), and the hwtype check
    // that runs beside them is the DA-extension register protocol, now measured on both directions:
    //   register WRITE = 09000100 (preamble) / status / 04000f00 (0x000F0004) / status /
    //                    20a00010 (the register address 0x1000a020 as a 4-byte LE frame) / <value LE> / two status
    //                    — the oracle prints "TX: 0x1000a020 -> 0x0" beside it;
    //   register READ  = the same preamble + 02000f00 (0x000F0002), then the address frame (50a00010 = 0x1000a050)
    //                    and a count frame (01000000), answered by a 4-byte data frame (67bb2b97) and a status —
    //                    "RX: 0x1000a050 -> 0x972bbb67".
    // Registers 0x1000a020..0x1000a03c are written zero, 0x1000a004 = 2, 0x1000a008 is polled (0x1, 0x2, 0x80008000,
    // 0xc0008000) and 0x1000a050.. return hashes. That is the DA-side HACC/AES verify chain, which a read-only state
    // query does not need — B5.2 reads the config block through the proven 0x10005 path and touches no register.
    // (An earlier pass read 20a00010 as a 0x10008 upload argument; the fuller walk of lines 22176–27760 shows it is
    // the register-address frame of a 0x000F0004 write. Corrected here so the ledger matches the capture.)
    // The unlock WRITE half is not measured — this unit was unlocked, so no unlock bytes exist to transcribe;
    // it stays unimplemented until a locked unit is captured. Never guess an id.
    string cfgStruct = ExtentStruct(0x0, 0x400);
    Check(cfgStruct == "0100000008000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000000000000000",
        "MTK B5 SECCFG: the config-block struct our codec builds is byte-identical to the RUN 48 oracle's first read frame — region 1, field 8, u64 offset, u64 length");
    Check(ExtentStruct(0x600, 0x200) == "0100000008000000000600000000000000020000000000000000000000000000000000000000000000000000000000000000000000000000",
        "MTK B5 SECCFG: a mid-sweep probe frame matches the oracle byte for byte - the struct layout survives offsets the GPT never uses");
    Check(ExtentStruct(0x0, 0x4200) == "0100000008000000000000000000000000420000000000000000000000000000000000000000000000000000000000000000000000000000",
        "MTK B5 SECCFG: the full-config read (0x0 + 0x4200) is byte-exact - the oracle reads the config block whole, not by sector");
    Check(ExtentStruct(0x46800000, 0x800000) == "0100000008000000000080460000000000008000000000000000000000000000000000000000000000000000000000000000000000000000",
        "MTK B5 SECCFG: the 8-MiB seccfg config extent (0x46800000 + 0x800000) is expressible and byte-exact - the read the 128-frame loop served");

    var scLines = new List<string>();
    var (scSes, scDev) = await BootedDa(scLines, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(Filler(0x77, 0x400))));
    int scStart = scDev.Writes.Count;
    byte[] cfgBlock = await scSes.ReadExtentAsync(0x0, 0x400);
    CheckFrames(SentFrames(scDev, scStart), "MTK B5 SECCFG: reading the config block goes out as the measured oracle shape — geometry, limits, 0x10005, the 0x0+0x400 struct, one ack per frame",
        PreFrame, StorageCmd, PreFrame, LimitsCmd, ReadCmd, cfgStruct, ZeroWord);
    Check(cfgBlock.Length == 0x400 && cfgBlock.All(b => b == 0x77),
        "MTK B5 SECCFG: the config block comes back whole — the same frame-driven reader that dumped logo serves a seccfg extent");
    scSes.Dispose();

    // ---- B5.2: the config block decodes into a lock state, and only into that -------------------------
    // The 64 bytes below are RUN 48's own answer, verbatim: the head of the first 0x10000 frame of the
    // 0x46800000 + 0x800000 read (stdout line 19621), printed beside the oracle's two verdicts —
    // "Detected V4 Lockstate" and "Device is already unlocked". Nothing in this block is reconstructed; the
    // zero runs and the one other non-zero word are the capture's own, and the sha256 pins all 1024 bytes.
    const string SeccfgMeasuredHead =
        "4d4d4d4d040000003c00000003000000000000000000000045454545ace82370" +
        "da27d32c6c068424b679a2239ba49310dc43f62bf5d171e6e1d1361200000000";
    string SeccfgMeasuredBlock = SeccfgMeasuredHead + Filler(0, 0x1C0) + "9079092200000001" + Filler(0, 0x1F8);
    Check(SeccfgMeasuredBlock.Length == 0x400 * 2,
        "MTK B5.2: the fixture block is the measured 0x400 the oracle asked for, assembled from the capture's own runs");
    byte[] seccfgBlockBytes = Convert.FromHexString(SeccfgMeasuredBlock);
    Check(Sha256Of(seccfgBlockBytes) ==
              "de5596b4cea5987939e256717f2dadd55047a7cac11a97eb812f67c573f2ae4c",
        "MTK B5.2: the config block fixture hashes to the RUN 48 capture — a hand-edited lock-state test would test nothing, so the hash is the gate");
    Check(MtkSeccfg.HeadReadLength(512) == 0x400 && MtkSeccfg.HeadReadLength(1024) == 0x400 &&
              MtkSeccfg.HeadReadLength(4096) == 0x1000 && MtkSeccfg.HeadReadLength(2048) % 2048 == 0,
        "MTK B5.2: the head the DA is asked for is the measured 0x400 rounded up to whole blocks — the session refuses a length that is not, so this is the only shape that can be sent");

    MtkSeccfgConfig cfgUnlocked = MtkSeccfg.Parse(seccfgBlockBytes, 0x46800000, 0x400);
    Check(cfgUnlocked.Version == 4 && cfgUnlocked.HeaderField == 0x3C && cfgUnlocked.Checksum == 0x7023E8ACu &&
              cfgUnlocked.Marker == "EEEE" && cfgUnlocked.MarkerExpected,
        "MTK B5.2: the measured block decodes to v4, header 0x3c, checksum 0x7023e8ac and the EEEE marker — the words the oracle printed its own verdicts beside");
    Check(cfgUnlocked.LockState == 3 && cfgUnlocked.IsUnlocked && cfgUnlocked.LockStateName == "UNLOCKED",
        "MTK B5.2: state 0x03 is UNLOCKED — the same unit the oracle called \"Device is already unlocked\"");
    Check(cfgUnlocked.Describe.Contains("UNLOCKED") && cfgUnlocked.Describe.Contains("v4") &&
              cfgUnlocked.Describe.Contains("0x46800000") && cfgUnlocked.Describe.Contains("checksum 0x7023e8ac"),
        "MTK B5.2: the line the operator sees names the version, the extent it was read from and the state — the numbers a scatter or a capture can be checked against");
    Check(!cfgUnlocked.Describe.Contains("da27d32c") && !cfgUnlocked.Describe.Contains("45454545") &&
              !cfgUnlocked.Describe.Contains("4d4d4d4d"),
        "MTK B5.2: no raw block bytes reach Describe — the 28 bytes at +0x20 are bound to this phone, and this product keeps device identity out of its records and its logs");

    string cfgLocked = SeccfgMeasuredHead[..24] + "00000000" + SeccfgMeasuredHead[32..];
    Check(MtkSeccfg.Parse(Convert.FromHexString(cfgLocked)).LockStateName == "LOCKED" &&
              !MtkSeccfg.Parse(Convert.FromHexString(cfgLocked)).IsUnlocked,
        "MTK B5.2: the same block with the state word at 0x00 reads LOCKED — the tool's prior empirical rule, applied in both directions");
    string cfgOddState = SeccfgMeasuredHead[..24] + "02000000" + SeccfgMeasuredHead[32..];
    Check(MtkSeccfg.Parse(Convert.FromHexString(cfgOddState)).LockStateName == "unknown (raw 0x00000002)",
        "MTK B5.2: a state word this decoder has no measured meaning for is reported as its raw number, not renamed to a guess");
    Check(MtkSeccfg.Parse(Convert.FromHexString(SeccfgMeasuredHead)).ReadLength == 0 &&
              MtkSeccfg.Parse(Convert.FromHexString("4d4d4d4d040000003c00000003000000" + new string('0', 2048 - 32))).LockState == 3,
        "MTK B5.2: only the first 0x20 bytes are decoded, so a head read of any whole-block length parses — and an unattributed read stays out of the record");
    string SeccfgDecodeError(Func<MtkSeccfgConfig> decode)
    {
        try { decode(); return "(parsed, so nothing was refused)"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    Check(SeccfgDecodeError(() => MtkSeccfg.Parse(new byte[0x1F])).Contains("shorter than 0x20"),
        "MTK B5.2: a block that does not reach the state word is refused instead of reporting a state it never read");
    string cfgNoMagic = "4d4d4d4e" + SeccfgMeasuredHead[8..];
    Check(SeccfgDecodeError(() => MtkSeccfg.Parse(Convert.FromHexString(cfgNoMagic))).Contains("magic mismatch") &&
              SeccfgDecodeError(() => MtkSeccfg.Parse(Convert.FromHexString(cfgNoMagic))).Contains("4d4d4d4e"),
        "MTK B5.2: a block whose magic is not MMMM is refused with the bytes it actually started with — a wrong extent is not a lock state");
    Check(SeccfgDecodeError(() => MtkSeccfg.Parse(new byte[0x20])).Contains("00000000") &&
              SeccfgDecodeError(() => MtkSeccfg.Parse(new byte[0x20])).Contains("magic mismatch"),
        "MTK B5.2: an all-zero block is refused as a magic mismatch rather than read as LOCKED — a zeroed extent and a locked bootloader must never print the same word");
    string headLengthError = "(no refusal)";
    try { MtkSeccfg.HeadReadLength(0); } catch (Exception ex) { headLengthError = ex.GetType().Name + ": " + ex.Message; }
    Check(headLengthError.Contains("zero block size"),
        "MTK B5.2: a descriptor that reports a zero block size cannot size a head read, and is refused instead of dividing by it");

    // The live-session half: the same read on a device whose own GPT names seccfg, and the refusals around it.
    // The table is built through the harness's own GPT helpers, so the extent under test is the device's own
    // first/last LBA pair — 2310144..2326527, which is what printgpt listed for seccfg on this unit.
    (byte[] Header, byte[] Entries) SeccfgTable(params (string Name, long First, long Last)[] parts)
    {
        byte[] header = GptHeader(2, parts.Length, 128);
        byte[] entries = new byte[((parts.Length * 128 + 511) / 512) * 512];   // whole blocks, as the session reads them
        for (int i = 0; i < parts.Length; i++)
            GptEntry(parts[i].Name, parts[i].First, parts[i].Last).CopyTo(entries, i * 128);
        return (header, entries);
    }
    var (scTab, scEnt) = SeccfgTable(("logo", 2987008, 2989055), ("seccfg", 2310144, 2326527));
    var stLines = new List<string>();
    var (stSes, stDev) = await BootedDa(stLines, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(scTab))).Concat(ReadGroup(LcHex(scEnt))));
    MtkDaExtent scExtent = await stSes.RequirePartitionAsync("seccfg", default);
    Check(scExtent.ByteOffset == 0x46800000 && scExtent.ByteLength == 0x800000,
        "MTK B5.2: LBA 2310144 resolves to byte 0x46800000 + 0x800000 — the extent printgpt listed and the oracle read, derived from the device's own table");
    int stStart = stDev.Writes.Count;
    stDev.Append(ReadGroup(SeccfgMeasuredBlock));
    MtkSeccfgConfig liveCfg = await stSes.ReadSeccfgConfigAsync();
    CheckFrames(SentFrames(stDev, stStart),
        "MTK B5.2: the state query is one proven READ over the config head — table, geometry and limits are all cached, so the cable carries nothing else",
        ReadCmd, ExtentStruct(0x46800000, 0x400), ZeroWord);
    Check(!SentFrames(stDev, stStart).Contains(WriteCmd) && !SentFrames(stDev, stStart).Contains(EraseCmd) &&
              !SentFrames(stDev, stStart).Contains(UploadCmd) && !SentFrames(stDev, stStart).Contains(CommitCmd),
        "MTK B5.2: reading the state sends no WRITE, no ERASE and no upload frame — this path cannot change a lock policy, whatever a button called seccfg might suggest");
    Check(liveCfg.LockState == 3 && liveCfg.ReadOffset == 0x46800000 && liveCfg.ReadLength == 0x400,
        "MTK B5.2: the decoded state carries the extent it was read from, so the record says where the number came from");
    Check(stLines.Any(l => l.Contains("seccfg:") && l.Contains("UNLOCKED")),
        "MTK B5.2: the session logs the verdict once, on the same line that names the partition and the block");
    int stAfterRead = stDev.Writes.Count;
    Check((await DaFailure(stSes, () => stSes.WritePartitionAsync("seccfg", shortImage, null, true, default)))
              .Contains("program payload has never been measured"),
        "MTK B5.9: writing seccfg through the live session is refused — the unlock payload was never captured, so unmeasured bytes would change the lock policy silently instead of failing");
    Check((await DaFailure(stSes, () => stSes.ErasePartitionAsync("seccfg", default)))
              .Contains("erased through a live session is refused"),
        "MTK B5.9: erasing seccfg is refused by the same guard — wiping the lock policy is not a repair, and no capture has ever proven what the device does afterwards");
    Check(stDev.Writes.Count == stAfterRead,
        "MTK B5.9: both seccfg program refusals cost the port zero frames, and the read the state query is entitled to still went out — the block is a gate on programming, not on reading");
    stSes.Dispose();
    Check((await DaFailure(stSes, () => stSes.ReadSeccfgConfigAsync())).Contains("The DA is not ready"),
        "MTK B5.2: a closed session reads no seccfg either — the same spent-session rule every other command obeys");

    var (noTab, noEnt) = SeccfgTable(("logo", 2987008, 2989055));
    var (onlySes, onlyDev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(noTab))).Concat(ReadGroup(LcHex(noEnt))));
    await onlySes.ReadPartitionTableAsync();   // the table is what the name check needs — pay for it once, up front
    int onlyStart = onlyDev.Writes.Count;
    Check((await DaFailure(onlySes, () => onlySes.ReadSeccfgConfigAsync())).Contains("has no partition named 'seccfg'"),
        "MTK B5.2: a device whose own table has no seccfg is refused by name, with the entry count that proves the table was read");
    Check(SentFrames(onlyDev, onlyStart).Count == 0,
        "MTK B5.2: with the table cached, the name refusal costs the port not one frame — a refusal is a decision, not a transaction");
    onlySes.Dispose();

    var (tinyTab, tinyEnt) = SeccfgTable(("seccfg", 2310144, 2310144));   // one block: no room for the config block
    var (tnSes, tnDev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(tinyTab))).Concat(ReadGroup(LcHex(tinyEnt))));
    await tnSes.ReadPartitionTableAsync();
    int tnStart = tnDev.Writes.Count;
    Check((await DaFailure(tnSes, () => tnSes.ReadSeccfgConfigAsync())).Contains("under the 1024 byte(s)"),
        "MTK B5.2: a seccfg partition smaller than the config block is refused rather than read short — a truncated head is a layout this decoder has not seen");
    Check(SentFrames(tnDev, tnStart).Count == 0,
        "MTK B5.2: the too-small refusal sent no frame at all, so the device was never asked for bytes it does not have");
    tnSes.Dispose();

    // A DA that answers the head with something else is refused by the decoder, not reinterpreted.
    var (badTab, badEnt) = SeccfgTable(("seccfg", 2310144, 2326527));
    var (badSes, badDev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(badTab))).Concat(ReadGroup(LcHex(badEnt))).Concat(ReadGroup(Filler(0x11, 0x400))));
    await badSes.ReadPartitionTableAsync();
    int badStart = badDev.Writes.Count;
    Check((await DaFailure(badSes, () => badSes.ReadSeccfgConfigAsync())).Contains("magic mismatch"),
        "MTK B5.2: a device that hands back a block without the MMMM magic is answered with a refusal naming the bytes — the state is not guessed from noise");
    Check(SentFrames(badDev, badStart).Contains(ReadCmd),
        "MTK B5.2: the bad-magic refusal happens after the read the device was entitled to serve — the read went out, the interpretation stopped");
    badSes.Dispose();

    // ---- B5.4: what a live session hands the operator and the scatter flows --------------------------
    // A descriptor carrying the measured 16-byte CID region. The part number sits at +3..+8 of it — proven by the
    // working client printing the same reply twice ("EMMC ID: CJTD4R" beside "EMMC CID: 150100434a5444345204e992496597ad").
    // The bytes after it are this card's serial: they are set to a value that would be unmistakable in any string
    // that leaked them, which is exactly what the checks below prove does not happen.
    byte[] idDescriptor = Convert.FromHexString(DescriptorPayload(0xE8F800000ul));
    "CJTD4R"u8.CopyTo(idDescriptor.AsSpan(75));
    byte[] cardSerial = { 0xde, 0xad, 0xbe, 0xef };
    cardSerial.CopyTo(idDescriptor.AsSpan(82));
    string serialHex = Convert.ToHexString(cardSerial);
    string cidHex = Convert.ToHexString(idDescriptor.AsSpan(72, 16)).ToLowerInvariant();
    MtkStorageInfo idInfo = MtkStorageInfo.Parse(idDescriptor);
    Check(idInfo.EmmcPartNumber == "CJTD4R",
        "MTK B5.4: the eMMC part number decodes from the six ASCII bytes the working client prints as its EMMC ID");
    Check(idInfo.Describe.Contains("block 512") && !idInfo.Describe.Contains(serialHex) &&
          !idInfo.Describe.Contains(cidHex),
        "MTK B5.4: the geometry line the session speaks carries sizes only — no card serial, no CID bytes");
    Check(!idInfo.ToString().Contains(serialHex) && !idInfo.ToString().Contains(cidHex),
        "MTK B5.4: nothing on the decoded record renders the serial region, so no caller can log it by accident");
    Check(MtkStorageInfo.Parse(Convert.FromHexString(DescriptorPayload(0xE8F800000ul))).EmmcPartNumber == null,
        "MTK B5.4: a descriptor whose name field is blank yields no part number — the absence stays visible");
    byte[] junkName = Convert.FromHexString(DescriptorPayload(0xE8F800000ul));
    junkName[77] = 0x01;                      // one control byte inside the six name bytes
    Check(MtkStorageInfo.Parse(junkName).EmmcPartNumber == null,
        "MTK B5.4: a non-printable name field is not dressed up as a model name; it reports nothing");
    byte[] ufsLike = Convert.FromHexString(DescriptorPayload(0xE8F800000ul));
    "CJTD4R"u8.CopyTo(ufsLike.AsSpan(75));
    ufsLike[0] = 2;                           // a storage type this tool has never measured
    Check(MtkStorageInfo.Parse(ufsLike).EmmcPartNumber == null,
        "MTK B5.4: the part number is only read from the descriptor layout that was actually measured");

    var idLines = new List<string>();
    string idStorageReply = DaFrame(Convert.ToHexString(idDescriptor).ToLowerInvariant());
    var (idSes, idDev) = await BootedDa(idLines, new[] { Status, Status, idStorageReply, Status });
    int idDaStage = idDev.Writes.Count;            // the BROM prelude is not DA frames; only what follows is
    MtkStorageInfo queried = await idSes.QueryStorageAsync(default);
    Check(queried.EmmcPartNumber == "CJTD4R",
        "MTK B5.4: a live session decodes the name from the bytes its own device answered, not from a fixture");
    Check(SentFrames(idDev, idDaStage).Count(f => f == StorageCmd) == 1,
        "MTK B5.4: the storage query goes out exactly once — the name comes off the reply the device chose to send");
    Check(idLines.Any(l => l.Contains("DA storage: type 1")) &&
          !idLines.Any(l => l.Contains(serialHex, StringComparison.OrdinalIgnoreCase) || l.Contains(cidHex)),
        "MTK B5.4: the session log of the storage query shows the geometry and never the identity bytes");
    Check(idSes.RecipeChipset == "MT6765-TEST" && idSes.DeviceHwCode == 0x0766,
        "MTK B5.4: the CPU the status line prints is the recipe the boot proved against — a device answering a different HW code never reaches this line");
    idSes.Dispose();

    string bindA = MtkNativeBench.GptBinding(0x0766, 512, 62537072640ul, 43);
    Check(bindA == "HW 0x0766 · block 512 · user 62537072640 B · 43 partition(s)",
        "MTK B5.4: a table's binding is spelled out from the four facts the DA itself reported, so a reused table says whose it is");
    Check(bindA != MtkNativeBench.GptBinding(0x0766, 512, 62537072640ul, 44) &&
          bindA != MtkNativeBench.GptBinding(0x0768, 512, 62537072640ul, 43) &&
          bindA != MtkNativeBench.GptBinding(0x0766, 4096, 62537072640ul, 43),
        "MTK B5.4: a different partition count, HW code or block size gives a different binding — the guard is not decorative");
    Check(!bindA.Contains(serialHex, StringComparison.OrdinalIgnoreCase) && !bindA.Contains(cidHex),
        "MTK B5.4: the binding that lands in the disk cache is geometry, not identity");
    string status = MtkNativeBench.DeviceStatusLine("MT6765", 0x0766, "CJTD4R", "58.23 GB");
    Check(status == "MT6765 (HW 0766) · CJTD4R · 58.23 GB",
        "MTK B5.4: the device status line reads as the same three facts mtkclient's Read Info put there, from the native side");
    Check(MtkNativeBench.DeviceStatusLine("MT6765", 0x0766, null, "58 GB") == "MT6765 (HW 0766) · 58 GB" &&
          MtkNativeBench.DeviceStatusLine("", 0x0766, null, "") == "HW 0x0766",
        "MTK B5.4: a missing part number or size leaves no separator and no placeholder behind — the line never looks like a value it does not have");
    Check(!status.Contains(serialHex, StringComparison.OrdinalIgnoreCase) && !status.Contains(cidHex),
        "MTK B5.4: the screen line shows the model name of the chip, never its serial");
    Check(MtkNativeBench.BootProvenOnHardware(0x0766) && MtkNativeBench.BootProvenOnHardware(0x0959) &&
          !MtkNativeBench.BootProvenOnHardware(0x0768) && !MtkNativeBench.BootProvenOnHardware(0x6885),
        "MTK B5.4/B6: exactly the HW codes this tool booted on hardware are claimed as proven (MT6765 and, from the " +
        "2026-11-04 UFS run, MT6877); a new chipset still says it is a first run");
    Check(MtkDaExtent.SizeText(512 * 1024) == "512 KiB" && MtkDaExtent.SizeText(4UL * 1024 * 1024) == "4 MiB" &&
          MtkDaExtent.SizeText(255919652864UL) == "238 GiB" && MtkDaExtent.SizeText(512) == "512 B",
        "MTK B6: a 512 KiB partition reads as 512 KiB, not 0 MiB - the unit an operator acts on");
    Check(MtkNativeBench.ConciseNativeLine("[i] BROM wire 0: tx 0xFD") == null &&
          MtkNativeBench.ConciseNativeLine("    misc  LBA 8..1031  byte 0x8000 + 0x400000 (4 MiB)") == null &&
          MtkNativeBench.ConciseNativeLine("[*] BROM pacing: 0 ms before the greet") == null &&
          MtkNativeBench.ConciseNativeLine("[ ] BROM step 162: the ask never completed") == null,
        "MTK B6: the simple screen hides the per-byte wire, the partition rows, the pacing knobs and the optional-step detail");
    Check(MtkNativeBench.ConciseNativeLine("[+] BROM handshake complete: A0->5F 0A->F5 50->AF 05->FA \u2014 the face will now take a command.") == "[+] Handshake OK" &&
          MtkNativeBench.ConciseNativeLine("[+] MTK DA ready \u2014 MT6877 (HW 0959), boot replayed from recipe.") == "[+] DA ready" &&
          MtkNativeBench.ConciseNativeLine("[+] Live GPT: 77 partition(s), block 4096 B \u2014 resolved once, no further replug needed.") == "[+] Live GPT: 77 partition(s)" &&
          MtkNativeBench.ConciseNativeLine("[+] READ 0x1000 + 0x1000 \u2014 4096 byte(s) in 1 frame(s).") == "[+] READ 0x1000 + 0x1000",
        "MTK B6: the screen keeps the fact of each milestone and drops the explanation native-da.log already holds");

    // ---- B3.5: the live GPT, read once and cached ----------------------------------------------------
    byte[] liveHeader = GptHeader(2, 3, 128);
    var liveEntries = new byte[512];
    GptEntry("logo", 2987008, 2989055).CopyTo(liveEntries, 0);        // 2987008 * 512 = 0x5b280000, the measured extent
    GptEntry("tee1", 34, 2081).CopyTo(liveEntries, 128);
    GptEntry("flashinfo", 6, 33).CopyTo(liveEntries, 256);
    var gLines = new List<string>();
    var (gSes, gDev) = await BootedDa(gLines, StorageQuery()
        .Concat(LimitsQuery()).Concat(ReadGroup(LcHex(liveHeader)))
        .Concat(ReadGroup(LcHex(liveEntries))));
    int gptStart = gDev.Writes.Count;
    MtkDaPartitionTable table = await gSes.ReadPartitionTableAsync();
    Check(table.Extents.Count == 3 && table.BlockSize == 512,
        "MTK B3 GPT: the device's own table parses into its three partitions, in the DA's reported block size");
    CheckFrames(SentFrames(gDev, gptStart), "MTK B3 GPT: header at LBA 1, entries at the header's own EntryLba, one ack each, and the queries are not repeated between the two reads",
        PreFrame, StorageCmd, PreFrame, LimitsCmd, ReadCmd, ExtentStruct(512, 512), ZeroWord,
        ReadCmd, ExtentStruct(1024, 512), ZeroWord);
    Check(gLines.Any(l => l.Contains("Live GPT: 3 partition(s), block 512 B")), "MTK B3 GPT: the table is announced once, which is what a live session is for");
    MtkDaExtent logoExtent = await gSes.RequirePartitionAsync("logo", default);
    Check(logoExtent.ByteOffset == LogoAt && logoExtent.ByteLength == 0x100000 && logoExtent.SectorCount == 2048,
        "MTK B3 GPT: LBA 2987008 resolves to byte 0x5b280000 + 1 MiB — the exact extent the write capture used");
    Check(logoExtent.Describe.Contains("0x5b280000") && logoExtent.Describe.Contains("1 MiB"),
        "MTK B3 GPT: an extent prints in the units the DA addresses, so the operator can check it against a scatter file");
    Check(gSes.Partitions.TryFind("LOGO", out MtkDaExtent anyCase) && anyCase.FirstLba == 2987008,
        "MTK B3 GPT: partition names are matched the way the scatter path matches them, case-insensitively");
    int afterGpt = gDev.Writes.Count;
    Check((await DaFailure(gSes, () => gSes.RequirePartitionAsync("nope", default))).Contains("has no partition named 'nope'"),
        "MTK B3 GPT: a name the device does not have is refused by name, with the entry count that proves it was checked");
    Check((await DaFailure(gSes, () => gSes.ErasePartitionAsync("flashinfo", default)))
              .Contains("never erased through a live session"),
        "MTK B3 GPT: flashinfo is table/identity — the same list the scatter path refuses, enforced before the erase is framed");
    Check((await DaFailure(gSes, () => gSes.WritePartitionAsync("flashinfo", shortImage, null, true, default)))
              .Contains("never written through a live session"),
        "MTK B3 GPT: a name on the never-program list is refused for writes too, even when the device really has that partition");
    string tooBigImage = Path.Combine(dir, "too-big.bin");
    File.WriteAllBytes(tooBigImage, new byte[1024 * 1024 + 512]);
    Check((await DaFailure(gSes, () => gSes.WritePartitionAsync("logo", tooBigImage, null, true, default)))
              .Contains("would run into the next partition"),
        "MTK B3 WRITE: an image one block longer than the live partition is refused — an overrun silently rewrites its neighbour");
    Check(gDev.Writes.Count == afterGpt,
        "MTK B3 GPT: all four refusals were table- and size-based, so the cached table cost the port nothing");
    gSes.Dispose();

    // ---- P1.1: the live GPT saved as a folder, over the session that is already open ------------------
    // The header fixture below is a UEFI 2.10 §5.3.2 shape with its two CRC fields computed over its own bytes,
    // because the snapshot decoder reads them and reports on them. Everything else — the block size, the user area,
    // the entry LBA — is the same fixture the B3.5 table run above uses, so a save is measured against geometry this
    // harness has already proven the session can read.
    byte[] SnapshotHeader(byte[] entries, int entryCount, long backupLba, long myLba = 1, bool crcsOk = true)
    {
        var h = new byte[512];
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
        h[9] = 1;                                          // revision 1.0
        BitConverter.GetBytes(92).CopyTo(h, 12);           // this header claims 92 bytes of itself
        BitConverter.GetBytes(myLba).CopyTo(h, 24);
        BitConverter.GetBytes(backupLba).CopyTo(h, 32);
        BitConverter.GetBytes(34L).CopyTo(h, 40);          // first usable LBA — never the same number twice
        new Guid("11111111-2222-3333-4444-555555555555").ToByteArray().CopyTo(h, 56);
        BitConverter.GetBytes(2L).CopyTo(h, 72);           // entries at LBA 2, the one layout every MTK GPT read here uses
        BitConverter.GetBytes(entryCount).CopyTo(h, 80);
        BitConverter.GetBytes(128).CopyTo(h, 84);
        BitConverter.GetBytes(crcsOk ? MtkGptBackup.Crc32(entries, 0, entryCount * 128) : 0xDEADBEEF).CopyTo(h, 88);
        BitConverter.GetBytes(0u).CopyTo(h, 16);
        if (crcsOk)
        {
            var head = new byte[92];
            Array.Copy(h, head, 92);
            BitConverter.GetBytes(MtkGptBackup.Crc32(head, 0, 92)).CopyTo(h, 16);
        }
        else BitConverter.GetBytes(0xDEADBEEF).CopyTo(h, 16);
        return h;
    }
    var snapEntries = new byte[512];
    GptEntry("logo", 2987008, 2989055).CopyTo(snapEntries, 0);
    GptEntry("tee1", 34, 2081).CopyTo(snapEntries, 128);
    GptEntry("flashinfo", 6, 33).CopyTo(snapEntries, 256);
    // The user area the storage fixture reports is 0xE8F800000 B at 512-byte blocks, so its last LBA is 122142719.
    const long LastLbaOfFixture = 122142719;

    Check(MtkGptBackup.Crc32(Encoding.ASCII.GetBytes("123456789"), 0, 9) == 0xCBF43926u,
        "MTK P1.1: the CRC-32 this decoder uses is the standard one (the \"123456789\" check value), which is what the " +
        "GPT header and entry-array CRC fields are — a private variant would report every real disk as corrupt");

    MtkGptLayout snap = MtkGptBackup.Inspect(SnapshotHeader(snapEntries, 3, LastLbaOfFixture), snapEntries, 512, 0xE8F800000ul);
    Check(snap.BlockSize == 512 && snap.HeaderSize == 92 && snap.EntriesLba == 2 && snap.EntryCount == 3 &&
              snap.EntrySize == 128 && snap.ArraySectors == 1 && snap.BackupHeaderLba == LastLbaOfFixture &&
              snap.LastLba == LastLbaOfFixture && snap.BackupMatchesCapacity && snap.HeaderCrcOk && snap.ArrayCrcOk,
        "MTK P1.1: every number the snapshot carries comes out of the header's own fields — block, entry array, backup LBA and both CRC verdicts");
    Check(snap.SecondaryBytes == 1024 && snap.DiskGuid == "11111111-2222-3333-4444-555555555555",
        "MTK P1.1: the backup pass is one entry array plus one header block, sized from the array the header describes, " +
        "and the disk GUID is read as the lowercase form the folder's own name would use");
    Check(snap.Describe == "block 512 B, 3 × 128-byte entries at LBA 2 (1 sector(s)), backup header at LBA 122142719, header 92 B" &&
              snap.CrcText == "header CRC ok, entry array CRC ok",
        "MTK P1.1: the line the operator sees states the geometry in the units the DA addresses and both verdicts, with no number missing");
    string snapJson = snap.LayoutJson("HW 0x0766 · block 512 · user 62537072640 B · 3 partition(s)");
    Check(snapJson.Contains("\"sector_size\": 512") && snapJson.Contains("\"entry_array_sectors\": 1") &&
              snapJson.Contains("\"secondary_header_lba\": 122142719") && snapJson.Contains("\"header_crc_ok\": true") &&
              snapJson.Contains("\"secondary_lba_matches_capacity\": true") &&
              snapJson.Contains("HW 0x0766 · block 512 · user 62537072640 B"),
        "MTK P1.1: the layout file is the geometry plus the two verdicts and the binding the table came from — the record that says whose disk this is");
    Check(!snapJson.Contains("IMEI") && !snapJson.Contains("serial") && !snapJson.Contains("CID"),
        "MTK P1.1: nothing in the layout file is a device identity — it is written from the header and the DA's geometry reply, and no other field reaches it");

    MtkGptLayout notLast = MtkGptBackup.Inspect(SnapshotHeader(snapEntries, 3, LastLbaOfFixture - 3), snapEntries, 512, 0xE8F800000ul);
    Check(!notLast.BackupMatchesCapacity && notLast.BackupHeaderLba == LastLbaOfFixture - 3,
        "MTK P1.1: a backup LBA that is not the last LBA of the user area is recorded as a disagreement, not as a fault — it is the header's own number and this tool has never read a UFS GPT through its own session");
    MtkGptLayout crcBad = MtkGptBackup.Inspect(SnapshotHeader(snapEntries, 3, LastLbaOfFixture, crcsOk: false), snapEntries, 512, 0xE8F800000ul);
    Check(!crcBad.HeaderCrcOk && !crcBad.ArrayCrcOk &&
              crcBad.CrcText == "header CRC MISMATCH, entry array CRC MISMATCH",
        "MTK P1.1: both CRC fields are verified against the bytes the device sent and a mismatch is named, because a table that does not match its own checksums is a fact the operator has to see");
    string crcLine = MtkNativeBench.GptInspectLine(crcBad, 3);
    Check(crcLine.Contains("[*] GPT: ") && crcLine.Contains("MISMATCH") && crcLine.Contains("3 partition(s)") &&
              !crcLine.Contains("note:"),
        "MTK P1.1: the inspect line states the geometry, the partition count and both CRC verdicts in one line — a save is not allowed to look cleaner than the table it read");
    Check(MtkNativeBench.GptInspectLine(notLast, 3).Contains("CRC ok") &&
              MtkNativeBench.GptInspectLine(notLast, 3).EndsWith("note: its backup LBA is not the last LBA of the user area"),
        "MTK P1.1: a backup LBA that disagrees with the reported capacity is the only thing the line flags, and it is flagged as a note rather than a verdict — the header's own number, not a failure this tool can judge");

    string SnapInspectError(byte[] header, byte[] entries, uint block = 512)
    {
        try { MtkGptBackup.Inspect(header, entries, block, 0xE8F800000ul); return "(no refusal)"; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }
    Check(SnapInspectError(new byte[512], snapEntries).Contains("not a GPT header"),
        "MTK P1.1: a block with no \"EFI PART\" signature is refused as not-a-table rather than saved as one — the DA answering bytes is not proof they are a GPT");
    Check(SnapInspectError(SnapshotHeader(snapEntries, 3, LastLbaOfFixture), snapEntries, 4096).Contains("not one 4096-byte sector"),
        "MTK P1.1: a header shorter than the device's own block is refused — on a 4096-byte unit a 512-byte read is a partial header, and a partial header is not a geometry");
    byte[] oversized = SnapshotHeader(snapEntries, 3, LastLbaOfFixture);
    BitConverter.GetBytes(600).CopyTo(oversized, 12);
    Check(SnapInspectError(oversized, snapEntries).Contains("outside the 92..512"),
        "MTK P1.1: a header that claims more bytes than the block it was read as is refused — its CRC field would then cover bytes nobody read");
    byte[] zeroSize = SnapshotHeader(snapEntries, 3, LastLbaOfFixture);
    Array.Clear(zeroSize, 84, 4);
    Check(SnapInspectError(zeroSize, snapEntries).Contains("do not describe a readable entry array"),
        "MTK P1.1: an entry size of 0 is refused with the field values that prove it, rather than divided by later");
    Check(SnapInspectError(SnapshotHeader(snapEntries, 3, LastLbaOfFixture), new byte[256]).Contains("the read did not return the whole array"),
        "MTK P1.1: a short entry array is refused — half a table written out as a GPT backup is the file an operator restores from and blames afterwards");
    Check(SnapInspectError(SnapshotHeader(snapEntries, 3, LastLbaOfFixture, myLba: 0), snapEntries).Contains("calls itself LBA 0"),
        "MTK P1.1: the header at LBA 1 must call itself LBA 1 — this decoder knows one primary position, and the harness's own table fixture (which leaves the field zero) is refused by it");
    Check(SnapInspectError(SnapshotHeader(snapEntries, 3, 3), snapEntries).Contains("not past the primary array"),
        "MTK P1.1: a backup LBA inside or beside the primary array is refused — a read there would save the primary table twice and call one of them the backup");

    string snapFolder = Path.Combine(dir, "p1-snapshot");
    byte[] secondary = new byte[1024];
    for (int i = 0; i < secondary.Length; i++) secondary[i] = (byte)(0xA0 + i % 16);
    IReadOnlyList<string> savedFiles = MtkGptBackup.Write(snapFolder, snap,
        MtkNativeBench.GptBinding(0x0766, 512, 0xE8F800000ul, 3), SnapshotHeader(snapEntries, 3, LastLbaOfFixture),
        snapEntries, secondary);
    Check(savedFiles.SequenceEqual(new[] { "gpt-primary-header.bin", "gpt-primary-entries.bin", "gpt-secondary.bin",
              "gpt-layout.json", "gpt-sha256.txt" }),
        "MTK P1.1: the save is five files with the names the pre-native path already used — a backup folder an operator has seen before is one he can find again");
    Check(File.ReadAllBytes(Path.Combine(snapFolder, "gpt-primary-header.bin")).Length == 512 &&
              File.ReadAllBytes(Path.Combine(snapFolder, "gpt-primary-entries.bin")).SequenceEqual(snapEntries) &&
              File.ReadAllBytes(Path.Combine(snapFolder, "gpt-secondary.bin")).SequenceEqual(secondary),
        "MTK P1.1: the three binaries are the device's bytes verbatim — one header block, the array the header describes, and the backup pass exactly as read");
    Check(!Directory.EnumerateFiles(snapFolder).Any(f => f.EndsWith(".tmp")),
        "MTK P1.1: every file arrives through a temp name and a move, so a folder that completed has no half-written file in it");
    string crcFolder = Path.Combine(dir, "p1-crc-mismatch");
    MtkGptBackup.Write(crcFolder, crcBad, MtkNativeBench.GptBinding(0x0766, 512, 0xE8F800000ul, 3),
        SnapshotHeader(snapEntries, 3, LastLbaOfFixture, crcsOk: false), snapEntries, secondary);
    string crcJson = File.ReadAllText(Path.Combine(crcFolder, "gpt-layout.json"));
    Check(crcJson.Contains("\"header_crc_ok\": false") && crcJson.Contains("\"entry_array_crc_ok\": false"),
        "MTK P1.1: a checksum mismatch is saved and labelled, not refused — this tool has read one eMMC GPT's CRCs and no UFS one, so a snapshot the operator can inspect beats no snapshot at all");
    string[] shaLines = File.ReadAllLines(Path.Combine(snapFolder, "gpt-sha256.txt"));
    Check(shaLines.Length == 4 && shaLines.Select(l => l.Substring(66)).SequenceEqual(new[] { "gpt-primary-header.bin",
              "gpt-primary-entries.bin", "gpt-secondary.bin", "gpt-layout.json" }) &&
              shaLines.All(l => l.Length > 66 && l[64] == ' ' && l[65] == ' ' &&
                                 Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                                     Path.Combine(snapFolder, l.Substring(66))))).ToLowerInvariant() == l[..64]),
        "MTK P1.1: the sidecar is the conventional \"<sha256>  <name>\" list over the four saved files and every line re-verifies " +
        "against the file beside it — integrity the operator can check with any tool, not only with this one");
    Check(MtkNativeBench.GptSavedLine("/x/gpt", 5, 3, 2960) ==
              "[+] Native DA: GPT saved to /x/gpt — 5 file(s) for 3 partition(s), 2960 byte(s) on disk. " +
              "Everything in it was read off this device; nothing was written.",
        "MTK P1.1: the completion line states the folder, the file count, the partition count and the bytes on disk, and says in the same breath that the op was read-only");

    // The session half: one read on top of the table the boot already cost, and no program frame anywhere.
    byte[] okHeader = SnapshotHeader(snapEntries, 3, LastLbaOfFixture);
    var pLines = new List<string>();
    var (pSes, pDev) = await BootedDa(pLines, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(okHeader))).Concat(ReadGroup(LcHex(snapEntries))));
    await pSes.ReadPartitionTableAsync();
    int pStart = pDev.Writes.Count;
    string liveFolder = Path.Combine(dir, "p1-live");
    pDev.Append(ReadGroup(Filler(0x5A, 1024)));
    IReadOnlyList<string> liveFiles = await pSes.SaveGptAsync(liveFolder);
    CheckFrames(SentFrames(pDev, pStart),
        "MTK P1.1: on a session whose table is already cached, a GPT save is exactly one READ — the backup array and its " +
        "header, taken from the backup LBA the primary header itself names. The batched client needs a whole second boot for this",
        ReadCmd, ExtentStruct(0xE8F7FFC00ul, 1024), ZeroWord);
    Check(!SentFrames(pDev, pStart).Contains(WriteCmd) && !SentFrames(pDev, pStart).Contains(EraseCmd) &&
              !SentFrames(pDev, pStart).Contains(UploadCmd) && !SentFrames(pDev, pStart).Contains(CommitCmd),
        "MTK P1.1: a save frames no WRITE, ERASE, UPLOAD or COMMIT — the GPT names are on the never-program list precisely because a table is not something a live session overwrites");
    Check(liveFiles.Count == 5 &&
              File.ReadAllBytes(Path.Combine(liveFolder, "gpt-secondary.bin")).All(b => b == 0x5A) &&
              File.ReadAllBytes(Path.Combine(liveFolder, "gpt-primary-entries.bin")).SequenceEqual(snapEntries),
        "MTK P1.1: what lands on disk is what the DA sent — the backup pass byte for byte and the entry array the table read already cost");
    Check(pLines.Any(l => l.Contains("[*] GPT: block 512 B, 3 × 128-byte entries at LBA 2") && l.Contains("CRC ok")) &&
              pLines.Any(l => l.Contains("[+] Native DA: GPT saved to " + liveFolder) &&
                              l.Contains("5 file(s) for 3 partition(s)")),
        "MTK P1.1: the session logs the geometry it read and the folder it wrote, in that order, so a saved table can be checked against the device it came from");
    Check(pLines.Any(l => l.Contains("[+] READ 0xe8f7ffc00 + 0x400")),
        "MTK P1.1: the backup read is addressed at (backup LBA − array sectors) × block, the same arithmetic the primary read uses — no second source for the offset is invented");
    pSes.Dispose();

    var (noLocSes, noLocDev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(liveHeader))).Concat(ReadGroup(LcHex(liveEntries))));
    await noLocSes.ReadPartitionTableAsync();
    int noLocStart = noLocDev.Writes.Count;
    string refusedFolder = Path.Combine(dir, "p1-refused");
    Check((await DaFailure(noLocSes, () => noLocSes.SaveGptAsync(refusedFolder))).Contains("declares itself 0 bytes"),
        "MTK P1.1: the harness's own table fixture — a header whose size and self-position fields are zero — reads a partition table fine and is refused as a snapshot, which is the difference between the two decoders stated in one refusal");
    Check(noLocDev.Writes.Count == noLocStart && !Directory.Exists(refusedFolder),
        "MTK P1.1: a refused save costs the port not one frame and the disk not one file — the geometry is checked before the backup read is asked for and before any folder is created");
    noLocSes.Dispose();

    // ---- P1.2: several partitions in one session, and the manifest the existing restore reads back ------
    var (bkTab, bkEnt) = SeccfgTable(("nvram", 100, 100), ("logo", 2987008, 2987009));
    var (bkSes, bkDev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(bkTab))).Concat(ReadGroup(LcHex(bkEnt))));
    MtkDaPartitionTable bkTable = await bkSes.ReadPartitionTableAsync();

    Dictionary<string, long> bkLayout = MtkNativeBench.BackupLayout(bkTable, new[] { "NVRAM", "logo", "logo" },
        "Select partitions in Flash first.", out string bkRefusal);
    Check(bkRefusal == null && bkLayout.Count == 2 && bkLayout["nvram"] == 512 && bkLayout["logo"] == 1024,
        "MTK P1.2: the read list and the manifest layout are the same map — the byte lengths recorded are the extents " +
        "the reads are addressed by, names fold case-insensitively and a name ticked twice is read once");
    Check(MtkNativeBench.BackupLayout(bkTable, new[] { "nvram", "boot" }, "x", out string missingName) == null &&
              missingName.Contains("no partition named 'boot'") && missingName.Contains("2 entries read"),
        "MTK P1.2: a name the device's own GPT does not have stops the whole backup with the entry count that proves it was checked — a manifest listing a partition that was never read is a lie on disk");
    Check(MtkNativeBench.BackupLayout(bkTable, Array.Empty<string>(), "Select partitions in Flash first.", out string noneChosen) == null &&
              noneChosen == "Select partitions in Flash first." &&
              MtkNativeBench.BackupLayout(bkTable, new[] { "", "  " }, "none of the six NV partitions", out string blanks) == null &&
              blanks.Contains("none of the six NV partitions"),
        "MTK P1.2: an empty selection is the caller's own reason, and blank names are not silently counted as a selection — NV and Flash refuse with different words because they mean different things");

    string nativeBackupDir = Path.Combine(dir, "p1-native-backup");
    int bkStart = bkDev.Writes.Count;
    bkDev.Append(ReadGroup(Filler(0x11, 512)));
    await bkSes.ReadPartitionToFileAsync("nvram", Path.Combine(nativeBackupDir, "nvram.bin"), null, default);
    bkDev.Append(ReadGroup(Filler(0x22, 1024)));
    await bkSes.ReadPartitionToFileAsync("logo", Path.Combine(nativeBackupDir, "logo.bin"), null, default);
    CheckFrames(SentFrames(bkDev, bkStart),
        "MTK P1.2: two partitions are two reads and nothing else — the table, the geometry and the limits all stay cached, " +
        "which is the six-boots-per-NV-backup the batched client spends gone",
        ReadCmd, ExtentStruct(100 * 512, 512), ZeroWord,
        ReadCmd, ExtentStruct(LogoAt, 1024), ZeroWord);
    ShopServices.WriteBackup(nativeBackupDir, "MTK", bkLayout, bkLayout.Keys, "test");
    var bkManifest = ShopServices.VerifyBackup(nativeBackupDir);
    Check(bkManifest.Platform == "MTK" && bkManifest.Files.Count == 2 &&
              bkManifest.Files.Sum(f => f.Bytes) == 1536 && bkManifest.DeviceIdentity.StartsWith("Unknown") &&
              bkManifest.Files.All(f => f.File == f.Partition + ".bin" &&
                  File.ReadAllBytes(Path.Combine(nativeBackupDir, f.File)).All(b => b == (f.Partition == "nvram" ? 0x11 : 0x22))),
        "MTK P1.2: a native backup is the folder the existing restore already reads — same manifest schema, same sizes, verified by the same call, with the device's bytes in the files");
    Check(!SentFrames(bkDev, bkStart).Contains(WriteCmd) && !SentFrames(bkDev, bkStart).Contains(EraseCmd) &&
              !SentFrames(bkDev, bkStart).Contains(CommitCmd),
        "MTK P1.2: the batch backup is reads only — no WRITE, ERASE or COMMIT frame appears anywhere in a two-partition run");
    bkSes.Dispose();

    // ---- P1.3: the NV erase set and the NV restore plan, both decided before the port is touched ------
    // The risk this stage adds is not a new frame shape — ERASE and WRITE+verify are both hardware-proven — it is
    // a BATCH: six partitions are resolved, confirmed and then sent, so the decision that stops a half-done wipe
    // or a restore from a folder that belongs to another phone has to be made on the host, from the device's own
    // GPT and the backup's own manifest, with zero frames already spent.
    IReadOnlyList<MtkDaExtent> epSet = MtkNativeBench.NvErasePlan(bkTable, new[] { "NVRAM", "logo", "nvram" },
        "Nothing to erase.", out string epRefusal);
    Check(epRefusal == null && epSet.Count == 2 && epSet[0].Name == "nvram" && epSet[1].Name == "logo",
        "MTK P1.3 ERASE: the set is the device's own extents in the order asked — names fold case-insensitively and one asked twice is erased once, because the button carries six names and the phone decides six extents");
    Check(MtkNativeBench.NvErasePlan(bkTable, new[] { "nvram", "boot" }, "x", out string epMissing) == null &&
              epMissing.Contains("no partition named 'boot'") && epMissing.Contains("2 entries read") &&
              epMissing.Contains("Nothing was sent to the device"),
        "MTK P1.3 ERASE: a name this device's own GPT does not report refuses the whole set with the entry count that proves it was checked — five of six gone and a throw at the sixth is the failure an all-or-nothing plan cannot have");
    var guardedTable = new MtkDaPartitionTable(512, new[]
    {
        new MtkDaExtent("nvram", 100, 100, 512),
        new MtkDaExtent("gpt", 1, 63, 512),
        new MtkDaExtent("seccfg", 2310144, 2326527, 512),
    }, null, null);
    Check(MtkNativeBench.NvErasePlan(guardedTable, new[] { "nvram", "gpt" }, "x", out string epTable) == null &&
              epTable.Contains("partition table or the chip identity") &&
              epTable.Contains("whole set is refused before the first frame"),
        "MTK P1.3 ERASE: a set that carries the partition table is refused by the same never-program gate the single-partition erase obeys — batching does not inherit a wider permission");
    Check(MtkNativeBench.NvErasePlan(guardedTable, new[] { "nvram", "seccfg" }, "x", out string epSeccfg) == null &&
              epSeccfg.Contains("program payload has never been measured"),
        "MTK P1.3 ERASE: seccfg inside an erase set is refused too — wiping the lock policy is not part of an NV job, and a batch is exactly where it could slip through");
    Check(MtkNativeBench.NvErasePlan(bkTable, new[] { "", "  " }, "Nothing to erase.", out string epBlank) == null &&
              epBlank == "Nothing to erase.",
        "MTK P1.3 ERASE: blank names are not counted as a set, and an empty set is the caller's own reason rather than a silent success");

    var (p3Tab, p3Ent) = SeccfgTable(("nvram", 100, 100), ("logo", 2987008, 2987009));
    var (p3Ses, p3Dev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(p3Tab))).Concat(ReadGroup(LcHex(p3Ent))));
    MtkDaPartitionTable p3Table = await p3Ses.ReadPartitionTableAsync();
    int p3RefusedStart = p3Dev.Writes.Count;
    Check(MtkNativeBench.NvErasePlan(p3Table, new[] { "nvram", "boot" }, "x", out _) == null &&
              p3Dev.Writes.Count == p3RefusedStart,
        "MTK P1.3 ERASE: with the table cached, a refused set costs the port not one frame — the refusal is a decision, not a transaction");
    IReadOnlyList<MtkDaExtent> p3Set = MtkNativeBench.NvErasePlan(p3Table, new[] { "nvram", "logo" }, "x", out _);
    string NvramStruct = ExtentStruct(100 * 512, 512);
    string P3LogoStruct = ExtentStruct(LogoAt, 1024);   // this fixture's logo is two LBAs, so 1024 byte(s) — not the 0x1000 the B3 fixture uses
    int p3Start = p3Dev.Writes.Count;
    p3Dev.Append(new[] { Status, Status, DaFrame("04000440"), DaFrame("64000000"), DaFrame("05000440") }
        .Concat(new[] { Status, Status, DaFrame("04000440"), DaFrame("64000000"), DaFrame("05000440") }));
    foreach (MtkDaExtent extent in p3Set)
        await p3Ses.ErasePartitionAsync(extent.Name, default);
    CheckFrames(SentFrames(p3Dev, p3Start),
        "MTK P1.3 ERASE: the set is two bare erase groups and nothing else — no second table read, no geometry, no limits, no preamble",
        EraseCmd, NvramStruct, ZeroWord, EraseCmd, P3LogoStruct, ZeroWord);
    p3Ses.Dispose();

    // The backup folder the restore is given: two real files and the manifest the backup path itself writes.
    string nvSrc = Path.Combine(dir, "p13-nv-backup");
    Directory.CreateDirectory(nvSrc);
    File.WriteAllBytes(Path.Combine(nvSrc, "nvram.bin"), Enumerable.Repeat((byte)0x01, 512).ToArray());
    File.WriteAllBytes(Path.Combine(nvSrc, "logo.bin"), Enumerable.Repeat((byte)0x02, 1024).ToArray());
    Dictionary<string, long> nvSrcLayout = MtkNativeBench.BackupLayout(bkTable, new[] { "nvram", "logo" }, "x", out _);
    ShopServices.WriteBackup(nvSrc, "MTK", nvSrcLayout, nvSrcLayout.Keys, "test");
    var nvManifest = ShopServices.VerifyBackup(nvSrc);
    IReadOnlyList<MtkNativeBench.NvRestoreItem> rp = MtkNativeBench.NvRestorePlan(bkTable, nvSrc, nvManifest.Files,
        new[] { "nvram", "nvcfg", "LOGO" }, out string rpRefusal);
    Check(rpRefusal == null && rp.Count == 2 && rp[0].Extent.Name == "nvram" && rp[1].Extent.Name == "logo" &&
              rp[0].ImagePath == Path.Combine(nvSrc, "nvram.bin") &&
              rp[0].Sha256 == nvManifest.Files.Single(f => f.Partition == "nvram").Sha256,
        "MTK P1.3 RESTORE: the plan is the manifest's own entries matched to the device's own extents, in the order the six are asked, with the image path contained by the backup folder and the hash travelling with it");
    Check(MtkNativeBench.MissingFromPlan(new[] { "nvram", "nvcfg", "logo" }, rp.Select(i => i.Extent.Name)) == "nvcfg",
        "MTK P1.3 RESTORE: a partition the backup does not carry is named as not restored — a five-partition folder is legal, an unspoken one is not");
    var shrunkTable = new MtkDaPartitionTable(512, new[]
    { new MtkDaExtent("nvram", 100, 100, 512), new MtkDaExtent("logo", 2987008, 2987008, 512) }, null, null);
    Check(MtkNativeBench.NvRestorePlan(shrunkTable, nvSrc, nvManifest.Files, new[] { "nvram", "logo" },
        out string rpSize) == null &&
              rpSize.Contains("logo is 1024 byte(s) in this backup and 512 byte(s) on this device") &&
              rpSize.Contains("nothing was written"),
        "MTK P1.3 RESTORE: exact byte equality per extent is the gate, and it names both numbers — the size the backup claims and the size the device reports, because a partial overlap would silently leave old NV bytes behind");
    var oneNameTable = new MtkDaPartitionTable(512,
        new[] { new MtkDaExtent("nvram", 100, 100, 512) }, null, null);
    Check(MtkNativeBench.NvRestorePlan(oneNameTable, nvSrc, nvManifest.Files, new[] { "nvram", "logo" },
        out string rpName) == null &&
              rpName.Contains("This backup carries logo (1024 byte(s))") && rpName.Contains("1 entries read"),
        "MTK P1.3 RESTORE: a backup that names a partition this phone's GPT does not have is another phone's layout, and the whole set is refused before the first write");
    Check(MtkNativeBench.NvRestorePlan(bkTable, nvSrc, nvManifest.Files, new[] { "nvdata", "proinfo" },
        out string rpNone) == null &&
              rpNone.Contains("records none of the NV partitions") && rpNone.Contains("2 file(s) recorded"),
        "MTK P1.3 RESTORE: a folder with no NV partition in it is refused with what it does hold, and points at the route that can still serve raw dumps");
    var otherNames = new[]
    {
        new ShopServices.BackupFile("nvram", "nvram.bin", 512, "AA"),
        new ShopServices.BackupFile("logo", "logo.bin", 1024, "BB"),
        new ShopServices.BackupFile("boot", "boot.bin", 2048, "CC"),
    };
    IReadOnlyList<MtkNativeBench.NvRestoreItem> rpMixed = MtkNativeBench.NvRestorePlan(bkTable, nvSrc, otherNames,
        new[] { "nvram", "nvdata", "nvcfg", "proinfo", "protect1", "protect2" }, out string rpMixedRefusal);
    Check(rpMixedRefusal == null && rpMixed.Count == 1 && rpMixed[0].Extent.Name == "nvram",
        "MTK P1.3 RESTORE: entries outside the six are ignored, not refused — which is what lets a 43-partition mtkclient manifest and a six-name native manifest both serve the same NV restore");

    string rpConfirm = MtkNativeBench.NvRestoreConfirmText(rp, nvSrc);
    Check(rpConfirm.Contains("Restore 2 partition(s)") && rpConfirm.Contains(nvSrc) &&
              rpConfirm.Contains("hash-checked against the backup manifest") &&
              rpConfirm.Contains("read back and compared") && rpConfirm.Contains("rebooted once"),
        "MTK P1.3 RESTORE: the one dialog says what was already proved (hash-checked, extents matched) and what will be done (raw write, full read-back, one reboot) — each of those is a fact about code paths that exist, not a promise");
    Check(MtkNativeBench.NvRestoreConfirmText(new[] { rp[0] }, nvSrc).Contains("Restore 1 partition(s)"),
        "MTK P1.3 RESTORE: the dialog counts what it lists, so a partial restore is not presented as a whole one");

    var (rpTab, rpEnt) = SeccfgTable(("nvram", 100, 100), ("logo", 2987008, 2987009));
    var rpLines = new List<string>();
    var (rpSes, rpDev) = await BootedDa(rpLines, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(LcHex(rpTab))).Concat(ReadGroup(LcHex(rpEnt)))
        .Concat(Enumerable.Repeat(Status, 7)).Concat(ReadGroup(Filler(0x01, 512)))
        .Concat(Enumerable.Repeat(Status, 7)).Concat(ReadGroup(Filler(0x02, 1024))));
    await rpSes.ReadPartitionTableAsync();
    int rpStart = rpDev.Writes.Count;
    foreach (MtkNativeBench.NvRestoreItem item in rp)
        await rpSes.WritePartitionAsync(item.Extent.Name, item.ImagePath, null, verify: true, default);
    CheckFrames(SentFrames(rpDev, rpStart),
        "MTK P1.3 RESTORE: the planned set goes out as one WRITE + read-back per partition — the bytes are the backup's own files, and the cached table, geometry and limits mean the cable carries nothing else",
        WriteCmd, NvramStruct, ZeroWord, "00020000", Filler(0x01, 512), PreFrame, CommitCmd,
        ReadCmd, NvramStruct, ZeroWord,
        WriteCmd, P3LogoStruct, ZeroWord, "00080000", Filler(0x02, 1024), PreFrame, CommitCmd,
        ReadCmd, P3LogoStruct, ZeroWord);
    Check(rpLines.Count(l => l.Contains("VERIFY OK")) == 2,
        "MTK P1.3 RESTORE: every partition in the set is read back and compared on its own — one verified write is not a verified set");
    rpSes.Dispose();

    // ---- P1.4: the scatter this tool writes beside its own firmware dump ------------------------------
    static bool Throws(Action build)
    {
        try { build(); } catch (InvalidDataException) { return true; }
        return false;
    }

    // The bundled wfscatter's own reader, reduced to its grammar: a stripped line starting with
    // "- partition_index" opens a block; any other line splits on the FIRST colon and the key is kept VERBATIM
    // (it is not trimmed of a leading dash). That is the whole difference between a package the client reads and
    // "Scatter has no partitions." — the 2026-11-05 flash failed on exactly that, so the writer is checked with
    // the client's grammar, not only with our own lenient parser.
    static List<Dictionary<string, string>> WfScatterRead(string text)
    {
        var parts = new List<Dictionary<string, string>>();
        Dictionary<string, string>? cur = null;
        foreach (string line in text.Split('\n'))
        {
            string s = line.Trim();
            if (s.StartsWith("- partition_index", StringComparison.Ordinal))
            {
                if (cur != null) parts.Add(cur);
                cur = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }
            if (cur == null || !s.Contains(':') || s.StartsWith("#", StringComparison.Ordinal)) continue;
            int colon = s.IndexOf(':');
            cur[s[..colon].Trim()] = s[(colon + 1)..].Trim();
        }
        if (cur != null) parts.Add(cur);
        return parts;
    }

    // The risk here is a file the FLASH tab reads back as a WRITE LIST: an offset printed as an LBA, a name that
    // breaks the key/value grammar, or a table area marked downloadable is a package that programs the wrong bytes.
    // So every check is the round trip — build rows from the device's own extents, render, then re-read with the
    // parser the FLASH tab uses and see which rows THAT calls flashable.
    var swTable = new MtkDaPartitionTable(512, new[]
    {
        new MtkDaExtent("nvram", 0x297400, 0x2b73ff, 512),
        new MtkDaExtent("boot", 0x2b9400, 0x2d93ff, 512),
        new MtkDaExtent("seccfg", 2310144, 2326527, 512),
        new MtkDaExtent("otp", 0x745a401, 0x746fc00, 512),
    }, null, null);
    MtkStorageInfo swEmmc = MtkStorageInfo.Parse(Convert.FromHexString(DescriptorPayload(0xE8F800000ul)));
    MtkStorageInfo swUfs = MtkStorageInfo.ParseUfs(Convert.FromHexString(UfsGeometryPayload()));
    IReadOnlyList<ScatterRow> swRows = MtkNativeBench.ScatterRows(swTable,
        new[] { "nvram", "boot", "seccfg", "otp" }, swEmmc, out string swRefusal);
    Check(swRefusal == null && swRows.Count == 4 && swRows.All(r => r.Region == "EMMC_USER"),
        "MTK P1.4 SCATTER: one row per dumped partition, all in the region a GPT user area lives in");
    Check(swRows.Single(r => r.Name == "boot") is { FileName: "boot.bin", ByteOffset: 0x57280000, ByteLength: 0x4000000 },
        "MTK P1.4 SCATTER: the extent is carried in BYTES (LBA 0x2b9400 × 512 = 0x57280000, 64 MiB) and named boot.bin — " +
        "the file the backup loop actually wrote, and the unit a scatter addresses in");
    Check(swRows.Single(r => r.Name == "boot").IsDownload && swRows.Single(r => r.Name == "nvram").IsDownload &&
          !swRows.Single(r => r.Name == "otp").IsDownload && !swRows.Single(r => r.Name == "seccfg").IsDownload,
        "MTK P1.4 SCATTER: the chip identity and the lock policy are dumped but marked is_download false — the same " +
        "gate the single-partition write and the erase set obey, so a package cannot offer what a session refuses");
    Check(MtkNativeBench.ScatterRows(swTable, new[] { "nvram", "tee1" }, swEmmc, out string swMissing) == null &&
          swMissing.Contains("tee1") && swMissing.Contains("no scatter is written"),
        "MTK P1.4 SCATTER: a name the live GPT does not report stops the file instead of inventing an offset for it");
    Check(MtkNativeBench.ScatterRows(swTable, new[] { "nvram" }, swUfs, out string swUfsRefusal) == null &&
          swUfsRefusal.Contains("UFS") && swUfsRefusal.Contains("no scatter is written"),
        "MTK P1.4 SCATTER: a UFS device gets its images and manifest but no scatter — the region word of a UFS " +
        "package has never been read off one here, and an invented region is unmeasured bytes in a write list");
    Check(MtkNativeBench.ScatterRows(swTable, Array.Empty<string>(), swEmmc, out string swEmpty) == null &&
          swEmpty.Contains("nothing was dumped"),
        "MTK P1.4 SCATTER: an empty dump does not produce an empty package file");
    Check(ScatterWriter.DefaultFileName("MT6765") == "MT6765_scatter.txt" &&
          ScatterWriter.DefaultFileName("MT 67/65!").EndsWith("scatter.txt", StringComparison.Ordinal),
        "MTK P1.4 SCATTER: the package is named so the FLASH tab's own *scatter*.txt filter shows it, and a messy " +
        "chipset string cannot make an unusable file name");

    // wfscatter rebuilds the GPT from the rows it parsed and drops every live partition that has no row. A dump of
    // one partition must therefore still describe the whole table, or "flash this dump back" repartitions the phone.
    IReadOnlyList<ScatterRow> swPartial = MtkNativeBench.ScatterRows(swTable, new[] { "boot" }, swEmmc,
        out string swPartialRefusal);
    Check(swPartialRefusal == null && swPartial.Count == swTable.Extents.Count &&
          swPartial.Single(r => r.Name == "boot") is { IsDownload: true, IsReserved: false, FileName: "boot.bin" } &&
          swPartial.Where(r => r.Name != "boot")
                   .All(r => r.IsReserved && !r.IsDownload && r.FileName == "NONE" && r.ByteLength > 0),
        "MTK P1.4 SCATTER: a partial dump still lists every live extent — the dumped one as an image, the rest as " +
        "reserved rows carrying their own extent, so the rebuilt GPT keeps all four partitions");
    string swPartialText = ScatterWriter.Build("MT6765", "PMK-device-dump", "EMMC", swPartial);
    Check(swPartialText.Contains("  partition_name: boot\r\n", StringComparison.Ordinal) &&
          swPartialText.Contains("  is_reserved: true\r\n  operation_type: RESERVED\r\n", StringComparison.Ordinal) &&
          swPartialText.Contains("  file_name: NONE\r\n", StringComparison.Ordinal),
        "MTK P1.4 SCATTER: a reserved row is written in the keys wfscatter reads for it (is_reserved, " +
        "operation_type RESERVED, no image) — that is what makes the client re-read the extent from the live table");

    string swDir = Path.Combine(Path.GetTempPath(), "pmk-scatter-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(swDir);
    try
    {
        foreach (ScatterRow r in swRows) File.WriteAllBytes(Path.Combine(swDir, r.FileName), new byte[4096]);
        string swText = ScatterWriter.Build("MT6765", "PMK-device-dump", "EMMC", swRows);
        string swPath = Path.Combine(swDir, ScatterWriter.DefaultFileName("MT6765"));
        File.WriteAllText(swPath, swText);
        FirmwarePackage swPkg = ScatterParser.Load(swPath);
        Check(swText.StartsWith(ScatterWriter.Header + "\r\n- general: MTK_PLATFORM_CFG", StringComparison.Ordinal) &&
              swPkg.FormatSupported && swPkg.Platform == "MT6765" && swPkg.Entries.Count == 4,
        "MTK P1.4 SCATTER: what the writer emits, the reader accepts — four rows, platform MT6765");
        List<Dictionary<string, string>> wfParts = WfScatterRead(swText);
        Check(wfParts.Count == 4 &&
              wfParts.All(p => p.ContainsKey("partition_name") && p.ContainsKey("partition_size") &&
                               p.ContainsKey("region") && p["region"] == "EMMC_USER") &&
              wfParts.Single(p => p["partition_name"] == "boot")["physical_start_addr"] == "0x57280000",
        "MTK P1.4 SCATTER: the bundled wfscatter's own grammar finds every row at the same byte offset — it keeps " +
        "the key verbatim, so a list-style '- partition_name' line parses as zero partitions and the flash writes " +
        "nothing (2026-11-05 hardware run)");
        Check(!swText.Contains("  - partition_name", StringComparison.Ordinal) &&
              !swText.Contains("  - physical_start_addr", StringComparison.Ordinal),
        "MTK P1.4 SCATTER: no entry key carries a list dash — the one byte that made the client's parser see an " +
        "empty scatter");
        Check(swPkg.Find("boot") is { LinearStart: 0x57280000, Size: 0x4000000, NumbersValid: true, Region: StorageRegion.EmmcUser },
        "MTK P1.4 SCATTER: the offset and size survive the hex round trip (the failure this check exists for is an " +
        "LBA written where a byte address belongs — every row would then program a different partition)");
        Check(swPkg.Find("boot") is { PhysicalStart: 0x57280000 },
            "MTK P1.4 SCATTER: physical_start_addr carries the same byte offset as the linear one — the reader treats " +
            "a number without 0x as decimal, so a missing prefix would silently aim the row at a different byte");
        Check(swPkg.FlashableEntries.Count() == 2 &&
              swPkg.Find("boot") is { Selectable: true, ImageExists: true } &&
              swPkg.Find("nvram") is { Selectable: true } &&
              swPkg.Find("otp") is { Selectable: false } && swPkg.Find("seccfg") is { Selectable: false },
        "MTK P1.4 SCATTER: re-parsed, exactly the dumped-and-allowed rows are the ones the FLASH grid can select");
        Check(!swPkg.Issues.Any(i => i.Severity == IssueSeverity.Error) &&
              !swPkg.Issues.Any(i => i.Code == "address-overlap") && !swPkg.HasBlockingErrors,
        "MTK P1.4 SCATTER: the generated package carries no error and no overlap — a device's own extents cannot " +
        "overlap, and if the reader ever says otherwise the dump was not the table it claimed");
        Check(FirmwarePreflight.Run(swPkg, new[] { "boot", "nvram" }, null,
                  new BackendCapabilities { Name = "mtkclient", SupportsFlash = true })
              .Checks.Single(c => c.Name == "Scatter structure").State == VerifyState.VerifiedCompatible,
        "MTK P1.4 SCATTER: the preflight the flash button runs accepts a self-made package");
        Check(FirmwarePreflight.Run(swPkg, new[] { "otp" }, null,
                  new BackendCapabilities { Name = "mtkclient", SupportsFlash = true })
              .Checks.Single(c => c.Name == "Selected image files").State == VerifyState.Mismatch,
        "MTK P1.4 SCATTER: selecting the never-program row anyway is blocked by preflight — the reader never maps a " +
        "row it excludes, so its image counts as missing");
        string swPartialPath = Path.Combine(swDir, "MT6765_partial_scatter.txt");
        File.WriteAllText(swPartialPath, swPartialText);
        FirmwarePackage swPartialPkg = ScatterParser.Load(swPartialPath);
        Check(swPartialPkg.FormatSupported && swPartialPkg.Entries.Count == swPartial.Count &&
              swPartialPkg.FlashableEntries.Count() == 1 &&
              swPartialPkg.Find("boot") is { Selectable: true, ImageExists: true } &&
              swPartialPkg.Find("nvram") is { Selectable: false, IsDownload: false } &&
              !swPartialPkg.Issues.Any(i => i.Code == "missing-image"),
        "MTK P1.4 SCATTER: the FLASH tab reads the partial package as one flashable row plus reserved rows, with no " +
        "missing-image noise for images the dump never claimed to have");
    }
    finally { try { Directory.Delete(swDir, true); } catch { } }

    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "EMMC",
              new[] { new ScatterRow("bad:name", "x.bin", 0, 0x1000, "EMMC_USER", true) })),
        "MTK P1.4 SCATTER: a partition name carrying a colon is refused — the reader splits on the first colon, so " +
        "one line would become two keys");
    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "EMMC",
              new[] { new ScatterRow("a\r\n- partition_index: SYS999", "x.bin", 0, 0x1000, "EMMC_USER", true) })),
        "MTK P1.4 SCATTER: a name carrying a newline cannot inject a row into the package it is written into");
    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "EMMC",
              new[] { new ScatterRow("boot", "..\\escape.bin", 0, 0x1000, "EMMC_USER", true) })),
        "MTK P1.4 SCATTER: a file name that could leave the package folder is refused at build, before a reader has to block it");
    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "EMMC", new[]
              {
                  new ScatterRow("boot", "boot.bin", 0, 0x1000, "EMMC_USER", true),
                  new ScatterRow("BOOT", "boot2.bin", 0x1000, 0x1000, "EMMC_USER", true),
              })),
        "MTK P1.4 SCATTER: a duplicated partition name is refused — the reader drops its second row, so the image " +
        "pointed at there would silently never be written");
    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "EMMC", new[]
              {
                  new ScatterRow("boot", "boot.bin", 0, 0x2000, "EMMC_USER", true),
                  new ScatterRow("nvram", "nvram.bin", 0x1000, 0x1000, "EMMC_USER", true),
              })),
        "MTK P1.4 SCATTER: two rows sharing bytes are refused instead of becoming a write list with an order " +
        "dependency nobody chose");
    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "EMMC",
              new[] { new ScatterRow("boot", "boot.bin", 0, 0, "EMMC_USER", true) })),
        "MTK P1.4 SCATTER: a zero-length extent has no place in a scatter (the reader would mark the row invalid)");
    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "EMMC",
              new[] { new ScatterRow("boot", "boot.bin", 0, 0x1000, "UFS_LU0", true) })),
        "MTK P1.4 SCATTER: a region our own reader does not know is refused at build — the package would be " +
        "unselectable on the very tab it is written for");
    Check(Throws(() => ScatterWriter.Build("", "p", "EMMC",
              new[] { new ScatterRow("boot", "boot.bin", 0, 0x1000, "EMMC_USER", true) })),
        "MTK P1.4 SCATTER: no chipset, no package — an empty platform makes the reader reject the whole file as an " +
        "unsupported scatter");
    Check(Throws(() => ScatterWriter.Build("MT6765", "p", "UFS",
              new[] { new ScatterRow("boot", "boot.bin", 0, 0x1000, "EMMC_USER", true) })) &&
          Throws(() => ScatterWriter.Build("MT6765", "p", "HW_STORAGE_EMMC",
              new[] { new ScatterRow("boot", "boot.bin", 0, 0x1000, "EMMC_USER", true) })),
        "MTK P1.4 SCATTER: the general storage word is the reader's own EMMC — a UFS or an entry-level word is " +
        "refused here, because the writer can only describe the one storage it measured");

    // ---- B3.6: the guards that must fire before the extent reaches the device ------------------------
    var (oSes, oDev) = await BootedDa(null, StorageQuery());
    int oStart = oDev.Writes.Count;
    Check((await DaFailure(oSes, () => oSes.WriteExtentAsync(LogoAt, new byte[1000])))
              .Contains("Length 0x3e8 is not a multiple of the device block size 512"),
        "MTK B3 WRITE: an extent that is not whole blocks is refused instead of being silently rounded by the device");
    Check(!SentFrames(oDev, oStart).Contains(WriteCmd), "MTK B3 WRITE: the refusal happens on the extent check, so no WRITE command was ever framed");
    oSes.Dispose();
    var (aSes, aDev) = await BootedDa(null, StorageQuery());
    int aStart = aDev.Writes.Count;
    Check((await DaFailure(aSes, () => aSes.ReadExtentAsync(LogoAt + 1, LogoLen)))
              .Contains("is not a multiple of the device block size 512"),
        "MTK B3 READ: an offset inside a block is refused — the DA addresses blocks, and a rounded read would dump the wrong partition");
    Check(!SentFrames(aDev, aStart).Contains(ReadCmd), "MTK B3 READ: the alignment refusal sent no READ command, only the descriptor query it needed to check");
    aSes.Dispose();
    var (cSes, cDev) = await BootedDa(null, StorageQuery());
    Check((await DaFailure(cSes, () => cSes.ReadExtentAsync(0xE8F800000ul - 512, 1024)))
              .Contains("runs past the end of the user area"),
        "MTK B3: an extent that crosses the user area the DA reported is refused — past it are hardware partitions");
    cSes.Dispose();
    var (zSes, zDev) = await BootedDa(null, Array.Empty<string>());
    Check((await DaFailure(zSes, () => zSes.ReadExtentAsync(LogoAt, 0))).Contains("zero-length extent"),
        "MTK B3: a zero-length extent was never observed, so it is refused instead of opening a transfer that reads forever");
    Check((await DaFailure(zSes, () => zSes.ReadExtentAsync(0, 128 * 1024 * 1024))).Contains("does not belong in memory"),
        "MTK B3: a 128 MiB read asks for a file, not a buffer — a partition dump must not become an allocation");
    Check(SentFrames(zDev, 29).Count == 0, "MTK B3: both refusals happened before a single frame went to the device");
    zSes.Dispose();
    Check((await DaFailure(zSes, () => zSes.ReadExtentAsync(LogoAt, LogoLen))).Contains("The DA is not ready"),
        "MTK B3: a disposed session sends nothing, whatever the UI asks it to do");

    // ---- B3.7: a DA that disagrees mid-extent --------------------------------------------------------
    var (_, x1Frames) = await OperationRefusal(
        "MTK B3 READ: a frame bigger than the transfer size the DA itself advertised is desynchronisation, and is stopped before it is acked",
        StorageQuery().Concat(LimitsQuery()).Concat(ReadGroup(Filler(0x77, 2048))),
        s => s.ReadExtentAsync(LogoAt, LogoLen), "above the DA's own advertised transfer size");
    Check(!x1Frames.Contains(ZeroWord), "MTK B3 READ: the oversized frame is refused before its ack, so the device is not told to continue");
    var (_, x2Frames) = await OperationRefusal(
        "MTK B3 READ: the device over-delivering past the extent is refused, not appended to a dump that is already complete",
        StorageQuery().Concat(LimitsQuery()).Concat(ReadGroup(Filler(0x77, 8192))),
        s => s.ReadExtentAsync(LogoAt, LogoLen), "only 4096 remain of the requested extent");
    Check(!x2Frames.Contains(ZeroWord), "MTK B3 READ: an over-long frame is refused before its ack too");
    await OperationRefusal(
        "MTK B3 READ: an empty frame with bytes still outstanding is a refusal, not a zero-filled hole in a partition dump",
        StorageQuery().Concat(LimitsQuery()).Concat(ReadGroup("")),
        s => s.ReadExtentAsync(LogoAt, LogoLen), "carries no data");
    await OperationRefusal(
        "MTK B3 READ: a DA that accepts the command and then says nothing is a timeout, not an infinite wait",
        StorageQuery().Concat(LimitsQuery()).Concat(new[] { Status, Status }),
        s => s.ReadExtentAsync(LogoAt, LogoLen), "timed out");
    var (_, x5Frames) = await OperationRefusal(
        "MTK B3 READ: a non-zero status on the READ command stops the operation immediately",
        StorageQuery().Concat(LimitsQuery()).Concat(new[] { DaFrame("01000000"), Status, Status }),
        s => s.ReadExtentAsync(LogoAt, LogoLen), "the DA refused (status");
    Check(x5Frames.Count == 5 && x5Frames[4] == ReadCmd,
        "MTK B3 READ: a refused READ stops right after the command frame — the 56-byte extent never goes out");
    var (_, x6Frames) = await OperationRefusal(
        "MTK B3 ERASE: a refused erase stops on the command status, before the extent struct is ever framed",
        StorageQuery().Concat(new[] { DaFrame("01000000") }),
        s => s.EraseExtentAsync(LogoAt, LogoLen), "the DA refused");
    Check(x6Frames.Count == 3 && x6Frames[2] == EraseCmd && !x6Frames.Contains(LogoStruct),
        "MTK B3 ERASE: nothing that could wipe a partition was framed after the refusal");

    // ---- B3.8: one port, one operation ---------------------------------------------------------------
    var (mSes, mDev) = await BootedDa(null, StorageQuery().Concat(LimitsQuery())
        .Concat(ReadGroup(Filler(0xA0, 1024), Filler(0xA1, 1024), Filler(0xA2, 1024), Filler(0xA3, 1024))));
    mDev.HoldBack = 1;   // the status after the last ack has not arrived, so this read is really mid-flight
    var firstOp = mSes.ReadExtentAsync(LogoAt, LogoLen);
    bool busyRefused = false;
    string busyMessage = "";
    try { await mSes.ReadExtentAsync(LogoAt, LogoLen); }
    catch (InvalidOperationException ex) { busyRefused = true; busyMessage = ex.Message; }
    Check(busyRefused && busyMessage.Contains("read 0x5b280000"),
        "MTK B3: a second extent while a read is in flight is refused by name — two writers on one COM port would interleave frames into both");
    Check(SentFrames(mDev, 29).Count(f => f == ReadCmd) == 1,
        "MTK B3: the refused operation framed nothing at all, so the DA is still mid-way through the first read");
    mDev.HoldBack = 0;
    Check((await firstOp).Length == 0x1000, "MTK B3: the read that was already in flight completed untouched once its status arrived");
    mSes.Dispose();

    // ---- B4: the NATIVE tab's gates, asserted without a form, a port or a phone -----------------------
    Check(MtkNativeBench.DiscoverProfiles().All(File.Exists),
        "MTK B4: recipe discovery reads the installed tree and never invents a recipe path — an empty folder is a normal state");
    Check(MtkBootProfile.FindProfiles(Path.Combine(dir, "no-such-recipes")).Count == 0,
        "MTK B4: with no boot recipe there is nothing to list, so the NATIVE tab does not exist — fail-closed by absence, not a clickable guess");
    Check(MtkNativeBench.SessionDirectory(Path.Combine(dir, "data")).EndsWith(Path.Combine("MediaTekSessions", "native-da")) &&
          MtkNativeBench.SessionDirectory(Path.Combine(dir, "data")).StartsWith(Path.GetFullPath(Path.Combine(dir, "data"))),
        "MTK B4: native dumps get their own folder under the tool's data directory — a GUID-named mtkclient session folder can never be overwritten");
    Check(MtkNativeBench.SafePartitionToken("logo") == "logo" && MtkNativeBench.SafePartitionToken("boot_a") == "boot_a",
        "MTK B4: real GPT names survive the filename filter untouched");
    Check(MtkNativeBench.SafePartitionToken("../../windows/system32") == "windowssystem32",
        "MTK B4: a device-supplied name cannot carry path syntax into a dump filename");
    bool blankNamed = false;
    try { MtkNativeBench.SafePartitionToken("  ..  "); } catch (InvalidOperationException) { blankNamed = true; }
    Check(blankNamed, "MTK B4: a name with no filename-safe characters is a refusal, not a dump file named '.img'");
    Check(MtkNativeBench.SafePartitionToken(new string('x', 90)).Length == 40,
        "MTK B4: an absurd partition name is bounded, so the dump path cannot blow past MAX_PATH");
    string benchData = Path.Combine(dir, "bench-data");
    string dump1 = MtkNativeBench.DumpPathFor(benchData, "logo", new DateTime(2026, 10, 30, 14, 5, 6));
    Check(Path.GetFileName(dump1) == "logo-20261030-140506.img" && Directory.Exists(Path.GetDirectoryName(dump1)),
        "MTK B4: a dump lands as one timestamped .img in a folder that is created for it, so two runs never collide");
    Check(Path.GetDirectoryName(MtkNativeBench.DumpPathFor(benchData, "../escape", DateTime.Now)) ==
          MtkNativeBench.SessionDirectory(benchData),
        "MTK B4: even a name that looks like traversal is resolved inside the dump folder — the harness cannot be aimed at an arbitrary path");
    Check(MtkNativeBench.BootBlocker(true, false)!.Contains("already open") &&
          MtkNativeBench.BootBlocker(false, true)!.Contains("mtkclient") &&
          MtkNativeBench.BootBlocker(false, false) == null,
        "MTK B4: a second boot and a boot beside a running mtkclient are both refused in words — one MTK VCOM has exactly one master");
    Check(!MtkNativeBench.ProgramAllowed("flashinfo", out string whyFlashinfo) && whyFlashinfo.Contains("bricks"),
        "MTK B4: the UI refuses a never-program name before the dialog, and says plainly that it is a brick rather than a repair");
    Check(MtkNativeBench.ProgramAllowed("logo", out _), "MTK B4: an ordinary partition passes the same gate, so the guard is a list and not a blanket no");
    Check(!MtkNativeBench.ProgramAllowed("seccfg", out string whySeccfg) &&
          whySeccfg.Contains("never been measured") && whySeccfg.Contains("Read it"),
        "MTK B5.9: the grid refuses a seccfg Write or Erase before the dialog and names the read path as still open — an operator told only 'no' would click again");
    Check(MtkDaSession.ReadOnlyPayloadNames.Contains("seccfg") && !MtkDaSession.NeverProgramNames.Contains("seccfg"),
        "MTK B5.9: seccfg sits on its own read-only list, not folded into the brick list — its reason is an unmeasured payload, not a destroyed partition table");
    string restoreText = MtkNativeBench.ConfirmText("restore", new MtkDaExtent("logo", 2987008, 2989055, 512), @"C:\dumps\logo.img");
    Check(restoreText.Contains("0x5b280000") && restoreText.Contains("logo.img") && restoreText.Contains("read back"),
        "MTK B4: the restore confirmation shows the exact byte extent and promises the read-back, so agreeing means knowing what is written");
    string eraseText = MtkNativeBench.ConfirmText("erase", new MtkDaExtent("logo", 2987008, 2989055, 512), null);
    Check(eraseText.Contains("1 MiB") && eraseText.Contains("not proven"),
        "MTK B4: the erase confirmation says the DA's acceptance is not measured zeroing — no operator is allowed to think it was wiped for certain");
    bool unknownOp = false;
    try { MtkNativeBench.ConfirmText("format-all", new MtkDaExtent("logo", 2987008, 2989055, 512), null); }
    catch (ArgumentException) { unknownOp = true; }
    Check(unknownOp, "MTK B4: an operation with no written confirmation text cannot be shown — the dialog is enumerated, not improvised");
    Check(MtkNativeBench.StatusLine(false, "", 0, 0).Contains("closed") &&
          MtkNativeBench.StatusLine(true, "COM7 0E8D:0003", 0x0766, 44).Contains("44 partition(s)"),
        "MTK B4: the tab states what the session is in the same words a screenshot of a bench run carries");
    Check(MtkNativeBench.ProgressLine("dump logo", 25) == "dump logo — 25%",
        "MTK B4: progress is labelled with the operation it belongs to, so a bar cannot be read as something else's");
    Check(Path.GetFileName(MtkNativeBench.BenchLogPath(benchData)) == "native-da.log" &&
          Path.GetDirectoryName(MtkNativeBench.BenchLogPath(benchData)) == MtkNativeBench.SessionDirectory(benchData),
        "MTK B4: the bench log lives in the native folder and nowhere else — a real run can never overwrite an mtkclient recording");

    string loggedData = Path.Combine(dir, "log-data");
    string logPath = MtkNativeBench.BenchLogPath(loggedData);
    MtkNativeBench.AppendBenchLog(loggedData, "[*] Native DA: waiting for the MTK VCOM", new DateTimeOffset(2026, 10, 30, 14, 5, 6, TimeSpan.FromHours(6.5)));
    MtkNativeBench.AppendBenchLog(loggedData, "[OK] Native DA session is live on COM7 0E8D:0003", new DateTimeOffset(2026, 10, 30, 14, 5, 19, TimeSpan.FromHours(6.5)));
    string[] logged = File.ReadAllLines(logPath);
    Check(logged.Length == 2 && logged[0].Contains("14:05:06") && logged[0].Contains("waiting for the MTK VCOM") &&
          logged[1].Contains("[OK]") && logged[1].StartsWith("2026-10-30"),
        "MTK B4: every bench line is timestamped and appended in order, so the first real boot is evidence afterwards and not only on screen");

    string unwrittenData = Path.Combine(dir, "log-refused");
    bool blankLogged = false;
    try { MtkNativeBench.AppendBenchLog(unwrittenData, "   ", DateTimeOffset.Now); } catch (ArgumentException) { blankLogged = true; }
    Check(blankLogged && !File.Exists(MtkNativeBench.BenchLogPath(unwrittenData)),
        "MTK B4: a blank line is refused before the folder is made — the bench log never collects an empty record of a run that said nothing");
    Check(MtkNativeBench.FaceBootReadiness("0003") == "measured" && MtkNativeBench.DescribeFace("0003").Contains("stale DA"),
        "MTK B4: the face a completed boot was measured riding is still named with the RUN 9 caveat — a stale DA wears it too");
    Check(MtkNativeBench.FaceBootReadiness("2001") == "not-brom" && MtkNativeBench.FaceBootReadiness("20FF") == "not-brom" &&
          MtkNativeBench.FaceBootReadiness("2000") == "unproven" && MtkNativeBench.FaceBootReadiness("9999") == "unknown",
        "MTK B4: faces are graded measured / unproven / not-brom / unknown instead of a yes-no, because this bench has proof for one and hints for the rest");
    Check(MtkNativeBench.DescribeFace("1234").Contains("only the recipe") && MtkNativeBench.DescribeFace("").Contains("????"),
        "MTK B4: an unknown face is admitted as unknown — the harness never claims a PID proves BROM, the echo does");
    Check(wrongEchoText.Contains(MtkNativeBench.StaleDaRecovery.Trim()),
        "MTK B4: a refused handshake carries the recovery that RUN 9 proved necessary, not just the fact that it refused");

    // The refuse-then-recheck wording, branch by branch. This is the sentence an operator reads after a failed
    // boot on a phone that is no longer where it was, so each shape RUN 11 could have been has to be named.
    var busStayed = new List<MtkNativeBench.MtkBusNode> { new("0003", "COM3") };
    string stayedText = MtkNativeBench.DescribeBusAfterRefusal("COM3", "0003", busStayed);
    Check(stayedText.Contains("still enumerated as 0e8d:0003") && stayedText.Contains("did not move"),
        "MTK B4: a phone still on the face the boot used is reported as a refusal, not as a disappearance");
    string pidChangedText = MtkNativeBench.DescribeBusAfterRefusal("COM3", "0003",
        new List<MtkNativeBench.MtkBusNode> { new("2001", "COM3") });
    Check(pidChangedText.Contains("now 0e8d:2001") && pidChangedText.Contains("started on 0e8d:0003"),
        "MTK B4: a COM port that survived the boot but changed PID names both PIDs — a DA arriving on the same port is a real outcome");
    string movedText = MtkNativeBench.DescribeBusAfterRefusal("COM3", "0003",
        new List<MtkNativeBench.MtkBusNode> { new("2000", "COM7") });
    Check(movedText.Contains("COM7=0e8d:2000") && movedText.Contains("different serial face"),
        "MTK B4: a phone that moved to another serial face is reported with the face it moved to");
    string hidText = MtkNativeBench.DescribeBusAfterRefusal("COM3", "0003",
        new List<MtkNativeBench.MtkBusNode> { new("20FF", null) });
    Check(hidText.Contains("no COM port at all") && hidText.Contains("0e8d:20FF"),
        "MTK B4: RUN 11's real shape — COM3 gone, a HID-only face left — reads as a phone that re-enumerated, not as a dead port");
    Check(MtkNativeBench.DescribeBusAfterRefusal("COM3", "0003", new List<MtkNativeBench.MtkBusNode>()).Contains("no MediaTek node"),
        "MTK B4: an empty bus is named as the phone leaving, which is the one thing no byte on the wire can show");
    Check(MtkNativeBench.DescribeBusAfterRefusal(null, null, null).Contains("is gone") &&
          MtkNativeBench.DescribeBusAfterRefusal("com3", "0003", new List<MtkNativeBench.MtkBusNode> { new("0003", "COM3") }).Contains("did not move"),
        "MTK B4: the phrasing survives a missing port, a missing snapshot and lower-case input instead of throwing at the moment of a refusal");

    // B4 UI wiring — the session surfaced on INFO and FLASH. The logic that guards a grid click has to hold the same
    // words the bench dialogs use, so the checks read the exact strings the operator will see.
    string writeText = MtkNativeBench.ConfirmText("write", new MtkDaExtent("logo", 2987008, 2989055, 512), @"C:\fw\logo.bin");
    Check(writeText.Contains("0x5b280000") && writeText.Contains("logo.bin") && writeText.Contains("read back") &&
          writeText.Contains("raw bytes"),
        "MTK B4: the grid write confirmation names the extent, the image and the raw-bytes honesty — a smaller image leaves the rest of the extent untouched");
    Check(MtkNativeBench.DumpMatchesPartition(@"C:\d\logo-20261030-140506.img", "logo") &&
          !MtkNativeBench.DumpMatchesPartition(@"C:\d\seccfg-20261030-140506.img", "logo") &&
          !MtkNativeBench.DumpMatchesPartition(@"C:\d\logo-20261030-140506.img", "seccfg"),
        "MTK B4: a session dump is matched to its partition by the token the dumper minted — a grid write may reuse only a dump of the same name");
    Check(!MtkNativeBench.DumpMatchesPartition(null, "logo") && !MtkNativeBench.DumpMatchesPartition(@"C:\d\x.img", "") &&
          !MtkNativeBench.DumpMatchesPartition(@"C:\d\x.img", "!!!"),
        "MTK B4: a missing or unusable partition name matches nothing — the grid write falls through to the explicit no-image refusal, never to a guess");

    // ---- P1.5: the multi-partition write list and the fallback's scatter --------------------------------
    // A native flash never rebuilds the GPT, so the package row is only allowed to name an extent the live table
    // already reports at the same bytes. These checks pin every refusal before the first frame, not after a write.
    var nfTable = new MtkDaPartitionTable(512, new[]
    {
        new MtkDaExtent("boot", 0x1000, 0x1fff, 512),
        new MtkDaExtent("nvram", 0x2000, 0x200f, 512),
        new MtkDaExtent("otp", 0x2010, 0x201f, 512),
    }, null, null);
    MtkDaExtent nfBoot = nfTable.Extents.Single(e => e.Name == "boot");
    MtkDaExtent nfNvram = nfTable.Extents.Single(e => e.Name == "nvram");
    MtkDaExtent nfOtp = nfTable.Extents.Single(e => e.Name == "otp");
    string nfBootFile = Path.Combine(dir, "p15-boot.bin");
    string nfLargeFile = Path.Combine(dir, "p15-large.bin");
    string nfSparseFile = Path.Combine(dir, "p15-sparse.bin");
    string nfMissingFile = Path.Combine(dir, "p15-missing.bin");
    File.WriteAllBytes(nfBootFile, new byte[512]);
    File.WriteAllBytes(nfLargeFile, new byte[(int)nfNvram.ByteLength + 1]);
    var nfSparseBytes = new byte[1024];
    nfSparseBytes[0] = 0x3A; nfSparseBytes[1] = 0xFF; nfSparseBytes[2] = 0x26; nfSparseBytes[3] = 0xED;
    File.WriteAllBytes(nfSparseFile, nfSparseBytes);

    var nfPlan = MtkNativeBench.NativeFlashPlan(nfTable,
        new[] { ("boot", nfBootFile, (long)nfBoot.ByteOffset, (long)nfBoot.ByteLength) }, out string nfPlanRefusal);
    Check(nfPlanRefusal == null && nfPlan.Count == 1 && nfPlan[0].Extent == nfBoot &&
          nfPlan[0].ImagePath == nfBootFile && nfPlan[0].ImageLength == 512,
        "MTK P1.5 FLASH: a row whose offset and length equal the live extent resolves to that extent — the write is " +
        "addressed by the device's own bytes, not by the scatter's claim");
    Check(MtkNativeBench.NativeFlashPlan(nfTable,
              new[] { ("boot", nfBootFile, (long)nfBoot.ByteOffset + 512, (long)nfBoot.ByteLength) },
              out string nfOffsetRefusal) == null &&
          nfOffsetRefusal.Contains("different layout") && nfOffsetRefusal.Contains("Nothing was written"),
        "MTK P1.5 FLASH: a package row at a different offset is refused before the port — the native path writes by " +
        "name and never rebuilds the GPT, so a foreign layout has no meaning here");
    Check(MtkNativeBench.NativeFlashPlan(nfTable,
              new[] { ("boot", nfBootFile, (long)nfBoot.ByteOffset, (long)nfBoot.ByteLength + 512) },
              out string nfSizeRefusal) == null && nfSizeRefusal.Contains("different layout"),
        "MTK P1.5 FLASH: a row whose length disagrees with the live extent is refused too — an equal start is not an " +
        "equal extent");
    Check(MtkNativeBench.NativeFlashPlan(nfTable,
              new[] { ("boot", nfSparseFile, (long)nfBoot.ByteOffset, (long)nfBoot.ByteLength) },
              out string nfSparseRefusal) == null &&
          nfSparseRefusal.Contains("sparse") && nfSparseRefusal.Contains("Nothing was written"),
        "MTK FLASH: malformed sparse headers are refused before preparation or device writes");
    Check(MtkNativeBench.NativeFlashPlan(nfTable,
              new[] { ("otp", nfBootFile, (long)nfOtp.ByteOffset, (long)nfOtp.ByteLength) },
              out string nfNeverRefusal) == null && nfNeverRefusal.Contains("bricks"),
        "MTK P1.5 FLASH: a never-program name stops the whole set before the first frame, with the same brick " +
        "reason the single-partition write gives");
    Check(MtkNativeBench.NativeFlashPlan(nfTable,
              new[] { ("nvram", nfLargeFile, (long)nfNvram.ByteOffset, (long)nfNvram.ByteLength) },
              out string nfLargeRefusal) == null && nfLargeRefusal.Contains("run into the next partition"),
        "MTK P1.5 FLASH: an image larger than its extent is refused instead of running into the next partition");
    Check(MtkNativeBench.NativeFlashPlan(nfTable,
              new[] { ("tee1", nfBootFile, 0L, 0L) }, out string nfNameRefusal) == null &&
          nfNameRefusal.Contains("no partition named 'tee1'"),
        "MTK P1.5 FLASH: a name the live GPT does not report stops the set — no write list is invented for it");
    Check(MtkNativeBench.NativeFlashPlan(nfTable,
              new[] { ("boot", nfMissingFile, (long)nfBoot.ByteOffset, (long)nfBoot.ByteLength) },
              out string nfFileRefusal) == null && nfFileRefusal.Contains("is missing"),
        "MTK P1.5 FLASH: a missing image is refused before the port, not discovered after three partitions were written");
    Check(MtkNativeBench.NativeFlashPlan(nfTable, new[]
              {
                  ("boot", nfBootFile, (long)nfBoot.ByteOffset, (long)nfBoot.ByteLength),
                  ("BOOT", nfBootFile, (long)nfBoot.ByteOffset, (long)nfBoot.ByteLength),
              }, out string nfDuplicateRefusal) == null && nfDuplicateRefusal.Contains("appears twice"),
        "MTK P1.5 FLASH: the same partition twice is refused — one session writes each extent once, and a duplicate " +
        "row would mean a second, unexplained write");
    Check(MtkNativeBench.NativeFlashPlan(nfTable, Array.Empty<(string, string, long, long)>(),
              out string nfEmptyRefusal) == null && nfEmptyRefusal.Contains("nothing to write"),
        "MTK P1.5 FLASH: an empty selection produces no dialog and no write list");
    string nfConfirm = MtkNativeBench.NativeFlashConfirmText(nfPlan);
    Check(nfConfirm.Contains("boot") && nfConfirm.Contains(Path.GetFileName(nfBootFile)) &&
          nfConfirm.Contains("not rebuilt") && nfConfirm.Contains("read back"),
        "MTK P1.5 FLASH: the one dialog names the extent and image and says both facts the native route changes — " +
        "no GPT rebuild, and every write is read back");

    // ---- P1.5 sparse: a stock sparse image is planned at its EXPANDED length, then prepared on the host ----
    // The container is smaller than the bytes it writes, so the plan must carry the expanded length (the size the
    // extent fit and the block-alignment check use) and preparation must turn it into raw bytes before any frame.
    string nfsDir = Path.Combine(dir, "nfs");
    Directory.CreateDirectory(nfsDir);
    string nfsSparseFile = Path.Combine(nfsDir, "userdata.img");
    using (var nfsFs = File.Create(nfsSparseFile))
    using (var nfsBw = new BinaryWriter(nfsFs))
    {
        nfsBw.Write(0xED26FF3Au); nfsBw.Write((ushort)1); nfsBw.Write((ushort)0);
        nfsBw.Write((ushort)28); nfsBw.Write((ushort)12);
        nfsBw.Write(4096u); nfsBw.Write(6u); nfsBw.Write(3u); nfsBw.Write(0u);
        nfsBw.Write((ushort)0xCAC1); nfsBw.Write((ushort)0); nfsBw.Write(1u); nfsBw.Write(12u + 4096u);
        for (int i = 0; i < 4096; i++) nfsBw.Write((byte)(i % 256));
        nfsBw.Write((ushort)0xCAC2); nfsBw.Write((ushort)0); nfsBw.Write(2u); nfsBw.Write(16u);
        nfsBw.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        nfsBw.Write((ushort)0xCAC3); nfsBw.Write((ushort)0); nfsBw.Write(3u); nfsBw.Write(12u);
    }
    var nfsExtent = new MtkDaExtent("userdata", 0x100, 0x13F, 512);   // 64 blocks x 512 = 32768 B, fits 24576 expanded
    var nfsTable = new MtkDaPartitionTable(512, new[] { nfsExtent }, null, null);
    var nfsPlan = MtkNativeBench.NativeFlashPlan(nfsTable,
        new[] { ("userdata", nfsSparseFile, (long)nfsExtent.ByteOffset, (long)nfsExtent.ByteLength) },
        out string nfsPlanRefusal);
    Check(nfsPlanRefusal == null && nfsPlan.Count == 1 && nfsPlan[0].IsSparse && nfsPlan[0].ImageLength == 24576 &&
          nfsPlan[0].ImagePath == nfsSparseFile,
        "MTK P1.5 SPARSE: a valid sparse image is planned at its expanded byte length (6 x 4096), not its container " +
        "size — the extent fit and the block-alignment check both use the bytes that will actually be written");
    string nfsAlignFile = Path.Combine(nfsDir, "unaligned.bin");
    File.WriteAllBytes(nfsAlignFile, new byte[513]);
    Check(MtkNativeBench.NativeFlashPlan(nfsTable,
              new[] { ("userdata", nfsAlignFile, (long)nfsExtent.ByteOffset, (long)nfsExtent.ByteLength) },
              out string nfsAlignRefusal) == null &&
          nfsAlignRefusal.Contains("not aligned") && nfsAlignRefusal.Contains("Nothing was written"),
        "MTK P1.5 SPARSE: a raw image whose length is not a whole number of device blocks is refused before the port");
    string nfsConfirm = MtkNativeBench.NativeFlashConfirmText(nfsPlan);
    Check(nfsConfirm.Contains("sparse") && nfsConfirm.Contains("expanded") &&
          nfsConfirm.Contains("Temporary disk space required") && nfsConfirm.Contains("zeroes") &&
          nfsConfirm.Contains("kept") && nfsConfirm.Contains("read back"),
        "MTK P1.5 SPARSE: the one dialog names the sparse image, its expanded size and the temporary disk it needs, " +
        "and still says only the listed ranges are overwritten and every write is read back");

    string nfsRoot = Path.Combine(nfsDir, "prep");
    Directory.CreateDirectory(nfsRoot);
    var nfsLogs = new List<string>();
    var nfsPrep = MtkNativeBench.NativeFlashPreparation.Create(nfsPlan, nfsRoot, default, nfsLogs.Add);
    string nfsRawPath;
    string nfsPrepDir;
    try
    {
        Check(nfsPrep.Plan.Count == 1 && !nfsPrep.Plan[0].IsSparse && nfsPrep.Plan[0].ImageLength == 24576 &&
              nfsPrep.Plan[0].ImagePath != nfsSparseFile,
            "MTK P1.5 SPARSE: preparation replaces the sparse container with a raw file of the expanded length before " +
            "any partition is written");
        nfsRawPath = nfsPrep.Plan[0].ImagePath;
        nfsPrepDir = Path.GetDirectoryName(nfsRawPath)!;
        byte[] nfsRaw = File.ReadAllBytes(nfsRawPath);
        bool nfsRawOk = nfsRaw.Length == 24576;
        for (int i = 0; i < 4096 && nfsRawOk; i++) if (nfsRaw[i] != (byte)(i % 256)) nfsRawOk = false;
        for (int i = 4096; i < 12288 && nfsRawOk; i++)
            if (nfsRaw[i] != new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }[(i - 4096) % 4]) nfsRawOk = false;
        for (int i = 12288; i < nfsRaw.Length && nfsRawOk; i++) if (nfsRaw[i] != 0) nfsRawOk = false;
        Check(nfsRawOk, "MTK P1.5 SPARSE: the prepared raw file carries the raw chunk, the fill pattern and zeroes for " +
            "the don't-care range — the bytes the DA will receive, expanded on this PC");
        Check(nfsLogs.Any(l => l.Contains("expanding and validating") && l.Contains("userdata.img")),
            "MTK P1.5 SPARSE: preparation logs the expand-and-validate step naming the image before any write");
    }
    finally { nfsPrep.Dispose(); }
    Check(!Directory.Exists(nfsPrepDir),
        "MTK P1.5 SPARSE: disposing the preparation removes the temporary raw directory — no expanded image is left " +
        "on the shop PC after the operation ends");

    File.WriteAllBytes(nfsSparseFile, new byte[512]);   // same name, no longer the sparse image the plan measured
    bool nfsChanged = false;
    try { MtkNativeBench.NativeFlashPreparation.Create(nfsPlan, nfsRoot); }
    catch (IOException ex) { nfsChanged = ex.Message.Contains("changed after preflight"); }
    Check(nfsChanged, "MTK P1.5 SPARSE: an image that changed after the plan was measured is refused before any write " +
        "— the bytes that reach the DA are the bytes that passed preflight");
    using (var nfsRestore = File.Create(nfsSparseFile))
    using (var nfsRb = new BinaryWriter(nfsRestore))
    {
        nfsRb.Write(0xED26FF3Au); nfsRb.Write((ushort)1); nfsRb.Write((ushort)0);
        nfsRb.Write((ushort)28); nfsRb.Write((ushort)12);
        nfsRb.Write(4096u); nfsRb.Write(6u); nfsRb.Write(3u); nfsRb.Write(0u);
        nfsRb.Write((ushort)0xCAC1); nfsRb.Write((ushort)0); nfsRb.Write(1u); nfsRb.Write(12u + 4096u);
        for (int i = 0; i < 4096; i++) nfsRb.Write((byte)(i % 256));
        nfsRb.Write((ushort)0xCAC2); nfsRb.Write((ushort)0); nfsRb.Write(2u); nfsRb.Write(16u);
        nfsRb.Write(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        nfsRb.Write((ushort)0xCAC3); nfsRb.Write((ushort)0); nfsRb.Write(3u); nfsRb.Write(12u);
    }
    var nfsCts = new CancellationTokenSource();
    nfsCts.Cancel();
    bool nfsCancelled = false;
    try { MtkNativeBench.NativeFlashPreparation.Create(nfsPlan, nfsRoot, nfsCts.Token); }
    catch (OperationCanceledException) { nfsCancelled = true; }
    Check(nfsCancelled && Directory.GetDirectories(nfsRoot).Length == 0,
        "MTK P1.5 SPARSE: a cancelled preparation throws before creating any temporary directory — nothing is expanded " +
        "and nothing is written");
    string nfsRawImage = Path.Combine(nfsDir, "raw.bin");
    File.WriteAllBytes(nfsRawImage, new byte[512]);
    var nfsRawPlan = MtkNativeBench.NativeFlashPlan(nfTable,
        new[] { ("boot", nfsRawImage, (long)nfBoot.ByteOffset, (long)nfBoot.ByteLength) }, out string nfsRawRefusal);
    using (var nfsRawPrep = MtkNativeBench.NativeFlashPreparation.Create(nfsRawPlan, nfsRoot))
        Check(nfsRawPrep.Plan.Count == 1 && !nfsRawPrep.Plan[0].IsSparse &&
              nfsRawPrep.Plan[0].ImagePath == nfsRawImage,
            "MTK P1.5 SPARSE: a non-sparse image passes through preparation unchanged — no temporary copy is made for " +
            "a raw file that already is the bytes to write");
    Check(Directory.GetDirectories(nfsRoot).Length == 0,
        "MTK P1.5 SPARSE: a raw-only preparation creates no temporary directory at all");

    // ---- P1.5 service erase: the FRP / Userlock target is resolved from the live GPT, fail-closed ----
    // The service buttons name one partition (frp or userdata). The target must be the device's own extent, present
    // exactly once in both the parsed GPT and the cache, inside the usable LBAs, non-overlapping and inside capacity.
    byte[] svcHeader(int count)
    {
        var h = GptHeader(2, count, 128);
        BitConverter.GetBytes(1L).CopyTo(h, 24);      // MyLBA
        BitConverter.GetBytes(4096L).CopyTo(h, 32);   // backup LBA, past last usable
        BitConverter.GetBytes(34L).CopyTo(h, 40);     // first usable
        BitConverter.GetBytes(4000L).CopyTo(h, 48);   // last usable
        return h;
    }
    byte[] svcEntries(params (string Name, long First, long Last)[] rows)
    {
        var e = new byte[rows.Length * 128];
        for (int i = 0; i < rows.Length; i++) GptEntry(rows[i].Name, rows[i].First, rows[i].Last).CopyTo(e, i * 128);
        return e;
    }
    MtkDaExtent svcExtent(string name, long first, long last) => new(name, first, last, 512);
    var svcStorage = MtkStorageInfo.Parse(Convert.FromHexString(DescriptorPayload(0xE8F800000ul)));
    var svcUser = svcExtent("userdata", 1000, 1999);
    var svcFrp = svcExtent("frp", 100, 199);
    var svcMeta = svcExtent("metadata", 200, 299);
    var svcRows = new[] { ("userdata", 1000L, 1999L), ("frp", 100L, 199L), ("metadata", 200L, 299L) };
    var svcTable = new MtkDaPartitionTable(512, new[] { svcUser, svcFrp, svcMeta }, svcHeader(3), svcEntries(svcRows));
    Check(ReferenceEquals(MtkNativeBench.ServiceEraseTarget(svcTable, svcStorage, true), svcUser) &&
          ReferenceEquals(MtkNativeBench.ServiceEraseTarget(svcTable, svcStorage, false), svcFrp),
        "MTK P1.5 SERVICE: Userlock resolves to the userdata extent and FRP to the frp extent — the device's own cached " +
        "extent, matched to the live GPT by name");
    string svcUserText = MtkNativeBench.ServiceEraseConfirmText(true);
    string svcFrpText = MtkNativeBench.ServiceEraseConfirmText(false);
    Check(svcUserText.Contains("userdata") && svcUserText.Contains("KEPT: metadata, FRP, NV/IMEI") &&
          svcUserText.Contains("server-side accounts") && svcUserText.Contains("Continue?") &&
          svcFrpText.Contains("frp") && svcFrpText.Contains("KEPT: userdata, metadata, NV/IMEI"),
        "MTK P1.5 SERVICE: each confirmation names what is erased and what is KEPT, and says it does not remove " +
        "server-side accounts — the destructive dialog never hides the preserved partitions");
    bool svcRefusal(Func<MtkDaExtent> act, string fragment)
    {
        try { act(); return false; }
        catch (InvalidDataException ex) { return ex.Message.Contains(fragment); }
    }
    var svcNoRaw = new MtkDaPartitionTable(512, new[] { svcUser, svcFrp, svcMeta }, null, null);
    Check(svcRefusal(() => MtkNativeBench.ServiceEraseTarget(svcNoRaw, svcStorage, true), "live GPT and storage geometry"),
        "MTK P1.5 SERVICE: without the raw GPT header and entries the target cannot be verified against the device, so " +
        "the erase is refused before any frame");
    var svcUfsStorage = MtkStorageInfo.ParseUfs(Convert.FromHexString(UfsGeometryPayload()));
    Check(svcRefusal(() => MtkNativeBench.ServiceEraseTarget(svcTable, svcUfsStorage, true), "live GPT and storage geometry"),
        "MTK P1.5 SERVICE: a storage geometry whose block size differs from the table's is refused — an LBA-to-byte " +
        "factor that disagrees would aim the erase at the wrong bytes");
    var svcNoUser = new MtkDaPartitionTable(512, new[] { svcFrp, svcMeta }, svcHeader(2),
        svcEntries(("frp", 100L, 199L), ("metadata", 200L, 299L)));
    Check(svcRefusal(() => MtkNativeBench.ServiceEraseTarget(svcNoUser, svcStorage, true), "Exactly one userdata"),
        "MTK P1.5 SERVICE: a device with no userdata partition is refused — the service button never invents a target");
    var svcDup = new MtkDaPartitionTable(512, new[] { svcFrp, svcMeta, svcExtent("userdata", 1000, 1499), svcExtent("userdata", 1500, 1999) },
        svcHeader(4), svcEntries(("frp", 100L, 199L), ("metadata", 200L, 299L), ("userdata", 1000L, 1499L), ("userdata", 1500L, 1999L)));
    Check(svcRefusal(() => MtkNativeBench.ServiceEraseTarget(svcDup, svcStorage, true), "Exactly one userdata"),
        "MTK P1.5 SERVICE: two userdata partitions are refused — an ambiguous target is not erased on a guess");
    var svcCacheMiss = new MtkDaPartitionTable(512, new[] { svcExtent("userdata", 1100, 1999), svcFrp, svcMeta },
        svcHeader(3), svcEntries(svcRows));
    Check(svcRefusal(() => MtkNativeBench.ServiceEraseTarget(svcCacheMiss, svcStorage, true), "cached extent differs"),
        "MTK P1.5 SERVICE: a cached extent whose LBAs differ from the live GPT is refused — the erase addresses the " +
        "device's current bytes, not a stale snapshot");
    var svcOverlap = new MtkDaPartitionTable(512, new[] { svcUser, svcFrp, svcExtent("metadata", 1500, 2500) },
        svcHeader(3), svcEntries(("userdata", 1000L, 1999L), ("frp", 100L, 199L), ("metadata", 1500L, 2500L)));
    Check(svcRefusal(() => MtkNativeBench.ServiceEraseTarget(svcOverlap, svcStorage, true), "overlaps another partition"),
        "MTK P1.5 SERVICE: a target that overlaps another partition is refused — erasing it would take bytes that " +
        "belong to a partition the operator did not name");
    var svcSmallStorage = MtkStorageInfo.Parse(Convert.FromHexString(DescriptorPayload(600000ul)));
    Check(svcRefusal(() => MtkNativeBench.ServiceEraseTarget(svcTable, svcSmallStorage, true), "exceeds the device capacity"),
        "MTK P1.5 SERVICE: a target whose bytes run past the reported user area is refused — the erase never runs off " +
        "the end of the disk");

    var nfLive = nfTable.Extents
        .Select(e => new MtkPartition(e.Name, (long)e.ByteOffset, (long)e.ByteLength)).ToList();
    IReadOnlyList<ScatterRow> nfRows = MtkNativeBench.ScatterRowsFromGpt(nfLive, new[] { "boot" }, "EMMC",
        out string nfScatterRefusal);
    Check(nfScatterRefusal == null && nfRows.Count == nfLive.Count &&
          nfRows.Single(r => r.Name == "boot") is { IsDownload: true, IsReserved: false, FileName: "boot.bin" } &&
          nfRows.Where(r => r.Name != "boot").All(r => r.IsReserved && !r.IsDownload && r.FileName == "NONE"),
        "MTK P1.5 BACKUP: the fallback's GPT produces the same full-layout package — the dumped partition as an " +
        "image, every other live extent as a reserved row that keeps the rebuilt table whole");
    Check(MtkNativeBench.ScatterRowsFromGpt(nfLive, new[] { "boot" }, "UFS", out string nfUfsRefusal) == null &&
          nfUfsRefusal.Contains("UFS") && nfUfsRefusal.Contains("no scatter is written"),
        "MTK P1.5 BACKUP: a UFS run gets its images and manifest but no scatter — the region word has never been " +
        "read off a real UFS package on this bench");
    Check(MtkNativeBench.ScatterRowsFromGpt(nfLive, new[] { "boot" }, "", out string nfNoStorageRefusal) == null &&
          nfNoStorageRefusal.Contains("did not report"),
        "MTK P1.5 BACKUP: a run that never says whether the user area is eMMC or UFS is refused the same way — an " +
        "invented region word is unmeasured bytes in a write list");
    Check(MtkNativeBench.ScatterRowsFromGpt(nfLive, new[] { "tee1" }, "EMMC", out string nfScatterMissing) == null &&
          nfScatterMissing.Contains("tee1"),
        "MTK P1.5 BACKUP: a dumped name the GPT does not report stops the scatter instead of inventing an offset for it");
    Check(MtkNativeBench.ScatterRowsFromGpt(Array.Empty<MtkPartition>(), new[] { "boot" }, "EMMC",
              out string nfEmptyLive) == null && nfEmptyLive.Contains("live GPT is empty"),
        "MTK P1.5 BACKUP: an empty GPT produces no scatter file");

    // ---- P1.6: the startup prune of failed-session folders (the 52 GB class) -----------------------------
    string pruneData = Path.Combine(dir, "prune-data");
    string pruneRoot = PMKUnlocker.MediaTek.MtkSessionPrune.SessionsRoot(pruneData);
    Directory.CreateDirectory(pruneRoot);
    Directory.CreateDirectory(Path.Combine(pruneRoot, "seccfg-backup"));
    Directory.CreateDirectory(Path.Combine(pruneRoot, "seccfg-backup", "archive-20261013"));
    Directory.CreateDirectory(Path.Combine(pruneRoot, "native-da"));
    File.WriteAllText(Path.Combine(pruneRoot, "native-da", "pacing.txt"), "0 0 0 n");
    Guid[] pruneGuids =
    {
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
    };
    for (int i = 0; i < pruneGuids.Length; i++)
    {
        string g = Path.Combine(pruneRoot, pruneGuids[i].ToString("N"));
        Directory.CreateDirectory(g);
        File.WriteAllText(Path.Combine(g, "last-run.raw.log"), "x");
        File.WriteAllText(Path.Combine(g, "logo.bin"), new string('a', 1024));
        Directory.SetLastWriteTimeUtc(g, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i));
    }
    var (pruneCount, pruneBytes) = PMKUnlocker.MediaTek.MtkSessionPrune.Prune(pruneData, keep: 2);
    Check(pruneCount == 3 && pruneBytes == 3 * (1024 + 1),
        "P1.6 PRUNE: only the newest 2 GUID session folders survive a startup prune, and the bytes freed are every " +
        "staged byte that was in them (the raw log and the .bin)");
    Check(Directory.Exists(Path.Combine(pruneRoot, "seccfg-backup")) &&
          Directory.Exists(Path.Combine(pruneRoot, "seccfg-backup", "archive-20261013")) &&
          Directory.Exists(Path.Combine(pruneRoot, "native-da")) &&
          File.Exists(Path.Combine(pruneRoot, "native-da", "pacing.txt")),
        "P1.6 PRUNE: seccfg-backup, its archive and the native-da bench folder are untouched — a non-GUID name can never be a prune candidate");
    Check(Directory.Exists(Path.Combine(pruneRoot, pruneGuids[3].ToString("N"))) &&
          Directory.Exists(Path.Combine(pruneRoot, pruneGuids[4].ToString("N"))),
        "P1.6 PRUNE: the newest folders are the ones kept, by write time");
    Check(!Directory.Exists(Path.Combine(pruneRoot, pruneGuids[0].ToString("N"))) &&
          !Directory.Exists(Path.Combine(pruneRoot, pruneGuids[1].ToString("N"))),
        "P1.6 PRUNE: the oldest folders are the ones deleted");
    var (pruneAgain, pruneAgainBytes) = PMKUnlocker.MediaTek.MtkSessionPrune.Prune(pruneData, keep: 2);
    Check(pruneAgain == 0 && pruneAgainBytes == 0,
        "P1.6 PRUNE: a second startup prune with nothing to do deletes nothing and frees nothing");
    string pruneMissing = Path.Combine(dir, "no-such-data");
    var (missingCount, missingBytes) = PMKUnlocker.MediaTek.MtkSessionPrune.Prune(pruneMissing, keep: 2);
    Check(missingCount == 0 && missingBytes == 0,
        "P1.6 PRUNE: a data directory that has never held a session is not an error");
    var (negCount, negBytes) = PMKUnlocker.MediaTek.MtkSessionPrune.Prune(pruneData, keep: -1);
    Check(negCount == 0 && negBytes == 0,
        "P1.6 PRUNE: a negative keep is refused rather than deleting every folder");

    // ---- P1.7: the one Format All resolver both routes share --------------------------------
    string[] fmtDevice = { "preloader_a", "lk", "seccfg", "gpt", "nvram", "nvdata", "proinfo", "boot", "SYSTEM",
        "userdata", "vendor", "vbmeta", "unknown_new_partition", "boot", "misc" };
    string[] fmtNv = { "nvram", "nvdata", "nvcfg", "proinfo", "protect1", "protect2" };
    var fmt = PMKUnlocker.MediaTek.MtkNativeBench.FormatAllTargets(fmtDevice, fmtNv).ToList();
    Check(fmt.SequenceEqual(new[] { "boot", "misc", "SYSTEM", "userdata", "vbmeta", "vendor" }),
        "P1.7 FORMAT ALL: whitelist ∩ device GPT only, case-folded, duplicates gone, alphabetical — the erase list " +
        "is a deterministic function of the device's own names");
    Check(!fmt.Contains("preloader_a") && !fmt.Contains("lk") && !fmt.Contains("seccfg") && !fmt.Contains("gpt") &&
        !fmt.Contains("nvram") && !fmt.Contains("nvdata") && !fmt.Contains("proinfo") &&
        !fmt.Contains("unknown_new_partition"),
        "P1.7 FORMAT ALL: bootchain, seccfg, GPT, NV and names the whitelist never heard of are kept by construction");
    var fmtNvConflict = PMKUnlocker.MediaTek.MtkNativeBench.FormatAllTargets(
        new[] { "userdata", "boot" }, new[] { "userdata" });
    Check(fmtNvConflict.Count == 1 && fmtNvConflict[0] == "boot",
        "P1.7 FORMAT ALL: a whitelist name the NV set claims is still refused — the two guards stack, not either/or");
    var fmtEmpty = PMKUnlocker.MediaTek.MtkNativeBench.FormatAllTargets(
        new[] { "preloader", "lk", "seccfg" }, fmtNv);
    Check(fmtEmpty.Count == 0,
        "P1.7 FORMAT ALL: a device whose GPT names nothing erasable formats nothing — empty, not null, not an error");
    Check(PMKUnlocker.MediaTek.MtkNativeBench.FormatAllEraseable
            .Intersect(PMKUnlocker.MediaTek.MtkNativeBench.FormatAllProtected, StringComparer.OrdinalIgnoreCase)
            .Count() == 0,
        "P1.7 FORMAT ALL: no name sits in both the eraseable whitelist and the protected bootchain list — the two " +
        "arrays stay mutually exclusive");

    // ---- P1.8: the ADB post-flash check (one getprop dump, no phone) -------------------------
    string getpropBooted =
        "* daemon started successfully *\r\n" +
        "[ro.product.manufacturer]: [Xiaomi]\r\n" +
        "[ro.product.model]: [Redmi 9A]\r\n" +
        "[ro.serialno]: [9A2B7C3D]\r\n" +
        "[ro.build.version.release]: [10]\r\n" +
        "[ro.build.id]: [QP1A.190711.020]\r\n" +
        "[ro.boot.verifiedbootstate]: [green]\r\n" +
        "[ro.boot.flash.locked]: [1]\r\n" +
        "[persist.radio.imei1]: [861234567890123]\r\n" +
        "[sys.boot_completed]: [1]\r\n" +
        "[dev.bootcomplete]: [1]\r\n";
    var bootProps = MtkPostFlashCheck.ParseGetProp(getpropBooted);
    Check(bootProps.Count == 10 && bootProps["ro.product.model"] == "Redmi 9A" &&
        !bootProps.ContainsKey("* daemon started successfully *"),
        "P1.8 VERIFY BOOT: getprop parsing keeps every [key]: [value] line and drops the adb daemon banner");
    var bootEvidence = MtkPostFlashCheck.Read(bootProps);
    Check(bootEvidence.AdbAnswered && bootEvidence.BootCompleted && bootEvidence.DevBootCompleted &&
        bootEvidence.Missing.Count == 0,
        "P1.8 VERIFY BOOT: sys.boot_completed=1 is the boot verdict, and a complete dump reports nothing missing");
    Check(MtkPostFlashCheck.Verdict(bootEvidence) == "BOOTED",
        "P1.8 VERIFY BOOT: a booted phone is BOOTED, not \"adb is up\"");
    Check(bootEvidence.ImeiKey == "persist.radio.imei1" && bootEvidence.Imei == "861234567890123",
        "P1.8 VERIFY BOOT: the newest IMEI property (imei1) wins over the older single imei");
    Check(MtkPostFlashCheck.MaskImei("861234567890123") == "***********0123" &&
        MtkPostFlashCheck.MaskImei("1234") == "****" &&
        MtkPostFlashCheck.MaskImei("12345") == "*2345" &&
        MtkPostFlashCheck.MaskImei("") == "",
        "P1.8 VERIFY BOOT: the IMEI keeps only its last four digits; short and empty values are still masked");
    var bootFacts = MtkPostFlashCheck.Facts(bootEvidence);
    string flatBoot = string.Join(" | ", bootFacts.Select(f => f.Label + ": " + f.Value));
    Check(flatBoot.Contains("sys.boot_completed=1") &&
        flatBoot.Contains("green") && flatBoot.Contains("flash.locked=1") &&
        flatBoot.Contains("***********0123") &&
        !flatBoot.Contains("861234567890123"),
        "P1.8 VERIFY BOOT: the report names the boot flag and verified-boot state, and never prints the full IMEI");
    Check(flatBoot.Contains("cannot prove the FRP state"),
        "P1.8 VERIFY BOOT: even a PASS keeps the honest limit — ADB does not report the FRP state");

    var orangeEvidence = MtkPostFlashCheck.Read(MtkPostFlashCheck.ParseGetProp(
        "[sys.boot_completed]: [1]\r\n[ro.boot.verifiedbootstate]: [orange]\r\n[ro.boot.flash.locked]: [0]\r\n"));
    string flatOrange = string.Join(" | ", MtkPostFlashCheck.Facts(orangeEvidence).Select(f => f.Value));
    Check(flatOrange.Contains("bootloader unlocked") && flatOrange.Contains("cannot prove the FRP state"),
        "P1.8 VERIFY BOOT: an orange verified-boot state is decoded as an unlocked bootloader, and the report still states its limit");

    var bootingEvidence = MtkPostFlashCheck.Read(MtkPostFlashCheck.ParseGetProp(
        "[sys.boot_completed]: [0]\r\n[dev.bootcomplete]: []\r\n[ro.product.model]: [Redmi 9A]\r\n"));
    var bootingFacts = MtkPostFlashCheck.Facts(bootingEvidence);
    Check(!bootingEvidence.BootCompleted && MtkPostFlashCheck.Verdict(bootingEvidence) == "STILL BOOTING" &&
        bootingEvidence.Missing.Contains("ro.boot.verifiedbootstate") &&
        bootingEvidence.Missing.Contains(MtkPostFlashCheck.ImeiMissingLabel) &&
        bootingFacts.Count(f => f.Label == "IMEI") == 1 &&
        bootingFacts.Any(f => f.Label == "IMEI" && f.Value.Contains("not readable") && f.Value.Contains("*#06#")),
        "P1.8 VERIFY BOOT: an adb answer without sys.boot_completed=1 is STILL BOOTING, and the IMEI is reported once, readably");
    var olderImei = MtkPostFlashCheck.Read(MtkPostFlashCheck.ParseGetProp(
        "[persist.radio.imei]: [351234567890123]\r\n[persist.radio.imei2]: [351234567890124]\r\n"));
    Check(olderImei.ImeiKey == "persist.radio.imei" && olderImei.Imei == "351234567890123",
        "P1.8 VERIFY BOOT: without imei1 the older persist.radio.imei is used before imei2");

    var bootScript = new Queue<string>(new[]
    {
        "[sys.boot_completed]: [0]\r\n",
        "[sys.boot_completed]: [0]\r\n",
        "[sys.boot_completed]: [1]\r\n"
    });
    var bootCommands = new List<string>();
    var waited = await MtkPostFlashCheck.WaitForBootAsync(
        command => { bootCommands.Add(command); return Task.FromResult(bootScript.Dequeue()); },
        TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), _ => Task.CompletedTask);
    Check(bootCommands.Count == 3 && bootCommands.All(c => c == MtkPostFlashCheck.GetPropCommand) &&
        waited.BootCompleted && waited.Polls == 3,
        "P1.8 VERIFY BOOT: the wait stops on the first sys.boot_completed=1 and reports which poll answered");

    int neverCalls = 0;
    var never = await MtkPostFlashCheck.WaitForBootAsync(
        _ => { neverCalls++; return Task.FromResult("[sys.boot_completed]: [0]"); },
        TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), _ => Task.CompletedTask);
    Check(neverCalls == 3 && !never.BootCompleted && never.AdbAnswered &&
        MtkPostFlashCheck.Verdict(never) == "STILL BOOTING",
        "P1.8 VERIFY BOOT: a phone that never finishes booting spends the whole budget and is not called booted");

    var noAdb = await MtkPostFlashCheck.WaitForBootAsync(
        _ => Task.FromResult(""), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(4), _ => Task.CompletedTask);
    Check(noAdb.Polls == 2 && !noAdb.AdbAnswered && MtkPostFlashCheck.Verdict(noAdb) == "NO ADB" &&
        MtkPostFlashCheck.Facts(noAdb).Any(f => f.Value.Contains("not a flash verdict")),
        "P1.8 VERIFY BOOT: no adb answer is NO ADB, and the report says it is not a flash verdict");

    var adbTimeout = await MtkPostFlashCheck.WaitForBootAsync(
        _ => throw new TimeoutException("adb shell timed out"),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(4), _ => Task.CompletedTask);
    Check(!adbTimeout.AdbAnswered && adbTimeout.Polls == 2,
        "P1.8 VERIFY BOOT: a timeout inside adb is a poll that answered nothing, not a crashed run");

    int zeroCalls = 0;
    var zeroWindow = await MtkPostFlashCheck.WaitForBootAsync(
        _ => { zeroCalls++; return Task.FromResult(""); },
        TimeSpan.Zero, TimeSpan.FromSeconds(4), _ => Task.CompletedTask);
    Check(zeroCalls == 1 && zeroWindow.Polls == 1,
        "P1.8 VERIFY BOOT: a zero-length wait still asks once — one cheap poll, never zero");

    using (var bootCancel = new CancellationTokenSource())
    {
        bootCancel.Cancel();
        bool cancelled = false;
        try
        {
            await MtkPostFlashCheck.WaitForBootAsync(
                _ => Task.FromResult("[sys.boot_completed]: [0]"),
                TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(4), _ => Task.CompletedTask, bootCancel.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled,
            "P1.8 VERIFY BOOT: the operator's cancel aborts the wait instead of being swallowed as an adb timeout");
    }

    // ---- P2.0: chipset coverage — which CPUs this build can boot, checked without a phone -----------
    // Adding a CPU to the native session is adding a recipe for its HW code: the command layer above the boot is
    // chipset-generic, the BROM prelude and DA/EMI images are not. These checks pin the listing, the two ways a
    // recipe tree goes wrong, the offline hash audit, and the refusal an unclaimed HW code now produces.
    var peekGood = MtkBootProfile.Peek(goodProfilePath);
    Check(peekGood.Readable && peekGood.Chipset == "MT6765-TEST" && peekGood.HwCode == 0x0766 &&
          peekGood.BlobCount == 3 && peekGood.BlobBytes == 20 && peekGood.BlobsOnDisk && peekGood.Problem.Length == 0 &&
          peekGood.Label == "MT6765-TEST (HW 0x0766)",
        "P2.0 COVERAGE: Peek reads the chipset, HW code and blob sizes without hashing, and labels a recipe by the code that selects it");

    var peekMissing = MtkBootProfile.Peek(FixturePath("peek-missing", Recipe(GoodBrom, GoodDa),
        ("kamakiri.bin", kamakiri), ("da1.bin", da1)));
    Check(peekMissing.Readable && !peekMissing.BlobsOnDisk && peekMissing.Problem.Contains("da2.bin"),
        "P2.0 COVERAGE: a recipe whose images are not all beside it is still listed, and the missing file is named");

    string brokenFolder = Path.Combine(bootFix, "broken");
    Directory.CreateDirectory(brokenFolder);
    string brokenRecipePath = Path.Combine(brokenFolder, "broken.json");
    File.WriteAllText(brokenRecipePath, "{ this is not a recipe");
    var peekBroken = MtkBootProfile.Peek(brokenRecipePath);
    Check(!peekBroken.Readable && peekBroken.Problem.Length > 0 && peekBroken.Label == "broken.json",
        "P2.0 COVERAGE: a file that is not JSON is reported as unreadable instead of being listed as a CPU");

    var headerList = MtkRecipeAudit.Headers(new[] { goodProfilePath, brokenRecipePath });
    Check(headerList.Count == 2 && headerList[0].Readable && !headerList[1].Readable,
        "P2.0 COVERAGE: the listing keeps recipe order and marks only the unreadable one");
    string coverageLine = MtkRecipeAudit.CoverageLine(headerList);
    Check(coverageLine.Contains("2 recipe(s) installed") &&
          coverageLine.Contains("MT6765-TEST (HW 0x0766)") &&
          coverageLine.Contains("1 refused") && !coverageLine.Contains(" \u2014 "),
        "P2.0 COVERAGE: one line names the installed CPU, its HW code and how many recipes were refused (no em dash, so the screen keeps it whole)");
    Check(MtkRecipeAudit.CoverageLine(Array.Empty<MtkRecipeHeader>()).Contains("mtk\\boot-profiles"),
        "P2.0 COVERAGE: an empty tree says there is no CPU this build can boot, and where a recipe goes");
    Check(MtkRecipeAudit.CoverageNames(headerList) == "MT6765-TEST (HW 0x0766)",
        "P2.0 COVERAGE: the short form names only the readable CPUs, for the NATIVE tab's own hint");

    var dupHeaders = MtkRecipeAudit.Headers(new[]
    {
        FixturePath("dup-a", RecipeOn("MT6765-TEST", "0x0766", GoodBrom, GoodDa), AutoBlobs),
        FixturePath("dup-b", RecipeOn("MT6765-CLONE", "0x0766", GoodBrom, GoodDa), AutoBlobs)
    });
    Check(MtkRecipeAudit.Problems(dupHeaders).Any(p => p.Contains("HW 0x0766") &&
            p.Contains("MT6765-TEST") && p.Contains("MT6765-CLONE") && p.Contains("only one can stay")),
        "P2.0 COVERAGE: two recipes claiming one HW code are flagged by name — Auto would run the first and the second would silently never boot");

    var problemList = MtkRecipeAudit.Problems(MtkRecipeAudit.Headers(new[] { peekMissing.Path, brokenRecipePath }));
    Check(problemList.Any(p => p.Contains("da2.bin")) && problemList.Any(p => p.Contains("broken.json")),
        "P2.0 COVERAGE: a missing image and an unreadable recipe are both reported before a phone is plugged in");

    var verifyGood = MtkRecipeAudit.Verify(new[] { goodProfilePath });
    Check(verifyGood.Count == 1 && verifyGood[0].Loaded && verifyGood[0].BromSteps == bootProfile.BromSteps.Count &&
          verifyGood[0].DaSteps == bootProfile.DaSteps.Count && verifyGood[0].BlobBytes == 20,
        "P2.0 COVERAGE: the offline audit runs the real gate — hash-verified blobs and both step counts, no phone needed");
    Check(MtkRecipeAudit.VerifyLine(verifyGood[0]).Contains("hash-verified") &&
          MtkRecipeAudit.VerifySummary(verifyGood) == "1 recipe(s) audited: 1 hash-verified, 0 refused",
        "P2.0 COVERAGE: the audit prints what it proved in one row and one headline");

    var verifySwapped = MtkRecipeAudit.Verify(new[]
    {
        FixturePath("swapped-da", Recipe(GoodBrom, GoodDa),
            ("kamakiri.bin", kamakiri), ("da1.bin", da1), ("da2.bin", new byte[] { 0xAA, 0xA1, 0xA2, 0xA3 }))
    });
    Check(verifySwapped.Count == 1 && !verifySwapped[0].Loaded && verifySwapped[0].Problem.Contains("sha256") &&
          MtkRecipeAudit.VerifyLine(verifySwapped[0]).Contains("refused"),
        "P2.0 COVERAGE: a recipe whose image does not match its hash is refused by the offline audit with the message a boot would give");
    var verifyBroken = MtkRecipeAudit.Verify(new[] { brokenRecipePath });
    Check(verifyBroken.Count == 1 && !verifyBroken[0].Loaded && verifyBroken[0].Label == "broken.json" &&
          MtkRecipeAudit.VerifySummary(verifyBroken).Contains("0 hash-verified, 1 refused"),
        "P2.0 COVERAGE: an unreadable recipe is one refused row in the audit, not an exception that stops the run");

    string unknownChip = MtkRecipeAudit.UnknownChipText(0x6768,
        new[] { ("MT6765", (ushort)0x0766), ("MT6877", (ushort)0x0959) });
    Check(unknownChip.Contains("HW 0x6768") && unknownChip.Contains("MT6765 on 0x0766") &&
          unknownChip.Contains("MT6877 on 0x0959") && unknownChip.Contains("mtk\\boot-profiles") &&
          unknownChip.Contains("Nothing chipset-specific was sent"),
        "P2.0 COVERAGE: an unclaimed HW code names the code, the CPUs that are installed, where a recipe goes, and that the phone was not sprayed");
    Check(MtkRecipeAudit.UnknownChipText(0x6768, Array.Empty<(string, ushort)>()).Contains("none installed"),
        "P2.0 COVERAGE: with an empty tree the same refusal says none are installed instead of naming a CPU it does not have");

    // ---- P2.1: what the next CPU still needs — loader packs joined against installed recipes ------------
    // "Add other CPUs the same way" is two inputs: DA/EMI images (already on disk for two dozen chipsets) and one
    // recorded boot per chipset (not on disk). This pins the join, so the tab names the next CPU and what it needs
    // instead of the operator discovering it as a failed plug-in.
    string packRoot = Path.Combine(dir, "mtk-loaders");
    void Pack(string relative, string content = "x")
    {
        string path = Path.Combine(packRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    Pack(Path.Combine("Huawei", "DA_PL_M6768_HONOR", "preloader.bin"));
    Pack(Path.Combine("Huawei", "DA_PL_M6768_HONOR", "da.auth"));
    Pack(Path.Combine("Vivo", "MT6833_DA1", "da1.bin"));
    Pack(Path.Combine("Notes", "readme.txt"));
    Pack(Path.Combine("Misc", "no-chipset-here.bin"));

    var packs = MtkRecipeWorkshop.ScanPacks(packRoot);
    Check(packs.Count == 2 && packs[0].Chipset == "MT6768" && packs[0].Images == 1 && packs[0].AuthFiles == 1 &&
          packs[0].Files == 2 && packs[0].Folder.Contains("DA_PL_M6768_HONOR") &&
          packs[1].Chipset == "MT6833" && packs[1].Images == 1 && packs[1].AuthFiles == 0,
        "P2.1 NEXT CPU: the loader scan groups images by the chipset their path names, counts .bin and .auth separately, and skips files that name no chipset");
    Check(MtkRecipeWorkshop.ScanPacks(Path.Combine(dir, "no-such-loaders")).Count == 0,
        "P2.1 NEXT CPU: a missing loader root is an empty list, not an exception on tab build");

    Check(MtkRecipeWorkshop.ChipsetToken(Path.Combine("Xiaomi", "MTK_AllInOne_DA_6761", "da.bin")) == "MT6761" &&
          MtkRecipeWorkshop.ChipsetToken("Misc/no-chipset-here.bin") == "" &&
          MtkRecipeWorkshop.ChipsetToken(Path.Combine("Oppo-Realme-OnePlus", "DA_BR_MT6765_20271", "x.bin")) == "MT6765",
        "P2.1 NEXT CPU: the MT form is read from either spelling, and a bare model number is never mistaken for a chipset");
    Check(MtkRecipeWorkshop.ChipsetToken(Path.Combine("Samsung", "MTK_AllInOne_DA-S96616AA1", "da.bin")) == "",
        "P2.1 NEXT CPU: a model code inside an MTK_AllInOne folder name is not read as a chipset, so the queue cannot invent a CPU");

    var packCovered = new[] { new MtkRecipeHeader("a", "MT6768", 0x0768, 1, 1, true, "") };
    var missingPacks = MtkRecipeWorkshop.WithoutRecipe(packs, packCovered);
    Check(missingPacks.Count == 1 && missingPacks[0].Chipset == "MT6833",
        "P2.1 NEXT CPU: a chipset whose recipe is installed is not listed as missing, so the list only grows the queue");

    string nextLine = MtkRecipeWorkshop.NextCpuLine(packs, packCovered);
    Check(nextLine.StartsWith("1 CPU(s) have loader packs but no recipe yet: MT6833") &&
          nextLine.Contains("next is MT6833 (1 file(s) under Vivo\\MT6833_DA1)") &&
          nextLine.Contains("one recorded BROM-to-DA boot") && !nextLine.Contains(" \u2014 "),
        "P2.1 NEXT CPU: one line names the CPUs that only lack a capture, which one is next, and the exact missing input (no em dash, so the screen keeps it whole)");

    var covered = new[]
    {
        new MtkRecipeHeader("a", "MT6768", 0x0768, 1, 1, true, ""),
        new MtkRecipeHeader("b", "MT6833", 0x6833, 1, 1, true, "")
    };
    Check(MtkRecipeWorkshop.NextCpuLine(packs, covered) == "every CPU with a loader pack (2) already has a recipe",
        "P2.1 NEXT CPU: when every packed CPU is covered the line says so instead of printing an empty queue");
    Check(MtkRecipeWorkshop.NextCpuLine(Array.Empty<MtkLoaderPack>(), covered).Contains("no loader pack is installed either"),
        "P2.1 NEXT CPU: an empty pack tree names the other missing half instead of blaming the recipe");

    for (int i = 0; i < 12; i++)
        Pack(Path.Combine("Bulk", "MT68" + (10 + i).ToString("00"), "da.bin"));
    var bulkPacks = MtkRecipeWorkshop.ScanPacks(packRoot);
    string bulkLine = MtkRecipeWorkshop.NextCpuLine(bulkPacks, Array.Empty<MtkRecipeHeader>());
    Check(bulkLine.StartsWith("14 CPU(s) have loader packs but no recipe yet: ") && bulkLine.Contains("+6 more"),
        "P2.1 NEXT CPU: a long queue is capped and counts the rest, so the tab line stays one line");

    // ---- P2.1: the two engines, one per-CPU table ----------------------------------------------------
    // The standard engine picks a catalog DA by chipset; the native session replays a recipe. A phone on the bench
    // needs both answers at once, and the table is what says which engine can serve this CPU today.
    var coverage = MtkRecipeWorkshop.Coverage(packs, packCovered,
        chip => chip == "MT6833" ? "Vivo\\MT6833_DA1 (name+code)" : "",
        new[] { "MT6833" });
    Check(coverage.Count == 2 && coverage[0].Chipset == "MT6768" && coverage[0].NativeRecipe && !coverage[0].EngineServed &&
          !coverage[0].DedicatedPayload &&
          coverage[1].Chipset == "MT6833" && !coverage[1].NativeRecipe && coverage[1].EngineServed &&
          coverage[1].DedicatedPayload,
        "P2.1 COVERAGE TABLE: each CPU carries both verdicts — a catalog loader for the MTK engine, whether a dedicated BROM payload exists, and whether a native recipe exists");
    Check(MtkRecipeWorkshop.CoverageSummary(coverage) ==
          "2 CPU(s): 1 served by the MTK engine (1 with a catalog DA, 1 with a dedicated BROM payload), 0 also have a native recipe, 0 with neither",
        "P2.1 COVERAGE TABLE: the summary counts served, catalog DA, payload, both-engine and unserved CPUs separately");
    Check(MtkRecipeWorkshop.CoverageSummary(MtkRecipeWorkshop.Coverage(packs, Array.Empty<MtkRecipeHeader>(), _ => "")) ==
          "2 CPU(s): 0 served by the MTK engine (0 with a catalog DA, 0 with a dedicated BROM payload), 0 also have a native recipe, 2 with neither",
        "P2.1 COVERAGE TABLE: a CPU with no loader, no payload and no recipe is counted as unserved instead of silently dropped");

    string coverageMd = MtkRecipeWorkshop.CoverageMarkdown(coverage, "2026-11-07 12:00");
    Check(coverageMd.Contains("| CPU | MTK engine") && coverageMd.Contains("| BROM payload |") &&
          coverageMd.Contains("| MT6768 | no loader | generic fallback | yes |") &&
          coverageMd.Contains("| MT6833 | Vivo\\MT6833_DA1 (name+code) | dedicated | no - needs one recorded boot |"),
        "P2.1 COVERAGE TABLE: the markdown names the loader, the BROM payload and the native recipe per CPU, and a missing native recipe says what it needs");

    // A chip mtkclient ships a dedicated payload for is served by the standard engine through its built-in DA
    // even with no catalog loader and no recipe — the whole payload set must appear, not only the catalog subset.
    var payloadOnly = MtkRecipeWorkshop.Coverage(packs, packCovered,
        chip => chip == "MT6833" ? "Vivo\\MT6833_DA1 (name+code)" : "",
        new[] { "MT6833", "MT6765" });
    var pOnly = payloadOnly.SingleOrDefault(r => r.Chipset == "MT6765");
    Check(payloadOnly.Count == 3 && pOnly != null && !pOnly.EngineServed && pOnly.DedicatedPayload && !pOnly.NativeRecipe,
        "P2.1 COVERAGE TABLE: a dedicated-payload CPU with no catalog loader or recipe still gets a row, so the full mtkclient payload set is enumerated");
    Check(pOnly != null && MtkRecipeWorkshop.CoverageMarkdown(new[] { pOnly }, "x").Contains("| MT6765 | mtkclient built-in DA | dedicated |"),
        "P2.1 COVERAGE TABLE: a payload-only CPU is shown as served by mtkclient's built-in DA, not 'no loader'");
    Check(MtkRecipeWorkshop.CoverageSummary(payloadOnly) ==
          "3 CPU(s): 2 served by the MTK engine (1 with a catalog DA, 2 with a dedicated BROM payload), 0 also have a native recipe, 0 with neither",
        "P2.1 COVERAGE TABLE: the summary counts a payload-only CPU as served by the MTK engine");
    Check(!MtkRecipeWorkshop.HasDedicatedPayload("") && !MtkRecipeWorkshop.HasDedicatedPayload("not-a-chip") &&
          !MtkRecipeWorkshop.HasDedicatedPayload("MT676"),
        "P2.1 COVERAGE TABLE: the payload notice only answers for a well-formed MT#### token");

    string payloadRoot = Path.Combine(dir, "mtk-payloads");
    Directory.CreateDirectory(payloadRoot);
    File.WriteAllText(Path.Combine(payloadRoot, "mt6739_payload.bin"), "x");
    File.WriteAllText(Path.Combine(payloadRoot, "mt6833_payload.bin"), "x");
    File.WriteAllText(Path.Combine(payloadRoot, "da_x.bin"), "x");
    File.WriteAllText(Path.Combine(payloadRoot, "mt6765_payload.bin.bak"), "x");
    var payloadsFound = MtkRecipeWorkshop.ScanPayloads(payloadRoot);
    Check(payloadsFound.SequenceEqual(new[] { "MT6739", "MT6833" }) &&
          MtkRecipeWorkshop.ScanPayloads(Path.Combine(dir, "no-payloads")).Count == 0,
        "P2.1 COVERAGE TABLE: the BROM payload scan reads only chip-named payloads, so a chip with no dedicated exploit is not called supported");

    var throwing = MtkRecipeWorkshop.Coverage(packs, Array.Empty<MtkRecipeHeader>(),
        _ => throw new IOException("loader tree is unreadable"));
    Check(throwing.Count == 2 && throwing.All(r => !r.EngineServed),
        "P2.1 COVERAGE TABLE: an unreadable loader tree is an unserved cell, not an exception that stops the report");

    // ---- SPD FDL pairing: one folder must carry both halves (a mixed pair bricks the phone) ----
    string spdRoot = Path.Combine(dir, "spd-loaders");
    string spdFull = Path.Combine(spdRoot, "sc9863a_Realme_C11");
    string spdHalf1 = Path.Combine(spdRoot, "7731e_fdl1_only");
    string spdHalf2 = Path.Combine(spdRoot, "9832e_fdl2_only");
    string spdPlain = Path.Combine(spdRoot, "9820a_generic");
    string spdNested = Path.Combine(spdRoot, "nested_only");
    Directory.CreateDirectory(spdFull);
    Directory.CreateDirectory(spdHalf1);
    Directory.CreateDirectory(spdHalf2);
    Directory.CreateDirectory(spdPlain);
    Directory.CreateDirectory(Path.Combine(spdNested, "inner"));
    foreach (string name in new[] { "fdl1-dl.bin", "fdl1-sign.bin", "fdl2-dl.bin", "fdl2-sign.bin" })
        File.WriteAllText(Path.Combine(spdFull, name), "x");
    File.WriteAllText(Path.Combine(spdHalf1, "fdl1.bin"), "x");
    File.WriteAllText(Path.Combine(spdHalf1, "notes.txt"), "x");
    File.WriteAllText(Path.Combine(spdHalf2, "fdl2.bin"), "x");
    File.WriteAllText(Path.Combine(spdPlain, "fdl1.bin"), "x");
    File.WriteAllText(Path.Combine(spdPlain, "fdl2.bin"), "x");
    File.WriteAllText(Path.Combine(spdNested, "inner", "fdl1.bin"), "x");
    File.WriteAllText(Path.Combine(spdNested, "inner", "fdl2.bin"), "x");

    var paired = SpdLoaderPair.FromFolder(spdFull);
    Check(paired != null && Path.GetFileName(paired.Fdl1) == "fdl1-sign.bin" &&
          Path.GetFileName(paired.Fdl2) == "fdl2-sign.bin",
        "SPD LOADER PAIR: a folder holding -sign and -dl copies yields the signed pair");
    var plainPair = SpdLoaderPair.FromFolder(spdPlain);
    Check(plainPair != null && Path.GetFileName(plainPair.Fdl1) == "fdl1.bin" &&
          Path.GetFileName(plainPair.Fdl2) == "fdl2.bin",
        "SPD LOADER PAIR: an unsigned-only folder still yields its own pair");
    Check(SpdLoaderPair.FromFolder(spdHalf1) == null && SpdLoaderPair.FromFolder(spdHalf2) == null,
        "SPD LOADER PAIR: a folder with only one half is refused instead of handing back half a pair");
    Check(SpdLoaderPair.FromFolder(spdNested) == null,
        "SPD LOADER PAIR: a pair buried in a subfolder does not count — only the folder's own files do");
    Check(SpdLoaderPair.FromFolder(Path.Combine(dir, "no-such-loaders")) == null &&
          SpdLoaderPair.FromFolder(null) == null && SpdLoaderPair.FromFolder("") == null,
        "SPD LOADER PAIR: a missing or empty folder is null, not an exception");
    var firstPair = SpdLoaderPair.FirstFromFolders(new[] { spdHalf1, spdHalf2, spdFull });
    Check(firstPair != null && firstPair.Folder == spdFull,
        "SPD LOADER PAIR: the scan skips FDL1-only and FDL2-only folders and takes the first complete pair");
    Check(SpdLoaderPair.FirstFromFolders(new[] { spdHalf1, spdHalf2 }) == null &&
          SpdLoaderPair.FirstFromFolders(Array.Empty<string>()) == null &&
          SpdLoaderPair.FirstFromFolders(null) == null,
        "SPD LOADER PAIR: two half-folders never add up to a pair (the old recursive scan stitched them)");
    Check(SpdLoaderPair.SameFolder(Path.Combine(spdFull, "fdl1-sign.bin"), Path.Combine(spdFull, "FDL2-SIGN.BIN")) &&
          !SpdLoaderPair.SameFolder(Path.Combine(spdFull, "fdl1-sign.bin"), Path.Combine(spdPlain, "fdl2.bin")) &&
          !SpdLoaderPair.SameFolder("fdl1.bin", ""),
        "SPD LOADER PAIR: the mixed-source warning fires only when the two halves really live in different folders");

    Console.WriteLine($"{passed} regression checks passed.");
    if (skipped > 0) Console.WriteLine($"{skipped} checks skipped (bundled tools not in this checkout).");


}
finally { Directory.Delete(dir, recursive: true); }

// Progress<T> dispatches on the thread pool - synchronous recorder avoids assertion races
internal sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> V { get; } = new();
    public void Report(T value) { lock (V) V.Add(value); }
}

/// <summary>
/// A scripted MTK device for the boot-recipe replay and the B3 command layer: it answers with the recorded
/// reply frames in order and records every byte the session sends. Overriding one reply is how the boot's
/// fail-closed paths (wrong HW code, refused status, missing SYNC) get exercised without a phone, and
/// <see cref="Append"/> scripts what the DA answers after the boot — which is the only honest way to test
/// either half, because on real hardware each of those mistakes is a brick instead of an assertion.
/// </summary>
internal sealed class FakeMtkTransport : IMtkTransport
{
    private readonly List<string> _replies;
    private readonly Queue<byte[]> _staged = new();
    private int _next;

    public List<byte[]> Writes { get; } = new();
    public long WrittenBytes { get; private set; }

    /// <summary>Replace one reply (by index) with different bytes, to simulate a device that disagrees.</summary>
    public (int Index, string Hex)? ReplyOverride { get; set; }

    /// <summary>How many of the scripted replies to keep back, so an operation can be left mid-flight on purpose.</summary>
    public int HoldBack { get; set; }

    /// <summary>
    /// Answer this exact reply every time the queue runs dry, however many times it is asked — a device that keeps
    /// giving the same answer instead of a script that advances.
    /// </summary>
    public string? AlwaysReply { get; set; }

    public FakeMtkTransport(string[] replies) => _replies = replies.ToList();

    /// <summary>Script what the device answers next, once the boot has consumed its recorded replies.</summary>
    public void Append(IEnumerable<string> more) => _replies.AddRange(more);

    public string Description { get; set; } = "FAKE-COM";

    /// <summary>Set when the session releases the pipe — a closed BROM face and a still-open one are different states.</summary>
    public bool Disposed { get; private set; }

    /// <summary>What the session is told about the bus when it refuses. A scripted device has no bus, so the default is a fixed phrase.</summary>
    public string BusNote { get; set; } = "FAKE-COM is still enumerated as 0e8d:0003 — scripted device, no real bus";

    /// <summary>
    /// Make this write (1-based) and every later one die the way RUN 18's seventh did — <c>libusb -9</c>, nothing
    /// accepted. The failed write is not recorded, because it never left the host.
    /// </summary>
    public int FailWriteAt { get; set; }

    public string FailWriteDetail { get; set; } = "libusb -9 pipe error, 0/1 byte(s) accepted";

    /// <summary>Make the bus re-check itself fail, so the rule "a diagnostic never replaces the real refusal" is testable.</summary>
    public bool BusThrows { get; set; }

    public string BusAfterRefusal() => BusThrows ? throw new IOException("the bus could not be read") : BusNote;

    private void StageNext()
    {
        // HoldBack withholds the tail of the script, which is how a test keeps one operation genuinely in
        // flight: this transport answers instantly, so without it every "async" call finishes synchronously.
        if (_staged.Count > 0) return;
        if (AlwaysReply != null) { _staged.Enqueue(Convert.FromHexString(AlwaysReply)); return; }
        if (_next >= _replies.Count - HoldBack) return;

        // A scripted "" is the device saying nothing at all: the wire stays silent until the session writes again.
        // Every capture shows that shape — a BROM answers the frame it was given and then has nothing more to say —
        // and the tail read has to be able to see it, or a device that answered one byte would look like one that
        // kept talking.
        if (_replies[_next].Length == 0) return;

        string hex = _replies[_next];
        if (ReplyOverride is { } over && over.Index == _next) hex = over.Hex;
        _next++;
        _staged.Enqueue(Convert.FromHexString(hex));
    }

    public int BytesAvailable
    {
        get { StageNext(); return _staged.Count == 0 ? 0 : _staged.Peek().Length; }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        StageNext();
        if (_staged.Count == 0) return 0;
        byte[] head = _staged.Dequeue();
        int take = Math.Min(count, head.Length);
        Array.Copy(head, 0, buffer, offset, take);
        if (take < head.Length) _staged.Enqueue(head[take..]);
        return take;
    }

    public void Write(byte[] data, int offset, int count)
    {
        if (FailWriteAt > 0 && Writes.Count + 1 >= FailWriteAt)
            throw new MtkDaProtocolException("Writing " + count + " byte(s) to " + Description +
                " failed (" + FailWriteDetail + "). The device may have left BROM; the boot was stopped with the " +
                "recipe half-applied.") { TransportFailure = true };
        Writes.Add(data.AsSpan(offset, count).ToArray());
        WrittenBytes += count;
        // A new command opens the wire again after a scripted silence.
        if (_staged.Count == 0 && AlwaysReply == null && _next < _replies.Count - HoldBack && _replies[_next].Length == 0)
            _next++;
    }

    public void Dispose() { Disposed = true; /* the recorder keeps what it caught */ }
}

/// <summary>
/// The endpoint-zero side of the scripted device. Wraps a <see cref="FakeMtkTransport"/> for the recorded bulk
/// walk and answers control transfers from a scripted list of libusb results, recording what the boot asked for.
/// A device under test cannot be present in CI, so the probe expectations — accepted line coding, stalled
/// descriptor read — are exercised here against the recipe's own recorded bytes.
/// </summary>
internal sealed class FakeCtrlTransport : IMtkTransport, IMtkControlTransport
{
    private readonly FakeMtkTransport _inner;

    public FakeCtrlTransport(FakeMtkTransport inner) => _inner = inner;

    /// <summary>The 8-byte setup packet of every control transfer the boot sent, in order, as hex.</summary>
    public List<string> ControlFrames { get; } = new();

    /// <summary>The data stage of every control transfer (payload for OUT, the receive buffer for IN).</summary>
    public List<byte[]> ControlData { get; } = new();

    /// <summary>Scripted libusb results, consumed one per call; once dry every transfer succeeds with its data length.</summary>
    public Queue<int> ControlScript { get; } = new();

    /// <summary>Sit inside every control transfer this long first — RUN B6-1's phone, which left the bus with the
    /// transfer in flight and so never completed it. The session is expected to stop waiting and say so.</summary>
    public int BlockMs { get; set; }

    public FakeMtkTransport Inner => _inner;

    public int ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data, int timeoutMs)
    {
        if (BlockMs > 0) Thread.Sleep(BlockMs);
        int wLength = data?.Length ?? 0;
        var setup = new byte[8];
        setup[0] = bmRequestType;
        setup[1] = bRequest;
        setup[2] = (byte)wValue;
        setup[3] = (byte)(wValue >> 8);
        setup[4] = (byte)wIndex;
        setup[5] = (byte)(wIndex >> 8);
        setup[6] = (byte)wLength;
        setup[7] = (byte)(wLength >> 8);
        ControlFrames.Add(Convert.ToHexString(setup));
        ControlData.Add((byte[])data!.Clone());
        // Past the script the fake is the recorded device: a host-to-device request is accepted in full, a
        // device-to-host read stalls (libusb -9) — which is exactly what RUN 38 measured the phone doing.
        return ControlScript.Count > 0 ? ControlScript.Dequeue()
             : (bmRequestType & 0x80) != 0 ? -9
             : wLength;
    }

    public string Description { get => _inner.Description; set => _inner.Description = value; }
    public string BusAfterRefusal() => _inner.BusAfterRefusal();
    public int BytesAvailable => _inner.BytesAvailable;
    public void Write(byte[] data, int offset, int count) => _inner.Write(data, offset, count);
    public int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// The BROM→DA handoff as a script: hand back the face the test names, throw the way a missing face does, or answer
/// with nothing at all. What a real handoff does — wait for the phone to re-enumerate — is not what is under test
/// here; what the session does with each answer is.
/// </summary>
internal sealed class FakeStageHandoff : IMtkDaStageHandoff
{
    private readonly Func<IMtkTransport?> _open;
    internal FakeStageHandoff(Func<IMtkTransport?> open) => _open = open;

    public IMtkTransport OpenDaStageTransport(string bromDescription) => _open()!;
}

/// <summary>
/// The face proof's second pipe as a script: hand back a fresh pipe, answer with none (the face never came back), or
/// throw the way a face that returned but cannot be claimed does. What a real reclaim does — wait for the node and
/// clear its halt — is not under test; what the proof does with each answer is.
/// </summary>
internal sealed class FakeBromRecovery : IMtkBromRecovery
{
    private readonly Func<IMtkTransport?> _reopen;
    public int Calls { get; private set; }
    public int LastWaitMs { get; private set; }

    internal FakeBromRecovery(Func<IMtkTransport?> reopen) => _reopen = reopen;

    public IMtkTransport Reopen(int waitMs)
    {
        Calls++;
        LastWaitMs = waitMs;
        return _reopen()!;
    }
}

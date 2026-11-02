using PMKUnlocker;
using PMKUnlocker.MediaTek;
using PMKUnlocker.MtkFirmware;
using PMKUnlocker.Qualcomm;
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
    string probe = AppContext.BaseDirectory, projRoot = "";
    while (probe != null && !(Directory.Exists(Path.Combine(probe, "PMKUnlocker")) && Directory.Exists(Path.Combine(probe, "tests"))))
        probe = Path.GetDirectoryName(probe);
    if (probe != null) projRoot = probe;
    Check(!string.IsNullOrEmpty(projRoot), "Project root resolved for wired catalog check");
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
    Check(LcHex(MtkDaFrame.Command(MtkDaCommand.Read)) == "efeeeefe010000000400000005000100", "MTK DA: READ travels as a 4-byte command frame");
    Check(LcHex(MtkDaFrame.Ack()) == "efeeeefe010000000400000000000000", "MTK DA: the host ack is a zero u32le inside its own frame");
    Check(MtkDaFrame.TryParseHeader(Convert.FromHexString("efeeeefe010000000c000000"), out uint declaredLen) && declaredLen == 12,
        "MTK DA: the declared length is read little-endian (0x0c000000 would be 201 MB)");
    Check(!MtkDaFrame.TryParseHeader(Convert.FromHexString("efeeeefe0200000004000000"), out _), "MTK DA: a frame whose type is not 1 is not a DA header");
    Check(!MtkDaFrame.TryParseHeader(Convert.FromHexString("efeeef0e0100000004000000"), out _), "MTK DA: wrong magic is refused");
    Check(!MtkDaFrame.TryParseHeader(new byte[11], out _), "MTK DA: an 11-byte header is refused");
    Check(MtkDaFrame.Header(0).Length == MtkDaFrame.HeaderSize && MtkDaFrame.Pack(new byte[4]).Length == 16,
        "MTK DA: header size 12, and Pack is header + payload");

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
    string noCtrlError = null;
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
    string greetError = null;
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
    string reopenExhausted = null;
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
    var logoFromLba = new MtkDaExtent("logo", 2987008, 2989055, 512);
    Check(logoFromLba.ByteOffset == LogoAt && logoFromLba.ByteLength == 0x100000,
        "MTK B3: LBA × the DA's own block size gives byte 0x5b280000 + 1 MiB — the exact extent the write capture used");
    var probeTable = new MtkDaPartitionTable(512, new[] { logoFromLba });
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
    int afterQueries = qDev.Writes.Count;
    Check(ReferenceEquals(await qSes.QueryLimitsAsync(default), limitsQ) && ReferenceEquals(await qSes.QueryStorageAsync(default), storageQ) &&
          qDev.Writes.Count == afterQueries,
        "MTK B3: both queries are cached, so the rest of the session costs the port nothing and the DA is not asked twice");
    Check(qLines.Any(l => l.Contains("DA limits: write chunk 0x800, transfer 0x400")),
        "MTK B3: the sizes the device advertised are shown to the operator before anything is transferred");
    var (descSes, descDev) = await BootedDa(null, new[] { Status, Status, DaFrame(DescriptorPayload(0xE8F800000ul)[..206]), Status });
    Check((await DaFailure(descSes, () => descSes.QueryStorageAsync(default)))
              .Contains("must be 104 bytes"),
        "MTK B3: a 103-byte descriptor is refused — extents cannot be addressed from a truncated reply");
    Check(descSes.Storage == null, "MTK B3: nothing is cached from a refused descriptor, so the next operation cannot run on a half-read geometry");
    CheckFrames(SentFrames(descDev, 29), "MTK B3: the descriptor query is the recorded preamble + 0x40001 pair", PreFrame, StorageCmd);
    descSes.Dispose();

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
    int afterRead = rDev.Writes.Count;
    rDev.Append(ReadGroup(Filler(0x5A, 1024), Filler(0x5A, 1024), Filler(0x5A, 1024), Filler(0x5A, 1024)));
    byte[] secondRead = await rSes.ReadExtentAsync(LogoAt, LogoLen);
    Check(secondRead.All(b => b == 0x5A), "MTK B3 READ: a second extent on the same session reads the device, not the previous answer");
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

    Console.WriteLine($"{passed} regression checks passed.");


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

    public FakeMtkTransport Inner => _inner;

    public int ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data, int timeoutMs)
    {
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

using PMKUnlocker;
using PMKUnlocker.MediaTek;
using PMKUnlocker.MtkFirmware;
using PMKUnlocker.Qualcomm;
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

    Console.WriteLine($"{passed} regression checks passed.");
}
finally { Directory.Delete(dir, recursive: true); }

// Progress<T> dispatches on the thread pool - synchronous recorder avoids assertion races
internal sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> V { get; } = new();
    public void Report(T value) { lock (V) V.Add(value); }
}

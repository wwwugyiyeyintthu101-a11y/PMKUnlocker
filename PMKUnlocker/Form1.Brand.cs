#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using PMKUnlocker.Qualcomm;
using PMKUnlocker.Spreadtrum;

namespace PMKUnlocker
{
    public partial class Form1
    {
        // ================= BRAND INFO / KNOX (adb) =================
        private async Task RunBrandInfoAsync(string brand)
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   " + brand.ToUpper() + " - DEVICE INFO", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!devices.Contains("\tdevice"))
            {
                Log("[!] No ADB device found - enable USB debugging and connect the phone.", Color.OrangeRed);
                return;
            }

            string brandProp = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.brand");
            string model = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.model");
            string device = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.device");
            string android = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.release");
            string sdk = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.sdk");
            string patch = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.security_patch");
            string build = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.display.id");
            string serial = await ExecuteCommandQuickAsync("adb.exe", "get-serialno");
            string csc = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.csc.sales_code");
            string bl = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.bootloader");

            Log("  • Serial         : " + serial.Trim(), Color.White);
            Log("  • Brand          : " + brandProp.Trim(), Color.White);
            Log("  • Model          : " + model.Trim(), Color.White);
            Log("  • Codename       : " + device.Trim(), Color.White);
            Log("  • Android        : " + android.Trim() + " (SDK " + sdk.Trim() + ")", Color.White);
            Log("  • Build          : " + build.Trim(), Color.White);
            Log("  • Security Patch : " + patch.Trim(), Color.White);
            if (!string.IsNullOrWhiteSpace(csc)) Log("  • CSC / Region   : " + csc.Trim(), Color.White);
            if (!string.IsNullOrWhiteSpace(bl)) Log("  • Bootloader     : " + bl.Trim(), Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        private async Task RunKnoxStatusAsync()
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - KG / KNOX STATUS", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!devices.Contains("\tdevice"))
            {
                Log("[!] No ADB device found.", Color.OrangeRed);
                return;
            }

            string kg = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.kg.state");
            string kgId = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.kg.bit");
            string warranty = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.warranty_bit");
            string vboot = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.verifiedbootstate");
            string flashLock = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.flash.locked");

            Log("  • KG State       : " + (string.IsNullOrWhiteSpace(kg) ? "(n/a)" : kg.Trim()), Color.White);
            Log("  • KG Bit         : " + (string.IsNullOrWhiteSpace(kgId) ? "(n/a)" : kgId.Trim()), Color.White);
            Log("  • Warranty Bit   : " + (string.IsNullOrWhiteSpace(warranty) ? "(n/a)" : warranty.Trim()), Color.White);
            Log("  • Verified Boot  : " + (string.IsNullOrWhiteSpace(vboot) ? "(n/a)" : vboot.Trim()), Color.White);
            Log("[*] Removing the KG/Knox lock needs a Samsung-specific path (this tab only reads status).", Color.Orange);
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        // FRP Reset — adb (USB debugging) ရနိုင်တဲ့ Samsung/SPD/Huawei အတွက် setup wizard ကျော်နည်း
        // ADB FRP Reset (no root)
        // မှတ်ချက်: Android 11/12+ မှာ `pm clear <system pkg>` ကို shell ကို ခွင့်မပြုတော့ဘူး
        //   ("does not have permission android.permission.CLEAR_APP_USER_DATA") — ဒါက ပုံမှန်။
        //   အလုပ်လုပ်တဲ့ အဓိက အဆင့်တွေက provisioning/setup_complete သတ်မှတ်တာ + setup wizard ကို
        //   user 0 အတွက် disable လုပ်တာ ဖြစ်တယ်။ ဒါကြောင့် pm clear ကို "best effort" အနေနဲ့ပဲ လုပ်ပြီး
        //   block ဖြစ်ရင် error မပြဘဲ မှတ်ချက်ပဲ ပြတယ် (FAIL banner မထုတ်တော့ဘူး)။
        private async Task RunAdbFrpResetAsync(string brand, bool skipConfirm = false)
        {
            if (!skipConfirm)
            {
                if (MessageBox.Show(
                        brand + " - FRP Reset (adb နည်းလမ်း)" + Environment.NewLine + Environment.NewLine +
                        "Setup wizard ကို ကျော်ပြီး Google account verification ကို ဖျက်ပါမယ်။" + Environment.NewLine +
                        "• Root မလိုပါ၊ ဒါပေမဲ့ adb (USB debugging) ရနိုင်ရမယ်" + Environment.NewLine +
                        "• Android 11/12+ မှာ `pm clear` ကို system က ပိတ်ထားတယ် — settings + wizard disable နဲ့ လုပ်မယ်" + Environment.NewLine +
                        "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)" + Environment.NewLine + Environment.NewLine +
                        "ဆက်လုပ်မလား?",
                        "Confirm FRP Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   " + brand.ToUpper() + " - FRP RESET (ADB / no root)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("Removing FRP...", Color.Orange);

            // ၁။ အဓိက အဆင့် — provisioning + setup complete (ဒါက Android 12+ မှာ အလုပ်လုပ်တယ်)
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put secure user_setup_complete 1");
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put global device_provisioned 1");
            string chk = (await ExecuteCommandQuickAsync("adb.exe",
                "shell settings get global device_provisioned")).Trim();
            Log("[OK] device_provisioned=1, user_setup_complete=1" +
                (string.IsNullOrWhiteSpace(chk) ? "" : "  (verify: device_provisioned=" + chk + ")"), Color.LightGreen);

            // ၂။ Setup wizard ကို user 0 အတွက် disable (best effort — package အလိုက် ကွာတယ်)
            string[] wizards = { "com.google.android.setupwizard", "com.android.setupwizard",
                                 "com.google.android.pixel.setupwizard", "com.sec.android.app.setupwizard" };
            // Huawei / HiSilicon ကို HW setup wizard တွေပါ ထည့်
            if (brand.Contains("Huawei", StringComparison.OrdinalIgnoreCase) ||
                brand.Contains("HiSilicon", StringComparison.OrdinalIgnoreCase))
            {
                wizards = wizards.Concat(new[]
                {
                    "com.huawei.android.hwsetupwizard",
                    "com.huawei.android.wizard",
                    "com.huawei.hwsetupwizard",
                    "com.huawei.systemapp.setupwizard"
                }).ToArray();
                Log("[i] Huawei setup wizard packages included.", Color.Gray);
            }
            int disabled = 0;
            foreach (string pkg in wizards)
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm disable-user --user 0 " + pkg)).Trim();
                string low = outp.ToLowerInvariant();
                if (low.Contains("new state: disabled"))
                {
                    Log("[OK] " + pkg + " → disabled (user 0)", Color.LightGreen);
                    disabled++;
                }
                else if (low.Contains("securityexception") || low.Contains("not allowed") || low.Contains("permission"))
                {
                    Log("[i] " + pkg + " → disable blocked by system policy (Android 12+ normal)", Color.Gray);
                }
                else if (low.Contains("unknown package") || low.Contains("unable to find") || low.Contains("not installed"))
                {
                    // ဒီ device မှာ မရှိ — ကျော်
                }
                else if (!string.IsNullOrWhiteSpace(outp))
                {
                    Log("[i] " + pkg + " → " + outp.Split('\n')[0], Color.Gray);
                }
            }
            if (disabled == 0)
                Log("[i] Setup wizard disable was not allowed - the provisioning settings alone apply (try reboot).", Color.Orange);

            // ၃။ pm clear (best effort — Android 11+ မှာ block ဖြစ်တာ ပုံမှန်)
            var clearPkgs = new List<string> { "com.google.android.setupwizard", "com.google.android.gms" };
            if (brand.Contains("Huawei", StringComparison.OrdinalIgnoreCase) ||
                brand.Contains("HiSilicon", StringComparison.OrdinalIgnoreCase))
            {
                clearPkgs.Add("com.huawei.android.hwsetupwizard");
                clearPkgs.Add("com.huawei.android.wizard");
            }
            foreach (string pkg in clearPkgs)
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm clear " + pkg)).Trim();
                if (outp.ToLowerInvariant().Contains("success"))
                    Log("[OK] " + pkg + " data cleared", Color.LightGreen);
                else
                    Log("[i] " + pkg + " clear → blocked (Android 11+ normal) — skip", Color.Gray);
            }

            Log("[i] Press Reboot System (or restart the phone) - the launcher should come up.", Color.Orange);
            Log("[OK] ADB FRP steps done.", Color.LightGreen);
        }

        private async void BtnSamFrp_Click(object sender, EventArgs e) { await RunAdbFrpResetAsync("Samsung"); }
        private async void BtnSpdFrp_Click(object sender, EventArgs e) { await RunAdbFrpResetAsync("Spreadtrum"); }
        private async void BtnHisiFrp_Click(object sender, EventArgs e) { await RunAdbFrpResetAsync("HiSilicon / Huawei"); }

        // ================= XIAOMI TEMP ROOT FRP (ADB) =================
        // USB debugging ရှိပြီးသား → ADB ကနေ temp root (su / kernel exploit) သုံးပြီး FRP ဖြေရှင်း
        // - su uid=0 ရရင်: accounts*.db + device_policies + MIUI/Xiaomi pkgs + frp partition

    }
}

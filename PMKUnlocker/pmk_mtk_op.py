#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PMK MTK helper — mtkclient CLI ကို wrap လုပ်ပြီး operation ပြီးရင် ဖုန်းကို
Android ထဲ တကယ် ပြန်တက်စေတယ်၊ ပြီးရင် တကယ်တက်/မတက် ကိုယ်တိုင် စစ်ပေးတယ်။

ဘာကြောင့် လိုအပ်လဲ (source ထဲ စစ်တွေ့ချက်):
  • mtkclient ရဲ့ CLI `reset` command က DA shutdown(bootmode=0) ပို့ပြီး
    "Reset command was sent. Disconnect usb cable to power off." လို့ ပြတယ် —
    ဆိုလိုတာက auto reboot မဟုတ်ဘူး၊ user ကိုယ်တိုင် cable ဖြုတ်ရမယ်။
  • MT6833 / k6833 (hwcode 0x989) အတွက် mtkclient က damode=XFLASH သုံးတယ်
    (config/brom_config.py) — DAXFlash.shutdown() ရဲ့ packet မှာ bootmode
    (0=power off, 1=home screen, 2=fastboot) နဲ့ leaveusb flag ပါတယ်။
    mtkclient က leaveusb=0 ထားတာမို့ DA က USB ကို မဖြုတ်ဘူး → ဖုန်းက USB
    ပေါ်မှာတင် ဆက်ရှိနေပြီး Android မတက်တာ ဒီကနေ လာတယ်။
  • ဒါကြောင့် bootmode=2 (fastboot) + leaveusb=1 ပို့ပြီး၊ ဖုန်း fastboot ရောက်ရင်
    PC ကနေ `fastboot reboot` ဆက်ပို့၊ ပြီးရင် တကယ် တက်/မတက် device ကို စစ်တယ်။

Crash resilience (အရေးကြီး):
  • ဖုန်းက BROM mode မဟုတ်ဘဲ (preloader/DA mode စတဲ့) အခြေအနေမှာ ရှိနေရင်
    mtkclient ရဲ့ BROM handshake က libusb ကို ထပ်ခါတလဲလဲ ခေါ်ရင်း **native crash**
    (access violation, exit 0xC0000005) ဖြစ်တယ် — Python က ဒါကို မဖမ်းနိုင်ဘူး။
  • ဒါကြောင့် mtkclient CLI ကို **child process** နဲ့ run တယ် (`--internal-cli`)။
    Child သေသွားရင်လည်း ဒီ script က ဆက်အလုပ်လုပ်ပြီး user ကို ဘာလုပ်ရမလဲ
    ပြနိုင်တယ် + တစ်ခါ ပြန်စမ်းပေးတယ်။

Device ဆိုင်ရာ တွေ့ချက် (MT6833 / k6833, UFS — ဒီစက်မှာ စမ်းသပ်ပြီး):
  • DAXFlash.shutdown() (bootmode=1/2, leaveusb=0/1 မရွေး) ကို DA က protocol အဆင့်မှာ
    လက်ခံပေမယ့် **ဖုန်းကို တကယ် reboot မလုပ်ဘူး** — ဖုန်းက USB ပေါ်မှာတင် (BROM/DA
    mode, VID_0E8D:PID_0003) ဆက်ရှိနေပြီး Android မတက်။
  • ဒါကြောင့် BROM JUMP_BL (boot chain ကို ဆက်တက်စေတဲ့ command) ကို ထပ်ဆင့် စမ်းတယ် —
    ဒါပေမယ့် reset ပြီးနောက် BROM က handshake ကို မဖြေတော့ဘူး → kick မရဘူး။
  • ရလဒ်: ဒီ device မှာ operation ပြီးရင် **physical power နှိပ် (၁၀-၁၅ စက္ကန့်)** မှ
    Android တက်တယ်။ Tool က အဲဒါကို ရှင်းရှင်းလင်းလင်း ပြတယ် (မဟုတ်တဲ့ အောင်/မအောင်
    မပြဘူး)။

အသုံးပြုပုံ:
  python pmk_mtk_op.py multi "e frp;reset"

Environment:
  PMK_MTK_BOOTMODE        2=fastboot (default) | 1=home screen | 0=power off
  PMK_MTK_LEAVEUSB        1=shutdown အချိန် DA က USB ဖြုတ် (default) | 0=မဖြုတ်
  PMK_MTK_BOOT_WAIT       fastboot/ADB ကို စောင့်တဲ့ စက္ကန့် (default 24)
  PMK_MTK_ANDROID_WAIT    fastboot reboot နောက် စောင့်တဲ့ စက္ကန့် (default 40)
  PMK_MTK_CONNECT_RETRIES device မရှိရင် ပြန်စမ်းတဲ့ အကြိမ် (default 20)
  PMK_MTK_CRASH_RETRIES   native crash ဖြစ်ရင် ပြန်စမ်းတဲ့ အကြိမ် (default 1)
  PMK_MTK_KICK            JUMP_BL kick အဆင့် — default 0 (ပိတ်)၊ တခြား device အတွက် 1
  PMK_MTK_SERIAL          auto (default) | off (USB အတင်း) | COM5 (port တိုက်ရိုက်)
  PMK_MTK_KICK_NOPROGRESS / PMK_MTK_KICK_TIMEOUT / PMK_MTK_BOOT_WAIT / PMK_MTK_ANDROID_WAIT

UI (PMKUnlocker) ဖတ်တဲ့ markers — stdout မှာ ASCII သက်သက် ပေါ်တယ်:
  PMK_OK | PMK_ERROR: <reason> | PMK_NEED_REPLUG | PMK_MTK_NOT_BROM
  PMK_DA_CRASH | PMK_DA_REBOOT_FAILED | PMK_BOOT_FASTBOOT | PMK_BOOT_OK |
  PMK_MTK_STILL_ATTACHED | PMK_BOOT_MANUAL
"""

import os
import shutil
import subprocess
import sys
import threading
import time

# Native crash (access violation စတာတွေ) ဖြစ်ရင် Python traceback ကို stderr မှာ
# ရိုက်ထုတ်ပေးတယ် — မဟုတ်ရင် process က စကားမပြောဘဲ သေသွားတတ်တယ် (exit 0xC0000005)။
import faulthandler
faulthandler.enable()

# Windows မှာ stdout/stderr ကို pipe နဲ့ ချိတ်ရင် encoding က locale (charmap) ဖြစ်တတ်တယ် —
# mtkclient က non-ASCII character တွေ ရိုက်တဲ့အခါ UnicodeEncodeError နဲ့ ရပ်သွားနိုင်တယ်
# (အဲဒါက child output ဖတ်တဲ့ loop ကို ဖျက်ပြီး အောင်/မအောင် စစ်တာ မှားသွားစေတယ်)။
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


def _safe_print(text):
    """Encoding ပြဿနာကြောင့် print မရလည်း ဘယ်တော့မှ မရပ်အောင် ရိုက်တယ်။"""
    try:
        print(text)
    except Exception:
        try:
            print(str(text).encode("ascii", "replace").decode("ascii"))
        except Exception:
            pass
    try:
        sys.stdout.flush()
    except Exception:
        pass

# mtkclient ရဲ့ output ထဲ မအောင်မြင်တဲ့ အချက်တွေ — exit code က 0 ဖြစ်နေရင်လည်း
# "အောင်" လို့ မမှတ်ရဘူး (empty/unexpected output = success မဟုတ်)
FAIL_MARKERS = (
    "Please disconnect, start mtkclient and reconnect",
    "Error on running da",
    "Failed to format",
    "Couldn't detect partition",
    "Failed to format all partitions",
    "Error on writing seccfg",
    "Failed to write seccfg",
    "Traceback (most recent call last)",
)

# Windows native crash codes (signed/unsigned နှစ်မျိုးလုံး လာနိုင်တယ်)
CRASH_CODES = (0xC0000005, 0xC0000006, 0xC0000008, 0xC000001D, 0xC0000409, 0xC0000374)

_NO_WINDOW = 0x08000000 if os.name == "nt" else 0

# Child process က ဒီ prefix နဲ့ စတဲ့ line တွေကို parent ဖတ်ပြီး UI မှာ မပြတော့ဘူး
_INTERNAL_PREFIX = "PMK_INTERNAL_"

# reboot command တကယ် ပို့ဖြစ်/မဖြစ် မှတ်ထားဖို့ (patch ထဲက ဖြည့်တယ်)
SHUTDOWN_STATE = {"called": 0, "ok": None}


def _tool_path(name):
    """App folder (ဒီ script ရှိတဲ့နေရာ) → မရှိရင် PATH အစဉ်လိုက် ရှာတယ်။"""
    here = os.path.dirname(os.path.abspath(__file__))
    cand = os.path.join(here, name)
    if os.path.exists(cand):
        return cand
    return shutil.which(name) or name


def _run_tool(args, timeout=20):
    """Tool တစ်ခုကို run ပြီး output ကို ပြန်ပေးတယ် (run မရ/ timeout ဆို "")။"""
    try:
        proc = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              timeout=timeout, creationflags=_NO_WINDOW)
        return proc.stdout.decode("utf-8", "replace")
    except Exception:
        return ""


# ================= MTKCLIENT PATCHES (child process ထဲမှာ တင်တယ်) =================

def _write_child_result():
    """Child ရဲ့ reboot command အခြေအနေကို ဖိုင်ထဲ ရေးတယ် — output stream ပြတ်သွားလည်း
    parent က ဒီဖိုင်ကနေ အောင်/မအောင် ဆက်စစ်နိုင်တယ် (မှားတဲ့ FAIL မပြအောင်)။"""
    path = os.environ.get("PMK_MTK_RESULT_FILE")
    if not path:
        return
    try:
        with open(path, "w", encoding="utf-8") as fh:
            fh.write("shutdown_called=%d\n" % SHUTDOWN_STATE["called"])
            fh.write("shutdown_ok=%d\n" % (1 if SHUTDOWN_STATE["ok"] else 0))
    except Exception:
        pass


def _install_bootmode_patch():
    """DAloader.shutdown() ကို bootmode အစားထိုးတဲ့ version နဲ့ လဲတယ်။
    ပြီးရင် အောင်/မအောင် ကို parent ဆီ marker နဲ့ ပြန်ပို့တယ်။"""
    forced = int(os.environ.get("PMK_MTK_BOOTMODE", "2"))
    from mtkclient.Library.DA.mtk_daloader import DAloader

    original = DAloader.shutdown

    def patched(self, bootmode=0):
        # CLI က reset အတွက် bootmode=0 (power off) ပို့တယ် — ကျွန်တော်တို့က
        # ဖုန်းကို auto reboot ဖြစ်စေချင်တာမို့ လိုချင်တဲ့ bootmode နဲ့ အစားထိုးတယ်။
        res = original(self, bootmode=forced)
        SHUTDOWN_STATE["called"] += 1
        SHUTDOWN_STATE["ok"] = bool(res)
        # output stream ပြတ်သွားလည်း parent သိနိုင်အောင် ဖိုင်ထဲပါ ရေးတယ်
        print("%sshutdown called=1 ok=%d" % (_INTERNAL_PREFIX, 1 if res else 0))
        _write_child_result()
        return res

    DAloader.shutdown = patched
    return forced


def _install_leaveusb_patch():
    """DAXFlash.shutdown() ကို stock နဲ့ တူအောင် (leaveusb=0 + close(reset=True))
    ပြင်တယ် — stock comment အရ leaveusb=0 = "Disconnect usb" ဖြစ်တယ်။ ကျွန်တော်တို့
    leaveusb=1 + close(reset=False) နဲ့ ပြောင်းခဲ့တာ ပြဿနာဖြစ်ခဲ့:
      ① leaveusb=1 ဆိုတာ DA ကို USB ဆက်ချိတ်ထားခိုင်းတာ (backwards)
      ② close(reset=False) က libusb device reset မလုပ် → phone BROM မှာ ကျန်ခဲ့
    stock path (xflash_lib.py:813-833): leaveusb=0 + close(reset=True) ကို ပြန်သုံးတယ်။
    Library version ပြောင်းလို့ patch မရရင် မူရင်း behavior အတိုင်း ဆက်လုပ်တယ်။"""
    if str(os.environ.get("PMK_MTK_LEAVEUSB", "1")).strip() in ("0", "false", "False"):
        return False
    try:
        from struct import pack
        from mtkclient.Library.DA.xflash.xflash_lib import DAXFlash

        def patched(self, async_mode=0, dl_bit=0, bootmode=None):
            if bootmode is None:
                bootmode = self.ShutDownModes.NORMAL
            # stock xflash_lib.py:823 — leaveusb=0 means "Disconnect usb"
            leaveusb = 0
            if self.xsend(self.cmd.SHUTDOWN):
                status = self.status()
                if status == 0:
                    hasflags = 0
                    if async_mode or dl_bit or bootmode != self.ShutDownModes.NORMAL:
                        hasflags = 1
                    enablewdt = 0      # watchdog ပိတ်
                    dont_resetrtc = 0  # RTC reset
                    if self.xsend(pack("<IIIIIIII", hasflags, enablewdt, async_mode, bootmode,
                                       dl_bit, dont_resetrtc, leaveusb, 0)):
                        status = self.status()
                        if status == 0:
                            # stock: close(reset=True) = libusb_reset_device + sleep(2)
                            # usblib.close ထဲမှာ device.reset() ကို try/except နဲ့ ဖုံးထားတယ် —
                            # device ပြုတ်သွားရင် Python exception သာ, native crash မဟုတ်ဘူး။
                            try:
                                self.mtk.port.close(reset=True)
                            except Exception:
                                try:
                                    self.mtk.port.close(reset=False)
                                except Exception:
                                    pass
                            return True
                else:
                    self.error("Error on sending shutdown: %s" % self.eh.status(status))
            try:
                self.mtk.port.close(reset=True)
            except Exception:
                pass
            return False

        DAXFlash.shutdown = patched
        return True
    except Exception as ex:
        print("PMK_WARN: leaveusb patch failed (%s) - using stock shutdown" % ex)
        return False


# ================= CHILD PROCESS (mtkclient CLI) =================

def _is_crash(rc):
    """Windows native crash (access violation စတာတွေ) ဟုတ်/မဟုတ်။"""
    if rc is None:
        return False
    return (int(rc) & 0xFFFFFFFF) in CRASH_CODES


# နောက်ဆုံး child ရဲ့ result file path (parent က ဖတ်ဖို့)
_LAST_RESULT_FILE = [""]


def _read_child_result():
    """Child က ရေးထားတဲ့ result file ကို ဖတ်တယ် — output stream ပြတ်သွားရင်
    (encoding error စတာတွေ) ဒီကနေ အောင်/မအောင် ဆက်စစ်နိုင်တယ်။"""
    path = _LAST_RESULT_FILE[0]
    if not path or not os.path.exists(path):
        return None
    try:
        data = {}
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "=" in line:
                    key, val = line.strip().split("=", 1)
                    data[key] = val
        return data
    except Exception:
        return None


def _run_child(args, timeout=None, quiet=False, no_progress_kill=None, progress_markers=()):
    """Child process (mtkclient CLI / kick step) ကို run ပြီး output ကို stream လုပ်တယ်။
    Native crash ဖြစ်ရင် child သာ သေတယ် — ဒီ parent က ဆက်အလုပ်လုပ်နိုင်တယ်။

    timeout            : စုစုပေါင်း အများဆုံး စက္ကန့်
    no_progress_kill   : ဒီစက္ကန့်အတွင်း progress_markers မပေါ်ရင် ရပ်လိုက်တယ်
                         (kick က ဒီ device မှာ အလုပ်မလုပ်တာ သိပြီးသားမို့ အချိန်မကုန်စေ)
    progress_markers   : ဒါတွေ ပေါ်ရင် 'အလုပ်လုပ်နေတယ်' လို့ မှတ်ပြီး timeout အထိ ဆက်ခွင့်ပြုတယ်
    """
    cmd = [sys.executable, os.path.abspath(__file__)] + args
    env = dict(os.environ)
    env["PMK_MTK_PARENT_PID"] = str(os.getpid())
    env["PYTHONIOENCODING"] = "utf-8:replace"
    env["PYTHONUTF8"] = "1"
    result_file = os.path.join(os.environ.get("TEMP", "."), "pmk_child_result.txt")
    if args and args[0] == "--internal-cli":
        # result file ကို CLI child အတွက်ပဲ ရှင်းတယ် (kick child က ဖျက်လိုက်ရင်
        # CLI ရဲ့ reboot evidence ကို ဆုံးရှုံးမယ်)
        try:
            if os.path.exists(result_file):
                os.remove(result_file)
        except Exception:
            pass
    env["PMK_MTK_RESULT_FILE"] = result_file
    _LAST_RESULT_FILE[0] = result_file
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            creationflags=_NO_WINDOW, env=env)

    progress_seen = [False]

    def guard():
        start = time.time()
        while proc.poll() is None:
            time.sleep(0.4)
            elapsed = time.time() - start
            reason = ""
            if no_progress_kill and not progress_seen[0] and elapsed > no_progress_kill:
                reason = "no progress in %ss" % no_progress_kill
            elif timeout and elapsed > timeout:
                reason = "timeout %ss" % timeout
            if reason:
                _safe_print("PMK: child stopped (%s)" % reason)
                try:
                    proc.kill()
                except Exception:
                    pass
                return

    if timeout or no_progress_kill:
        threading.Thread(target=guard, daemon=True).start()

    lines = []
    try:
        for raw in proc.stdout:
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            lines.append(line)
            for marker in progress_markers:
                if marker in line:
                    progress_seen[0] = True
            if line.startswith(_INTERNAL_PREFIX):
                continue
            # quiet mode (kick step): mtkclient ရဲ့ handshake retry spam ကို မပြတော့ဘူး —
            # PMK ရဲ့ အဓိပ္ပာယ်ရှိတဲ့ လိုင်းတွေပဲ UI ဆီ ပို့တယ်
            if quiet and not line.startswith("PMK"):
                continue
            # ဒီ print က encoding error နဲ့ ရပ်သွားရင် child output ဖတ်တာ ပြတ်ပြီး
            # အောင်/မအောင် စစ်တာ မှားသွားမယ် — ဒါကြောင့် safe print သုံးတယ်။
            _safe_print(line)
    except Exception as ex:
        _safe_print("PMK_WARN: child output read error (%s)" % ex)
        try:
            while True:  # ကျန်တဲ့ output ကို ဆက်ဖတ်ပါ (marker တွေ မလွတ်အောင်)
                raw = proc.stdout.readline()
                if not raw:
                    break
                lines.append(raw.decode("utf-8", "replace").rstrip("\r\n"))
        except Exception:
            pass
    proc.wait()
    return proc.returncode, lines


def _watch_parent():
    """Parent (ဒီ script ရဲ့ main process) သေသွားရင် child ကိုယ်တိုင် ထွက်တယ် —
    app ရဲ့ STOP နှိပ်လိုက်ရင် mtkclient က USB device ကို ဆက်ကိုင်မနေအောင်။"""
    pid = os.environ.get("PMK_MTK_PARENT_PID")
    if not pid or os.name != "nt":
        return
    try:
        import ctypes
        import threading
        SYNCHRONIZE = 0x00100000
        INFINITE = 0xFFFFFFFF
        handle = ctypes.windll.kernel32.OpenProcess(SYNCHRONIZE, False, int(pid))
        if not handle:
            return

        def waiter():
            ctypes.windll.kernel32.WaitForSingleObject(handle, INFINITE)
            os._exit(0)

        threading.Thread(target=waiter, daemon=True).start()
    except Exception:
        pass


def _internal_kick_main():
    """BROM မှာ ရပ်နေတဲ့ ဖုန်းကို ပုံမှန် boot ဆက်တက်စေတယ် (BROM command JUMP_BL)။
    ဘာကြောင့် လိုလဲ: DA ရဲ့ reset command ပြီးနောက် ဒီ device (MT6833/UFS) က SoC reset
    ဖြစ်ပေမယ့် BROM download-wait မှာ ပြန်ရပ်နေတတ်တယ် → Android မတက်။ BROM ကို
    ပြန် handshake လုပ်ပြီး JUMP_BL ပို့ရင် boot chain က ဆက်တက်သွားတယ်။"""
    import logging
    try:
        from mtkclient.Library.mtk_class import Mtk
        from mtkclient.config.mtk_config import MtkConfig
    except Exception as ex:
        print("PMK: cannot import mtkclient (%s)" % ex)
        return 11

    try:
        config = MtkConfig(loglevel=logging.INFO, gui=None, guiprogress=None)
        mtk = Mtk(config=config, loglevel=logging.INFO, serialportname=None)
    except Exception as ex:
        print("PMK: could not create mtkclient object (%s)" % ex)
        return 12

    try:
        if not mtk.preloader.init():
            print("PMK: BROM handshake failed - JUMP_BL not sent")
            print("%skick ok=0 reason=handshake" % _INTERNAL_PREFIX)
            return 1
        ok = mtk.preloader.jump_bl()
        print("PMK: JUMP_BL %s" % ("accepted" if ok else "rejected"))
        print("%skick ok=%d reason=sent" % (_INTERNAL_PREFIX, 1 if ok else 0))
        return 0 if ok else 2
    except Exception as ex:
        print("PMK: JUMP_BL error (%s)" % ex)
        return 3
    finally:
        try:
            mtk.port.close(reset=False)
        except Exception:
            pass


def _detect_serial_port():
    """MediaTek device ရဲ့ VCOM/serial (COM) port ကို ရှာတယ် — UnlockTool သုံးတဲ့ လမ်းအတိုင်း
    (ဥပမာ "COM5 [BOOTROM:0E8D:0003]")။ mtkclient ကို --serialport <COM> ပေးရင် USB (libusb)
    အစား serial backend နဲ့ သွားတယ်။ မတွေ့ရင် "" ပြန်ပေးတယ် (USB အတိုင်း ဆက်သွား)။"""
    try:
        from serial.tools import list_ports
    except Exception:
        return ""
    for p in list_ports.comports():
        text = ("%s %s" % (getattr(p, "description", "") or "", getattr(p, "hwid", "") or "")).lower()
        if "mediatek" in text or "preloader" in text or "mtk" in text:
            return getattr(p, "device", "") or ""
    return ""


def _apply_transport(args):
    """ချိတ်ဆက်နည်း (transport) ကို ဆုံးဖြတ်တယ်: USB (libusb) ဒါမှမဟုတ် serial/VCOM (COM port)။
    PMK_MTK_SERIAL = auto (default) | off | COM5 စသဖြင့်။
    --serialport ကို app က ပေးပြီးသားဆိုရင် ဒါက ဘာမှ မလုပ်ဘူး (user override ကို လေးစားတယ်)။"""
    args_text = " ".join(args)
    if "--serialport" in args_text:
        port = ""
        parts = args_text.split("--serialport", 1)[1].strip().split()
        if parts:
            port = parts[0]
        print("PMK: transport = serial/VCOM (%s) [from app]" % (port or "DETECT"))
        print("PMK_TRANSPORT_SERIAL %s" % port)
        return args

    mode = str(os.environ.get("PMK_MTK_SERIAL", "auto")).strip()
    low = mode.lower()
    port = ""
    if low in ("", "auto", "1", "true"):
        port = _detect_serial_port()
    elif low not in ("off", "0", "false"):
        port = mode  # user က COM port ကို တိုက်ရိုက် ပေးထားတာ

    if port:
        print("PMK: transport = serial/VCOM (%s) - COM port တွေ့တာမို့ serial နဲ့ သွားတယ်" % port)
        print("PMK_TRANSPORT_SERIAL %s" % port)
        return ["--serialport", port] + args
    print("PMK: transport = USB (libusb/WinUSB)")
    print("PMK_TRANSPORT_USB")
    return args


def _current_brom_device():
    """BROM mode (VID_0E8D & PID_0003) device ရှိ/မရှိ။"""
    for dev in _mtk_usb_devices():
        if "PID_0003" in dev.upper():
            return dev
    return ""


def _internal_cli_main(argv):
    """Child process ထဲမှာ patch တွေ တင်ပြီး mtkclient CLI ကို run တယ်။"""
    _watch_parent()
    try:
        forced = _install_bootmode_patch()
    except Exception as ex:
        print("ERROR: cannot import mtkclient (%s) - check: pip install mtkclient" % ex)
        return 11

    leaveusb = _install_leaveusb_patch()

    try:
        from mtkclient.mtk import main as cli_main
    except Exception as ex:
        print("ERROR: cannot run mtkclient CLI (%s)" % ex)
        return 12

    label = {0: "power off", 1: "home screen (Android)", 2: "fastboot"}.get(forced, str(forced))
    print("PMK: calling mtkclient - reset bootmode=%s, DA USB detach=%s"
          % (label, "yes" if leaveusb else "no"))
    sys.stdout.flush()

    sys.argv = ["mtk"] + argv
    try:
        rc = cli_main()
    except SystemExit as ex:
        # mtkclient က အတွင်းကနေ sys.exit() ခေါ်တတ်တယ် — ဒါကို ဖမ်းပြီး ဆက်စစ်ရမယ်
        rc = ex.code if isinstance(ex.code, int) else 0
    return rc if isinstance(rc, int) else 0


# ================= POST-OP BOOT WATCH =================
# Operation ပြီးနောက် ဖုန်း ဘယ်အခြေအနေ ရောက်သွားလဲ ကိုယ်တိုင် စောင့်ကြည့်တယ်။
# adb = Android တက်၊ fastboot = fastboot mode (PC က reboot ဆက်ပို့)၊
# MTK USB (VID_0E8D) = ဖုန်းက download/DA mode မှာတင် ဆက်ရှိနေတယ်။

def _fastboot_serials():
    out = _run_tool([_tool_path("fastboot.exe"), "devices"], timeout=15)
    found = []
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[1].strip().lower() == "fastboot":
            found.append(parts[0])
    return found


def _adb_serials():
    out = _run_tool([_tool_path("adb.exe"), "devices"], timeout=15)
    found = []
    for line in out.splitlines()[1:]:
        parts = line.split()
        if len(parts) >= 2 and parts[1].strip() == "device":
            found.append(parts[0])
    return found


def _mtk_usb_devices():
    """Windows မှာ VID_0E8D (MediaTek) device တွေ ရှိ/မရှိ + ဘယ် driver နဲ့ ချိတ်ထားလဲ —
    preloader/BROM/DA mode စစ်ဖို့။ libusb က libusbK/WinUSB binding နဲ့သာ ကိုင်နိုင်တယ်။"""
    ps = ["powershell", "-NoProfile", "-NonInteractive", "-Command",
          "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | "
          "Where-Object { $_.InstanceId -like '*VID_0E8D*' } | "
          "ForEach-Object { $s = (Get-PnpDeviceProperty -InstanceId $_.InstanceId "
          "-KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data; "
          "$_.InstanceId + '|' + $s }"]
    out = _run_tool(ps, timeout=30)
    return [ln.strip() for ln in out.splitlines() if "VID_0E8D" in ln.upper()]


def _preflight_mode_check():
    """Tool မ run ခင် ဖုန်းက BROM mode မှာ တကယ် ရှိ/မရှိ + driver binding ကို စစ်တယ်။
    BROM = PID_0003 ပါပဲ။ တခြား PID (preloader/DA) ဆိုရင် handshake က libusb crash
    (0xC0000005) ဖြစ်နိုင်တာမို့ user ကို အရင် သတိပေးတယ်။"""
    mtk = _mtk_usb_devices()
    if not mtk:
        return
    print("PMK: MediaTek USB device attached: %s" % " | ".join(mtk))
    for dev in mtk:
        inst = dev.split("|")[0].strip()
        svc = (dev.split("|")[-1].strip().lower() if "|" in dev else "")
        up = inst.upper()
        if "PID_0003" not in up:
            print("PMK: NOT BROM mode (%s) - handshake may crash libusb" % inst)
            print("PMK_MTK_NOT_BROM")
            return
        # BROM mode ဖြစ်ပေမယ့် driver က libusb-compatible မဟုတ်ရင် (ဥပမာ MTK serial/wdm_usb)
        # libusb က device ကို ကိုင်နိုင်မှာ မဟုတ်ဘူး — WinUSB/libusbK ပြောင်းရမယ်။
        if svc and svc not in ("libusbk", "winusb", "libusb0"):
            print("PMK: BROM device driver is '%s' (not libusbK/WinUSB) - libusb may fail" % svc)
            print("PMK_MTK_BAD_DRIVER")


def _watch_boot(boot_wait=None, android_wait=None):
    """Auto reboot ရဲ့ တကယ့်ရလဒ်ကို စစ်တယ်။ UI အတွက် marker တွေ print တယ်။
    boot_wait/android_wait ကို ပေးမထားရင် env (default 24s / 40s) သုံးတယ်။
    USB (VID_0E8D) ပျောက်သွားရင် reboot ဖြစ်နေပြီ = LIKELY (adb off ဖြစ်နိုင်)။"""
    if boot_wait is None:
        boot_wait = float(os.environ.get("PMK_MTK_BOOT_WAIT", "24"))
    if android_wait is None:
        android_wait = float(os.environ.get("PMK_MTK_ANDROID_WAIT", "40"))

    print("PMK: watching device state after reboot (%.0fs)..." % boot_wait)
    deadline = time.time() + boot_wait
    saw_fastboot = False
    had_mtk = bool(_mtk_usb_devices())

    while time.time() < deadline:
        if _adb_serials():
            print("PMK: ADB device found - phone is in Android.")
            print("PMK_BOOT_OK")
            return True
        fb = _fastboot_serials()
        if fb:
            saw_fastboot = True
            print("PMK: fastboot device found (%s) - sending fastboot reboot." % ",".join(fb))
            print("PMK_BOOT_FASTBOOT")
            _run_tool([_tool_path("fastboot.exe"), "reboot"], timeout=30)
            break
        # အရင် USB ပေါ်မှာ ရှိခဲ့ရင် — အခု ပျောက်သွားပြီ = reboot/re-enumerate ဖြစ်နေတယ်
        mtk_now = _mtk_usb_devices()
        if had_mtk and not mtk_now:
            print("PMK: MediaTek USB left - phone is rebooting (adb may be off).")
            print("PMK_BOOT_LIKELY")
            return True
        if mtk_now:
            had_mtk = True
        time.sleep(2)
    else:
        # fastboot/ADB မပေါ်ဘူး — ဖုန်းက USB ပေါ်မှာ ဆက်ရှိနေလား စစ် (download/DA mode)
        mtk = _mtk_usb_devices()
        if mtk:
            print("PMK: phone is still attached on USB (%s) - Android did not start." % ",".join(mtk))
            print("PMK_MTK_STILL_ATTACHED")
            print("PMK_BOOT_MANUAL")
        else:
            # USB ကနေ ထွက်သွားပြီ = ဖုန်း boot ဖြစ်နေတယ် (adb မပေါ်တာက USB debugging
            # ပိတ်ထား/authorize မလုပ်ထားလို့ ဖြစ်နိုင်တယ် — ဒါ ကောင်းတဲ့ လက္ခဏာ)။
            print("PMK: device left USB - phone is booting (adb may be off/unauthorized).")
            print("PMK_BOOT_LIKELY")
        return False

    if not saw_fastboot:
        return False

    # fastboot reboot ပြီးနောက် — fastboot device ပျောက်သွားရင် boot ဖြစ်နေပြီ
    print("PMK: after fastboot reboot - watching for boot (%.0fs)..." % android_wait)
    deadline = time.time() + android_wait
    while time.time() < deadline:
        if _adb_serials():
            print("PMK_BOOT_OK")
            return True
        if not _fastboot_serials():
            print("PMK: fastboot device is gone - phone is booting.")
            print("PMK_BOOT_OK")
            return True
        time.sleep(2)

    print("PMK: phone is still in fastboot - reboot command was not accepted.")
    print("PMK_MTK_STILL_ATTACHED")
    print("PMK_BOOT_MANUAL")
    return False


# ================= MAIN (parent process) =================

def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 10

    # ချိတ်ဆက်နည်း: COM port (serial/VCOM) ရှိရင် အဲဒါကို ဦးစားပေး (UnlockTool လမ်းအတိုင်း)၊
    # မရှိရင် USB (libusb/WinUSB)။ PMK_MTK_SERIAL=off နဲ့ USB ကို အတင်းရွေးလို့ရတယ်။
    args = _apply_transport(args)

    _preflight_mode_check()

    # ဖုန်းက USB မှာ ကြိုချိတ်ထားရင် mtkclient က "disconnect ပြီး reconnect" လို့ ပြတယ် —
    # အဲဒီအခါ user ဖုန်းကို ဖြုတ်/ပြန်တပ် လုပ်နိုင်အောင် ခဏ စောင့်ပြီး ပြန်စမ်းတယ်။
    attempts = int(os.environ.get("PMK_MTK_CONNECT_RETRIES", "20"))
    crash_left = int(os.environ.get("PMK_MTK_CRASH_RETRIES", "1"))
    # Kick (JUMP_BL) အတွက် အချိန် ကန့်သတ်ချက်:
    #   PMK_MTK_KICK_NOPROGRESS — ဒီစက္ကန့်အတွင်း BROM က handshake မဖြေရင် ရပ် (default 12)
    #   PMK_MTK_KICK_TIMEOUT    — ဖြေရင် exploit+jump အတွက် အများဆုံး စက္ကန့် (default 45)
    # (ဒီ device မှာ BROM က reset ပြီးနောက် မဖြေတာမို့ no-progress kill က ၁၂ စက္ကန့်ပဲ ကုန်တယ်။)
    kick_timeout = int(os.environ.get("PMK_MTK_KICK_TIMEOUT", "45"))
    kick_noprogress = int(os.environ.get("PMK_MTK_KICK_NOPROGRESS", "12"))
    delay = 3
    rc = 0
    text = ""

    for attempt in range(1, attempts + 1):
        rc, lines = _run_child(["--internal-cli"] + args)
        text = "\n".join(lines)

        # child က "reboot တကယ် ပို့ဖြစ်လား" ကို marker နဲ့ ပြန်ပေးတယ်
        for line in lines:
            if line.startswith(_INTERNAL_PREFIX + "shutdown"):
                parts = dict(p.split("=", 1) for p in line.split()[1:] if "=" in p)
                SHUTDOWN_STATE["called"] += int(parts.get("called", "0"))
                SHUTDOWN_STATE["ok"] = parts.get("ok", "0") == "1"

        # marker မတွေ့ရင် (output stream ပြတ်သွားရင်) result file ကနေ ဖတ်တယ်
        if SHUTDOWN_STATE["called"] == 0:
            res = _read_child_result()
            if res is not None:
                SHUTDOWN_STATE["called"] = int(res.get("shutdown_called", "0") or 0)
                SHUTDOWN_STATE["ok"] = res.get("shutdown_ok", "0") == "1"

        # native crash (access violation) — Python က မဖမ်းနိုင်တာမို့ child သာ သေတယ်
        if _is_crash(rc):
            print("PMK: mtkclient crashed natively (exit 0x%08X) during USB/handshake phase"
                  % (int(rc) & 0xFFFFFFFF))
            print("PMK_MTK_NOT_BROM")
            if crash_left > 0:
                crash_left -= 1
                print("PMK: power-cycle the phone (10s) then replug in BROM mode - retrying once ...")
                print("PMK_NEED_REPLUG")
                time.sleep(8)
                continue
            print("PMK_DA_CRASH")
            return 30

        need_replug = "Please disconnect, start mtkclient and reconnect" in text
        if need_replug and attempt < attempts:
            # instruction ကို တစ်ခါပဲ ရိုက်၊ ပြီးရင် ၅ ကြိမ်တစ်ခါ အနှစ်ချုပ် ပြတယ် (log မရှုပ်အောင်)
            if attempt == 1:
                print("PMK: please unplug and replug the phone (new BROM session) - "
                      "waiting %d/%d ..." % (attempt, attempts))
                print("PMK_NEED_REPLUG")
            elif attempt % 5 == 0:
                print("PMK: still waiting for a fresh BROM session (%d/%d) ..." % (attempt, attempts))
            time.sleep(delay)
            continue
        break

    # exit code က 0/None ဖြစ်နေရင်လည်း failure အချက် ရှိရင် မအောင်ဘူးလို့ မှတ်။
    # မှတ်ချက်: "Couldn't detect partition" က partition တစ်ခု မရှိလို့ ဖြစ်တယ် — တခြား
    # partition တွေ ဖျက်ပြီးသားဆိုရင် partial success (ဥပမာ: ဒီ device မှာ config မရှိ)။
    erased_something = ("Formatted sector" in text) or ("Formatting addr" in text)
    for marker in FAIL_MARKERS:
        if marker not in text:
            continue
        if marker == "Couldn't detect partition" and erased_something:
            print("PMK_WARN: %s (partition တချို့ ဒီဖုန်းမှာ မရှိပါ — ရှိတာတွေ ဖျက်ပြီး)" % marker)
            print("PMK_PARTIAL_ERASE")
            continue
        print("PMK_ERROR: %s" % marker)
        if "Please disconnect" in marker:
            print("PMK_NEED_REPLUG")
        return 20

    if isinstance(rc, int) and rc != 0:
        return rc

    # reset ပါတဲ့ command ဆိုရင် shutdown တကယ် အောင်မှ success (positive evidence)
    # မှတ်ချက်: args က ["multi", "e frp;reset"] ဖြစ်တာမို့ element ချင်း တိုက်စစ်လို့ မရဘူး —
    # command စာသား တစ်ခုလုံးထဲမှာ "reset" ရှိ/မရှိ စစ်ရတယ်။
    cmd_text = " ".join(args)
    if "reset" in cmd_text:
        if SHUTDOWN_STATE["called"] == 0:
            print("PMK_ERROR: reset step was not reached")
            print("PMK_DA_REBOOT_FAILED")
            return 22
        if not SHUTDOWN_STATE["ok"]:
            print("PMK_ERROR: DA shutdown failed - reboot command was not accepted")
            print("PMK_DA_REBOOT_FAILED")
            return 21

        # DA ရဲ့ reset ပြီးနောက် ဖုန်းက BROM download-wait မှာ ရပ်ကျန်နေတတ်တယ် (Android မတက်)။
        # အဲဒီအခါ BROM JUMP_BL ပို့ပြီး boot chain ကို ဆက်တက်စေတယ်။
        kicked = False
        kick_ok = False
        # PMK_MTK_KICK=1 ဆိုမှ kick အဆင့်ကို လုပ်တယ်။ Default 0 (ပိတ်) — ဒီ device (MT6833)
        # မှာ DA reset ပြီးနောက် BROM က handshake မဖြေတာမို့ kick က အလုပ်မလုပ်ဘူး (စမ်းစစ်ပြီး)၊
        # ပိတ်ထားရင် run တစ်ခုကို ~၁၂ စက္ကန့် ချွေတာတယ်။ (တခြား device မှာ လိုရင် 1 လုပ်ပါ။)
        kick_enabled = str(os.environ.get("PMK_MTK_KICK", "0")).strip() not in ("0", "false", "False")
        brom = _current_brom_device() if kick_enabled else ""
        if brom:
            print("PMK: phone is in BROM after reset - sending JUMP_BL to continue boot")
            print("PMK_KICK_SENT")
            kicked = True
            # BROM အသက်ရှင်ရင် handshake က ၁-၂ စက္ကန့်အတွင်း ဖြေတယ် — ဒါကြောင့်
            # ၁၂ စက္ကန့်အတွင်း 'အလုပ်လုပ်နေတယ်' လက္ခဏာ မပေါ်ရင် ရပ်လိုက်တယ် (အလကား မစောင့်)။
            # ပေါ်ရင်တော့ exploit + jump အတွက် ၄၅ စက္ကန့်အထိ ခွင့်ပြုတယ်။
            krc, klines = _run_child(["--internal-kick"], timeout=kick_timeout,
                                     no_progress_kill=kick_noprogress,
                                     progress_markers=("Detected regular mode", "Detected iot mode",
                                                       "Bypassing", "CPU:"),
                                     quiet=True)
            kick_line = ""
            for kl in klines:
                if kl.startswith(_INTERNAL_PREFIX + "kick"):
                    kick_line = kl
            if _is_crash(krc):
                print("PMK: JUMP_BL step crashed natively (exit 0x%08X)" % (int(krc) & 0xFFFFFFFF))
            elif "ok=1" in kick_line:
                kick_ok = True
                print("PMK: JUMP_BL accepted by BROM - waiting for the phone to boot")
            elif "handshake" in kick_line:
                print("PMK: BROM did not answer the handshake after the reset (phone needs a power cycle)")
            else:
                print("PMK: JUMP_BL failed (exit %s)" % krc)
        else:
            print("PMK: no BROM device after reset - nothing to kick")

        # Kick မရဘူး/မလုပ်ဘူးဆိုရင် — ဖုန်း reboot ဖြစ်/မဖြစ် စောင့်ကြည့်ချိန်။
        # အရင် ၆ စက္ကန့်က တိုလွန်း (DA shutdown ပြီး USB re-enumerate ဖြစ်ဖို့
        # ၈-၁၅ စက္ကန့် ကြာတတ်) — ဒါကြောင့် ၂၀ စက္ကန့် စောင့်တယ်။
        if not kick_enabled:
            short_wait = 20
        elif kicked and not kick_ok:
            short_wait = 12
        else:
            short_wait = None
        # ဖုန်း တကယ် တက်/မတက် ကိုယ်တိုင် စစ်တယ်
        _watch_boot(boot_wait=short_wait)

    print("PMK_OK")
    return 0


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--internal-cli":
        sys.exit(_internal_cli_main(sys.argv[2:]))
    if len(sys.argv) > 1 and sys.argv[1] == "--internal-kick":
        sys.exit(_internal_kick_main())
    sys.exit(main())

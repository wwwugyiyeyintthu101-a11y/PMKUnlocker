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
  PMK_MTK_BOOTMODE        1=home screen (default) | 2=fastboot | 0=power off
  PMK_MTK_LEAVEUSB        1=shutdown အချိန် DA က USB ဖြုတ် (default) | 0=မဖြုတ်
  PMK_MTK_BOOT_WAIT       fastboot/ADB/Android USB ကို စောင့်တဲ့ စက္ကန့် (default 45)
  PMK_MTK_ANDROID_WAIT    fastboot reboot နောက် စောင့်တဲ့ စက္ကန့် (default 40)
  PMK_MTK_CONNECT_RETRIES device မရှိရင် ပြန်စမ်းတဲ့ အကြိမ် (default 20)
  PMK_MTK_CRASH_RETRIES   native crash ဖြစ်ရင် ပြန်စမ်းတဲ့ အကြိမ် (default 1)
  PMK_MTK_OPENFAIL_RETRIES COM port ဖွင့်မရရင် ပြန်စမ်းတဲ့ အကြိမ် (default 3) → exit 24
  PMK_MTK_KICK            JUMP_BL kick အဆင့် — default 0 (ပိတ်)၊ တခြား device အတွက် 1
  PMK_MTK_SERIAL          auto (default) | off (USB အတင်း) | COM5 (port တိုက်ရိုက်)
  PMK_MTK_KICK_NOPROGRESS / PMK_MTK_KICK_TIMEOUT / PMK_MTK_BOOT_WAIT / PMK_MTK_ANDROID_WAIT

UI (PMKUnlocker) ဖတ်တဲ့ markers — stdout မှာ ASCII သက်သက် ပေါ်တယ်:
  PMK_OK | PMK_ERROR: <reason> | PMK_NEED_REPLUG | PMK_MTK_NOT_BROM
  PMK_DA_CRASH | PMK_DA_REBOOT_FAILED | PMK_BOOT_FASTBOOT | PMK_BOOT_OK |
  PMK_MTK_STILL_ATTACHED | PMK_BOOT_MANUAL | PMK_MTK_COM_BUSY <COM>
  PMK_USB_FAILNODE        Windows မှာ descriptor ဖတ်မရ (error 43) — re-kick လုပ်နေတုန်း
  PMK_STAGE <1-4>|<text>   UnlockTool-style အဆင့်ပြ header ([1/4] Port → [4/4] Op)
  PMK_ERASE_OK [<parts>]   erase အောင်မြင် — partition နံပါတ်တွေနဲ့ ([+] Erased line)
  PMK_PARTIAL_ERASE        တောင်းတဲ့ partition အချို့ မရှိ — ကျန်တာတွေ အောင်မြင်

Exit codes: 20=tool error marker, 21=DA shutdown, 22=reset not reached,
            23=never connected (BROM/handshake), 24=COM port open failed,
            30=native crash.
"""

import os
import re
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
    "PMK_ERROR: Firmware",
    "Please disconnect, start mtkclient and reconnect",
    "Error on running da",
    "Failed to format",
    "Couldn't detect partition",
    "Failed to format all partitions",
    "Error on writing seccfg",
    "Failed to write seccfg",
    "Failed to write",     # da_write ထဲက per-partition write fail ("Failed to write X to sector")
    "Traceback (most recent call last)",
)

# Windows native crash codes (signed/unsigned နှစ်မျိုးလုံး လာနိုင်တယ်)
CRASH_CODES = (0xC0000005, 0xC0000006, 0xC0000008, 0xC000001D, 0xC0000409, 0xC0000374)

_NO_WINDOW = 0x08000000 if os.name == "nt" else 0

# Child process က ဒီ prefix နဲ့ စတဲ့ line တွေကို parent ဖတ်ပြီး UI မှာ မပြတော့ဘူး
_INTERNAL_PREFIX = "PMK_INTERNAL_"

# reboot command တကယ် ပို့ဖြစ်/မဖြစ် မှတ်ထားဖို့ (patch ထဲက ဖြည့်တယ်)
SHUTDOWN_STATE = {"called": 0, "ok": None}

# ================= STAGES (UnlockTool-style: [1/4] Port → [2/4] Auth →
#                            [3/4] Info → [4/4] Op) =================
# child output ထဲက line တွေကို stream ဖတ်ရင်း အဆင့်တိုင်းကို တစ်ခါပဲ ပေါ်အောင်
# marker ထုတ်တယ်။ attempt (retry) တစ်ခုချင်းစီမှာ state reset — ဖုန်း ပြန်ချိတ်ရင်
# အဆင့်တွေ ပြန်ပေါ်တယ် (UnlockTool လို connect တိုင်းမှာ အဆင့်စာရင်း ပြန်မြင်ရ)။
_STAGE_DONE = [0]   # bit1..bit4 = stage 1..4 ပြီးပြီ (1<<1 .. 1<<4)
_STAGE1_SEEN = set()  # [1/4] ထုတ်ပြီးသား port/driver state တွေ — retry မှာ ထပ်မပြ
                      # (port ပြောင်းသွားရင် စာသား မတူတဲ့အတွက် ပြန်ပေါ်တယ်)
# CPU line တွေ့ပြီး [3/4] မထုတ်ရသေး — UnlockTool order (2 → 3) အတွက်
# [2/4] auth ထွက်ပြီးမှ ပဲ ထုတ်တယ် (None = CPU မတွေ့ရသေး)
_STAGE_PENDING3 = [None]
_STAGE_INFO = {"cpu": "", "hw": "", "meid": "", "soc": "", "storage": ""}
_STAGE_OP = [""]    # stage-4 label (argv ကနေ တွက်)


def _stage_reset():
    _STAGE_DONE[0] = 0
    _STAGE_PENDING3[0] = None
    for _k in _STAGE_INFO:
        _STAGE_INFO[_k] = ""


def _emit_stage(n, text):
    if n < 1 or n > 4 or (_STAGE_DONE[0] & (1 << n)):
        return
    _STAGE_DONE[0] |= (1 << n)
    print("PMK_STAGE %d|%s" % (n, text))
    sys.stdout.flush()


def _emit_stage1(text):
    """[1/4] Port check — တစ်ခုတည်းသော port/driver state ကို တစ်ခါပဲ ပြတယ်။"""
    if text in _STAGE1_SEEN:
        return
    _STAGE1_SEEN.add(text)
    _emit_stage(1, text)


def _stage_emit3():
    """[3/4] — [2/4] auth ထွက်ပြီးမှပဲ ထုတ် (UnlockTool order 1→2→3→4)။
    CPU line မတွေ့ရသေးရင် မထုတ်ဘူး (False)။"""
    if (_STAGE_DONE[0] & 8) or _STAGE_PENDING3[0] is None:
        return False
    cpu = _STAGE_PENDING3[0]
    _STAGE_PENDING3[0] = None
    _STAGE_INFO["cpu"] = cpu
    _emit_stage(3, "Reading hardware info... OK" + ((" — " + cpu) if cpu else ""))
    return True


def _stage_emit2_fallback():
    """auth line တစ်ခုမှ မထွက်ဘဲ op ရောက်သွားရင် (unprotected/in-session) ဖြည့်တယ်။"""
    if not (_STAGE_DONE[0] & 4):
        _emit_stage(2, "Bypassing authentication... (in-session / not required)")


def _stage_connected():
    """handshake တကယ်ဖြစ် (auth line / CPU line) မှ [2/4][3/4] ကို ဖြည့်ခွင့်ရှိတယ်။"""
    return bool(_STAGE_DONE[0] & 4) or _STAGE_PENDING3[0] is not None


def _flush_stages():
    # op က stage header အပြည့်မထွက်ဘဲ ပြီးသွားရင် (ဥပမာ reset-only / CPU line နောက်ကျ)
    # ဒီမှာ အဆင့်စီအတိုင်း ပြန်ဖြည့်တယ် — device မချိတ်ရသေးရင် ဘာမှ မထုတ်ဘူး
    if not _stage_connected():
        return
    _stage_emit2_fallback()
    if not (_STAGE_DONE[0] & 8):
        _STAGE_PENDING3[0] = _STAGE_PENDING3[0] or ""
        _stage_emit3()
    if not (_STAGE_DONE[0] & 16):
        _emit_stage(4, (_STAGE_OP[0] or "Running operation") + "...")


def _op_label(args):
    """stage-4 label — argv ထဲက mtkclient command ကို ဖတ်တယ်
    (multi ဆိုရင် ပထမ sub-command; global flag တွေ --serialport/--loader ကို ကျော်)။"""
    known = ("multi", "printgpt", "reset", "r", "w", "e", "wl", "rl", "rf",
             "ro", "wo", "es", "ess", "da", "pe", "ba", "peek")
    seq = list(args or [])
    start = 0
    for i, x in enumerate(seq):
        if x in known:
            start = i
            break
    else:
        return "Running operation"
    a = seq[start:]
    if a[0] == "multi":
        a = (a[1].split(";")[0] if len(a) > 1 else "").strip().split()
        if not a:
            return "Running operation"
    low = " ".join(a).lower()
    if low.startswith("e ") or low == "e":
        parts = [p.strip() for p in " ".join(a).split(None, 1)[1].split(",") if p.strip()]
        return "Erasing " + ", ".join(parts)
    if low.startswith("r ") and len(a) > 1:
        return "Reading " + a[1]
    if low.startswith("w ") and len(a) > 1:
        return "Writing " + a[1]
    if low.startswith("wl"):
        return "Flashing firmware"
    if "seccfg unlock" in low:
        return "Unlocking bootloader"
    if "seccfg lock" in low:
        return "Relocking bootloader"
    if low.startswith("printgpt"):
        return "Reading partition info"
    if low == "reset":
        return "Resetting device"
    if low.startswith("rl") or low.startswith("rf"):
        return "Dumping firmware"
    return " ".join(a)[:60]


def _erase_parts(args):
    """erase op တွေအတွက် partition နံပါတ်တွေကို argv ထဲက စုတယ် (multi ဆိုရင် sub-cmd အားလုံး)။"""
    known = ("multi", "printgpt", "reset", "r", "w", "e", "wl", "rl", "rf",
             "ro", "wo", "es", "ess", "da", "pe", "ba", "peek")
    seq = list(args or [])
    start = 0
    for i, x in enumerate(seq):
        if x in known:
            start = i
            break
    else:
        return []
    a = seq[start:]
    if a and a[0] == "multi":
        subs = a[1].split(";") if len(a) > 1 else []
    else:
        subs = [" ".join(a)]
    parts = []
    for sc in subs:
        toks = sc.strip().split()
        if len(toks) >= 2 and toks[0] == "e":
            for p in toks[1].split(","):
                p = p.strip()
                if p:
                    parts.append(p)
    return parts


def _stage1_text(args):
    """[1/4] — transport ကို ဘယ် port/driver နဲ့ စောင့်နေလဲ ပြတယ် (UnlockTool style)။"""
    port = ""
    if _uses_serial(args):
        port = _serial_port_from_args(args) or _detect_serial_port()
    if port:
        return "Initializing usb... OK — Waiting for device... %s [VCOM:0E8D]" % port
    devs = _mtk_usb_devices()
    if devs:
        parts = devs[0].split("|")
        inst = parts[0].strip().upper()
        svc = parts[1].strip() if len(parts) > 1 and parts[1].strip() else ""
        pid = inst.split("PID_", 1)[1][:4] if "PID_" in inst else "?"
        return "Initializing usb... OK — Waiting for device... USB 0E8D:%s%s" % (
            pid, (" (" + svc + ")") if svc else "")
    return "Initializing usb... OK — Waiting for device... (plug the phone in BROM mode)"


def _stage_watch(line):
    """child line stream ကနေ stage 2/3/4 ကို detect ပြီး UnlockTool-style header ထုတ်တယ်။
    order က အမြဲတမ်း 1 → 2 → 3 → 4 (CPU line က auth ထက် အရင် ရောက်တတ်လို့
    [3/4] ကို [2/4] ထွက်ပြီးမှပဲ ထုတ်တယ်)။"""
    if not line or line.startswith(_INTERNAL_PREFIX):
        return
    # ---- [2/4] auth bypass ----
    if not (_STAGE_DONE[0] & 4):
        if "Device is unprotected" in line:
            _emit_stage(2, "Bypassing authentication... not required (unprotected device)")
        elif ("Bypassing authentication" in line or "Device is protected" in line or
              "Kamakiri" in line or "DA SLA is disabled" in line or
              "Successfully bypassed" in line or "Security bypass" in line or
              "Bypassing security" in line or "Done sending payload" in line):
            _emit_stage(2, "Bypassing authentication... OK")
        if (_STAGE_DONE[0] & 4) and _STAGE_PENDING3[0] is not None:
            _stage_emit3()   # auth ပြီးမှ [3/4] (CPU အရင်ရောက်ထားလို့ pending ကနေ)
    # ---- [3/4] hardware info (CPU line ရောက်ရင် pending; stage2 ရှိပြီဆို ချက်ချင်း) ----
    if "CPU:" in line and _STAGE_PENDING3[0] is None and not (_STAGE_DONE[0] & 8):
        _STAGE_PENDING3[0] = line.split("CPU:", 1)[1].strip().strip("\t")
        if (_STAGE_DONE[0] & 4):
            _stage_emit3()
    # ---- [4/4] op (DA boot ပြီး/op progress စတာနဲ့ header ပေါ်) ----
    if not (_STAGE_DONE[0] & 16):
        if ("Successfully uploaded stage 2" in line or "DA Extensions successfully" in line or
                "Extensions were accepted" in line or "DA SLA is disabled" in line or
                "Formatting" in line or "Formatted" in line or "Erasing" in line or
                "Reading from" in line or "Writing to" in line or "PartName" in line or
                "seccfg" in line.lower()):
            _stage_emit2_fallback()
            if not (_STAGE_DONE[0] & 8):
                _STAGE_PENDING3[0] = _STAGE_PENDING3[0] or ""
                _stage_emit3()
            _emit_stage(4, (_STAGE_OP[0] or "Running operation") + "...")


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
    # default က HOME_SCREEN (1) — အရင် 2 (FASTBOOT) ကြောင့် reset ပြီးတိုင်း ဖုန်းက
    # fastboot (headless/screen ပိတ်) ရောက်ပြီး Windows မှာလည်း "Unknown USB Device
    # (Device Descriptor Request Failed)" ဖြစ်တယ်။ op ပြီးတိုင်း Android ပြန်
    # ဝင်စေချင်တာမို့ 1 က default ဖြစ်ရမယ် (fastboot လိုချင်ရင် env နဲ့ 2 ပေး)။
    forced = int(os.environ.get("PMK_MTK_BOOTMODE", "1"))
    # env PMK_MTK_BOOTMODE က file override ထက် ပိုအားကိုး (flash flow က env=1 ပို့တယ် —
    # stale %TEMP%\pmk_bootmode.txt=2 ကြောင့် RTC latch fastboot ပြန်မဖြစ်စေချင်)
    #   %TEMP%\pmk_bootmode.txt ထဲ ၁ လိုင်း (0=power off | 1=home screen | 2=fastboot)
    if "PMK_MTK_BOOTMODE" not in os.environ:
        try:
            _bmf = os.path.join(os.environ.get("TEMP", ""), "pmk_bootmode.txt")
            if os.path.exists(_bmf):
                with open(_bmf, "r") as _f:
                    _bv = _f.read().strip()
                if _bv in ("0", "1", "2"):
                    forced = int(_bv)
                    print("PMK: bootmode override from pmk_bootmode.txt = %d" % forced)
        except Exception:
            pass
    else:
        print("PMK: bootmode from env PMK_MTK_BOOTMODE = %d" % forced)
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
            # SHUTDOWN command ကို DA က လက်ခံပြီး/ reboot mode packet ပို့ပြီး ဆိုတာ
            # မှတ်ထား — status reply ဖတ်ရင်း device USB ပြုတ်ရင် exception ထွက်ပေမယ့်
            # ဖုန်းဆီ reboot အ指令 ရောက်ပြီးသား ဖြစ်လို့ success အဖြစ် သတ်မှတ်မယ်။
            # (မဟုတ်ရင် traceback က fail marker ဖြစ်ပြီး erase အောင်ပြီးမှ exit 20 ရတယ်)
            accepted = False
            flags_sent = False
            ok = False
            try:
                if self.xsend(self.cmd.SHUTDOWN):
                    status = self.status()
                    if status == 0:
                        accepted = True
                        hasflags = 0
                        if async_mode or dl_bit or bootmode != self.ShutDownModes.NORMAL:
                            hasflags = 1
                        enablewdt = 0      # watchdog ပိတ်
                        dont_resetrtc = 0  # RTC reset
                        if self.xsend(pack("<IIIIIIII", hasflags, enablewdt, async_mode, bootmode,
                                           dl_bit, dont_resetrtc, leaveusb, 0)):
                            flags_sent = True
                            status = self.status()
                            if status == 0:
                                ok = True
                    else:
                        self.error("Error on sending shutdown: %s" % self.eh.status(status))
            except Exception as ex:
                # pyserial _reconfigure_port → PermissionError(13, …, 31): device က
                # reset command လက်ခံပြီး USB ပြုတ်သွားလို့ status reply မဖတ်နိုင်တာ
                print("PMK: shutdown status read failed (%s) - the phone already left USB" % ex)
                ok = accepted or flags_sent
            # stock: close(reset=True) = libusb_reset_device + sleep(2) — ဖုန်း USB က
            # ပြုတ်ပြီးသားဆို libusb reset က native AV (0xC0000005) ဖြစ်တတ်တယ်။ reboot က
            # shutdown command ကပဲ လုပ်တာမို့ host-side reset မလို — reset=False နဲ့ ပိတ်တယ်။
            try:
                self.mtk.port.close(reset=False)
            except Exception:
                pass
            return ok

        DAXFlash.shutdown = patched
        return True
    except Exception as ex:
        print("PMK_WARN: leaveusb patch failed (%s) - using stock shutdown" % ex)
        return False


# ================= PRELOADER DUMP (DRAM/EMI setup) =================
# preloader မပေးရင် mtkclient က DRAM/EMI setup မလုပ်နိုင်ဘူး
# ("No preloader given. Operation may fail due to missing dram setup") — ဒီအခါ
# DA stage 2 jump မှာ ဖုန်းက USB ပြတ်ပြီး exit 20 ဖြစ်တယ်။ BROM က preloader ကို
# SRAM (0x200000) ထဲ တင်ထားတဲ့အတွက် RAM ကနေ dump ထုတ်ပြီး ဒီဖုန်းရဲ့ EMI ကို
# တန်းသုံးနိုင်တယ် (user firmware / --preloader မလိုဘူး)။

def _pmk_dump_preloader(mtk):
    from mtkclient.Library.DA.mtk_da_handler import DaHandler

    class _Stub(object):
        def __init__(self, m):
            self.mtk = m

        def error(self, msg):
            print("PMK: preloader dump: %s" % msg)

    data = DaHandler.dump_preloader_ram(_Stub(mtk), write_preloader_to_file=False)
    if not data:
        print("PMK: device preloader could not be read from RAM (no DRAM/EMI setup)")
        sys.stdout.flush()
        return None
    try:
        out = os.path.join(os.environ.get("TEMP", os.getcwd()), "pmk_preloader_dumped.bin")
        with open(out, "wb") as fh:
            fh.write(data)
        print("PMK: device preloader dumped (%d bytes) -> %s" % (len(data), out))
        print("PMK_PRELOADER_DUMP %s" % out)
    except Exception as ex:
        print("PMK: preloader dump write failed (%s)" % ex)
    sys.stdout.flush()
    return data


def _install_preloader_dump_patch():
    """DAloader.upload_da() မှာ preloader မရှိရင် RAM ကနေ dump ထုတ်ပြီး EMI သုံးအောင်
    ပြင်တယ်။ PMK_MTK_PRELOADER_DUMP=0 နဲ့ ပိတ်လို့ရတယ် (stock behavior ပြန်ရ)။"""
    if str(os.environ.get("PMK_MTK_PRELOADER_DUMP", "1")).strip() in ("0", "false", "False"):
        return False
    try:
        from mtkclient.Library.DA.mtk_daloader import DAloader
    except Exception as ex:
        print("PMK_WARN: preloader dump patch skipped (%s) - using stock" % ex)
        return False
    if getattr(DAloader, "_pmk_dump_patch", False):
        return True
    original = DAloader.upload_da

    def patched(self, preloader=None):
        if preloader is None and getattr(self, "config", None) is not None and \
                getattr(self.config, "preloader", None) is None:
            try:
                dumped = _pmk_dump_preloader(self.mtk)
            except Exception as ex:
                print("PMK: preloader dump failed (%s)" % ex)
                dumped = None
            if dumped:
                preloader = dumped
        return original(self, preloader=preloader)

    DAloader.upload_da = patched
    DAloader._pmk_dump_patch = True
    _diag("preloader dump patch installed")
    return True


# ================= CHILD PROCESS (mtkclient CLI) =================

def _is_crash(rc):
    """Windows native crash (access violation စတာတွေ) ဟုတ်/မဟုတ်။"""
    if rc is None:
        return False
    return (int(rc) & 0xFFFFFFFF) in CRASH_CODES


# နောက်ဆုံး child ရဲ့ result file path (parent က ဖတ်ဖို့)
_LAST_RESULT_FILE = [""]

# guard thread က child ကို ဘာကြောင့် kill လုပ်ခဲ့လဲ — main က retry ဆုံးဖြတ်ဖို့ (output ထဲ
# မပါတာမို့ lines/text ကနေ ဖတ်လို့မရ)။ "_run_child" တစ်ခုချင်းစီ စတိုင်မှာ reset လုပ်တယ်။
_CHILD_STOP_REASON = [""]

# App က "PMK:" prefix lines တွေကို filter ပစ်တယ် — transport/retry ဆုံးဖြတ်ချက်တွေကို
# %TEMP%\pmk_mtk_diag.txt မှာ သီးသန့် ရေးထားရင် run ပြီးရင် ပြန်ဖတ်လို့ရတယ်။
_DIAG_PATH = os.path.join(os.environ.get("TEMP") or os.environ.get("TMP") or os.getcwd(),
                          "pmk_mtk_diag.txt")


def _diag(msg):
    try:
        import datetime
        with open(_DIAG_PATH, "a", encoding="utf-8") as f:
            f.write("%s %s\n" % (datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3], msg))
    except Exception:
        pass


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
    if os.environ.get("PMK_FLASH_PLAN") and args and args[0] == "--internal-cli":
        # Cancellation is cooperative in pmk_flash_guard; a timer must not kill a write.
        timeout = None
        no_progress_kill = None
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
    _CHILD_STOP_REASON[0] = ""

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
                _CHILD_STOP_REASON[0] = reason
                _safe_print("PMK: child stopped (%s)" % reason)
                try:
                    proc.kill()
                except Exception:
                    pass
                return

    if timeout or no_progress_kill:
        threading.Thread(target=guard, daemon=True).start()

    lines = []
    # mtkclient progress bar က '\r' overwrite (newline မဟုတ်) — line iterator က
    # မမြင်ရဘူး → '\r' ကိုလည်း line break အဖြစ် ကိုင်ပြီး throttle ပေး (UI မှာ
    # partition အတွင်း real-time % + MB/s ပြနိုင်အောင်)
    _last_prog_t = [0.0]
    try:
        buf = b""
        while True:
            chunk = proc.stdout.read1(4096) if hasattr(proc.stdout, "read1") else proc.stdout.read(4096)
            if not chunk:
                break
            buf += chunk.replace(b"\r", b"\n")
            parts = buf.split(b"\n")
            buf = parts.pop()
            for rawb in parts:
                line = rawb.decode("utf-8", "replace").strip("\r\n")
                lines.append(line)
                for marker in progress_markers:
                    if marker in line:
                        progress_seen[0] = True
                # progress bar line (Done |/Progress: + %) — 2s throttle (log flood မဖြစ်အောင်)
                if ("|" in line and "%" in line) or line.startswith("Progress:"):
                    now = time.time()
                    if not line.startswith("Progress:") and now - _last_prog_t[0] < 2.0:
                        continue
                    _last_prog_t[0] = now
                # UnlockTool-style [1/4]..[4/4] header — mtkclient line တွေကနေ
                # အဆင့်တစ်ခုချင်းစီကို တစ်ခါပဲ ပေါ်အောင် ထုတ်တယ် (kick/quiet mode မှာ မလုပ်)
                if not quiet:
                    try:
                        _stage_watch(line)
                    except Exception:
                        pass
                if line.startswith(_INTERNAL_PREFIX):
                    continue
                # quiet mode (kick step): mtkclient ရဲ့ handshake retry spam ကို မပြတော့ဘူး —
                # PMK ရဲ့ အဓိပ္ပာယ်ရှိတဲ့ လိုင်းတွေပဲ UI ဆီ ပို့တယ်
                if quiet and not line.startswith("PMK"):
                    continue
                # ဒီ print က encoding error နဲ့ ရပ်သွားရင် child output ဖတ်တာ ပြတ်ပြီး
                # အောင်/မအောင် စစ်တာ မှားသွားမယ် — ဒါကြောင့် safe print သုံးတယ်။
                _safe_print(line)
        if buf.strip():
            line = buf.decode("utf-8", "replace").strip("\r\n")
            lines.append(line)
            if not quiet and not line.startswith(_INTERNAL_PREFIX):
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
    အစား serial backend နဲ့ သွားတယ် — wdm_usb/VCOM driver ကို pyserial ကတော့ ကိုင်နိုင်တယ်
    (libusb က မကိုင်နိုင်ဘူး)။ မတွေ့ရင် "" ပြန်ပေးတယ် (USB အတိုင်း ဆက်သွား)။"""
    try:
        from serial.tools import list_ports
    except Exception:
        return ""
    ports = list(list_ports.comports())
    # အရင်ဆုံး MediaTek VID (0E8D) ကို ကိုယ်တိုင် စစ် — description စာသားအပေါ် မမှီခိုအောင်
    for p in ports:
        if getattr(p, "vid", None) == 0x0E8D:
            return getattr(p, "device", "") or ""
    for p in ports:
        text = ("%s %s" % (getattr(p, "description", "") or "", getattr(p, "hwid", "") or "")).lower()
        if "mediatek" in text or "preloader" in text or "mtk" in text:
            return getattr(p, "device", "") or ""
    return ""


def _apply_transport(args):
    """ချိတ်ဆက်နည်း (transport) ကို ဆုံးဖြတ်တယ်: USB (libusb) ဒါမှမဟုတ် serial/VCOM (COM port)။
    PMK_MTK_SERIAL = auto (default) | off | COM5 စသဖြင့်။
    --serialport ကို app က ပေးပြီးသားဆိုရင် ဒါက ဘာမှ မလုပ်ဘူး (user override ကို လေးစားတယ်)။

    retry တစ်ခုချင်းစီမှာ ပြန်ခေါ်နိုင်အောင် announce ကို ရွေးချယ်မှု ပြောင်းမှသာ လုပ်တယ်
    (log မပိုပွားအောင်) — ဖုန်း နောက်မှ ချိတ်တဲ့အချိန်မှာ COM port ပေါ်လာရင် serial ကို ပြောင်းသုံးတယ်။
    (MediaTek VCOM/wdm_usb driver ကို libusb မကိုင်နိုင်ဘူး — serial ကပဲ အလုပ်လုပ်တယ်)။"""
    args_text = " ".join(args)
    if "--serialport" in args_text:
        port = ""
        parts = args_text.split("--serialport", 1)[1].strip().split()
        if parts:
            port = parts[0]
        if _LAST_TRANSPORT[0] != ("app", port):
            _LAST_TRANSPORT[0] = ("app", port)
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

    if not port and low in ("", "auto", "1", "true"):
        # MediaTek USB device တက်ပြီး COM port ပေါ်ဖို့ driver က စက္ကန့်နည်းနည်း ကြာတယ် —
        # ဒီမှာ စောင့်ပြီးမှ serial ကို ရွေး။ (USB/libusb path က wdm_usb driver ကို မကိုင်နိုင်
        # လို့ "Operation not supported" နဲ့ fail ဖြစ်တယ် — serial ကပဲ လုံးဝလိုအပ်တယ်)
        if _mtk_usb_devices():
            for _wi in range(20):
                port = _detect_serial_port()
                if port:
                    break
                time.sleep(0.25)

    if port:
        _diag("apply_transport -> serial %s (mode=%s)" % (port, mode))
        if _LAST_TRANSPORT[0] != ("serial", port):
            _LAST_TRANSPORT[0] = ("serial", port)
            print("PMK: transport = serial/VCOM (%s) - COM port တွေ့တာမို့ serial နဲ့ သွားတယ်" % port)
            print("PMK_TRANSPORT_SERIAL %s" % port)
        return ["--serialport", port] + args
    try:
        from serial.tools import list_ports as _lp
        _seen = ",".join([p.device for p in _lp.comports()]) or "none"
    except Exception as _ex:
        _seen = "err:%s" % _ex
    _diag("apply_transport -> usb (mode=%s, comports=%s)" % (mode, _seen))
    if _LAST_TRANSPORT[0] != ("usb",):
        _LAST_TRANSPORT[0] = ("usb",)
        print("PMK: transport = USB (libusb/WinUSB)")
        print("PMK_TRANSPORT_USB")
    return args


def _uses_serial(args):
    """လက်ရှိ args က serial/VCOM transport သုံးနေလား။"""
    return any(a == "--serialport" for a in args)


def _serial_port_from_args(args):
    """args ထဲက --serialport <COM> ကို ပြန်ယူတယ် (error message တွေမှာ ပြဖို့)။"""
    for i, a in enumerate(args):
        if a == "--serialport" and i + 1 < len(args):
            return args[i + 1]
    return ""


# COM port ကို Windows က ဖွင့်မပေးတဲ့ failure — CreateFileW("\\\\.\\COMx") က
# error 87 (INVALID_PARAMETER) / 2 / 5 ပြန်တယ်။ device က USB ပေါ်မှာ ရှိပေမယ့်
# COM port node က stuck (reset/power-cycle ပြီး ကျန်ခဲ့) ဒါမှမဟုတ် အခြား program က
# ကိုင်ထားတာ — timing ပြဿနာမဟုတ်လို့ retry 20 ခုက အချိန်ပဲ ကုန်တယ်။
_SERIAL_OPEN_MARKERS = ("could not open port", "OSError(22",
                        "The parameter is incorrect", "not currently available")


def _serial_open_failure(text):
    """child output ထဲမှာ COM port open failure ရှိ/မရှိ — ရှိရင် marker စာသားပြန်တယ်။"""
    for m in _SERIAL_OPEN_MARKERS:
        if m in text:
            return m
    return ""


def _serial_port_listed(port):
    """COM port က ချိတ်ဆက်မှု list (SERIALCOMM / PnP) မှာ ရှိ/မရှိ။
    မရှိရင် device ထွက်သွားတာ (stuck မဟုတ်) → retry loop ကို ဆက်ခွင့်ပေးတယ်။"""
    if not port:
        return False
    try:
        from serial.tools import list_ports
        return any(getattr(p, "device", "") == port for p in list_ports.comports())
    except Exception:
        return True


# transport ရွေးချယ်မှု ပြောင်းမှသာ announce — retry တိုင်းမှာ log ထပ်မပွားအောင်
_LAST_TRANSPORT = [None]
# preflight သတိပေးချက်တွေကို device တစ်ခုအတွက် တစ်ခါပဲ ပြ
_PREFLIGHT_REPORTED = set()


def _current_brom_device():
    """BROM mode (VID_0E8D & PID_0003) device ရှိ/မရှိ။"""
    for dev in _mtk_usb_devices():
        if "PID_0003" in dev.upper():
            return dev
    return ""


def _install_serial_ep_patch():
    """serial/VCOM transport မှာ mtkclient ရဲ့ DA upload/read က EP_OUT/EP_IN (USB endpoint)
    ကို တိုက်ရိုက်ယူထားလို့ AttributeError နဲ့ ကျတယ်
    ('SerialClass' object has no attribute 'EP_OUT')။ SerialClass အတွက် EP_OUT/EP_IN
    shim တပ်ပေးပြီး chunk size ကို serial နဲ့ ကိုက်အောင် ပြောင်းပေးတယ်။"""
    try:
        from mtkclient.Library import Port as _port_mod
        from mtkclient.Library.Connection.seriallib import SerialClass
    except Exception:
        return False
    if getattr(_port_mod, "_pmk_serial_ep", False):
        return True

    class _EpShim:
        wMaxPacketSize = 1024

        def __init__(self, cdc, is_read=False):
            self._cdc = cdc
            self._is_read = is_read

        def write(self, data, pktsize=None, **kw):
            try:
                return self._cdc.write(data, pktsize)
            except TypeError:
                return self._cdc.write(data)

        def read(self, size=None, timeout=-1, **kw):
            try:
                return self._cdc.read(size, timeout)
            except TypeError:
                return self._cdc.read(size)

    orig_init = _port_mod.Port.__init__

    def patched_init(self, *a, **kw):
        orig_init(self, *a, **kw)
        try:
            if isinstance(self.cdc, SerialClass) and not hasattr(self.cdc, "EP_OUT"):
                self.cdc.EP_OUT = _EpShim(self.cdc, False)
                self.cdc.EP_IN = _EpShim(self.cdc, True)
        except Exception:
            pass

    _port_mod.Port.__init__ = patched_init
    _port_mod._pmk_serial_ep = True
    _diag("serial EP_OUT/EP_IN shim installed")
    return True


def _install_payload_retry_patch():
    """kamakiri payload ack မရရင် mtkclient က sys.exit(1) နဲ့ တန်းထွက်တယ်
    (pltools.py runpayload) — ဒါပေမယ် BROM session က ဆက်ရှိနေသေးလို့ USB မကျဘဲ
    ဒီနေရာမှာပဲ retry လုပ်နိုင်တယ်။ ဒါကြောင့် ဖုန်းကို BROM mode တစ်ကြိမ်ထဲ
    ချိတ်ထားရုံနဲ့ payload ကံကောင်း/မကောင်း မသက်ဆိုင်ဘဲ အလုပ်ပြီးစေတယ်
    (replug မလို)။ PMK_MTK_PAYLOAD_SENTRIES=1 ဆိုရင် retry ပိတ်တယ် (default 3)။"""
    try:
        retries = int(os.environ.get("PMK_MTK_PAYLOAD_SENTRIES", "3"))
    except ValueError:
        retries = 3
    if retries <= 1:
        return False
    from mtkclient.Library.pltools import PLTools
    original = PLTools.runpayload

    def patched(self, filename, offset=0, ack=0xA1A2A3A4, addr=None, dontack=False):
        for attempt in range(1, retries + 1):
            try:
                return original(self, filename, offset, ack, addr, dontack)
            except SystemExit:
                if attempt >= retries:
                    raise
                print("PMK: payload send failed (attempt %d/%d) - retrying in the same "
                      "BROM session (no replug needed) ..." % (attempt, retries))
                sys.stdout.flush()
                time.sleep(1.5)
        return False

    PLTools.runpayload = patched
    _diag("payload retry patch installed (retries=%d)" % retries)
    return True


def _internal_cli_main(argv):
    """Child process ထဲမှာ patch တွေ တင်ပြီး mtkclient CLI ကို run တယ်။"""
    _watch_parent()
    _diag("child argv=%r" % (argv,))
    try:
        forced = _install_bootmode_patch()
    except Exception as ex:
        print("ERROR: cannot import mtkclient (%s) - check: pip install mtkclient" % ex)
        return 11

    leaveusb = _install_leaveusb_patch()
    _install_serial_ep_patch()
    _install_preloader_dump_patch()
    _install_payload_retry_patch()

    # Required for firmware writes; import/API failures must stop before CLI execution.
    if os.environ.get("PMK_FLASH_PLAN"):
        try:
            from pmk_flash_guard import install
            install()
        except Exception as ex:
            print("PMK_ERROR: Firmware guard unavailable: %s" % ex)
            return 32

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
    _diag("child sys.argv=%r" % (sys.argv,))
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


def _preflight_mode_check(serial_mode=False):
    """Tool မ run ခင် ဖုန်းက BROM mode မှာ တကယ် ရှိ/မရှိ + driver binding ကို စစ်တယ်။
    BROM = PID_0003 ပါပဲ။ တခြား PID (preloader/DA) ဆိုရင် handshake က libusb crash
    (0xC0000005) ဖြစ်နိုင်တာမို့ user ကို အရင် သတိပေးတယ်။

    serial_mode=True (serial/VCOM transport) ဆိုရင် libusb driver သတိပေးစရာ မလို —
    pyserial က wdm_usb/VCOM driver နဲ့ တိုက်ရိုက် အလုပ်လုပ်နိုင်တယ် (Zadig မလို)။"""
    mtk = _mtk_usb_devices()
    if not mtk:
        return
    fresh = [d for d in mtk if d.split("|")[0].strip() not in _PREFLIGHT_REPORTED]
    if not fresh:
        return
    print("PMK: MediaTek USB device attached: %s" % " | ".join(fresh))
    for dev in fresh:
        inst = dev.split("|")[0].strip()
        _PREFLIGHT_REPORTED.add(inst)
        svc = (dev.split("|")[-1].strip().lower() if "|" in dev else "")
        up = inst.upper()
        if "PID_0003" not in up:
            print("PMK: NOT BROM mode (%s) - handshake may crash libusb" % inst)
            print("PMK_MTK_NOT_BROM")
            continue
        # BROM mode ဖြစ်ပေမယ့် driver က libusb-compatible မဟုတ်ရင် (ဥပမာ MTK serial/wdm_usb)
        # libusb က device ကို ကိုင်နိုင်မှာ မဟုတ်ဘူး — WinUSB/libusbK ပြောင်းရမယ်။
        # (serial/VCOM transport ဆိုရင်တော့ ဒါက ပြဿနာမဟုတ်ဘူး)
        if serial_mode:
            continue
        if svc and svc not in ("libusbk", "winusb", "libusb0"):
            print("PMK: BROM device driver is '%s' (not libusbK/WinUSB) - libusb may fail" % svc)
            print("PMK_MTK_BAD_DRIVER")


def _fastboot_usb_present():
    """Windows မှာ Google fastboot device (VID_18D1&PID_D00D) ကို တွေ့မွေး/မတွေ့ စစ်တယ်။
    driver/interface GUID မကိုက်ရင် `fastboot devices` က အလွတ်ပြပေမယ့် USB ပေါ်
    တကယ် ရှိနေတာ ဒီကနေ သိရတယ် (Xiaomi driver နဲ့ fastboot.exe blind ဖြစ်တာ)။"""
    ps = ["powershell", "-NoProfile", "-NonInteractive", "-Command",
          "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | "
          "Where-Object { $_.InstanceId -like '*VID_18D1&PID_D00D*' } | "
          "ForEach-Object { $_.InstanceId }"]
    out = _run_tool(ps, timeout=30)
    return [ln.strip() for ln in out.splitlines() if "VID_18D1" in ln.upper()]


def _usb_scan():
    """Present phone-side USB device တွေကို PS တစ်ခုတည်းနဲ့ ယူပြီး parse တယ်:
    fail=Windows error43 node, mtk=VID_0E8D, fastboot=18D1:D00D,
    android=Android/MTP enumeration (2717/18D1)။ watch loop တစ်ခုချင်းမှာ
    PS spawn တစ်ခုပဲ ကျန်အောင် ဒီလို စုယူတယ် (loop နှေးမသွားအောင်)။"""
    ps = ["powershell", "-NoProfile", "-NonInteractive", "-Command",
          "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | "
          "Where-Object { $_.InstanceId -like 'USB\\VID_*' } | "
          "ForEach-Object { $_.InstanceId + '|' + $_.FriendlyName }"]
    out = _run_tool(ps, timeout=30)
    res = {"fail": "", "mtk": [], "fastboot": False, "android": False}
    for ln in out.splitlines():
        up = ln.upper()
        if "VID_0000&PID_0002" in up or "DEVICE DESCRIPTOR REQUEST FAILED" in up:
            res["fail"] = ln.split("|")[0].strip()
        elif "VID_0E8D" in up:
            res["mtk"].append(ln.split("|")[0].strip())
        elif "VID_18D1&PID_D00D" in up:
            res["fastboot"] = True
        elif "VID_18D1" in up or "VID_2717" in up:
            res["android"] = True
    return res


def _kick_fail_node(inst):
    """error43 (descriptor fail) node ကို disable → enable လုပ်ပြီး Windows ကို
    ပြန် enumerate ခိုင်းတယ် — BROM/fastboot/android က ဒီ node နောက်မှာ
    ပုန်းနေတတ်တယ် (မ kick ရင် detection အဆင့်တွေ တစ်ခုမှ မရောက်)။ child က
    app (admin) ရဲ့ token က inherit မိလို့ Disable-PnpDevice ခွင့်ရတယ်။"""
    if not inst:
        return False
    idq = inst.replace("'", "''")
    ps = ("$id = '%s'; Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop; "
          "Start-Sleep -Milliseconds 500; "
          "Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop; 'KICK_OK'" % idq)
    # kick output/rc ကို raw log မှာ မှတ် — Windows ဘက်က disable/enable တကယ်
    # အလုပ်မလုပ်/ဘာ error ထွက်တယ်ဆိုတာ နောက် test မှာ ဖြေရှင်းနိုင်အောင်
    try:
        proc = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              timeout=30, creationflags=_NO_WINDOW)
        out = proc.stdout.decode("utf-8", "replace").replace("\r", " ").replace("\n", " | ").strip()
        ok = ("KICK_OK" in out)
        if ok:
            print("PMK: kick completed (rc=%d)" % proc.returncode)
        else:
            print("PMK: kick FAILED (rc=%d) out=%s" % (proc.returncode, out[:200]))
        return ok
    except Exception as ex:
        print("PMK: kick exception (%s)" % ex)
        return False


def _watch_boot(boot_wait=None, android_wait=None):
    """Post-op auto reboot — စောင့်ရုံသက်သက်မဟုတ်ဘဲ active ပြင်တယ်:
      ① error43 node → re-enumerate kick (BROM/fastboot/android ပြန်ပေါ်အောင်)
      ② BROM download-wait → JUMP_BL (Android boot chain ဆက်တက်)
      ③ fastboot → `fastboot reboot`
      ④ adb / Android USB → OK
    ဘာမှ မဖြစ်ရင် အခြေအနေနဲ့ ကိုက်တဲ့ marker ပဲ ပြန်တယ် — poweroff/error43 ကို
    "Android is booting" လို့ မမှားပြောတော့ဘူး။"""
    if boot_wait is None:
        boot_wait = float(os.environ.get("PMK_MTK_BOOT_WAIT", "45"))
    if android_wait is None:
        android_wait = float(os.environ.get("PMK_MTK_ANDROID_WAIT", "40"))
    kick_timeout = int(os.environ.get("PMK_MTK_KICK_TIMEOUT", "45"))
    kick_noprogress = int(os.environ.get("PMK_MTK_KICK_NOPROGRESS", "12"))
    # hybrid flash phase1→phase2 handoff (env bootmode=2 set by C# flash flow) —
    # phone ကို fastboot မှာပဲ ထားရမယ် (fastboot reboot / BROM kick မလုပ်)
    handoff = str(os.environ.get("PMK_MTK_BOOTMODE", "")).strip() == "2"

    print("PMK: watching device state after reboot (%.0fs)%s..."
          % (boot_wait, ", fastboot handoff" if handoff else ""))
    t0 = time.time()
    deadline = t0 + boot_wait
    if handoff:
        # bootmode=2 reboot က ~40s+ ကြာတတ် — fastboot enumerate အတွက် grace
        deadline = max(deadline, t0 + 65)
    st = {"fail_reported": False, "fail_kick_at": 0.0, "fail_kicks": 0,
          "brom_kicked": False, "fb_sent": False, "fb_stuck": False,
          "saw_device": False, "left_at": None, "state_at": 0.0,
          "direct_kick_at": -1e9, "direct_kicks": 0, "grace_used": False}

    while time.time() < deadline:
        # ---- Android (ADB) ----
        if _adb_serials():
            print("PMK: ADB device found - phone is in Android.")
            print("PMK_BOOT_OK")
            return True

        scan = _usb_scan()
        if scan["fail"] or scan["mtk"] or scan["fastboot"] or scan["android"]:
            st["saw_device"] = True
            st["left_at"] = None
        elif st["left_at"] is None:
            st["left_at"] = time.time()

        # ---- 10s တစ်ခါ USB အခြေအနေ timeline (raw log ထဲ မှတ်) ----
        if time.time() - st["state_at"] >= 10:
            st["state_at"] = time.time()
            print("PMK: state @%.0fs fail=%d mtk=%d fastboot=%d android=%d"
                  % (time.time() - t0, 1 if scan["fail"] else 0, len(scan["mtk"]),
                     1 if scan["fastboot"] else 0, 1 if scan["android"] else 0))

        # ---- Android USB enumeration (ADB ပိတ်ထားပေမယ့် device ပေါ်ရင် တက်ပြီ) ----
        if scan["android"]:
            print("PMK: Android USB device enumerated - phone is up.")
            print("PMK_BOOT_OK")
            return True

        # ---- fastboot → reboot to Android ----
        fb = _fastboot_serials() if scan["fastboot"] else []
        blind = False
        if not fb and scan["fastboot"]:
            # fastboot.exe က driver/interface မကိုက်လို့ serial မမြင်ရင်တောင် —
            # USB ပေါ် fastboot တက်နေတာ သေချာမို့ reboot ကို ကြိုးစားတယ်။
            fb, blind = ["?"], True
            print("PMK: fastboot device present on USB - fastboot.exe cannot see it (driver mismatch?)")
        if fb:
            if handoff:
                # hybrid flash handoff — fastboot reboot မပို့ဘဲ fastboot မှာပဲ ထား
                # (phase2 = C# ဘက်က fastboot flash ဆက်လုပ်မယ်)
                print("PMK: fastboot handoff ready (%s) - device left in fastboot for phase 2."
                      % ",".join(fb))
                print("PMK_FASTBOOT_HOLD")
                return True
            if not st["fb_sent"]:
                st["fb_sent"] = True
                deadline = max(deadline, time.time() + android_wait)
                print("PMK: fastboot device found (%s) - sending fastboot reboot." % ",".join(fb))
                print("PMK_BOOT_FASTBOOT")
                _run_tool([_tool_path("fastboot.exe"), "reboot"], timeout=30)
                if blind:
                    time.sleep(5)
                    if not _fastboot_serials() and _usb_scan()["fastboot"]:
                        st["fb_stuck"] = True
                        print("PMK: fastboot reboot had no effect (driver/interface mismatch) - "
                              "install the Google USB driver or hold Power 10s to restart the phone.")
            elif not _fastboot_serials() and not scan["fastboot"]:
                print("PMK: fastboot device is gone - phone is booting.")
                print("PMK_BOOT_LIKELY")
                return True
            time.sleep(2)
            continue

        # ---- Windows error43 → direct USB kick (BROM/fastboot ဒီ node နောက် ပုန်း) ----
        # mtkclient က UsbDk mode ကနေ run လုပ်လို့ op အတွင်း phone က PnP မှာ မပေါ်ဘူး —
        # op ပြီး python ထွက်သွားမှ normal enumeration ပြန်ဖြစ်ပြီး descriptor ဖတ်မရ (c43)
        # ဖြစ်ရင် PnP မှာ ဘာမှ မမြင်ရပေမယ့် libusb/UsbDk ကတစ်ဆင့် BROM ကို တိုက်ရိုက်
        # ထိန်းနိုင်သေးတယ် → blind JUMP_BL ပို့ (ဖုန်း BROM/preloader မှာ ဆို Android boot chain
        # ဆက်တက်သွားမယ်)
        if scan["fail"]:
            if not st["fail_reported"]:
                st["fail_reported"] = True
                print("PMK: Windows failed to read the phone's USB descriptor (error 43).")
                print("PMK_USB_FAILNODE")
            if (not handoff) and st["direct_kicks"] < 3 and time.time() - st["direct_kick_at"] >= 20:
                st["direct_kick_at"] = time.time()
                st["direct_kicks"] += 1
                print("PMK: direct JUMP_BL attempt %d (PnP hides the phone behind error 43)..."
                      % st["direct_kicks"])
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
                    print("PMK: direct kick crashed natively (exit 0x%08X)" % (int(krc) & 0xFFFFFFFF))
                elif "ok=1" in kick_line:
                    print("PMK: JUMP_BL accepted - the phone should continue booting now")
                    st["brom_kicked"] = True
                    deadline = max(deadline, time.time() + android_wait)
                else:
                    print("PMK: direct kick did not reach BROM (%s)" % (kick_line or ("exit %s" % krc)))
                time.sleep(2)
                continue
            if time.time() - st["fail_kick_at"] >= 10:
                st["fail_kick_at"] = time.time()
                st["fail_kicks"] += 1
                print("PMK: re-enumerating failed USB node (kick %d)..." % st["fail_kicks"])
                _kick_fail_node(scan["fail"])
                time.sleep(2)
            time.sleep(1)
            continue

        # ---- BROM download-wait → JUMP_BL (Android boot chain ဆက်တက်) ----
        if any("PID_0003" in d.upper() for d in scan["mtk"]):
            if handoff:
                # handoff mode — BROM kick မလုပ် (fastboot ရောက်လာမလားပဲ စောင့်)
                time.sleep(2)
                continue
            if not st["brom_kicked"]:
                st["brom_kicked"] = True
                print("PMK: phone is in BROM after reset - sending JUMP_BL to continue boot")
                print("PMK_KICK_SENT")
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
                    print("PMK: JUMP_BL accepted by BROM - waiting for the phone to boot")
                elif "handshake" in kick_line:
                    print("PMK: BROM did not answer the handshake after the reset")
                else:
                    print("PMK: JUMP_BL failed (exit %s)" % krc)
            time.sleep(2)
            continue

        # ---- တခြား MTK mode (preloader/DA) — ရပ်ကျန်နေတာ၊ ဆက်စောင့် ----
        if scan["mtk"]:
            time.sleep(2)
            continue

        # ---- USB ဘာမှ မရှိ (reboot/poweroff ထဲ ရောက်နေ) ----
        if st["fb_sent"] and st["left_at"] and time.time() - st["left_at"] >= 15:
            print("PMK: fastboot device is gone - phone is booting.")
            print("PMK_BOOT_LIKELY")
            return True
        # တွေ့ထားတဲ့ device ထွက်သွား = reboot စတယ် — fastboot/Android enumerate ဖြစ်
        # ဖို့ deadline ကို ထွက်ချိန် + 60s အထိ ချဲ့ (bootmode=2 မှာ reboot က ~40s ကျော်မှ
        # ဖြစ်ပြီး fastboot ပေါ်တာ deadline နဲ့ ကပ်နေလို့ ရှေ့ run တွေမှာ လွဲသွားတယ်)
        if st["left_at"] is not None and not st["grace_used"]:
            st["grace_used"] = True
            deadline = max(deadline, st["left_at"] + 60)
            print("PMK: device left USB - waiting up to 60s for fastboot/Android to enumerate ...")
        time.sleep(2)

    # ---- deadline ရောက် → တစ်ခါ final kick ပြီးမှ တကယ့်အခြေအနေနဲ့ ကိုက်တာ ပြန် ----
    if _adb_serials():
        print("PMK_BOOT_OK")
        return True
    scan = _usb_scan()
    if scan["fail"]:
        print("PMK: USB error 43 still present after %d re-kick(s) - final re-enumeration attempt."
              % st["fail_kicks"])
        _kick_fail_node(scan["fail"])
        time.sleep(3)
        if _adb_serials():
            print("PMK_BOOT_OK")
            return True
        scan = _usb_scan()
    if scan["fail"]:
        print("PMK: USB error 43 still present after %d re-kick(s)." % st["fail_kicks"])
        print("PMK_USB_FAILNODE")
        print("PMK_BOOT_MANUAL")
        return False
    if any("PID_0003" in d.upper() for d in scan["mtk"]):
        print("PMK: phone is still in BROM - JUMP_BL did not continue the boot.")
        print("PMK_BOOT_MANUAL")
        return False
    if scan["fastboot"]:
        if handoff:
            print("PMK: fastboot present after wait (handoff mode) - device left in fastboot.")
            print("PMK_FASTBOOT_HOLD")
            return True
        print("PMK: phone is still in fastboot - reboot command was not accepted.")
        print("PMK_MTK_STILL_ATTACHED")
        print("PMK_BOOT_MANUAL")
        return False
    if scan["mtk"]:
        print("PMK: phone is still attached on USB (%s) - Android did not start." % ",".join(scan["mtk"]))
        print("PMK_MTK_STILL_ATTACHED")
        print("PMK_BOOT_MANUAL")
        return False
    if st["fb_sent"]:
        print("PMK: fastboot reboot was sent and the device left USB - phone is booting.")
        print("PMK_BOOT_LIKELY")
        return True
    if st["saw_device"]:
        # ဖုန်း USB ပေါ်မှာ တွေ့ခဲ့ပြီး နောက်ဆုံး ထွက်သွား = reboot ဖြစ်နေတယ်
        print("PMK: device left USB - phone is rebooting (check the screen).")
        print("PMK_BOOT_LIKELY")
        return True
    # တစ်ခါမှ မတွေ့ = power off / USB မချိတ် → power key လိုအပ်
    print("PMK: no phone USB activity after reset - the phone looks powered off.")
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
    #
    # အရေးကြီး: transport ကို တစ်ခါပဲ ဆုံးဖြတ်ရင် ဖုန်း နောက်မှ ချိတ်တဲ့အချိန်မှာ USB အဟောင်းနဲ့
    # ကျန်ခဲ့တယ် — MediaTek VCOM (wdm_usb) driver ကို libusb မကိုင်နိုင်လို့ အမြဲတမ်း wait ဖြစ်တယ်။
    # retry တိုင်းမှာ base_args ကနေ ပြန် detect လုပ်ပြီး COM port ပေါ်လာရင် serial ပြောင်းသုံးတယ်။
    base_args = list(args)
    # mtkclient `multi` က ';' နဲ့ခွဲပြီး sub-command ကို rcmd.split(" ") နဲ့ parse တယ် —
    # "; reset" (semicolon နောက် space ပါ) ဆိုရင် ဒုတိယ sub-cmd ရဲ့ ပထမ token က
    # '' ဖြစ်ပြီး "argument command: invalid choice: ''" error ထွက်တယ် (exit 2)။
    # sub-cmd အတွင်း space (ဥပမာ `e frp,config`) ကိုတော့ မထိဘူး — ';' နောက်ကပဲ ဖယ်တယ်။
    # double-quote တွေကိုလည်း ဖယ် — multi split(" ") က quotes ကို strip မလုပ်ဘူး
    # (`w "a,b" "c"` → partitionname `"a,b"` အဖြစ် literal quote ပါသွားလို့ lookup fail)
    if len(base_args) >= 2 and base_args[0] == "multi":
        base_args = ["multi", re.sub(r";\s+", ";", base_args[1]).replace('"', "")]
    args = _apply_transport(base_args)

    # UnlockTool-style အဆင့်ပြ header အတွက်: op label ကို argv ကနေ တစ်ခါပဲ တွက်၊
    # အဆင့် state ကို attempt တိုင်းမှာ reset (ဖုန်း ပြန်ချိတ်ရင် [1/4]..[4/4] ပြန်ပေါ်)။
    _STAGE_OP[0] = _op_label(base_args)
    _stage_reset()

    _preflight_mode_check(serial_mode=_uses_serial(args))

    # ဖုန်းက USB မှာ ကြိုချိတ်ထားရင် mtkclient က "disconnect ပြီး reconnect" လို့ ပြတယ် —
    # အဲဒီအခါ user ဖုန်းကို ဖြုတ်/ပြန်တပ် လုပ်နိုင်အောင် ခဏ စောင့်ပြီး ပြန်စမ်းတယ်။
    attempts = int(os.environ.get("PMK_MTK_CONNECT_RETRIES", "20"))
    crash_left = int(os.environ.get("PMK_MTK_CRASH_RETRIES", "1"))
    # kamakiri payload send fail = BROM session အဟောင်း/timing race — fresh BROM နဲ့ပဲ ပြန်ရတယ်
    # (progress marker (CPU:/ME_ID) ပါလို့ အထက်က retry တွေ မဖမ်းနိုင်ဘူး → ဒီမှာ သီးသန့် ဖမ်း)
    payload_retries = 0
    payload_retries_max = int(os.environ.get("PMK_MTK_PAYLOAD_RETRIES", "3"))
    # COM port ဖွင့်မရတာ = stuck/busy port (timing မဟုတ်) — 3 ကြိမ်ပဲ စမ်းပြီး fail fast
    open_fail_retries = 0
    open_fail_max = int(os.environ.get("PMK_MTK_OPENFAIL_RETRIES", "3"))
    # Kick (JUMP_BL) အတွက် အချိန် ကန့်သတ်ချက်:
    #   PMK_MTK_KICK_NOPROGRESS — ဒီစက္ကန့်အတွင်း BROM က handshake မဖြေရင် ရပ် (default 12)
    #   PMK_MTK_KICK_TIMEOUT    — ဖြေရင် exploit+jump အတွက် အများဆုံး စက္ကန့် (default 45)
    # (ဒီ device မှာ BROM က reset ပြီးနောက် မဖြေတာမို့ no-progress kill က ၁၂ စက္ကန့်ပဲ ကုန်တယ်။)
    kick_timeout = int(os.environ.get("PMK_MTK_KICK_TIMEOUT", "45"))
    kick_noprogress = int(os.environ.get("PMK_MTK_KICK_NOPROGRESS", "12"))
    # device ချိတ်/handshake မဖြစ်ဘဲ ဒီစက္ကန့်ကျော်ရင် child ကို ရပ်ပြီး transport ပြန် detect —
    # မဟုတ်ရင် mtkclient က နာရီပေါင်းများစွာ USB ထဲတိတ်တိတ်လေး စောင့်နေတယ် (hang)။
    # handshake အောင်မြင်ရင် marker (Device detected / CPU:) ထွက်တာမို့ အလုပ်လုပ်နေရင် မထိဘူး။
    # ၁၀s — BROM device တွေ အမြန် ပေါ်/ပျောက်တတ်လို့ retry cycle ကို တိုတောင်းအောင်။
    connect_timeout = int(os.environ.get("PMK_MTK_CONNECT_TIMEOUT", "6"))
    # device တွေ့ပြီဆိုရင် (handshake retry စနေပြီဆိုရင်) child ကို မသတ်ဘူး — BROM ရဲ့
    # အချိန်က တိုလွန်းလို့ ဒီမှာ သတ်ပစ်ရင် handshake ပြီးခင်မှာပဲ ဖြတ်ကျတယ်။
    # ဒါပေမယ် device တက်/ပျောက် အမြန်လွန်းတဲ့အတွက် overall cap တစ်ခုတော့ ထားတယ်။
    overall_timeout = int(os.environ.get("PMK_MTK_OP_TIMEOUT", "120"))
    # w / wl = firmware-sized writes (super.img က 4.5GB — MST flash လို ၃ မိနစ်ကျော်
    # ကြာတယ်) — default 120s cap နဲ့ မိနစ်လယ်မှာ ဖြတ်ခံရမယ်
    _label_low = _op_label(base_args).lower()
    if (_label_low.startswith("writing ") or _label_low.startswith("flashing ")
            or _label_low.startswith("reading ")):
        overall_timeout = max(overall_timeout, 3600)
    wait_markers = ("Handshake failed", "Device detected", "Handshake successful",
                    "CPU:", "HW code:", "ME_ID", "In handleProgram",
                    "Formatting addr", "Formatted sector")
    # op တကယ် ချိတ်မိ/တိုးတက်ကြောင်း သက်သေ (handshake fail တာ မပါ)
    progress_markers = ("Device detected", "Handshake successful", "CPU:", "HW code:",
                        "ME_ID", "In handleProgram", "Formatting addr", "Formatted sector")
    delay = 2
    rc = 0
    text = ""
    last_text = ""
    # erase/reset ပြီးမှ USB close crash ဖြစ်ခဲ့ရင် exit code ကို မကိုင်ဘဲ success လုပ်မယ်
    crashed_after_work = False

    for attempt in range(1, attempts + 1):
        if attempt > 1:
            # ဒီကြားမှာ ဖုန်း ချိတ်မိရင် COM port ပေါ်လာပြီ — serial transport ကို ပြောင်းသုံး
            args = _apply_transport(base_args)
            _preflight_mode_check(serial_mode=_uses_serial(args))
            _stage_reset()
        # [1/4] Port check — attempt တိုင်းမှာ ဘယ် port/driver နဲ့ စောင့်နေလဲ ပြတယ်
        _emit_stage1(_stage1_text(args))
        rc, lines = _run_child(["--internal-cli"] + args,
                               timeout=overall_timeout,
                               no_progress_kill=connect_timeout,
                               progress_markers=wait_markers)
        last_text = "\n".join(lines)
        text = (text + "\n" + last_text).strip("\n")
        if os.environ.get("PMK_FLASH_PLAN") and rc != 0:
            print("PMK_ERROR: Firmware session failed or cancelled; automatic retry disabled")
            return rc

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
            # erase / reset command တွေ ပြီးမှ USB close မှာ crash ဖြစ်တာ (usb reset ကြောင့်)
            # — အလုပ်က ပြီးသားမို့ success အဖြစ် ဆက်တယ်။ retry လုပ်ရင် ဖုန်း ပြန် BROM
            # မရှိတော့ဘဲ "no connection" နဲ့ အဆုံးတွင်း fail ဖြစ်သွားတယ် (erase အောင်ပြီးမှ)။
            finished = (SHUTDOWN_STATE["called"] > 0 or
                        "Reset command was sent" in text or
                        "Formatted sector" in text or "Successfully formatted" in text)
            if finished:
                _diag("crash after completed operation (rc=0x%08X) - treating as done"
                      % (int(rc) & 0xFFFFFFFF))
                if "Reset command was sent" in text:
                    SHUTDOWN_STATE["called"] = max(SHUTDOWN_STATE["called"], 1)
                    if SHUTDOWN_STATE["ok"] is None:
                        SHUTDOWN_STATE["ok"] = True
                crashed_after_work = True
                break
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

        # COM port ကို Windows က ဖွင့်မပေးဘူး (CreateFile → error 87/2/5): device က
        # USB ပေါ်မှာ တကယ်ရှိပေမယ့် port node က stuck ဖြစ်နေ/အခြား tool က ကိုင်ထားတာ။
        # timing ပြဿနာ မဟုတ်လို့ ဒီအခြေအနေမှာ retry 20 ခုက အချိန်ပဲ ကုန်တယ် —
        # 3 ကြိမ်နဲ့ ရပ်ပြီး user ကို ရှင်းရှင်းလင်းလင်း ပြောတယ် (port တကယ်ပျောက်ရင်
        # _serial_port_listed က False → ဒီမှာ မဖမ်းဘဲ အထက်က device-wait loop ကို ဆက်တယ်)
        open_fail = _serial_open_failure(last_text)
        if open_fail:
            open_port = _serial_port_from_args(args) or _detect_serial_port()
            if _serial_port_listed(open_port):
                open_fail_retries += 1
                _diag("serial open attempt=%d/%d marker=%s port=%s stop=%s" %
                      (open_fail_retries, open_fail_max, open_fail, open_port,
                       _CHILD_STOP_REASON[0] or "-"))
                if open_fail_retries == 1:
                    print("PMK_MTK_COM_BUSY %s" % open_port)
                    sys.stdout.flush()
                if open_fail_retries >= open_fail_max or attempt >= attempts:
                    print("PMK_ERROR: %s cannot be opened (busy or stuck) - the device is "
                          "on USB but Windows refuses the port" %
                          (open_port or "MediaTek COM port"))
                    print("PMK: close SP Flash Tool / UnlockTool / any program using the "
                          "port, then power off (~10s) and replug USB in BROM mode")
                    print("PMK_NEED_REPLUG")
                    return 24
                time.sleep(delay)
                continue

        need_replug = "Please disconnect, start mtkclient and reconnect" in last_text
        no_progress = _CHILD_STOP_REASON[0].startswith("no progress")
        if no_progress and not need_replug and attempt < attempts:
            _diag("retry attempt=%d/%d reason=%s args_serial=%s" %
                  (attempt, attempts, _CHILD_STOP_REASON[0] or "-", _uses_serial(args)))
            # device မတွေ့ရသေးဘူး — retry မှာ transport ပြန် detect လုပ်ပြီး serial ပြောင်းသုံးမယ်
            if attempt == 1:
                print("PMK: no MTK device yet - re-detecting transport; plug the phone in "
                      "BROM mode (power off ~10s, hold Vol+ & Vol-) ...")
            elif attempt % 5 == 0:
                print("PMK: still no BROM/serial connection (%d/%d) - unplug/replug USB ..."
                      % (attempt, attempts))
            time.sleep(delay)
            continue
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
        # kamakiri payload send fail — CPU:/ME_ID ထွက်ပြီးမှ fail တဲ့အတွက် progress marker
        # ရှိနေလို့ အထက်က retry တွေ မဖမ်းဘူး။ BROM session အသစ် (power cycle) မလို့ မအောင် —
        # user ကို ပြောပြီး fresh BROM နဲ့ ပြန်စမ်း (default 3 ကြိမ်၊ PMK_MTK_PAYLOAD_RETRIES နဲ့ ပြောင်းလို့ရ)
        if "Error on sending payload" in last_text and payload_retries < payload_retries_max \
                and attempt < attempts:
            payload_retries += 1
            _diag("retry attempt=%d/%d reason=kamakiri payload error (try %d/%d)" %
                  (attempt, attempts, payload_retries, payload_retries_max))
            print("PMK: kamakiri payload send failed (timing/BROM session) - power off the "
                  "phone (~10s), hold Vol+ & Vol- while replugging USB, then wait ... "
                  "(retry %d/%d)" % (payload_retries, payload_retries_max))
            print("PMK_NEED_REPLUG")
            time.sleep(6)
            continue
        # child က သူ့ဘာသာ ထွက်သွားပေမယ့် device နဲ့ ချိတ်မမိရင် (ဥပမာ libusb က wdm_usb
        # driver ကို မကိုင်နိုင်လို့ "Operation not supported" နဲ့ ကျ) — transport ပြန် detect
        # လုပ်ပြီး retry (မဟုတ်ရင် ဒီနေရာမှာပဲ ရပ်ပြီး တစ်ခါတည်း fail ဖြစ်သွားတယ်)
        if not any(m in last_text for m in progress_markers) and attempt < attempts:
            _diag("retry attempt=%d/%d reason=no connection (rc=%s, serial=%s)" %
                  (attempt, attempts, rc, _uses_serial(args)))
            if attempt == 1:
                print("PMK: not connected yet - re-detecting transport (this phone needs the "
                      "COM/serial path; USB is blocked by the wdm_usb driver) ...")
            elif attempt % 5 == 0:
                print("PMK: still no connection (%d/%d) - plug/replug the phone ..."
                      % (attempt, attempts))
            time.sleep(delay)
            continue
        break

    if os.environ.get("PMK_FLASH_PLAN"):
        import json
        expected = [item["name"] for item in json.loads(os.environ["PMK_FLASH_PLAN"])["items"]]
        written = [line.split(":", 1)[1].strip() for line in last_text.splitlines()
                   if line.startswith("PMK_FLASH_WRITTEN:")]
        if written != expected or "PMK_PREFLIGHT_OK:" not in last_text:
            print("PMK_ERROR: Firmware session lacks live preflight or complete write evidence")
            return 32

    # op က [4/4] header မထွက်ဘဲ ပြီးသွားရင် (ဥပမာ reset-only op) ဒီမှာ ဖြည့်တယ်
    _flush_stages()

    # ကြိုးစားအားလုံး ကုန်ပြီး handshake တစ်ခါမှ မအောင်ရင် — user ကို ရှင်းရှင်းလင်းလင်း ပြောတယ်
    # (မဟုတ်ရင် rc=0 နဲ့ PMK_OK ပြန်ပြီး အောင်မြင်သလို မှားထင်ရတာမို့)။
    if not any(m in text for m in progress_markers):
        _diag("final: no connection markers (rc=%s, child_len=%d)" % (rc, len(text)))
        print("PMK_ERROR: MediaTek device was not detected (BROM/serial handshake never completed)")
        print("PMK: power off the phone (~10s), hold Vol+ & Vol- while plugging USB, then run again")
        print("PMK_NEED_REPLUG")
        return 23

    # exit code က 0/None ဖြစ်နေရင်လည်း failure အချက် ရှိရင် မအောင်ဘူးလို့ မှတ်။
    # မှတ်ချက်: "Couldn't detect partition" က partition တစ်ခု မရှိလို့ ဖြစ်တယ် — တခြား
    # partition တွေ ဖျက်ပြီးသားဆိုရင် partial success (ဥပမာ: ဒီ device မှာ config မရှိ)။
    erased_something = ("Formatted sector" in text) or ("Formatting addr" in text)
    partial_erase = False   # "Couldn't detect partition" ကို tolerate လုပ်ခဲ့ရင် True (marker မထုတ်)
    cmd_text = " ".join(args)
    # erase (+ ပါတဲ့ reset) ပြီးမှ device က USB ပြုတ်လို့ ထွက်တဲ့ error/traceback တွေ —
    # အလုပ်က ပြီးသားမို့ failure အဖြစ် မမှတ်ဘူး (erase အောင်ပြီးမှ exit 20/1 ရတာ ပျက်အောင်)
    reset_cmd = "reset" in cmd_text
    # terminal အဆင့် ရောက်ပြီ = erase ပြီး / shutdown (reset) တကယ် ပို့ပြီး —
    # ဒီအခြေအနေမှာ fail marker တွေ stale (early retry attempt ထဲက) ဆိုရင် ignore
    reached_terminal = (erased_something or SHUTDOWN_STATE["called"] > 0
                        or "Reset command was sent" in text)
    completed = reached_terminal and (
        not reset_cmd or SHUTDOWN_STATE["called"] > 0 or "Reset command was sent" in text)
    # FAIL_MARKERS ကို final attempt ရဲ့ output နဲ့ပဲ စစ် — early retry attempt တွေက
    # transient connect-fail ("Please disconnect...") ထုတ်ရာမှာ ပါသွားရင် final
    # attempt အောင်ပြီးမှ stale marker ကြောင့် whole op fail မဖြစ်စေနဲ့
    marker_text = last_text if any(m in last_text for m in progress_markers) else text
    for marker in FAIL_MARKERS:
        if marker not in marker_text:
            continue
        if marker == "Couldn't detect partition" and (
                erased_something or " wl " in " " + cmd_text + " "
                or os.environ.get("PMK_MTK_TOLERATE_MISSING", "") == "1"):
            # multi (တစ်ခုထက်များ command) မှာ ဒီ device မရှိတဲ့ partition ကို skip လုပ်တာ
            # ပုံမှန်ဖြစ် — PMK_MTK_TOLERATE_MISSING=1 ဆို warning ပဲ (operation ကို မဖျက်)
            print("PMK_WARN: %s (partition တချို့ ဒီဖုန်းမှာ မရှိပါ - ကျန်တာတွေ ဆက်လုပ်)" % marker)
            partial_erase = True
            print("PMK_PARTIAL_ERASE")
            continue
        if completed:
            print("PMK_WARN: '%s' appeared AFTER the operation completed - ignored" % marker)
            continue
        print("PMK_ERROR: %s" % marker)
        if "Please disconnect" in marker:
            print("PMK_NEED_REPLUG")
        return 20

    # child က error traceback နဲ့ exit 1 ဖြစ်ပေမယ့် အလုပ် ပြီးပြီဆို rc ကို ပယ်ဖျက်
    if completed and isinstance(rc, int) and rc != 0:
        print("PMK: child exit %s ignored - the operation had already completed" % rc)
        rc = 0

    # erase op အောင်မြင်ကြောင်း marker — mtkclient ရဲ့ "Formatted sector ..." line ကို UI က
    # ဖျောက်ထားလို့ ဒီမှာ partition နံပါတ်နဲ့ အတည်ပြု ပြန်ပေးတယ် (Form1.cs → [+] Erased line)
    if erased_something and not partial_erase:
        parts_txt = ",".join(_erase_parts(args))
        print("PMK_ERASE_OK" + (" " + parts_txt if parts_txt else ""))

    if isinstance(rc, int) and rc != 0 and not crashed_after_work:
        return rc

    # reset ပါတဲ့ command ဆိုရင် shutdown တကယ် အောင်မှ success (positive evidence)
    # မှတ်ချက်: args က ["multi", "e frp;reset"] တဲ့အတွက် command စာသား တစ်ခုလုံးထဲမှာ
    # "reset" ရှိ/မရှိ စစ်ရတယ် (cmd_text အထက်မှာ တွက်ပြီးသား)။
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
            # auto reboot ကို active လုပ်တဲ့ watch (error43 re-kick / BROM JUMP_BL /
            # fastboot reboot) အတွက် စောင့်ချိန် ရှည်မှ အဆင့်တွေ အချိန်ရှိမယ်
            short_wait = float(os.environ.get("PMK_MTK_BOOT_WAIT", "45"))
        elif kicked and not kick_ok:
            short_wait = 12
        else:
            short_wait = None
        # ဖုန်း တကယ် တက်/မတက် ကိုယ်တိုင် စစ်တယ်
        boot_watch_started = time.monotonic()
        _watch_boot(boot_wait=short_wait)
        if os.environ.get("PMK_FLASH_PLAN"):
            print("PMK_TIMING: Reboot observation: %.1f seconds" % (time.monotonic() - boot_watch_started), flush=True)

    print("PMK_OK")
    return 0


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--internal-cli":
        sys.exit(_internal_cli_main(sys.argv[2:]))
    if len(sys.argv) > 1 and sys.argv[1] == "--internal-kick":
        sys.exit(_internal_kick_main())
    sys.exit(main())

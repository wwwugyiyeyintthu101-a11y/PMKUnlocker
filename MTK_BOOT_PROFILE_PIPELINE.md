# MTK native boot-recipe pipeline

ဒီစာရွက်စာတမ်းက PMK Mobile Tool ရဲ့ **native MTK boot recipe** ကို ထုတ်တဲ့ procedure ပါ။
Pipeline ကိုယ်တိုင် (pcap, DA/EMI blob, python tools) က repo ရဲ့ **အပြင်မှာ** ရှိတယ် —
`C:\Users\PMK\PMK-bench\mtk-da-spike`။

စာရွက်စာတမ်း ဒီမှာ ရှိတာ procedure ကို မှတ်တမ်းတင်ဖို့ပါ။ **DA blob, EMI image, pcap, extracted
`.bin` တွေကို repo ထဲ ဘယ်တော့မှ မထည့်ရ၊ commit မလုပ်ရ** — unlicensed pack တွေကနေ ဆင်းလာတာမို့
licensing က PMK ရဲ့ ဆုံးဖြတ်ချက်ပါ။ `tools\` နဲ့ `bundled-tools\`, `edl\`, `mtk/boot-profiles` ကို
`.gitignore` က ကာထားတယ်; pre-push gate ကို မချိုးရ။

---

## 1. Recipe ဆိုတာ ဘာလဲ

Recipe (boot profile) ဆိုတာ **တကယ် boot ဖြစ်ခဲ့တဲ့ session တစ်ခုကို byte-for-byte replay လုပ်တဲ့ script** ပါ။
ဖုန်းတစ်လုံးကို BROM ကနေ စပြီး live DA အထိ တင်တဲ့ frame အားလုံး (host က ပို့တာ၊ ဖုန်းက ပြန်တာ၊
endpoint-zero control transfer တွေ) ကို အစဉ်လိုက် မှတ်ထားတယ်။ ခန့်မှန်းတာ မပါဘူး — မြင်တဲ့ bytes ပဲ ပါတယ်။

Recipe ကို အပိုင်း နှစ်ပိုင်း ခွဲတယ်:

| အပိုင်း | အစ | အဆုံး |
|---|---|---|
| `bromSteps` | `0xfd` HW-code query | DA1 ရဲ့ `0xc0` alive byte (jump အောင်ပြီး) |
| `daSteps` | DA handshake + `SYNC` + `bl_loader_info` / DA2 / DA-extension uploads | ပထမ `0x10005` READ မတိုင်ခင် (storage command မပါ) |

Reply တစ်ခုချင်းကို content ကနေ အမျိုးအစား ခွဲတယ်: `echo` (host ပို့တာကို ပြန်တူ), `zero` (status 0),
`len` (register/RAM ဖြေ — အရှည်ပဲ စစ်တယ်၊ content က unit အလိုက် ကွာလို့), `token` (hw-code / da-alive),
`sync`, `hdr` (12-byte `EF EE EE FE` DA header), `complement` (greet), `ctrl-ack` / `ctrl-stall`.

`"opt": true` ဆိုတဲ့ step တွေက **post-payload CDC line-setting probe နှစ်ခု** ပါ (SET_LINE_CODING +
SET_CONTROL_LINE_STATE)။ Pipe က မသယ်နိုင်ရင် log ရေးပြီး ကျော်လို့ရတယ် — နောက်က bulk step က boot ကို သက်သေပြတယ်။
(အသေးစိတ်: `PMKUnlocker\MediaTek\MtkBootProfile.cs` ရဲ့ header comment)

---

## 2. Pipeline folder ထဲ ဘာတွေ ရှိလဲ

| နေရာ (bench) | အကြောင်း |
|---|---|
| `tools\b6-profile.py` | **recipe writer** — pcap → profile dir (အဓိက tool) |
| `tools\b6-profile.v1-mt6877.py` | MT6877 အတွက် hardcode လုပ်ထားတဲ့ မူရင်း version (reference ပဲ) |
| `tools\b6-blobs.py` | upload တွေကို သီးခြား ဆွဲထုတ်တဲ့ extractor (writer ရဲ့ `--blobdir` cross-check အတွက်) |
| `tools\r38load.py` | USBPcap pcap ဖတ်တဲ့ loader (writer က import လုပ်တယ်) |
| `tools\wireshark\tshark.exe` | pcap ကြည့်/ခွဲဖို့ |
| `capture\*.pcap` | သမိုင်းဝင် capture တွေ (MT6765 RUN 37/38/47/48, MT6877 B6/B6.4) |
| `capture\blobs\` | MT6877 B6 capture ရဲ့ သီးခြား upload extraction (sha256 ပါ) |
| `out-mt6877-b6\` | MT6877 recipe compile ရဲ့ နောက်ဆုံး ရလဒ် + `compile.log` |
| `out-mt6765-r38\` | RUN 38 capture ကနေ ဆင်းတဲ့ MT6765 recipe (စမ်းသပ်မှု) |

Python 3 လိုတယ် (`python tools\b6-profile.py ...` အတိုင်း run နိုင်တယ်)။

---

## 3. လက်ရှိ ရှိပြီးသား chipset

| Chipset | HW code | bromSteps | daSteps | blobs | အခြေအနေ |
|---|---|---|---|---|---|
| MT6765 | 0x0766 | 4486 | 250 | 5 | hardware-proven (Redmi 9A) |
| MT6877 | 0x0959 | 7717 | 250 | 5 | hardware-proven (Redmi Note 12 Pro) |

တပ်ဆင်ရမယ့် နေရာ:

```
PMKUnlocker\bin\Debug\net8.0-windows\mtk\boot-profiles\<chipset>\
    <chipset>_boot_profile.json
    <chipset>_kamakiri_payload.bin
    <chipset>_da1.bin
    <chipset>_bl_loader_info.bin
    <chipset>_da2_patched.bin
    <chipset>_daext_patched.bin
```

**C# ပြင်စရာ မလိုဘူး.** `MtkBootProfile.FindProfiles` က `mtk\boot-profiles\**\*.json` ကို ရှာပြီး
Auto က JSON ထဲက `hwCode` နဲ့ ဖုန်းကို ကိုက်တယ်။ `Load` က blob တိုင်းရဲ့ sha256 နဲ့ framed upload တိုင်းကို စစ်တယ် —
မကိုက်ရင် boot မစဘဲ ရပ်တယ်။

---

## 4. Chipset အသစ် ထည့်တဲ့ procedure

လိုအပ်ချက်: အဲဒီ chipset ရဲ့ **ဖုန်းတစ်လုံး** (bench ပေါ်မှာ) + USBPcap + admin shell။
Chip အချင်းချင်း မျှတဲ့ BROM prelude မရှိဘူး (MT6765 နဲ့ MT6877 ရဲ့ `bromSteps` 4486 vs 7717;
kamakiri 612 B ဒါပေမဲ့ sha256 မတူ; daext 16,428 B ဒါပေမဲ့ sha256 မတူ)။ ဒါကြောင့် **အဲဒီဖုန်းပေါ်မှာ capture ဆွဲရမယ်**။

### 4.1 Capture ဆွဲ

1. USBPcap driver ရှိမရှိ စစ် (`tools\USBPcapSetup-1.5.4.0.exe` က installer)။ admin shell မှာ:
   ```
   "C:\...\Wireshark\dumpcap.exe" -D
   ```
   and the capture device `\\.\USBPcap1` ရှိရမယ်။
2. Capture ကို **plug မတိုင်ခင် စထားရမယ်**:
   ```
   dumpcap -i \\.\USBPcap1 -w capture\<chip>-<date>.pcap
   ```
3. ဖုန်းကို BROM ထဲ ထည့်: Power off → volume up+down နှစ်ခုလုံး ဖိထား → USB plug။
4. အဲဒီ session ကို **DA live အထိ** တင်ရမယ် — mtkclient (`tools\mtkclient\mtk.exe`) ဒါမှမဟုတ်
   native tab က Boot Device (recipe ရှိပြီးသား chip ဆိုရင်)။
5. DA live ဖြစ်တာ သေချာပြီဆိုရင် capture ကို ရပ် (Ctrl+C)။

Capture မှာ ဒီနှစ်ခု ပါရမယ်: ပထမ DA command ရှေ့ **`0xc0` alive byte** နဲ့ အနည်းဆုံး **`0x10005` READ တစ်ခု**
(မပါရင် writer က ငြင်းတယ် — အဲဒါက boot အထိ မရောက်တဲ့ capture ပါ)။

### 4.2 Recipe compile

```
cd C:\Users\PMK\PMK-bench\mtk-da-spike
python tools\b6-profile.py "capture\<chip>-<date>.pcap" "out-<chip>" --blobdir "capture\blobs"
```

| Option | အဓိပ္ပာယ် |
|---|---|
| `<pcap>` | capture ဖိုင် |
| `<outdir>` | output folder (မရှိရင် ဆောက်တယ်) |
| `--chipset MT6768` | marketing name။ `CHIPSETS` table မှာ HW code ရှိရင် မလိုဘူး; ကွဲလွဲရင် ငြင်းတယ် |
| `--names a,b,c,d,e` | upload ၅ ခုရဲ့ role အမည် (မပေးရင် positional default) |
| `--blobdir DIR` | `b6-blobs.py` နဲ့ သီးခြား ဆွဲထုတ်ထားတာနဲ့ sha256 တိုက်စစ် |
| `--note TEXT` | profile ရဲ့ `note` ထဲ ထပ်ဖြည့်စာ |

`CHIPSETS` table က `tools\b6-profile.py` ထဲမှာ ရှိတယ်:

```python
CHIPSETS = {0x0766: "MT6765", 0x0959: "MT6877"}
```

ကိုယ့် chip မပါရင် `--chipset` ပေးရမယ်၊ ဒါမှမဟုတ် table ထဲ ထည့်လို့ရတယ် (bench-measured code ပဲ ထည့်ရမယ်၊
မှန်းထည့်တာ မလုပ်ရ)။

### 4.3 ရလဒ် စစ်

ဒီ line တွေ ထွက်ရမယ်:

```
BROM face: HW 0x0XXX status 0x0000
chipset MT68xx (HW 0x0XXX)
blob mt68xx_kamakiri_payload.bin      612 B chunk 612   framed False brom sha ...
blob mt68xx_da1.bin                ....... B chunk 512   framed False brom sha ...
blob mt68xx_bl_loader_info.bin     ....... B chunk ....  framed True  da   sha ...
blob mt68xx_da2_patched.bin        ....... B chunk 512   framed True  da   sha ...
blob mt68xx_daext_patched.bin      16428 B chunk 512   framed True  da   sha ...
CDC line-setting write(s) in front of the first greet answer, marked optional: 2
daSteps end before the first 0x10005 READ: NNN kept, NNN storage-stage steps left out
  bromSteps replayed against the wire: N B written, N B read, every assertion held
  daSteps   replayed against the wire: N B written, N B read, every assertion held
```

- `every assertion held` နှစ်ကြောင်းလုံး ရှိရမယ် — မရှိရင် recipe မသုံးရ။
- `daSteps` ကို `0x10005` READ မတိုင်ခင် ဖြတ်ထားတာ မှန်ရမယ်။
- `out-<chip>\` ထဲမှာ JSON တစ်ခု + `.bin` ၅ ခု ရှိရမယ်။

### 4.4 တပ်ဆင်

`out-<chip>\` ထဲက ဖိုင်အားလုံးကို

```
C:\Users\PMK\Desktop\PMKUnlocker\PMKUnlocker\bin\Debug\net8.0-windows\mtk\boot-profiles\<chipset>\
```

ထဲ copy။ (folder အမည်က အရေးမကြီး — JSON ကို recursive ရှာတယ်; `hwCode` ကပဲ ကိုက်တယ်)

### 4.5 Hardware proof

ဖုန်းကို BROM ထဲ ပြန်ထည့် → app ရဲ့ Boot Device → log မှာ `[+] MTK DA ready — MT68xx (HW XXXX)` ထွက်ရမယ်။
ဒါကို မရောက်သေးရင် recipe က "compiled" ပဲ၊ "proven" မဟုတ်သေးဘူး။ ၄င်းနောက်မှ geometry/GPT read,
FLASH grid အတိုးအလျှော့ကို စမ်းလို့ရတယ်။

---

## 5. Writer က ဘာတွေကို ငြင်းသလဲ (fail-closed)

| Message | အဓိပ္ပာယ် / လုပ်ရမယ့်အရာ |
|---|---|
| `no wire events on the session device` | pcap မှာ bulk traffic မရှိ — မှားတဲ့ interface ဖြစ်နိုင်; `dumpcap -D` နဲ့ ပြန်စစ် |
| `no 0xc0 DA1 alive byte before the first DA command frame` | DA1 ထဲ မ၀င်ခဲ့ဘူး — session ကို live DA အထိ တင်ပြီး ပြန် capture |
| `no 0x10005 READ in the DA stage` | DA stage မှာ storage read မပါ — capture အရှည် မလုံလောက် |
| `HW 0x.... has no name in CHIPSETS - pass --chipset` | `--chipset` ပေး ဒါမှမဟုတ် table ထဲ ထည့် |
| `--chipset X contradicts the measured table` | capture နဲ့ အမည် မကိုက် — မှားတဲ့ pcap ဖြစ်နိုင် |
| `the run boundary is wrong` / `declared N B` | upload အပိုင်းအခြား မှား — capture ကို ပြန်စစ်၊ writer ကို မဖြစ်အောင် မဖုံးရ |
| `is not in the independent extraction` | `--blobdir` က အခြား capture ရဲ့ extraction ဖြစ်နိုင်; မှန်တာနဲ့ ပြန်တိုက် |
| `an answer of N bytes runs past the end of the capture` | capture ပြတ်တောက် — session ကို အပြည့် ပြန် capture |

---

## 6. သတိထားရမယ် အချက်တွေ

- **Capture ကို passive ပဲ။** ဖုန်းကို writer/analysis က မထိတာ။ Capture ရှိမှ recorder က လုပ်ရတယ်။
- **DA blob, EMI image, pcap တွေကို repo ထဲ မထည့်ရ၊ commit မလုပ်ရ။** Pipeline folder တစ်ခုလုံး git အပြင်မှာ။
- **Device-unique bytes မ logged ဖို့**: recipe ထဲက `len` reply တွေက အရှည်ပဲ သိမ်းတယ်; `hwCode` ကလွဲလို့
  device id (serial, CID, SOC_ID) မပါဘူး။ Log/output ကို အပြင်မှာ မျှဝေရင် ဒါကို သတိထား။
- **Recipe ဟာ boot အထိပဲ။** Storage command (read/write/erase) ကို session က ကိုယ်တိုင် build လုပ်တယ်;
  recipe ကို ပြင်ပြီး storage step ထည့်တာ မလုပ်ရ။
- **Capture တစ်ခုက chip တစ်ခု။** ROM/bootloader version ကွာရင် `bromSteps` ကွာနိုင်တယ် — အဲဒီအခါ
  အဲဒီ unit အတွက် သီးခြား capture လိုတယ်။
- **`--names` က အလှပဲ။** Filename ကို loader မဖတ်ဘူး; bytes ပဲ ဖတ်တယ်။ ဒါကြောင့် default role အမည်
  (`bl_loader_info` နဲ့ တကယ့် `emi_settings` ကဲ့သို့) ကွာလည်း boot ကို မထိခိုက်။

---

## 7. ကိုးကား — သက်သေပြထားတဲ့ နှစ်ခု

| စမ်းသပ်မှု | ရလဒ် |
|---|---|
| MT6877 B6 capture ပြန် compile (`out-mt6877-b6\`) | `bromSteps` 7717 / `daSteps` 250 / blobs 5 — တပ်ဆင်ထားတဲ့ recipe နဲ့ **byte-identical**; `opt` က step 162/163 |
| RUN 38 capture (`out-mt6765-r38\`) | `--chipset` မပါဘဲ **MT6765 (0x0766)** အလိုအလျောက်; upload ၅ ခု sha256 က တပ်ဆင်ထားတဲ့ mt6765 blobs နဲ့ တူ; နှစ်ပိုင်းလုံး replay အောင် |
| MT6877 capture မှာ `--chipset MT6765` | ငြင်း (`contradicts the measured table`) |
| capture ပြတ်တောက် | readable message နဲ့ ရပ် (အရင် `IndexError` ဖြစ်တာ ပြင်ပြီး) |

MT6765 ရဲ့ step list က တပ်ဆင်ထားတဲ့ recipe နဲ့ ကွာတာက မှားတာ မဟုတ်ဘူး — တပ်ဆင်ထားတဲ့ mt6765 recipe က
capture သုံးခု (RUN 6 printgpt + RUN 38 control probes + RUN 39 re-open) ကို လက်နဲ့ ပေါင်းထားတာ၊ `source` field က
အဲဒါကို ဆိုထားတယ်။

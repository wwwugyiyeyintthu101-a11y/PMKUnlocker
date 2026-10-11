# PMK Mobile Tool — Project review

နောက်ဆက်တွဲ: ဤ review နောက်ပိုင်း code ပြင်ဆင်မှုများ ပြုလုပ်ထားသည်။ လက်ရှိပြင်ဆင်ပြီး/ကျန်ရှိသည့်အခြေအနေကို `REPAIR_NOTES_2026-10-11.md` တွင်ကြည့်ပါ။ အောက်ပါတွေ့ရှိချက်များသည် ပြင်ဆင်မတိုင်မီ snapshot ဖြစ်သည်။

စစ်ဆေးသည့်နေ့: 2026-10-11 (Asia/Rangoon)  
စစ်ဆေးသည့်အခြေအနေ: လက်ရှိ working tree၊ version 7.3.4-preview၊ မူလရှိနေသော modified/untracked files များအပါအဝင်။

## အဓိကသုံးသပ်ချက်

Build နှင့် ရှိပြီးသား automated checks များ အောင်မြင်သည်။ သို့သော် အများပြည်သူ/ဆိုင်များသို့ production release ဖြန့်ချိရန် အဆင်သင့်ဟု မသတ်မှတ်သင့်သေးပါ။ Device selection၊ firmware execution၊ admin web UI၊ license persistence နှင့် clean-PC setup တွင် အောက်ပါပြဿနာများကို ပြင်ဆင်ရန်လိုသည်။

ဤသည် source review နှင့် hardware မလိုသော checks ဖြစ်သည်။ ဖုန်း firmware မရေးထားပါ၊ USB operation မလုပ်ထားပါ၊ live license database ကို request မပို့ထားပါ၊ installer/update ကို မစမ်းထားပါ။ Binary third-party tools အတွင်းပိုင်းနှင့် model တစ်ခုချင်း compatibility ကို မစစ်ဆေးနိုင်သေးပါ။ Source အဓိကလမ်းကြောင်းများကို စစ်ဆေးထားခြင်းဖြစ်ပြီး code လိုင်းတိုင်း အပြစ်ကင်းကြောင်း အာမခံချက်မဟုတ်ပါ။

## အတည်ပြုနိုင်ခဲ့သော checks

| စစ်ဆေးချက် | ရလဒ် |
|---|---|
| Desktop app — `dotnet build PMKUnlocker/PMKUnlocker.csproj --no-restore` | PASS၊ warnings 0၊ errors 0 |
| License server — build --no-restore | PASS၊ warnings 0၊ errors 0 |
| License admin — build --no-restore | PASS၊ warnings 0၊ errors 0 |
| ရှိပြီးသား Regression program | 158 checks PASS |
| Loader-package fixture | 5 checks PASS |
| လက်ရှိ Debug output နှင့် source loader comparison | Brands 17၊ entries 860၊ unique files 860၊ SHA-256 comparison PASS |
| Python helper 4 ခု AST syntax parsing | PASS |
| Fresh Release publish / installer / clean Windows | မစမ်းထား |
| Real-phone flash / restore / boot / cable disconnect | မစမ်းထား |

Regression ပထမအကြိမ်သည် default temporary directory မှာ file move permission error ဖြင့် ရပ်ခဲ့သည်။ TEMP/TMP ကို workspace အတွင်း သီးခြား temporary directory သို့ ပြောင်းပြီး ပြန်စစ်ရာ 158 checks အောင်မြင်သည်။ ဤ error ကို application defect အဖြစ် မသတ်မှတ်ထားပါ။ Python launcher က location warning တစ်ခုထုတ်သော်လည်း syntax checks လေးခုလုံး ပြီးဆုံးပြီး exit code 0 ရရှိသည်။

Tests အသစ်မထည့်ထားပါ။ Application source ကို မပြင်ထားပါ။

## ဦးစားပေးပြင်ဆင်ရန် တွေ့ရှိချက်များ

### 1. [P1] Admin web UI — stored script injection

- Evidence: `PMKLicenseServer/AdminHtml.cs:97`, `:103`; `PMKLicenseServer/Program.cs:80`, `:146`.
- Account email/note ကို `innerHTML` နှင့် inline `onclick` ထဲ escape မလုပ်ဘဲ ထည့်ထားသည်။ Public registration ၏ email check က `@` နှင့် dot ရှိမရှိသာ စစ်သည်။
- မယုံကြည်ရသော registration value သည် admin က list ဖွင့်သောအခါ markup/script အဖြစ် အလုပ်လုပ်နိုင်သည်။ Admin key ကို page က ကိုင်ထားသောကြောင့် admin လုပ်ပိုင်ခွင့်များ ထိခိုက်နိုင်သည်။ Live exploitation မစမ်းထားပါ; input-to-render code path မှ အတည်ပြုထားသည်။
- ပြင်ရန်: DOM element များဖန်တီးပြီး account values ကို `textContent` ဖြင့်ထည့်ရန်၊ event handler ကို `addEventListener` ဖြင့်ချိတ်ရန်၊ email validation နှင့် length limits ထည့်ရန်။
- Acceptance: quotes/HTML ပါသော value များသည် plain text အဖြစ်သာ ပေါ်ရမည်၊ executable DOM attribute မဖြစ်ရ။

### 2. [P1] Operation အတွင်း target phone အလိုအလျောက် ပြောင်းနိုင်ခြင်း

- Evidence: `PMKUnlocker/ShopServices.cs:27`, `:32`; `PMKUnlocker/ShopPanel.cs:59`; command binding `Form1.cs:3076`.
- မူလရွေးထားသော serial မရှိတော့ဘဲ တခြားဖုန်းတစ်လုံးကျန်လျှင် `ResolvePreferred` က selection ကိုရှင်းပြီး ထိုဖုန်းကို အသစ်ရွေးသည်။ Command တစ်ခုချင်းမှာ binding ပြန်လုပ်သောကြောင့် multi-step workflow အတွင်း A ဖုန်းပြုတ်ပြီး B ဖုန်းသာကျန်လျှင် နောက် command B သို့သွားနိုင်သည်။
- ပြင်ရန်: operation စတင်ချိန် serial ကို pin လုပ်ပြီး operation ပြီးသည်အထိ မပြောင်းရန်။ Device ပျောက်လျှင် abort/redetect လုပ်ရန်။ Idle အချိန် device switch ကို သီးခြားကိုင်တွယ်ရန်။
- Acceptance: A ပျောက်ပြီး B ရှိလာသောအခါ workflow ရပ်ရမည်၊ B သို့ command မပို့ရ။ လက်ရှိ regression သည် auto-rebind behavior ကို PASS ဟု စစ်ထားခြင်းဖြစ်လို့ green tests က ဒီအန္တရာယ်ကို မဖမ်းနိုင်ပါ။

### 3. [P1] Xiaomi BAT parser က model / anti-rollback guards ကို မထိန်းထား

- Evidence: `PMKUnlocker/MiFlash.cs:71`, `:130`, `:139`; `Form1.cs:11034` မှ device-info display နှင့် `:11060` မှ condition handling.
- BAT control flow နှင့် failure tails ကို ဖြုတ်ထားပြီး `product` / `anti` guard ကို မသိမ်းထားပါ။ UI သည် device values ကိုပြသသော်လည်း firmware ၏ expected product / rollback condition နှင့် တိုက်စစ်ခြင်း မလုပ်ထားပါ။
- Firmware မကိုက်၍ မူရင်း BAT ရပ်သင့်သောအခြေအနေတွင် parsed flash operations ဆက်လုပ်နိုင်သည်။
- ပြင်ရန်: support လုပ်သော script format ကို တိတိကျကျ သတ်မှတ်ပြီး model/rollback guards ကို မဖြုတ်ဘဲ စစ်ရန်။ မနားလည်နိုင်သော control flow ရှိလျှင် flash ကို block လုပ်ရန်။
- Acceptance: wrong-product နှင့် rollback-mismatch တွင် erase/flash မစရ။

### 4. [P1] Xiaomi flash မှာ preflight မပြည့်စုံ၊ failure နောက် ဆက်ရေးနိုင်

- Evidence: `Form1.cs:11078`, `:11094`, `:11106`, `:11126`, `:11144`.
- Erase-first သည် ရွေးထားသော image အားလုံး ဖတ်လို့ရမရ မစစ်မီ လုပ်သည်။ File missing ကို file တစ်ခုချင်း ရေးမည့်အချိန်မှ သိသည်။ Flash failure က count တိုးပြီး loop ဆက်သွားသည်။ Erase failure များက failCount မတိုးသောကြောင့် auto-reboot ခွင့်ပြုနိုင်သည်။
- ပြင်ရန်: target + selected files + operation plan ကို ပထမ destructive command မတိုင်မီ စစ်ရန်။ Required write/erase failure တွင် ဆက်မလုပ်ရန်။ Optional missing partition ကို သီးခြားသတ်မှတ်ပြီး skipped ဟုဖော်ပြရန်။
- Acceptance: နောက်ဆုံး image ပျောက်နေသော်လည်း ပထမ erase မစရ။ Required erase/write ပျက်လျှင် နောက် write/reboot မလုပ်ရ။

### 5. [P1] Qualcomm sparse expansion နှင့် large-file splitting အစီအစဉ်မှား

- Evidence: `Form1.cs:5425` သည် split လုပ်ပြီးမှ `:5453` တွင် sparse expansion လုပ်သည်; `ReviewSafety.cs:49`, `:190`.
- Encoded sparse file သေးသော်လည်း ဖြည်ပြီး raw image ကြီးလာလျှင် loader အတွက် သတ်မှတ်ထားသော size limit ကို ကျော်သွားပြီး နောက်တစ်ကြိမ် split မလုပ်တော့ပါ။ Encoded sparse file ကိုယ်တိုင်ကြီးလျှင် container ကို အပိုင်းခွဲမိပြီး ပထမပိုင်းတစ်ခုတည်းကို complete sparse image အဖြစ်ဖတ်သည့် ပြဿနာရှိသည်။
- ပြင်ရန်: sparse validation/expansion → raw size အရ splitting → final XML address/range validation အစီအစဉ်သို့ပြောင်းရန်။ Split routine သည် file_sector_offset နှင့် entry-specific length ကိုပါ ထိန်းထားရမည်; လက်ရှိ whole-file split သည် original range ကို ယူဆထားသည်။
- Acceptance: သေးသော sparse → ကြီးသော raw၊ ကြီးသော sparse container၊ nonzero source offsets စသော case များတွင် final files/addresses တိကျရမည်။ လက်ရှိ tests သည် sparse နှင့် split ကို သီးခြားစစ်ပြီး pipeline တစ်ခုလုံး မစစ်ပါ။

### 6. [P1] Derived firmware cache ကို size/time ဖြင့်သာ ယုံကြည်ခြင်း

- Evidence: `ReviewSafety.cs:91`, `:208`.
- Expanded raw နှင့် split parts တို့ကို length နှင့် timestamp ကိုက်လျှင် ပြန်သုံးသည်။ Archive အသစ်ဖြည်ချရာ source timestamp အဟောင်းဖြစ်နိုင်သလို cached file ကို အရွယ်အစားမပြောင်းဘဲ ပြင်ထားလျှင်လည်း လွတ်သွားနိုင်သည်။
- ပြင်ရန်: source hash + converter version + output hashes ဖြင့် cache manifest ချိတ်ရန်၊ သို့မဟုတ် derived outputs အသစ်ဖန်တီးရန်။
- Acceptance: source content ပြောင်းပြီး length/time တူနေသည့် case၊ output content ပျက်ပြီး size မပြောင်းသည့် case တို့တွင် cached firmware ကို မသုံးရ။

### 7. [P1] License database — concurrent update ပျောက်နိုင်၊ corrupt file ကို overwrite လုပ်နိုင်

- Evidence: `PMKLicenseServer/Program.cs:87`, `:96`, `:100`, `:106`, `:155`, `:174` နှင့် အခြား mutation endpoints.
- Lock သည် Load နှင့် Save တစ်ခုချင်းကိုသာ ကာကွယ်ပြီး read-modify-write transaction အပြည့်ကို မကာကွယ်ပါ။ တစ်ပြိုင်နက် request နှစ်ခုက DB အဟောင်းတူတူဖတ်ပြီး နောက်ဆုံး save သည် ရှေ့ request ပြောင်းထားသမျှကို ပျောက်စေနိုင်သည်။
- Save က JSON ကို တိုက်ရိုက် overwrite လုပ်သည်။ ဖတ်မရသော file ကို empty database ဟုသတ်မှတ်သောကြောင့် နောက် register/save တစ်ခုက ရှိပြီးသား accounts များကို အစားထိုးနိုင်သည်။
- ပြင်ရန်: transactional store (ဥပမာ SQLite) သုံးရန်၊ သို့မဟုတ် mutation တစ်ခုလုံးကို lock + atomic replacement + recovery backup ဖြင့်လုပ်ရန်။ Corrupt DB မှာ fail closed လုပ်ရန်။
- Acceptance: concurrent register/renew/block တွင် changes မပျောက်ရ၊ corrupt DB ကြောင့် account list အလွတ်အသစ်ဖြင့် overwrite မလုပ်ရ။

### 8. [P1 — release blocker] Clean installation မှာ license endpoint အဆင်သင့် မဖြစ်သေး

- Evidence: `LicenseClient.cs:40`; `Program.cs:58` မှ server candidates; `scripts/check_release.ps1`; `installer/PMKMobileTool.iss:28`.
- Default URL သည် localhost ဖြစ်ပြီး auto-start လမ်းကြောင်းများသည် developer repository ၏ sibling build output ကိုသာရှာသည်။ Installer သည် desktop candidate ကိုထည့်ပြီး license server deployment/configuration ကို မဆောင်ရွက်ပေးပါ။
- Fresh PC မှာ remote URL ကို ကိုယ်တိုင်မသတ်မှတ်ထားလျှင် login မရနိုင်ပါ။ ဤသုံးသပ်ချက်သည် default installation အတွက်ဖြစ်ပြီး လက်ရှိ developer PC ၏ service အခြေအနေကို မဆိုလိုပါ။
- ပြင်ရန်: production HTTPS license endpoint provisioning၊ first-run configuration၊ service deployment/backup/health guide ထည့်ရန်။
- Acceptance: developer files မရှိသော PC တွင် install → register/approve → login ပြီးစီးရမည်။

### 9. [P2] Admin desktop app က remote HTTP endpoint သို့ key ပို့နိုင်

- Evidence: `PMKLicenseAdmin/AdminForm.cs:284`, `:341`, `:342`.
- User-entered URL ကို HTTPS/loopback စစ်ခြင်းမရှိဘဲ `X-Admin-Key` နှင့် password-bearing requests ပို့သည်။ Customer LicenseClient က remote HTTP ကိုကန့်သတ်ထားသော်လည်း Admin app မှာ မတူပါ။
- ပြင်ရန်: absolute URI validation၊ remote HTTPS only၊ loopback HTTP exception တို့ကို shared policy အဖြစ်သုံးရန်။
- Acceptance: remote HTTP ရွေးလျှင် network request မပို့မီ reject လုပ်ရမည်။

### 10. [P2] MTK partial-write result တွင် ရေးပြီးသား partitions မပေါ်

- Evidence: `Form1.cs:7319`, `:7330`, `:7351`; Python `pmk_flash_guard.py` တွင် `PMK_FLASH_WRITTEN` ထုတ်သည်။
- C# result သို့ Written ထည့်ခြင်းသည် session အားလုံးအောင်မြင်မှသာလုပ်သည်။ ပထမ partitions အချို့ရေးပြီး နောက်ပိုင်းပျက်/STOP လုပ်လျှင် result မှာ written 0 သို့မဟုတ် none ဖြစ်နိုင်သည်။ Recovery ဆုံးဖြတ်ချက်အတွက် မှားယွင်းစေသည်။
- ပြင်ရန်: structured per-partition completion events ကို တစ်ခုပြီးတိုင်း result ထဲစုရန်၊ cancelled/failed/not-started ခွဲရန်။ Python ၏ final complete-write evidence check က success အတုကိုကာကွယ်ထားသော်လည်း partial reporting ပြဿနာကို မဖြေရှင်းပါ။
- Acceptance: နှစ်ခုရေးပြီး တတိယတွင်ရပ်လျှင် Written နှစ်ခု၊ ကျန်သည့် partitions အခြေအနေတိတိကျကျဖော်ပြရမည်။

### 11. [P2] Integrity / update trust ကို ထပ်ခိုင်မာစေရန်လို

- Evidence: `ToolIntegrity.cs:41`, `:62`, `:63`, `:74`; `UpdateChecker.cs:81`, `:140`, `:182`.
- Bundled manifest ပျောက်/ပျက်လျှင် verification ကို ကျော်သည်; တစ်ကြိမ်စစ်ပြီးသား path ကို ပြန်စစ်ခြင်းမရှိပါ။ Override manifest URL တွင် HTTP ကို လက်ခံနိုင်သည်။ Installer download path သည် fixed temporary filename ဖြစ်သည်။
- ပြင်ရန်: bundled release files အတွက် missing/invalid integrity manifest ကို error လုပ်ရန်၊ changed file ကို recheck လုပ်ရန်၊ update metadata HTTPS နှင့် publisher trust သတ်မှတ်ရန်၊ unique protected staging directory သုံးပြီး install မတိုင်မီ ပြန် verify လုပ်ရန်။
- SHA-256 ရှိခြင်းတစ်ခုတည်းက manifest နှင့် installer အတူပြောင်းခံရသည့်အခြေအနေတွင် publisher identity ကို မသက်သေပြနိုင်ပါ။

### 12. [P2] Dependencies နှင့် release validation မပြည့်စုံ

- လက်ရှိ Debug output inventory တွင် `mtk.exe`, `python.exe`, `edl.py` မတွေ့ပါ။ `spd_dump.exe` သုံးခုနှင့် `unpac.exe` တစ်ခုတွေ့သည်။ Count သည် compatibility/readiness အတည်ပြုချက်မဟုတ်ပါ။
- `.gitignore` တွင် `bundled-tools/` နှင့် `tools/` မပါဝင်စေရန် သတ်မှတ်ထားသည်။ Third-party assets ရယူပုံ/version/checksum မသတ်မှတ်လျှင် fresh clone မှ ယနေ့စစ်ထားသော package ကို တူညီစွာ ပြန်မထုတ်နိုင်ပါ။
- `scripts/check_release.ps1` သည် fresh package helpers အချို့ကို required list တွင် မထည့်ရသေးပါ (`pmk_flash_guard.py`, `pmk_sparse.py`, `pmk_gpt.py`)။ csproj က copy လုပ်ပေးသော်လည်း release gate သည် helper တစ်ခုလွတ်သွားခြင်းကို သီးခြားမဖမ်းပါ။
- ပြင်ရန်: dependency version pins၊ source/hash inventory၊ package helper checks၊ clean-PC readiness tests ထည့်ရန်။ လက်ရှိ check script က distribution readiness false ဟု ရိုးသားစွာ မှတ်သားထားခြင်းက ကောင်းသည်။

## တည်ငြိမ်မှုနှင့် ထိန်းသိမ်းမှု

- `Form1.cs` သည် 15,720 lines ရှိပြီး UI၊ process lifecycle၊ protocol-specific workflows အများအပြား ပေါင်းနေသည်။ Safety fixes ပြီးမှ device session / command runner / firmware planner / platform services / UI အဖြစ် တစ်ပိုင်းချင်းခွဲရန်။ အားလုံးတစ်ကြိမ်တည်း rewrite မလုပ်သင့်ပါ။
- `PMKUnlocker.sln` သည် desktop project တစ်ခုသာပါသည်။ Server/admin/regression checks ကို တစ်စုတစ်စည်းတည်း build/review လုပ်နိုင်သော entry point လိုသည်။
- ရှိပြီးသား tests သည် အထောက်အကူဖြစ်သော်လည်း Form1 ၏ production workflows နှင့် license server ကို မစမ်းသပ်ပါ။ MTK backend tests တချို့မှာ mock သို့မဟုတ် delegate tests ဖြစ်သည်။
- Server တွင် login/register rate limiting မတွေ့ပါ၊ password minimum 4 ဖြစ်သည်၊ AutoTokenAt ကိုသိမ်းပေမဲ့ token age enforcement မတွေ့ပါ။ Internet-facing deployment မတိုင်မီ login abuse controls နှင့် token lifecycle ကို သတ်မှတ်ရန်။
- App manifest က administrator privilege တောင်းသည်; installer သည် per-user/lowest ဖြစ်သည်။ Standard Windows user က admin credential ဖြင့်ဖွင့်သောအခါ settings/account directory များ ဘယ် user အောက်ရောက်သည်ကို clean-PC တွင်စမ်းရန်။
- `SHOP_SETUP.md` ၏ unknown-image handling နှင့် `DISTRIBUTION_PLAN.md` ၏ 41 regression checks စာသားများသည် လက်ရှိ behavior/158 checks နှင့် မတူတော့ပါ။ Release docs ကို လက်ရှိ behavior နှင့်ညှိရန်။
- Installer compiler ၏ commercial-use limitation ကို လက်ရှိ docs က မအတည်ပြုရသေးဟု မှတ်သားထားသည်။ ဤ review တွင် licensing အခြေအနေကို သီးခြားအတည်မပြုထားပါ။ Third-party redistribution provenance/notices များကို release မတိုင်မီ ပြည့်စုံစေရန်။
- လက်ရှိ modified/untracked files အများအပြားကို release source snapshot အဖြစ် စနစ်တကျသိမ်းရန်။ Python cache နှင့် diagnostic trace ဖိုင်များသည် release source မဟုတ်သဖြင့် commit scope ပြန်စစ်ရန်။

## ဆက်လုပ်ရန် အစီအစဉ်

1. **လုံခြုံရေးနှင့် wrong-device ကာကွယ်မှု** — Admin rendering၊ operation-level serial pinning၊ license DB transaction/recovery ကို ဦးစွာပြင်ရန်။
2. **Firmware ရေးသွင်းမှု၏ မှန်ကန်မှု** — Xiaomi guards/preflight/abort နှင့် Qualcomm expand→split/cache ပြဿနာများကိုပြင်ရန်။ MTK partial results ကို တိကျစေရန်။
3. **ထည့်သွင်းအသုံးပြုနိုင်မှု** — License endpoint၊ dependency pins/setup၊ update trust နှင့် release required files ကိုပြီးစီးစေရန်။
4. **လက်တွေ့စမ်းသပ်မှု** — ပိုင်ဆိုင်ခွင့်ရှိသော test phones တွင် read-only စစ်ပြီး backup၊ restore၊ flash၊ STOP၊ disconnect နှင့် boot result ကို model/firmware/operation အလိုက် မှတ်တမ်းတင်ရန်။ Hardware operation မစတင်မီ သီးခြားအစီအစဉ်လိုသည်။
5. **Trial release** — clean Windows install/update/uninstall/rollback၊ settings/backups preservation နှင့် supported-model matrix အတည်ပြုပြီးမှ pilot shop သို့ကန့်သတ်ဖြန့်ရန်။

ပြီးဆုံးမှုကို build PASS ဖြင့်သာမဆုံးဖြတ်ဘဲ အထက်ပါ acceptance criteria နှင့် clean-PC/hardware evidence ဖြင့် ဆုံးဖြတ်ရန်။

# PMK Mobile Tool — ဆိုင်များသို့ ဖြန့်ချိရန် လုပ်ငန်းအစီအစဉ်

ရည်ရွယ်ချက်: အခြားဖုန်းပြင်ဆိုင်များတွင် တပ်ဆင်ပြီး တည်ငြိမ်စွာ အသုံးပြုနိုင်သော Windows tool ဖြစ်ရန်။

## 2026-09-23 implementation status

- Implemented: per-user installer source/build, x86 Desktop Runtime prerequisite check, user-data settings migration, configurable tools and readiness export, ADB/fastboot serial selection, MTK image/GPT size preflight, MTK backup manifests and restore verification, Backup Manager, operator device-test records.
- Automated validation: 41 regression checks; build and fresh release integrity checks pass.
- Still required: optional Python/MTK/EDL/Unisoc backend setup and version validation, model/firmware authenticity checks, broader per-platform firmware preflight, clean-PC installation/update/uninstall tests, real-phone test records, component redistribution review.
- Installer trial only: current compiler reports non-commercial use only. Commercial licensing and distribution readiness are not verified.

## ပထမဦးစားပေး — တပ်ဆင်ပြီး အလုပ်လုပ်နိုင်မှု

- Python၊ MTK၊ EDL၊ Unisoc dependencies များကို version သတ်မှတ်ပြီး စီမံရန်။ မထည့်ပေးနိုင်သည့် component အတွက် setup လမ်းညွှန်နှင့် startup readiness check ထည့်ရန်။
- Windows x86 .NET Desktop Runtime လိုအပ်မှုကို installer က စစ်ပေးရန် သို့မဟုတ် self-contained package ထုတ်ရန်။
- Desktop folder နှင့် developer PC ရှိ PATH မလိုအပ်သော tool resolution ပြုလုပ်ရန်။
- Bundled component တစ်ခုချင်း၏ မူရင်း source၊ version၊ license/redistribution terms နှင့် checksum မှတ်တမ်း ထားရန်။
- App၊ helper၊ device catalog ကို version တူသော package တစ်ခုအဖြစ် ဖြန့်ချိရန်။

## ဒုတိယဦးစားပေး — ဖုန်းနှင့် firmware မှန်ကန်မှု

- Operation တစ်ခုလုံးအတွက် device serial / port / platform နှင့် firmware ကို တစ်ကြိမ်ရွေးပြီး မှတ်ထားရန်။
- Firmware model၊ partition အရွယ်အစားနှင့် storage layout ကို ဖတ်ရရှိသော device အချက်အလက်နှင့် တိုက်စစ်ရန်။ မသိနိုင်သောအချက်ကို အတည်ပြုပြီးသကဲ့သို့ မပြရန်။
- Backup တွင် device အချက်အလက်၊ partition names/sizes၊ timestamp၊ SHA-256 manifest ထည့်ရန်။ Restore မတိုင်မီ manifest တိုက်စစ်ရန်။
- Dry-run၊ STOP၊ timeout၊ cable disconnect၊ disk full၊ failed backup တို့ကို regression tests ထဲ ထည့်ရန်။
- User preference: EDL authentication အဆင့်ကို STOP ဖြင့် ချက်ချင်းမဖြတ်ရန်။

## တတိယဦးစားပေး — ဆိုင်တွင်နေ့စဉ်သုံးနိုင်မှု

- ADB/fastboot device selector၊ operation preview၊ success/partial/failure/unknown ရလဒ်ခွဲခြားမှု။
- Backup Manager နှင့် customer data မပါဘဲ export လုပ်နိုင်သော support report။
- Tab တစ်ခုစီအတွက် dependency readiness နှင့် စမ်းသပ်အတည်ပြုထားသော device/operation matrix။
- App settings၊ backups နှင့် logs ကို install folder အစား user-writable data folder တွင် စနစ်တကျသိမ်းရန်။

## ဖြန့်ချိမတိုင်မီ အတည်ပြုရန်

1. `scripts/check_release.ps1` ကို run ပြီး fresh Release candidate နှင့် file manifest ထုတ်ရန်။ Script သည် app/phone commands မဖွင့်ပါ။ ပထမဆုံးအသုံးပြုချိန် package restore လုပ်ထားရန်လိုသည်။
2. Development tools မရှိသော clean Windows PC တွင် install/startup/driver detection စမ်းရန်။
3. ပံ့ပိုးမည့် model တစ်ခုစီအတွက် အမှန်တကယ်စမ်းသပ်ထားသော operation နှင့် firmware version ကို မှတ်တမ်းတင်ရန်။ Catalog entry ရှိခြင်းကို hardware test အောင်မြင်ခြင်းဟု မသတ်မှတ်ရန်။
4. Update ပြီးနောက် settings/backup မပျက်ခြင်းနှင့် version အဟောင်းသို့ rollback စမ်းရန်။
5. Test ရလဒ်၊ setup guide၊ known limitations၊ third-party notices တို့နှင့် release package ကို အတူထားရန်။

လက်ရှိ release check သည် build/tests/package အတွက်သာဖြစ်သည်။ ဖုန်းအားလုံးနှင့် ကိုက်ညီမှု သို့မဟုတ် ဆိုင်များသို့ ဖြန့်ချိရန် အဆင်သင့်ဖြစ်မှုကို အလိုအလျောက် အတည်မပြုပါ။

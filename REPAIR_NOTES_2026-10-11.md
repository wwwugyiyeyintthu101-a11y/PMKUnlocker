# 2026-10-11 ပြင်ဆင်မှုမှတ်တမ်း

ဤမှတ်တမ်းသည် PROJECT_REVIEW_2026-10-11.md တွင် တွေ့ရှိခဲ့သောအချက်များကို ပြင်ဆင်ပြီးနောက် အခြေအနေဖြစ်သည်။ မူလ review ကို သမိုင်းမှတ်တမ်းအဖြစ် ထိန်းထားသည်။

## ပြင်ဆင်ပြီးသောအချက်များ

| Review | ပြင်ဆင်မှု |
|---|---|
| 1 — Admin HTML | Account values ကို textContent ဖြင့်ပြပြီး event listeners ဖြင့် row ရွေးသည်။ User data ကို HTML/script ထဲ မထည့်တော့ပါ။ |
| 2 — Wrong device | မူလရွေးထားသော serial ပျောက်လျှင် အခြားဖုန်းသို့ auto-rebind မလုပ်တော့ပါ။ Operator က idle အချိန် Device selection မှ အသစ်ရွေးရမည်။ |
| 3 — Xiaomi guards | Product guard ကို plan တစ်ခုလုံးအပေါ် သက်ရောက်စေသည်။ Literal rollback guards စစ်သည်။ မနားလည်နိုင်သော BAT control flow၊ unknown options/operations နှင့် chained commands ကို block လုပ်သည်။ Slot options ကို ထိန်းထားသည်။ |
| 4 — Xiaomi failures | Workflow တစ်ခုလုံး lock လုပ်ထားသည်။ Selected image အားလုံးကို erase မတိုင်မီ ဖတ်စစ်ပြီး Windows file handles ဖြင့် ပြောင်းလဲမရအောင် ထိန်းထားသည်။ Required erase/write failure တွင် ရပ်ပြီး auto-reboot မလုပ်ပါ။ |
| 5 — Qualcomm processing | Sparse expansion → raw splitting အစီအစဉ်သို့ပြောင်းသည်။ Source offset၊ sector size၊ entry length ထိန်းထားသည်။ Malformed chunk lengths၊ CRC နှင့် trailing data ကို စစ်သည်။ |
| 6 — Stale firmware cache | Derived raw/split files ကို source မှ ပြန်ဖန်တီးသည်။ Timestamp/size အပေါ် မူတည်၍ အဟောင်းပြန်မသုံးတော့ပါ။ Preparation ပိုကြာနိုင်သည်။ |
| 7 — License DB | API transaction များ serialize လုပ်သည်။ DB တစ်ခုကို server process တစ်ခုသာဖွင့်ခွင့်ရှိသည်။ Atomic replacement နှင့် .bak ထားသည်။ Corrupt/schema-invalid DB ကို empty store ဖြင့် overwrite မလုပ်ပါ။ |
| 8 — License setup | First-run server dialog နှင့် login page Server settings link ထည့်သည်။ လိပ်စာမသတ်မှတ်ရသေးလျှင် ရှင်းလင်းသော error ပြသည်။ Developer-relative server auto-start သည် Debug build တွင်သာရှိသည်။ |
| 9 — Admin transport | Remote HTTPS only; loopback HTTP ခွင့်ပြုသည်။ Admin/customer HTTP redirects ကို ပိတ်ထား၍ credentials ကို redirect target သို့ မပို့ပါ။ |
| 10 — Partial MTK writes | Completion events ကို result ထဲ ထိန်းထားပြီး failure/STOP ဖြစ်သော်လည်း ရေးပြီးသား partitions ပြသည်။ Completion မသိသော partition ကို partially written ဖြစ်နိုင်ဟု ဖော်ပြသည်။ Backup location ကို user data folder သို့ပြောင်းသည်။ |
| 11 — Integrity/update | Bundled manifest မရှိ/ပျက်/entry မရှိလျှင် block လုပ်သည်။ Tool အသုံးပြုတိုင်း hash ပြန်စစ်သည်။ Update metadata သည် HTTPS GitHub repo ဖြစ်ရပြီး installer သည် ထို repo ၏ release asset ဖြစ်ရသည်။ Unique staging folder၊ install မတိုင်မီ hash ပြန်စစ်ခြင်းနှင့် file lock သုံးသည်။ Shell command ဖြင့် installer မဖွင့်တော့ပါ။ |
| 12 — Packaging | Build/publish တွင် helper/library hashes ပါသော runtime integrity manifest ထုတ်သည်။ Release/publish တွင် bundled inventory တစ်ခုလုံးစစ်သည်။ Helper လေးခု၊ catalog နှင့် integrity file ကို release required list ထဲထည့်သည်။ dependency-lock.json ဖြင့် bundled inventory/native libraries ကို pin လုပ်သည်။ |

ထပ်မံပြင်ဆင်ထားခြင်းများ: API request body limit 16 KiB၊ IP တစ်ခုလျှင် API requests 60/minute၊ password အသစ် 8–256 characters၊ remember-token အများဆုံး 30 days၊ malformed expiry handling နှင့် pending account days-left ကို သုညပြခြင်း။ Password အဟောင်းများဖြင့် login ကို မပိတ်ထားပါ။ Admin key ကို console ထဲ မထုတ်တော့ပါ။ Solution တွင် desktop/server/admin/regression projects အားလုံးပါဝင်သည်။

## စစ်ဆေးမှု

- ရှိပြီးသား Regression project ကို တိုးချဲ့စစ်ဆေးပြီး 174 checks PASS။ Test source file အသစ်မထည့်ထားပါ။
- သီးခြားယာယီ license server/database ဖြင့် unauthenticated access၊ invalid input၊ concurrent registration 8 ခု၊ approval/login/token login၊ corrupt DB preservation၊ token expiry၊ rate limiting တို့ PASS။ ရှိပြီးသား account database ကို မသုံးထားပါ။
- Runtime integrity inventory 1,031 entries ထုတ်ပြီး bundled files နှင့် dependency lock တိုက်စစ်မှု PASS။
- ဖုန်းသို့ command မပို့ထားပါ။ Flash/restore/USB driver behavior၊ installer/update execution နှင့် clean-PC usability ကို မစမ်းထားပါ။

## အသုံးပြုရာတွင် ပြောင်းလဲသွားမည့်အချက်များ

1. ဖုန်းတစ်လုံးပြောင်းချိတ်လျှင် **Setup / Backups → Device selection** မှ ရွေးချယ်မှသာ ဆက်လုပ်နိုင်သည်။
2. Xiaomi BAT parser သည် general Windows batch interpreter မဟုတ်ပါ။ Product guard မရှိခြင်း၊ complex rollback/branch logic၊ automatic bootloader locking သို့မဟုတ် unknown commands ပါခြင်းကို block လုပ်သည်။ Guard ကိုဖယ်ပြီး ဆက်မလုပ်သင့်ပါ; ထို package အတွက် supported vendor workflow ကိုသုံးရန် သို့မဟုတ် parser support ကို စစ်ဆေးတိုးချဲ့ရန်လိုသည်။
3. Firmware-derived files ကို ပြန်ဖန်တီးသောကြောင့် ကြီးမားသော ROM တွင် ပြင်ဆင်ချိန်နှင့် disk space လိုအပ်နိုင်သည်။
4. Update အောင်မြင်ပြီး installer စတင်လျှင် app ထွက်သည်။ Installer ပြီးနောက် shortcut မှ app ကို ပြန်ဖွင့်ရန်။ Shell-based auto-relaunch ကို ဖယ်ထားသည်။
5. Bundled tools ပြောင်းလဲမည်ဆိုလျှင် source/version/license ကိုစစ်ပြီး integrity inventory နှင့် dependency-lock.json ကို သက်ဆိုင်ရာ approval အရ update လုပ်ရမည်။ Hash mismatch ကို ဖုံးကွယ်ရန် manifest အလွတ်မဖန်တီးရ။

## ဆက်လိုအပ်သော ပြင်ပအလုပ်များ

- User က production License Server HTTPS လိပ်စာ မရှိသေးဟု အတည်ပြုထားသည်။ First-run configuration အဆင်သင့်ဖြစ်ပေမဲ့ server hosting၊ domain/TLS၊ admin account approval နှင့် backup operation မတပ်ဆင်ရသေးပါ။ LICENSE_SERVER_SETUP.md ကိုကြည့်ရန်။
- Python/mtkclient/edl.py ကို မထည့်သွင်းထားပါ။ မစမ်းသပ်ရသေးသော version ကို compatible ဟု မသတ်မှတ်ထားပါ။ Optional backend အတိအကျ version၊ complete environment နှင့် shop readiness ကို အတည်ပြုရန်ကျန်သည်။
- dependency-lock.json သည် byte identity ကို pin လုပ်ခြင်းသာဖြစ်သည်။ Third-party provenance/license approval နှင့် Authenticode publisher certificate/signing မပြီးသေးပါ။ Update trust သည် configured GitHub repo + HTTPS + SHA-256 အပေါ် မူတည်သည်။
- Clean Windows install/update/uninstall နှင့် real-phone backup/restore/flash/STOP/disconnect/boot tests မပြီးသေးပါ။ Build PASS ကို hardware certification ဟု မသတ်မှတ်ရ။
- Form1.cs ကို services အဖြစ် အဆင့်လိုက်ခွဲထုတ်ခြင်းသည် ရေရှည်ထိန်းသိမ်းမှုအလုပ်ဖြစ်ပြီး ဤ safety fixes နှင့်အတူ အကြီးစား rewrite မလုပ်ထားပါ။

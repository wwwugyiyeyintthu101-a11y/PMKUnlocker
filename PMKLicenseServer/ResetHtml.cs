namespace PMKLicenseServer;

// /reset?token=… — browser မှာ ဖွင့်ပြီး password အသစ် ထည့်တဲ့ page
internal static class ResetHtml
{
    internal static string Page(string token)
    {
        token = (token ?? "").Trim();
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            return Layout(@"<h1>Reset link မမှန်ပါ</h1>
<p class=""muted"">Link ကို email ထဲက အတိအကျ ပြန်ဖွင့်ပါ (သို့) admin ထံမှ link အသစ် တောင်းပါ။</p>");

        return Layout($@"<h1>Password အသစ် သတ်မှတ်ပါ</h1>
<p class=""muted"">PMK Mobile Tool account အတွက် password အသစ် (8–256 လုံး) ထည့်ပါ။ Link ကို တစ်ခါပဲ သုံးနိုင်သည်။</p>
<div id=""form"">
  <label>Password အသစ်</label>
  <input id=""p1"" type=""password"" maxlength=""256"" autocomplete=""new-password""/>
  <label>Password အသစ် ထပ်ရိုက်ပါ</label>
  <input id=""p2"" type=""password"" maxlength=""256"" autocomplete=""new-password""/>
  <button onclick=""submitPw()"">Password ပြောင်း</button>
</div>
<div class=""msg"" id=""msg""></div>
<script>
const TOKEN = ""{token}"";
const msg = document.getElementById('msg');
function setMsg(t, ok) {{ msg.textContent = t; msg.className = 'msg ' + (ok ? 'good' : 'bad'); }}
async function submitPw() {{
  const p = document.getElementById('p1').value;
  const c = document.getElementById('p2').value;
  if (p.length < 8 || p.length > 256) return setMsg('Password 8–256 လုံး ထည့်ပါ', false);
  if (p !== c) return setMsg('Password နှစ်ခု မတူပါ', false);
  try {{
    const r = await fetch('/api/reset', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }},
      body: JSON.stringify({{ token: TOKEN, password: p }}) }});
    const d = await r.json();
    setMsg(d.message || 'Unknown error', !!d.ok);
    if (d.ok) {{ document.getElementById('form').style.display = 'none'; document.title = 'PMK — Password changed'; }}
  }} catch (e) {{ setMsg('Server နဲ့ ချိတ်မရပါ: ' + e.message, false); }}
}}
document.getElementById('p2').addEventListener('keydown', e => {{ if (e.key === 'Enter') submitPw(); }});
</script>");
    }

    private static string Layout(string body) => $$"""
<!doctype html>
<html lang="my">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<meta name="referrer" content="no-referrer"/>
<title>PMK Mobile Tool — Password reset</title>
<style>
  body{font-family:Segoe UI,Arial,sans-serif;background:linear-gradient(135deg,#5691fa,#8b5cf6);margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;padding:24px}
  .card{background:#1c1f2a;color:#e8e8f0;border:1px solid #2a2f3d;border-radius:14px;padding:28px;max-width:420px;width:100%;box-shadow:0 18px 50px rgba(0,0,0,.35)}
  h1{color:#a78bfa;font-size:1.25rem;margin:0 0 10px}
  p{margin:0 0 14px}
  .muted{color:#9aa;font-size:.92rem}
  label{display:block;font-size:.85rem;color:#9aa;margin:12px 0 5px}
  input{width:100%;box-sizing:border-box;padding:11px 12px;border-radius:8px;border:1px solid #3a4050;background:#0e1016;color:#fff;font-size:1rem}
  button{margin-top:18px;width:100%;padding:11px 16px;border:0;border-radius:8px;background:#8b5cf6;color:#fff;cursor:pointer;font-weight:700;font-size:1rem}
  button:hover{background:#7c3aed}
  .msg{margin-top:14px;padding:10px;border-radius:8px;background:#0e1016;border:1px solid #333;min-height:1.2em;font-size:.9rem}
  .bad{border-color:#dc2626;color:#fca5a5}
  .good{border-color:#059669;color:#6ee7b7}
</style>
</head>
<body>
<div class="card">
{{body}}
</div>
</body>
</html>
""";
}

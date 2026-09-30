namespace PMKLicenseServer;

internal static class AdminHtml
{
    internal const string Fallback = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>PMK License Admin</title>
<style>
  body{font-family:Segoe UI,Arial,sans-serif;background:#12141a;color:#e8e8f0;margin:0;padding:24px}
  h1{color:#a78bfa;font-size:1.3rem;margin:0 0 16px}
  .card{background:#1c1f2a;border:1px solid #2a2f3d;border-radius:10px;padding:16px;margin-bottom:16px}
  label{display:block;font-size:.85rem;color:#9aa;margin:8px 0 4px}
  input,select{width:100%;box-sizing:border-box;padding:8px 10px;border-radius:6px;border:1px solid #3a4050;background:#0e1016;color:#fff;font-size:.95rem}
  button{margin-top:12px;padding:8px 16px;border:0;border-radius:6px;background:#8b5cf6;color:#fff;cursor:pointer;font-weight:600}
  button.sec{background:#334155}
  button.danger{background:#dc2626}
  button.ok{background:#059669}
  table{width:100%;border-collapse:collapse;font-size:.9rem}
  th,td{padding:8px 10px;border-bottom:1px solid #2a2f3d;text-align:left}
  th{color:#a78bfa}
  .row{display:grid;grid-template-columns:1fr 1fr 1fr;gap:12px}
  .msg{margin-top:10px;padding:8px;border-radius:6px;background:#0e1016;border:1px solid #333;min-height:1.2em}
  .bad{border-color:#dc2626;color:#fca5a5}
  .good{border-color:#059669;color:#6ee7b7}
  .tag{padding:2px 8px;border-radius:99px;font-size:.75rem}
  .tag.block{background:#7f1d1d;color:#fecaca}
  .tag.ok{background:#064e3b;color:#a7f3d0}
  .tag.exp{background:#78350f;color:#fde68a}
  .tag.pend{background:#1e3a5f;color:#93c5fd}
  button.approve{background:#2563eb}
  a{color:#a78bfa}
</style>
</head>
<body>
<h1>PMK Mobile Tool — License Admin</h1>

<div class="card">
  <label>Admin Key (X-Admin-Key)</label>
  <input id="key" type="password" value="pmk-admin-2026" placeholder="pmk-admin-2026" autocomplete="off"/>
  <button class="sec" onclick="loadList()">Load list</button>
  <button class="sec" onclick="search()">Search email</button>
  <div class="msg" id="msg"></div>
</div>

<div class="card">
  <strong>Create / update account</strong>
  <div class="row">
    <div><label>Email</label><input id="email" type="email" placeholder="user@gmail.com"/></div>
    <div><label>Password (min 4 — leave empty to keep)</label><input id="pass" type="text" placeholder="password"/></div>
    <div><label>Plan (သက်မှတ်ချက်)</label>
      <select id="plan">
        <option value="monthly">၁ လ (monthly)</option>
        <option value="quarterly">၃ လ (quarterly)</option>
        <option value="halfyearly">၆ လ (half-year)</option>
        <option value="yearly">၁၂ လ (yearly)</option>
      </select>
    </div>
  </div>
  <label>Note (optional — ဖုန်း/ဆိုင်)</label>
  <input id="note" type="text" placeholder="shop note"/>
  <button onclick="upsert()">Save (create or update)</button>
  <button class="approve" onclick="approve()">Approve / သက်မှတ်ပေး</button>
  <button class="ok" onclick="renew()">Renew + plan period</button>
  <button class="danger" onclick="setBlock(true)">Block</button>
  <button class="sec" onclick="setBlock(false)">Unblock</button>
  <button class="danger" onclick="del()">Delete</button>
</div>

<div class="card">
  <table>
    <thead><tr><th>Email</th><th>Plan</th><th>Expires</th><th>Days</th><th>Status</th><th>Note</th></tr></thead>
    <tbody id="rows"><tr><td colspan="6">— load list —</td></tr></tbody>
  </table>
</div>

<script>
const msg=document.getElementById('msg');
function K(){return document.getElementById('key').value.trim()}
function E(){return document.getElementById('email').value.trim()}
function setMsg(t,ok){msg.textContent=t;msg.className='msg '+(ok?'good':'bad')}
function planLabel(p){
  return {monthly:'၁ လ',quarterly:'၃ လ',halfyearly:'၆ လ',yearly:'၁၂ လ'}[p]||p;
}
async function api(path,body){
  const r=await fetch(path,{method:body?'POST':'GET',headers:{'Content-Type':'application/json','X-Admin-Key':K()},body:body?JSON.stringify(body):undefined});
  if(r.status===401){setMsg('Admin key မှားနေသည်',false);return null}
  return await r.json();
}
async function loadList(){
  const data=await api('/api/admin/list'); if(!data)return;
  const tb=document.getElementById('rows');
  if(!data.length){tb.innerHTML='<tr><td colspan="6">— empty —</td></tr>';setMsg('0 accounts',true);return}
  tb.innerHTML=data.map(a=>{
    let st;
    if(a.blocked) st='<span class="tag block">BLOCKED</span>';
    else if(a.pending) st='<span class="tag pend">PENDING</span>';
    else if(a.expired) st='<span class="tag exp">EXPIRED</span>';
    else st='<span class="tag ok">ACTIVE</span>';
    return `<tr style="cursor:pointer" onclick="pick('${a.email}')"><td>${a.email}</td><td>${planLabel(a.plan)}</td><td>${(a.expiresAt||'').slice(0,10)}</td><td>${a.daysLeft}</td><td>${st}</td><td>${a.note||''}</td></tr>`;
  }).join('');
  const pend=data.filter(a=>a.pending).length;
  setMsg(data.length+' account(s)'+(pend?' — '+pend+' pending':''),true);
}
function pick(email){
  document.getElementById('email').value=email;
  setMsg('Selected: '+email,true);
}
async function search(){
  const q=E().toLowerCase();
  const rows=document.querySelectorAll('#rows tr');
  let n=0;
  rows.forEach(tr=>{
    const hit=!q||tr.textContent.toLowerCase().includes(q);
    tr.style.display=hit?'':'none';
    if(hit)n++;
  });
  setMsg(n+' match(es) for "'+E()+'"',true);
}
async function upsert(){
  const r=await api('/api/admin/upsert',{email:E(),password:document.getElementById('pass').value,plan:document.getElementById('plan').value,note:document.getElementById('note').value});
  if(r){setMsg(r.message||r.ok,r.ok);if(r.ok)loadList()}
}
async function approve(){
  const plan=document.getElementById('plan').value;
  const r=await api('/api/admin/approve',{email:E(),plan});
  if(r){setMsg(r.message||r.ok,r.ok);if(r.ok)loadList()}
}
async function renew(){
  const plan=document.getElementById('plan').value;
  const months={monthly:1,quarterly:3,halfyearly:6,yearly:12}[plan]||0;
  const r=await api('/api/admin/renew',{email:E(),months});
  if(r){setMsg(r.message||r.ok,r.ok);if(r.ok)loadList()}
}
async function setBlock(b){
  const r=await api('/api/admin/block',{email:E(),blocked:b});
  if(r){setMsg(r.message||r.ok,r.ok);if(r.ok)loadList()}
}
async function del(){
  if(!confirm('Delete '+E()+'?'))return;
  const r=await api('/api/admin/delete',{email:E(),blocked:true});
  if(r){setMsg(r.message||r.ok,r.ok);if(r.ok)loadList()}
}
</script>
</body>
</html>
""";
}

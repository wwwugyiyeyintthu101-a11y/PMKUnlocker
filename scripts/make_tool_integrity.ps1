# bundled-tools အားလုံးရဲ့ SHA-256 manifest ထုတ်ပေးတယ် (ToolIntegrity.Verify က ဖတ်တယ်)။
# Tool / payload / loader တစ်ခုမျှ ပြင်/အစားထိုးပြီးရင် ပြန် run ပါ — integrity.json ပြန်ထုတ်ရမယ်။
param([string]$Root = (Join-Path $PSScriptRoot "..\PMKUnlocker\bundled-tools"))

$Root = (Resolve-Path -LiteralPath $Root).Path
$map = [ordered]@{}
Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($Root.Length + 1).Replace('\', '/')
    if ($rel -eq 'integrity.json') { return }   # manifest ကိုယ့်ကိုယ်ကို hash မလုပ်
    $map[$rel] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$entries = @($map.GetEnumerator() | ForEach-Object { [pscustomobject]@{ k = $_.Key; h = $_.Value } })
$out = Join-Path $Root "integrity.json"
$entries | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $out -Encoding UTF8
Write-Host ("{0} files -> {1}" -f $entries.Count, $out)

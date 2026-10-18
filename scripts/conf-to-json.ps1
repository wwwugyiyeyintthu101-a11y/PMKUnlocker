# HOCON kernel profile (.conf) -> standalone offsets JSON entry (legacy remote/main schema).
# Reverse of upstream app/src/main/kotlin/.../LegacyProfileConverter.kt (JSON -> HOCON).
# Usage:
#   .\conf-to-json.ps1 -Validate                      # convert 4 golden pairs, diff vs offsets-catalog.json
#   .\conf-to-json.ps1 -Releases @('k1','k2') -Out t.json
param(
    [string]$ProfilesDir = "$PSScriptRoot\..\..\..\Users\PMK\AppData\Local\Temp\opencode\gl-src\app\src\main\assets\kernel_profiles",
    [string[]]$Releases = @(),
    [string]$Out = "",
    [switch]$Validate
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ProfilesDir)) {
    $ProfilesDir = "C:\Users\PMK\AppData\Local\Temp\opencode\gl-src\app\src\main\assets\kernel_profiles"
}

$Script:SymFields = @('init_cred','init_task','root_task_group','security_hook_heads','selinux_blob_sizes','selinux_enforcing','slide_boot_id','slide_loggers_0_1','slide_nfulnl_logger')
$Script:TaskFields = @('atomic_flags','comm','cred','normal_prio','pi_blocked_on','pi_lock','pi_top_task','pi_waiters','pid','prio','real_cred','sched_task_group','seccomp','tasks','tgid')

function Parse-HoconFile {
    param([string]$Path, [hashtable]$Root, [string]$BaseDir)
    $txt = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    # strip comments (# to EOL)
    $sb = New-Object System.Text.StringBuilder
    $inStr = $false
    for ($i = 0; $i -lt $txt.Length; $i++) {
        $ch = $txt[$i]
        if ($inStr) {
            [void]$sb.Append($ch)
            if ($ch -eq '"') { $inStr = $false }
            continue
        }
        if ($ch -eq '"') { $inStr = $true; [void]$sb.Append($ch); continue }
        if ($ch -eq '#') { while ($i -lt $txt.Length -and $txt[$i] -ne "`n") { $i++ }; continue }
        [void]$sb.Append($ch)
    }
    $s = $sb.ToString()
    $i = 0
    $stack = New-Object System.Collections.Generic.Stack[hashtable]
    $stack.Push($Root)
    $n = $s.Length
    function SkipWs([string]$s, [ref]$i) { while ($i.Value -lt $s.Length -and [char]::IsWhiteSpace($s[$i.Value])) { $i.Value++ } }
    $pendingIdent = $null
    while ($i -lt $n) {
        SkipWs $s ([ref]$i)
        if ($i -ge $n) { break }
        $ch = $s[$i]
        if ($ch -eq '}') { [void]$stack.Pop(); $i++; continue }
        if ($ch -eq '{') { $i++; continue }
        if ($ch -eq '"') {
            # string value or include arg
            $j = $s.IndexOf('"', $i + 1)
            $str = $s.Substring($i + 1, $j - $i - 1)
            $i = $j + 1
            if ($pendingIdent -eq 'include') {
                $inc = Join-Path $BaseDir $str
                Parse-HoconFile -Path $inc -Root $stack.Peek() -BaseDir $BaseDir
            }
            $pendingIdent = $null
            continue
        }
        if ([char]::IsLetter($ch) -or $ch -eq '_') {
            $j = $i
            while ($j -lt $n -and ([char]::IsLetterOrDigit($s[$j]) -or $s[$j] -in @('_','.','-'))) { $j++ }
            $ident = $s.Substring($i, $j - $i)
            $i = $j
            SkipWs $s ([ref]$i)
            if ($i -lt $n -and $s[$i] -eq '{') {
                $cur = $stack.Peek()
                if (-not $cur.ContainsKey($ident) -or $cur[$ident] -isnot [hashtable]) { $cur[$ident] = New-Object hashtable }
                $stack.Push($cur[$ident])
                $i++
                $pendingIdent = $null
                continue
            }
            if ($i -lt $n -and $s[$i] -eq '=') {
                $i++
                SkipWs $s ([ref]$i)
                $vch = $s[$i]
                if ($vch -eq '"') {
                    $j2 = $s.IndexOf('"', $i + 1)
                    $stack.Peek()[$ident] = $s.Substring($i + 1, $j2 - $i - 1)
                    $i = $j2 + 1
                } else {
                    $j2 = $i
                    while ($j2 -lt $n -and (-not [char]::IsWhiteSpace($s[$j2])) -and $s[$j2] -notin @('}','{')) { $j2++ }
                    $raw = $s.Substring($i, $j2 - $i)
                    $i = $j2
                    if ($raw -eq 'null') { $stack.Peek()[$ident] = $null }
                    else { $stack.Peek()[$ident] = [Convert]::ToInt64($raw) }
                }
                continue
            }
            # bare ident (e.g. include before string)
            if ($ident -eq 'include') { $pendingIdent = 'include'; continue }
            $pendingIdent = $null
            continue
        }
        $i++
    }
}

function Get-Deep {
    param([hashtable]$m, [string[]]$path)
    $cur = $m
    foreach ($p in $path) {
        if ($cur -isnot [hashtable] -or -not $cur.ContainsKey($p)) { return $null }
        $cur = $cur[$p]
    }
    return $cur
}

function Convert-ConfToEntry {
    param([string]$Release, [string]$ProfilesDir)
    $file = Join-Path $ProfilesDir ($Release + '.conf')
    if (-not (Test-Path $file)) { throw "conf missing: $file" }
    $root = New-Object hashtable
    Parse-HoconFile -Path $file -Root $root -BaseDir $ProfilesDir

    $entry = [ordered]@{}
    $entry['release'] = if ($root.ContainsKey('release')) { [string]$root['release'] } else { $Release }

    # route: primary select_stack -> pselect; primary tcp_zerocopy -> compact; fallback select -> pselect
    $pselect = $null
    $compact = $null
    $sel = Get-Deep $root @('route','select_stack','waiter_shift')
    if ($sel -ne $null) { $pselect = [int64]$sel }
    $cp = Get-Deep $root @('route','tcp_zerocopy','compact_waiter')
    if ($cp -ne $null) { $compact = [int64]$cp }
    $fbTo = Get-Deep $root @('fallback','to')
    if ($fbTo -eq 'select_stack' -and $pselect -eq $null) {
        $fbSel = Get-Deep $root @('fallback','route','select_stack','waiter_shift')
        if ($fbSel -ne $null) { $pselect = [int64]$fbSel }
    }
    if ($pselect -ne $null) { $entry['pselect_waiter_shift'] = $pselect }
    if ($compact -ne $null) { $entry['compact_waiter'] = $compact }

    $mm = Get-Deep $root @('kernelsnitch','mm_struct_sz')
    if ($mm -ne $null) { $entry['mm_struct_sz'] = [int64]$mm }
    if ($root.ContainsKey('kernel_phys_load') -and $root['kernel_phys_load'] -ne $null) {
        $entry['kernel_phys_load'] = [int64]$root['kernel_phys_load']
    }
    if ($root.ContainsKey('kernel_phys_offset') -and $root['kernel_phys_offset'] -ne $null) {
        $entry['kernel_phys_offset'] = [int64]$root['kernel_phys_offset']
    }

    $offset = $null
    if ($root.ContainsKey('offset') -and $root['offset'] -is [hashtable]) { $offset = $root['offset'] }
    $syms = [ordered]@{}
    if ($offset) { foreach ($k in $Script:SymFields) { if ($offset.ContainsKey($k) -and $offset[$k] -ne $null) { $syms['off_' + $k] = [int64]$offset[$k] } } }
    $entry['symbols'] = $syms

    $tsk = $null
    if ($root.ContainsKey('task_struct') -and $root['task_struct'] -is [hashtable]) { $tsk = $root['task_struct'] }
    $fields = [ordered]@{}
    if ($tsk) { foreach ($k in $Script:TaskFields) { if ($tsk.ContainsKey($k) -and $tsk[$k] -ne $null) { $fields['task_' + $k] = [int64]$tsk[$k] } } }
    $entry['struct_fields'] = $fields
    return [pscustomobject]$entry
}

function To-Map {
    param($V)
    $m = @{}
    if ($V -is [System.Collections.IDictionary]) { foreach ($kk in $V.Keys) { $m[[string]$kk] = $V[$kk] } }
    else { foreach ($q in $V.PSObject.Properties) { $m[$q.Name] = $q.Value } }
    return $m
}

function Compare-Entry {
    param($A, $B)
    $errs = @()
    foreach ($p in $A.PSObject.Properties) {
        $name = $p.Name
        $bp = $B.PSObject.Properties[$name]
        if (-not $bp) { $errs += "missing in catalog: $name"; continue }
        if ($name -in @('symbols','struct_fields')) {
            $ap = To-Map $p.Value
            $bq = To-Map $bp.Value
            foreach ($k in @($ap.Keys)) { if (-not $bq.ContainsKey($k)) { $errs += "$name.$k missing" } elseif ([int64]$ap[$k] -ne [int64]$bq[$k]) { $errs += "${name}.$k $($ap[$k]) != $($bq[$k])" } }
            foreach ($k in @($bq.Keys)) { if (-not $ap.ContainsKey($k)) { $errs += "${name}.$k extra" } }
        } else {
            $av = $p.Value; $bv = $bp.Value
            if ($av -ne $bv) { $errs += "${name}: $av != $bv" }
        }
    }
    foreach ($p in $B.PSObject.Properties) { if (-not $A.PSObject.Properties[$p.Name]) { $errs += "extra in catalog: $($p.Name)" } }
    return $errs
}

if ($Validate) {
    $cat = [IO.File]::ReadAllText("C:\Users\PMK\Desktop\PMKUnlocker\PMKUnlocker\bundled-tools\exploits\payloads\offsets-catalog.json", [Text.Encoding]::UTF8) | ConvertFrom-Json
    $golden = @(
        '6.1.115-android14-11-ga2521ca27699-ab13294383',
        '6.1.138-android14-11-g0c3d559bcd85-ab14529422',
        '6.1.138-android14-11-g151cf2b6bfbe-ab13719792',
        '6.6.77-android15-8-g4a507830d890-ab13636293-4k'
    )
    $fail = 0
    foreach ($g in $golden) {
        $conv = Convert-ConfToEntry -Release $g -ProfilesDir $ProfilesDir
        $ref = $cat | Where-Object { $_.release -eq $g }
        $errs = Compare-Entry $conv $ref
        if ($errs.Count -eq 0) { Write-Host "PASS $g" }
        else {
            $fail++; Write-Host "FAIL $g"
            $errs | ForEach-Object { Write-Host "   $_" }
            Write-Host ("   CONV sym: " + (($conv.symbols.PSObject.Properties | ForEach-Object { $_.Name + '=' + $_.Value }) -join '; '))
            Write-Host ("   REF  sym: " + (($ref.symbols.PSObject.Properties | ForEach-Object { $_.Name + '=' + $_.Value }) -join '; '))
            Write-Host ("   CONV tf : " + (($conv.struct_fields.PSObject.Properties | Select-Object -First 3 | ForEach-Object { $_.Name + '=' + $_.Value }) -join '; '))
        }
    }
    if ($fail -gt 0) { exit 1 } else { Write-Host "ALL GOLDEN PASSED"; exit 0 }
}

$result = @()
foreach ($r in $Releases) {
    $result += Convert-ConfToEntry -Release $r -ProfilesDir $ProfilesDir
}
$json = ConvertTo-Json -InputObject @($result) -Depth 8
if ($Out -ne "") {
    [IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding($false)))
    Write-Host "wrote $($result.Count) entries -> $Out"
} else {
    Write-Host $json
}

# HOCON kernel profile (.conf) -> GLK1 wire v2 binary (native --load-prebuilt-profile <bin>).
# Mirrors upstream gradle exportKernelProfiles (ProfileExporter + ProfileMerger +
# ProfileResolver + NativeProfileDocument.toBinary) line-for-line semantics.
# Usage:
#   .\conf-to-glk1.ps1 -Validate                     # export all to temp, round-trip decode, golden cross-check vs offsets-catalog.json
#   .\conf-to-glk1.ps1 -Out <dir>                    # export all profiles -> <dir>\<release>.bin
#   .\conf-to-glk1.ps1 -Profile <file.conf> [-Out d]  # export one standalone conf (e.g. ghostlock-extract output; unknown top-level keys stripped with warning)
param(
    [string]$ProfilesDir = "C:\Users\PMK\AppData\Local\Temp\opencode\gl-src\app\src\main\assets\kernel_profiles",
    [string]$Out = "C:\Users\PMK\Desktop\PMKUnlocker\PMKUnlocker\bundled-tools\exploits\profiles",
    [string]$Profile = "",
    [switch]$Validate
)
$ErrorActionPreference = 'Stop'

$Script:RouteTokens = @('tcp_zerocopy', 'select_stack', 'multicast_waiter')
$Script:RouteWire = @{ tcp_zerocopy = 1; select_stack = 2; multicast_waiter = 3 }
$Script:TaskKeys = @('prio', 'normal_prio', 'sched_task_group', 'pi_lock', 'pi_waiters', 'pi_top_task', 'pi_blocked_on', 'pid', 'tgid', 'atomic_flags', 'real_cred', 'cred', 'comm', 'tasks', 'seccomp')
$Script:OffsetKeys = @('init_task', 'init_cred', 'empty_zero_page', 'root_task_group', 'selinux_enforcing', 'selinux_blob_sizes', 'security_hook_heads', 'slide_nfulnl_logger', 'slide_loggers_0_1', 'slide_boot_id')
$Script:CredOrder = @('copy_size', 'usage_offset', 'usage_value', 'caps_offset', 'caps_count', 'caps_value', 'ref_count', 'ref0_offset', 'ref1_offset', 'ref2_offset', 'ref3_offset', 'ref0_image', 'ref1_image', 'ref2_image', 'ref3_image')
$Script:CredU64 = @('caps_value', 'ref0_image', 'ref1_image', 'ref2_image', 'ref3_image')
$Script:KnownTopLevel = @('release', 'schema_version', 'kernel_major', 'recommend_shizuku', 'route', 'fallback', 'kernelsnitch', 'task_struct', 'cred', 'offset', 'kernel_phys_load', 'kernel_phys_offset', 'execution')
$Script:RequiredTopLevel = @('release', 'schema_version', 'kernel_major', 'route', 'task_struct', 'cred', 'offset')
$Script:RequiredCred = @('copy_size', 'caps_count')
$Script:RequiredOffset = @('init_task', 'init_cred', 'root_task_group', 'selinux_enforcing')
$Script:HeapKeys = @('prepare_max_attempts', 'prepare_timeout_ms', 'kernelsnitch_timeout_ms')
$Script:RaceKeys = @('route_wait_ms', 'route_done_timeout_ms', 'setup_settle_us', 'state_poll_interval_us')
$Script:StageKeys = @('w1_attempts', 'w1_settle_us', 'w1_scratch_repair_attempts', 'w2_attempts', 'w2_settle_us', 'w3_chain_rounds', 'w3_attempts', 'w3_settle_us')
$Script:HandoffKeys = @('pre_dispatch_settle_ms', 'module_poll_attempts', 'module_poll_interval_ms', 'enforce_poll_attempts', 'enforce_poll_interval_ms')
$Script:McastKeys = @('buffer_size', 'task_offset', 'lock_offset')

function New-M { [ordered]@{} }

function Strip-HoconComments {
    param([string]$Txt)
    $sb = New-Object System.Text.StringBuilder
    $inStr = $false
    for ($i = 0; $i -lt $Txt.Length; $i++) {
        $ch = $Txt[$i]
        if ($inStr) {
            [void]$sb.Append($ch)
            if ($ch -eq '\') {
                if ($i + 1 -lt $Txt.Length) { $i++; [void]$sb.Append($Txt[$i]) }
                continue
            }
            if ($ch -eq '"') { $inStr = $false }
            continue
        }
        if ($ch -eq '"') { $inStr = $true; [void]$sb.Append($ch); continue }
        if ($ch -eq '#') { while ($i -lt $Txt.Length -and $Txt[$i] -ne "`n") { $i++ }; continue }
        if ($ch -eq '/' -and ($i + 1) -lt $Txt.Length -and $Txt[$i + 1] -eq '/') { while ($i -lt $Txt.Length -and $Txt[$i] -ne "`n") { $i++ }; continue }
        [void]$sb.Append($ch)
    }
    return $sb.ToString()
}

function Skip-WsCommas {
    param([string]$S, $I)
    while ($I.Value -lt $S.Length -and ([char]::IsWhiteSpace($S[$I.Value]) -or $S[$I.Value] -eq ',')) { $I.Value++ }
}

function New-Value {
    param([string]$Tok)
    if ($Tok -eq 'null') { return $null }
    if ($Tok -eq 'true') { return $true }
    if ($Tok -eq 'false') { return $false }
    if ($Tok -match '^-?\d+$') { return [int64]$Tok }
    if ($Tok -match '^0x[0-9a-fA-F]+$') { return [Convert]::ToInt64($Tok.Substring(2), 16) }
    return $Tok
}

function Get-MutableChild {
    param($M, [string]$Key)
    $segments = $Key.Split('.')
    $node = $M
    foreach ($seg in $segments) {
        $child = $null
        if ($node.Contains($seg)) { $child = $node[$seg] }
        if ($child -isnot [System.Collections.IDictionary]) { $child = New-M; $node[$seg] = $child }
        $node = $child
    }
    return $node
}

function Set-PathValue {
    param($M, [string]$Key, $Value)
    $segments = $Key.Split('.')
    $node = $M
    for ($s = 0; $s -lt $segments.Count - 1; $s++) {
        $seg = $segments[$s]
        $child = $null
        if ($node.Contains($seg)) { $child = $node[$seg] }
        if ($child -isnot [System.Collections.IDictionary]) { $child = New-M; $node[$seg] = $child }
        $node = $child
    }
    $node[$segments[-1]] = $Value
}

function Read-QuotedString {
    param([string]$S, $I)
    # $I points at opening quote; returns content, advances past closing quote.
    $p = $I.Value + 1
    $sb = New-Object System.Text.StringBuilder
    while ($p -lt $S.Length) {
        $ch = $S[$p]
        if ($ch -eq '\') {
            if ($p + 1 -lt $S.Length) { $p++; [void]$sb.Append($S[$p]) }
            $p++
            continue
        }
        if ($ch -eq '"') { $p++; break }
        [void]$sb.Append($ch)
        $p++
    }
    $I.Value = $p
    return $sb.ToString()
}

function Parse-ValueNode {
    param([string]$S, $I, $Container, [string]$BaseDir, $Visited)
    Skip-WsCommas $S $I
    if ($I.Value -ge $S.Length) { return $null }
    $ch = $S[$I.Value]
    if ($ch -eq '"') { return Read-QuotedString $S $I }
    if ($ch -eq '{') {
        $I.Value++
        $obj = New-M
        Parse-Block $S $I $obj $true $BaseDir $Visited
        return $obj
    }
    if ($ch -eq '[') {
        $I.Value++
        $list = New-Object System.Collections.ArrayList
        while ($I.Value -lt $S.Length) {
            Skip-WsCommas $S $I
            if ($I.Value -ge $S.Length) { break }
            if ($S[$I.Value] -eq ']') { $I.Value++; break }
            $v = Parse-ValueNode $S $I $Container $BaseDir $Visited
            [void]$list.Add($v)
        }
        return , $list
    }
    $p = $I.Value
    while ($p -lt $S.Length -and -not [char]::IsWhiteSpace($S[$p]) -and $S[$p] -notin @(',', '}', ']')) { $p++ }
    $tok = $S.Substring($I.Value, $p - $I.Value)
    $I.Value = $p
    return (New-Value $tok)
}

function Parse-Block {
    param([string]$S, $I, $Container, [bool]$StopAtClose, [string]$BaseDir, $Visited)
    $n = $S.Length
    while ($I.Value -lt $n) {
        Skip-WsCommas $S $I
        if ($I.Value -ge $n) { break }
        $ch = $S[$I.Value]
        if ($ch -eq '}') {
            $I.Value++
            if ($StopAtClose) { return }
            continue
        }
        if (-not ([char]::IsLetter($ch) -or $ch -eq '_')) { $I.Value++; continue }
        $p = $I.Value
        while ($p -lt $n -and ([char]::IsLetterOrDigit($S[$p]) -or $S[$p] -in @('_', '.', '-'))) { $p++ }
        $ident = $S.Substring($I.Value, $p - $I.Value)
        $I.Value = $p
        Skip-WsCommas $S $I
        if ($I.Value -lt $n -and $S[$I.Value] -eq '{') {
            $I.Value++
            $child = Get-MutableChild $Container $ident
            Parse-Block $S $I $child $true $BaseDir $Visited
            continue
        }
        if ($I.Value -lt $n -and $S[$I.Value] -eq '=') {
            $I.Value++
            if ($ident -eq 'include') {
                Skip-WsCommas $S $I
                if ($I.Value -lt $n -and $S[$I.Value] -eq '"') {
                    $target = Read-QuotedString $S $I
                    Parse-HoconFileInto (Join-Path $BaseDir $target) $Container $BaseDir $Visited
                }
                continue
            }
            $v = Parse-ValueNode $S $I $Container $BaseDir $Visited
            Set-PathValue $Container $ident $v
            continue
        }
        if ($ident -eq 'include') {
            Skip-WsCommas $S $I
            if ($I.Value -lt $n -and $S[$I.Value] -eq '"') {
                $target = Read-QuotedString $S $I
                Parse-HoconFileInto (Join-Path $BaseDir $target) $Container $BaseDir $Visited
            }
            continue
        }
    }
}

function Parse-HoconFileInto {
    param([string]$Path, $Container, [string]$BaseDir, $Visited)
    $full = [IO.Path]::GetFullPath($Path)
    if ($Visited.Contains($full)) { return }
    [void]$Visited.Add($full)
    if (-not (Test-Path -LiteralPath $Path)) { throw "conf missing: $Path" }
    $txt = Strip-HoconComments ([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8))
    $i = 0
    Parse-Block $txt ([ref]$i) $Container $false $BaseDir $Visited
}

function Parse-ProfileFile {
    param([string]$Path, [string]$BaseDir)
    $root = New-M
    $visited = New-Object 'System.Collections.Generic.HashSet[string]'
    Parse-HoconFileInto $Path $root $BaseDir $visited
    return $root
}

function Get-LongAt {
    param($M, [string]$Path)
    $node = $M
    foreach ($seg in $Path.Split('.')) {
        if ($node -is [System.Collections.IDictionary]) {
            if (-not $node.Contains($seg)) { return $null }
            $node = $node[$seg]
        } else { return $null }
    }
    if ($node -is [int64]) { return $node }
    return $null
}

function Deep-Copy {
    param($V)
    if ($V -is [System.Collections.IDictionary]) {
        $out = New-M
        foreach ($k in @($V.Keys)) { $out[$k] = Deep-Copy $V[$k] }
        return $out
    }
    if ($V -is [System.Collections.ArrayList]) {
        $out = New-Object System.Collections.ArrayList
        foreach ($it in $V) { [void]$out.Add((Deep-Copy $it)) }
        return $out
    }
    return $V
}

function Deep-Merge {
    # mirrors ValueModel.deepMergeValues: map+map -> recurse, incoming null -> keep base, else replace
    param($Base, $Incoming)
    if ($null -eq $Incoming) { return $Base }
    foreach ($k in @($Incoming.Keys)) {
        $inc = $Incoming[$k]
        $cur = $null
        if ($Base.Contains($k)) { $cur = $Base[$k] }
        if ($inc -is [System.Collections.IDictionary] -and $cur -is [System.Collections.IDictionary]) {
            $Base[$k] = Deep-Merge $cur $inc
        } elseif ($null -ne $inc) {
            $Base[$k] = $inc
        }
    }
    return $Base
}

function Get-RouteName {
    param($Profile)
    $r = $null
    if ($Profile.Contains('route')) { $r = $Profile['route'] }
    if ($r -is [string]) { if ($r -and $r -ne 'null') { return $r } else { return $null } }
    if ($r -is [System.Collections.IDictionary]) {
        foreach ($k in @($r.Keys)) { if ($k -in $Script:RouteTokens) { return [string]$k } }
        return $null
    }
    return $null
}

function Get-FallbackTo {
    param($Profile)
    $to = $null
    if ($Profile.Contains('fallback') -and $Profile['fallback'] -is [System.Collections.IDictionary]) {
        $fb = $Profile['fallback']
        if ($fb.Contains('to')) { $to = $fb['to'] }
    }
    if ($to -is [string] -and $to -and $to -ne 'null') { return $to }
    if ($Profile.Contains('fallback_to')) {
        $to = $Profile['fallback_to']
        if ($to -is [string] -and $to -and $to -ne 'null') { return $to }
    }
    return $null
}

function Resolve-Merged {
    param([string]$DeviceRelease, $Builtin, $TuningExecution, $RoutePresets)
    $defaults = New-M
    $defaults['release'] = $DeviceRelease
    if ($null -ne $TuningExecution) { $defaults['execution'] = Deep-Copy $TuningExecution }
    $resolved = Deep-Merge $defaults $Builtin
    $resolved['schema_version'] = [int64]1
    $resolved['release'] = $DeviceRelease
    $execution = Get-MutableChild $resolved 'execution'
    $sel = New-M; $sel['main'] = [int64]0; $sel['consumer'] = [int64]1
    $execution['selected_cpus'] = $sel
    $routes = Get-MutableChild $execution 'routes'
    foreach ($route in $Script:RouteTokens) {
        if (-not $RoutePresets.Contains($route)) { continue }
        $preset = $RoutePresets[$route]
        $existing = $null
        if ($routes.Contains($route)) { $existing = $routes[$route] }
        if ($existing -isnot [System.Collections.IDictionary]) {
            $routes[$route] = Deep-Copy $preset
        } else {
            foreach ($k in @($preset.Keys)) {
                if (-not $existing.Contains($k)) { $existing[$k] = $preset[$k] }
            }
        }
    }
    # validateResolved
    if ($resolved['release'] -ne $DeviceRelease) { throw "profile release mismatch" }
    $km = Get-LongAt $resolved 'kernel_major'
    if ($null -eq $km -or $km -lt 5 -or $km -gt 6) { throw "invalid kernel_major: $km" }
    if (-not $resolved.Contains('execution')) { throw "missing execution tuning" }
    return $resolved
}

function Test-ValidateMerged {
    param($Profile, $Route, $FallbackTo)
    $errors = @()
    foreach ($key in @($Profile.Keys)) {
        if ($key -notin $Script:KnownTopLevel) { $errors += "$key : unknown top-level key" }
    }
    foreach ($key in $Script:RequiredTopLevel) {
        if (-not $Profile.Contains($key)) { $errors += "$key : missing required key" }
    }
    if ($null -eq $Route -or $Route -notin $Script:RouteTokens) { $errors += "route : missing or unknown route" }
    if ($null -ne $FallbackTo -and $FallbackTo -ne 'none' -and $FallbackTo -notin $Script:RouteTokens) { $errors += "fallback.to : unknown fallback route" }
    $task = $null
    if ($Profile.Contains('task_struct')) { $task = $Profile['task_struct'] }
    if ($task -isnot [System.Collections.IDictionary]) {
        $errors += "task_struct : not an object"
    } else {
        foreach ($f in $Script:TaskKeys) {
            if (-not $task.Contains($f) -or $task[$f] -isnot [int64]) { $errors += "task_struct.$f : missing" }
        }
    }
    $cred = $null
    if ($Profile.Contains('cred')) { $cred = $Profile['cred'] }
    if ($cred -isnot [System.Collections.IDictionary]) {
        $errors += "cred : not an object"
    } else {
        foreach ($f in $Script:RequiredCred) {
            if (-not $cred.Contains($f) -or $cred[$f] -isnot [int64]) { $errors += "cred.$f : missing" }
            elseif ([int64]$cred[$f] -eq 0) { $errors += "cred.$f : zero" }
        }
    }
    $offset = $null
    if ($Profile.Contains('offset')) { $offset = $Profile['offset'] }
    if ($offset -isnot [System.Collections.IDictionary]) {
        $errors += "offset : not an object"
    } else {
        foreach ($f in $Script:RequiredOffset) {
            if (-not $offset.Contains($f) -or $offset[$f] -isnot [int64]) { $errors += "offset.$f : missing" }
            elseif ([int64]$offset[$f] -eq 0) { $errors += "offset.$f : zero" }
        }
    }
    return , $errors
}

function Get-NativeValue {
    # mirrors ProfileResolver.nativeValue
    param($Merged, $Route, $FallbackTo, [string]$Path)
    if ($Path -eq 'execution.recommended_cpus.main' -or $Path -eq 'execution.recommended_cpus.consumer') {
        $slot = $Path.Substring('execution.recommended_cpus.'.Length)
        $sel = Get-LongAt $Merged "execution.selected_cpus.$slot"
        if ($null -ne $sel) { return $sel }
    }
    $branchField = $null
    if ($Path -eq 'compact_waiter') { $branchField = 'compact_waiter' }
    elseif ($Path -eq 'pselect_waiter_shift') { $branchField = 'waiter_shift' }
    if ($null -ne $branchField) {
        if ($Route) {
            $v = Get-LongAt $Merged "route.$Route.$branchField"
            if ($null -ne $v) { return $v }
        }
        if ($FallbackTo -and $FallbackTo -ne 'none') {
            $v = Get-LongAt $Merged "fallback.route.$FallbackTo.$branchField"
            if ($null -ne $v) { return $v }
        }
    }
    if ($Path.StartsWith('mcast.')) {
        $field = $Path.Substring('mcast.'.Length)
        if ($Route) {
            $v = Get-LongAt $Merged "route.$Route.$field"
            if ($null -ne $v) { return $v }
        }
        if ($FallbackTo -and $FallbackTo -ne 'none') {
            $v = Get-LongAt $Merged "fallback.route.$FallbackTo.$field"
            if ($null -ne $v) { return $v }
        }
        $v = Get-LongAt $Merged "mcast.$field"
        if ($null -ne $v) { return $v }
    }
    return (Get-LongAt $Merged $Path)
}

function U32 { param([long]$V) return ($V -band [int64]4294967295) }
function U8 { param([long]$V) return ($V -band [int64]255) }
function S32 {
    param([long]$V)
    $x = $V -band [int64]4294967295
    if ($x -ge [int64]2147483648) { $x = $x - [int64]4294967296 }
    return $x
}

function New-Section {
    param([string]$Name)
    return @{ Name = $Name; Entries = New-Object System.Collections.ArrayList }
}
function Add-Entry {
    param($Section, [string]$Key, $Value)
    [void]$Section.Entries.Add(@{ K = $Key; V = [int64]$Value })
}

function Build-Sections {
    param($Merged, $Route, $FallbackTo)
    $sections = New-Object System.Collections.ArrayList

    $fbWire = [int64]0
    if ($FallbackTo -and $RouteWire.Contains($FallbackTo)) { $fbWire = [int64]$RouteWire[$FallbackTo] }
    $meta = New-Section 'meta'
    Add-Entry $meta 'kernel_major' (U32 (Get-NativeValue $Merged $Route $FallbackTo 'kernel_major'))
    Add-Entry $meta 'recommend_shizuku' (U32 (Get-NativeValue $Merged $Route $FallbackTo 'recommend_shizuku'))
    Add-Entry $meta 'fallback_route' $fbWire
    Add-Entry $meta 'safe_mode' ([int64]0)
    [void]$sections.Add($meta)

    $task = New-Section 'task_struct'
    foreach ($k in $Script:TaskKeys) { Add-Entry $task $k (U32 (Get-NativeValue $Merged $Route $FallbackTo "task_struct.$k")) }
    [void]$sections.Add($task)

    $cred = New-Section 'cred'
    foreach ($k in $Script:CredOrder) {
        $v = Get-NativeValue $Merged $Route $FallbackTo "cred.$k"
        if ($null -eq $v) { $v = [int64]0 }
        if ($k -in $Script:CredU64) { Add-Entry $cred $k ([int64]$v) } else { Add-Entry $cred $k (U32 $v) }
    }
    [void]$sections.Add($cred)

    $offset = New-Section 'offset'
    foreach ($k in $Script:OffsetKeys) {
        $v = Get-NativeValue $Merged $Route $FallbackTo "offset.$k"
        if ($null -eq $v) { $v = [int64]0 }
        Add-Entry $offset $k ([int64]$v)
    }
    [void]$sections.Add($offset)

    # kernel section (OPT: omit when empty)
    $kern = New-Section 'kernel'
    $v = Get-NativeValue $Merged $Route $FallbackTo 'kernel_phys_load'
    if ($null -ne $v) { Add-Entry $kern 'kernel_phys_load' ([int64]$v) }
    $v = Get-NativeValue $Merged $Route $FallbackTo 'kernel_phys_offset'
    if ($null -ne $v) { Add-Entry $kern 'kernel_phys_offset' ([int64]$v) }
    $v = Get-NativeValue $Merged $Route $FallbackTo 'compact_waiter'
    if ($null -ne $v) { Add-Entry $kern 'compact_waiter' (U8 $v) }
    $v = Get-NativeValue $Merged $Route $FallbackTo 'kernelsnitch.collisions'
    if ($null -ne $v) { Add-Entry $kern 'kernelsnitch_collisions' (U32 $v) }
    $v = Get-NativeValue $Merged $Route $FallbackTo 'kernelsnitch.mm_struct_sz'
    if ($null -ne $v) { Add-Entry $kern 'mm_struct_sz' (U32 $v) }
    if ($kern.Entries.Count -gt 0) { [void]$sections.Add($kern) }

    $cpus = New-Section 'execution.recommended_cpus'
    Add-Entry $cpus 'main' (U32 (Get-NativeValue $Merged $Route $FallbackTo 'execution.recommended_cpus.main'))
    Add-Entry $cpus 'consumer' (U32 (Get-NativeValue $Merged $Route $FallbackTo 'execution.recommended_cpus.consumer'))
    [void]$sections.Add($cpus)

    $heap = New-Section 'execution.heap'
    foreach ($k in $Script:HeapKeys) { Add-Entry $heap $k (U32 (Get-NativeValue $Merged $Route $FallbackTo "execution.heap.$k")) }
    [void]$sections.Add($heap)

    $race = New-Section 'execution.race'
    foreach ($k in $Script:RaceKeys) { Add-Entry $race $k (U32 (Get-NativeValue $Merged $Route $FallbackTo "execution.race.$k")) }
    [void]$sections.Add($race)

    $stages = New-Section 'execution.stages'
    foreach ($k in $Script:StageKeys) { Add-Entry $stages $k (U32 (Get-NativeValue $Merged $Route $FallbackTo "execution.stages.$k")) }
    [void]$sections.Add($stages)

    $handoff = New-Section 'execution.handoff'
    foreach ($k in $Script:HandoffKeys) { Add-Entry $handoff $k (U32 (Get-NativeValue $Merged $Route $FallbackTo "execution.handoff.$k")) }
    [void]$sections.Add($handoff)

    $consumer = New-Section 'execution.consumer'
    Add-Entry $consumer 'max_calls' (U32 (Get-NativeValue $Merged $Route $FallbackTo 'execution.routes.select_stack.consumer_max_calls'))
    Add-Entry $consumer 'burst_calls' (U32 (Get-NativeValue $Merged $Route $FallbackTo 'execution.routes.select_stack.consumer_burst_calls'))
    [void]$sections.Add($consumer)

    if ($Route -eq 'tcp_zerocopy') {
        $rs = New-Section 'route.tcp_zerocopy'
        foreach ($k in @('attempts', 'arm_sequence', 'post_receive_hold_iterations')) {
            $v = Get-NativeValue $Merged $Route $FallbackTo "execution.routes.tcp_zerocopy.$k"
            if ($null -eq $v) { $v = [int64]0 }
            Add-Entry $rs $k (U32 $v)
        }
        [void]$sections.Add($rs)
    } elseif ($Route -eq 'select_stack') {
        $rs = New-Section 'route.select_stack'
        $v = Get-NativeValue $Merged $Route $FallbackTo 'pselect_waiter_shift'
        if ($null -ne $v) { Add-Entry $rs 'waiter_shift' (S32 $v) }
        $v = Get-NativeValue $Merged $Route $FallbackTo 'compact_waiter'
        if ($null -ne $v) { Add-Entry $rs 'compact_waiter' (U8 $v) }
        $v = Get-NativeValue $Merged $Route $FallbackTo 'execution.routes.select_stack.enter_delay_us'
        if ($null -ne $v) { Add-Entry $rs 'enter_delay_us' (U32 $v) }
        $v = Get-NativeValue $Merged $Route $FallbackTo 'execution.routes.select_stack.timeout_us'
        if ($null -ne $v) { Add-Entry $rs 'timeout_us' (U32 $v) }
        if ($rs.Entries.Count -gt 0) { [void]$sections.Add($rs) }
    } elseif ($Route -eq 'multicast_waiter') {
        $rs = New-Section 'route.multicast_waiter'
        $v = Get-NativeValue $Merged $Route $FallbackTo 'mcast.waiter_off'
        if ($null -ne $v) { Add-Entry $rs 'waiter_off' (S32 $v) }
        foreach ($k in $Script:McastKeys) {
            $v = Get-NativeValue $Merged $Route $FallbackTo "mcast.$k"
            if ($null -ne $v) { Add-Entry $rs $k (U32 $v) }
        }
        if ($rs.Entries.Count -gt 0) { [void]$sections.Add($rs) }
    }
    return , $sections
}

function Write-GLK1 {
    param([string]$Release, [int]$RouteWireVal, $Sections)
    $rb = [Text.Encoding]::UTF8.GetBytes($Release)
    if ($rb.Length -gt 0xFFFF) { throw "release is too long" }
    if ($Sections.Count -gt 0xFFFF) { throw "too many sections" }
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter($ms)
    $bw.Write([uint32]0x0D000721)
    $bw.Write([uint16]2)
    $bw.Write([uint16]1)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$RouteWireVal)
    $bw.Write([uint16]$rb.Length)
    $bw.Write([uint16]0)
    $bw.Write([byte[]]$rb)
    $bw.Write([uint16]$Sections.Count)
    foreach ($sec in $Sections) {
        $nb = [Text.Encoding]::UTF8.GetBytes([string]$sec.Name)
        if ($nb.Length -gt 255) { throw "section name too long: $($sec.Name)" }
        $bw.Write([byte]$nb.Length)
        $bw.Write([byte[]]$nb)
        $bw.Write([int32]$sec.Entries.Count)
        foreach ($e in $sec.Entries) {
            $kb = [Text.Encoding]::UTF8.GetBytes([string]$e.K)
            if ($kb.Length -gt 255) { throw "entry key too long: $($e.K)" }
            $bw.Write([byte]$kb.Length)
            $bw.Write([byte[]]$kb)
            $bw.Write([int64]$e.V)
        }
    }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Close()
    return $bytes
}

function Read-GLK1 {
    param([byte[]]$Bytes)
    $ms = New-Object IO.MemoryStream(, $Bytes)
    $br = New-Object IO.BinaryReader($ms)
    $magic = $br.ReadUInt32()
    if ($magic -ne [uint32]0x0D000721) { throw "bad magic: $magic" }
    $ver = $br.ReadUInt16()
    if ($ver -ne 2) { throw "bad version: $ver" }
    $fe = $br.ReadUInt16(); $bk = $br.ReadUInt16(); $rt = $br.ReadUInt16()
    $rl = $br.ReadUInt16(); $res = $br.ReadUInt16()
    $release = [Text.Encoding]::UTF8.GetString($br.ReadBytes($rl))
    $sc = $br.ReadUInt16()
    $sections = New-Object System.Collections.ArrayList
    for ($s = 0; $s -lt $sc; $s++) {
        $nl = [int]$br.ReadByte()
        $name = [Text.Encoding]::UTF8.GetString($br.ReadBytes($nl))
        $ec = $br.ReadInt32()
        $entries = New-Object System.Collections.ArrayList
        for ($e = 0; $e -lt $ec; $e++) {
            $kl = [int]$br.ReadByte()
            $key = [Text.Encoding]::UTF8.GetString($br.ReadBytes($kl))
            $val = $br.ReadInt64()
            [void]$entries.Add(@{ K = $key; V = $val })
        }
        [void]$sections.Add(@{ Name = $name; Entries = $entries })
    }
    if ($ms.Position -ne $Bytes.Length) { throw "trailing bytes: $($ms.Position) != $($Bytes.Length)" }
    $br.Close()
    return @{ Release = $release; RouteWire = $rt; Sections = $sections }
}

function Compare-RoundTrip {
    param([string]$Release, [int]$RouteWireVal, $Sections, $Decoded)
    $errs = @()
    if ($Decoded.Release -ne $Release) { $errs += "release: '$($Decoded.Release)' != '$Release'" }
    if ($Decoded.RouteWire -ne $RouteWireVal) { $errs += "route: $($Decoded.RouteWire) != $RouteWireVal" }
    if ($Decoded.Sections.Count -ne $Sections.Count) { $errs += "section count: $($Decoded.Sections.Count) != $($Sections.Count)"; return , $errs }
    for ($s = 0; $s -lt $Sections.Count; $s++) {
        $a = $Sections[$s]; $b = $Decoded.Sections[$s]
        if ($a.Name -ne $b.Name) { $errs += "section[$s]: '$($b.Name)' != '$($a.Name)'" ; continue }
        if ($a.Entries.Count -ne $b.Entries.Count) { $errs += "$($a.Name): entries $($b.Entries.Count) != $($a.Entries.Count)"; continue }
        for ($e = 0; $e -lt $a.Entries.Count; $e++) {
            if ($a.Entries[$e].K -ne $b.Entries[$e].K) { $errs += "$($a.Name)[$e]: key '$($b.Entries[$e].K)' != '$($a.Entries[$e].K)'" }
            if ([int64]$a.Entries[$e].V -ne [int64]$b.Entries[$e].V) { $errs += "$($a.Name).$($a.Entries[$e].K): $($b.Entries[$e].V) != $($a.Entries[$e].V)" }
        }
    }
    return , $errs
}

function Get-ExportInputs {
    param([string]$SrcDir)
    $inputs = @{ Tuning = $null; Presets = @{} }
    $tuningDoc = Parse-ProfileFile (Join-Path $SrcDir 'execution-tuning.conf') $SrcDir
    if ($tuningDoc.Contains('execution')) { $inputs.Tuning = $tuningDoc['execution'] }
    foreach ($route in $Script:RouteTokens) {
        $pf = Join-Path $SrcDir ("execution-" + $route.Replace('_', '-') + ".conf")
        if (-not (Test-Path -LiteralPath $pf)) { continue }
        $doc = Parse-ProfileFile $pf $SrcDir
        if (-not $doc.Contains('execution')) { continue }
        $exec = $doc['execution']
        if (-not ($exec -is [System.Collections.IDictionary] -and $exec.Contains('routes'))) { continue }
        $routes = $exec['routes']
        if ($routes -is [System.Collections.IDictionary] -and $routes.Contains($route)) {
            $inputs.Presets[$route] = $routes[$route]
        }
    }
    return $inputs
}

function Export-Single {
    # standalone conf (ghostlock-extract output): release from the file itself,
    # structural unknown top-level keys stripped with a warning (candidate source)
    param([string]$ConfPath, [string]$SrcDir, [string]$OutDir)
    $inputs = Get-ExportInputs $SrcDir
    $parsed = Parse-ProfileFile $ConfPath ([IO.Path]::GetDirectoryName($ConfPath))
    $release = $null
    if ($parsed.Contains('release')) { $release = $parsed['release'] }
    if (-not ($release -is [string]) -or -not $release) { throw "standalone conf has no release: $ConfPath" }
    $unknown = @()
    foreach ($k in @($parsed.Keys)) { if ($k -notin $Script:KnownTopLevel) { $unknown += $k } }
    if ($unknown.Count -gt 0) {
        Write-Host ("[warn] stripping unknown top-level key(s): " + ($unknown -join ', '))
        foreach ($k in $unknown) { $parsed.Remove($k) }
    }
    $route = Get-RouteName $parsed
    $fallbackTo = Get-FallbackTo $parsed
    $merged = Resolve-Merged $release $parsed $inputs.Tuning $inputs.Presets
    $errors = Test-ValidateMerged $merged $route $fallbackTo
    if ($errors.Count -gt 0) { throw "standalone profile fails validation: $($errors -join '; ')" }
    $wire = 0
    if ($null -eq $route) { throw "standalone profile has no route" }
    $wire = [int]$RouteWire[$route]
    $sections = Build-Sections $merged $route $fallbackTo
    $bytes = Write-GLK1 $release $wire $sections
    $decoded = Read-GLK1 $bytes
    $rtErrs = Compare-RoundTrip $release $wire $sections $decoded
    if ($rtErrs.Count -gt 0) { throw "standalone round-trip failed: $($rtErrs -join '; ')" }
    if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
    $outPath = Join-Path $OutDir ($release + '.bin')
    [IO.File]::WriteAllBytes($outPath, $bytes)
    Write-Host ("standalone export: {0} route={1} bytes={2} -> {3}" -f $release, $route, $bytes.Length, $outPath)
    return $outPath
}

function Export-All {
    param([string]$SrcDir, [string]$OutDir)
    if (-not (Test-Path -LiteralPath $SrcDir)) { throw "profiles dir does not exist: $SrcDir" }
    $index = Parse-ProfileFile (Join-Path $SrcDir 'index.conf') $SrcDir
    $profiles = $null
    if ($index.Contains('profiles')) { $profiles = $index['profiles'] }
    if ($profiles -isnot [System.Collections.ArrayList]) { throw "index.conf profiles must be a list" }

    $inputs = Get-ExportInputs $SrcDir
    $tuningExecution = $inputs.Tuning
    $routePresets = $inputs.Presets

    if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
    New-Item -ItemType Directory -Path $OutDir | Out-Null

    $count = 0
    $skippedTemplates = 0
    $routeCounts = @{ tcp_zerocopy = 0; select_stack = 0; multicast_waiter = 0 }
    $written = @{}
    foreach ($raw in $profiles) {
        if ($raw -isnot [System.Collections.IDictionary]) { throw "index.conf profiles entry is not an object" }
        if (-not $raw.Contains('file')) { throw "index entry missing 'file'" }
        if (-not $raw.Contains('release')) { throw "index entry missing 'release'" }
        $file = [string]$raw['file']
        $release = [string]$raw['release']
        if ($release.EndsWith('-template')) { $skippedTemplates++; continue }
        $parsed = Parse-ProfileFile (Join-Path $SrcDir $file) $SrcDir
        $actualRelease = $null
        if ($parsed.Contains('release')) { $actualRelease = $parsed['release'] }
        if ($actualRelease -ne $release) { throw "index/file release mismatch for $file" }
        $route = Get-RouteName $parsed
        $fallbackTo = Get-FallbackTo $parsed
        $merged = Resolve-Merged $release $parsed $tuningExecution $routePresets
        $errors = Test-ValidateMerged $merged $route $fallbackTo
        if ($errors.Count -gt 0) { throw "$file fails validation: $($errors -join '; ')" }
        $wire = 0
        if ($null -ne $route) { $wire = [int]$RouteWire[$route]; $routeCounts[$route]++ }
        $sections = Build-Sections $merged $route $fallbackTo
        $bytes = Write-GLK1 $release $wire $sections
        $decoded = Read-GLK1 $bytes
        $rtErrs = Compare-RoundTrip $release $wire $sections $decoded
        if ($rtErrs.Count -gt 0) { throw "$file round-trip failed: $($rtErrs -join '; ')" }
        $outPath = Join-Path $OutDir ($release + '.bin')
        [IO.File]::WriteAllBytes($outPath, $bytes)
        $written[$release] = @{ Wire = $wire; Bytes = $bytes.Length; Route = $route }
        $count++
    }
    return @{ Count = $count; Templates = $skippedTemplates; RouteCounts = $routeCounts; Written = $written; OutDir = $OutDir }
}

function Get-SectionMap {
    param($Decoded, [string]$Name)
    foreach ($s in $Decoded.Sections) { if ($s.Name -eq $Name) { return $s } }
    return $null
}
function Get-EntryValue {
    param($Section, [string]$Key)
    if ($null -eq $Section) { return $null }
    foreach ($e in $Section.Entries) { if ($e.K -eq $Key) { return [int64]$e.V } }
    return $null
}

if ($Profile) {
    if (-not (Test-Path -LiteralPath $Profile)) { throw "conf not found: $Profile" }
    [void](Export-Single $Profile $ProfilesDir $Out)
    exit 0
}

if ($Validate) {
    $catPath = "C:\Users\PMK\Desktop\PMKUnlocker\PMKUnlocker\bundled-tools\exploits\payloads\offsets-catalog.json"
    $cat = [IO.File]::ReadAllText($catPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $tmp = "C:\Users\PMK\AppData\Local\Temp\opencode\glk1-validate"
    $result = Export-All $ProfilesDir $tmp
    $fail = 0

    if ($result.Count -ne 67) { Write-Host "FAIL exported count $($result.Count) != 67"; $fail++ } else { Write-Host "PASS exported 67 profiles (+$($result.Templates) templates skipped)" }
    Write-Host ("     routes: tcp=$($result.RouteCounts.tcp_zerocopy) select=$($result.RouteCounts.select_stack) multicast=$($result.RouteCounts.multicast_waiter)")

    $golden = @(
        '6.1.115-android14-11-ga2521ca27699-ab13294383',
        '6.1.138-android14-11-g0c3d559bcd85-ab14529422',
        '6.1.138-android14-11-g151cf2b6bfbe-ab13719792',
        '6.6.77-android15-8-g4a507830d890-ab13636293-4k'
    )
    foreach ($g in $golden) {
        $errs = @()
        $binPath = Join-Path $tmp ($g + '.bin')
        if (-not (Test-Path -LiteralPath $binPath)) { Write-Host "FAIL $g (bin missing)"; $fail++; continue }
        $dec = Read-GLK1 ([IO.File]::ReadAllBytes($binPath))
        $ref = $cat | Where-Object { $_.release -eq $g }
        if ($null -eq $ref) { Write-Host "FAIL $g (no catalog entry)"; $fail++; continue }

        $secTask = Get-SectionMap $dec 'task_struct'
        foreach ($p in $ref.struct_fields.PSObject.Properties) {
            $field = $p.Name.Substring('task_'.Length)
            $gv = Get-EntryValue $secTask $field
            if ($null -eq $gv) { $errs += "task_struct.$field missing" }
            elseif ($gv -ne [int64]$p.Value) { $errs += "task_struct.$field $gv != $($p.Value)" }
        }
        $secOff = Get-SectionMap $dec 'offset'
        foreach ($p in $ref.symbols.PSObject.Properties) {
            $field = $p.Name.Substring('off_'.Length)
            $gv = Get-EntryValue $secOff $field
            if ($null -eq $gv) { $errs += "offset.$field missing" }
            elseif ($gv -ne [int64]$p.Value) { $errs += "offset.$field $gv != $($p.Value)" }
        }
        $secKern = Get-SectionMap $dec 'kernel'
        if ($null -ne $ref.mm_struct_sz) {
            $gv = Get-EntryValue $secKern 'mm_struct_sz'
            if ($null -eq $gv -or $gv -ne [int64]$ref.mm_struct_sz) { $errs += "mm_struct_sz $gv != $($ref.mm_struct_sz)" }
        }
        if ($null -ne $ref.kernel_phys_load) {
            $gv = Get-EntryValue $secKern 'kernel_phys_load'
            if ($null -eq $gv -or $gv -ne [int64]$ref.kernel_phys_load) { $errs += "kernel_phys_load $gv != $($ref.kernel_phys_load)" }
        }
        if ($null -ne $ref.kernel_phys_offset) {
            $gv = Get-EntryValue $secKern 'kernel_phys_offset'
            if ($null -eq $gv -or $gv -ne [int64]$ref.kernel_phys_offset) { $errs += "kernel_phys_offset $gv != $($ref.kernel_phys_offset)" }
        }
        if ($null -ne $ref.compact_waiter) {
            $found = Get-EntryValue $secKern 'compact_waiter'
            if ($null -eq $found) {
                foreach ($sec in $dec.Sections) {
                    if ($sec.Name.StartsWith('route.')) {
                        $found = Get-EntryValue $sec 'compact_waiter'
                        if ($null -ne $found) { break }
                    }
                }
            }
            if ($null -eq $found) { $errs += "compact_waiter missing (catalog $($ref.compact_waiter))" }
            elseif ($found -ne [int64]$ref.compact_waiter) { $errs += "compact_waiter $found != $($ref.compact_waiter)" }
        }
        $secRoute = $null
        foreach ($sec in $dec.Sections) { if ($sec.Name.StartsWith('route.')) { $secRoute = $sec } }
        $isSelect = $dec.RouteWire -eq 2
        if ($isSelect) {
            if ($null -ne $ref.pselect_waiter_shift) {
                $gv = Get-EntryValue $secRoute 'waiter_shift'
                if ($null -eq $gv -or $gv -ne [int64]$ref.pselect_waiter_shift) { $errs += "waiter_shift $gv != $($ref.pselect_waiter_shift)" }
            }
        } else {
            # tcp (or multicast) active: legacy catalog may carry a fallback-select shift,
            # the v1.2 wire carries only the active route section (upstream parity, by design)
            $gv = Get-EntryValue $secRoute 'waiter_shift'
            if ($null -ne $gv) { $errs += "unexpected waiter_shift in $($secRoute.Name) for non-select route" }
        }
        if ($secRoute -and $secRoute.Name -eq 'route.select_stack' -and $null -eq $ref.pselect_waiter_shift) {
            $gv = Get-EntryValue $secRoute 'waiter_shift'
            if ($null -ne $gv) { $errs += "waiter_shift present but catalog null" }
        }

        if ($errs.Count -eq 0) { Write-Host "PASS $g" }
        else {
            $fail++
            Write-Host "FAIL $g"
            $errs | ForEach-Object { Write-Host "   $_" }
        }
    }
    if ($fail -gt 0) { Write-Host "VALIDATION FAILED ($fail)"; exit 1 }
    Write-Host "ALL VALIDATION PASSED"
    exit 0
}

$res = Export-All $ProfilesDir $Out
Write-Host ("exported {0} profile(s) ({1} templates skipped) -> {2}" -f $res.Count, $res.Templates, $res.OutDir)
Write-Host ("routes: tcp_zerocopy={0} select_stack={1} multicast_waiter={2}" -f $res.RouteCounts.tcp_zerocopy, $res.RouteCounts.select_stack, $res.RouteCounts.multicast_waiter)

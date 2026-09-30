# Option B: fill pmk_codenames.json Oppo/Vivo/Samsung from loader DB phone keys + known board codenames.
# SoC-only loader keys are skipped. Unknown phones fall back to product-only display (graceful).
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $Root 'PMKUnlocker'
$codenamesPath = Join-Path $project 'pmk_codenames.json'
$loadersPath = Join-Path $project 'pmk_qc_loaders.json'

$codenames = Get-Content -LiteralPath $codenamesPath -Raw | ConvertFrom-Json
$loaders = Get-Content -LiteralPath $loadersPath -Raw | ConvertFrom-Json

function Strip-Mode([string]$key) {
    if ($key -match '^\[([^\]]+)\]\s*(.*)$') { return $Matches[2] }
    return $key
}

function Test-SocKey([string]$p) {
    if ($p -match '^(SM\d|SDM|OPPO_|prog_|loader$|MSM8|New_Chimera|Chimera)') { return $true }
    if ($p -match '^\d+([_\-]\d+)*$') { return $true }
    if ($p -match '(SNAPDRAGON|Chimera|New_Chimera|8G\d|8_ELITE|8SG4|8\+|7\+G2|6G1|695_|765G|710_|480_SPECIAL|845_SPECIAL|SM8\d{3}|SM7\d{3}|SM6\d{3}|SDM845|_V\d+\.\d|loader$|800X|MSM8|F1_FW|K10_VITALITY|^PAD$|^PAD_AIR$)') { return $true }
    if ($p -eq 'F1_FW' -or $p -eq 'MSM8x26' -or $p -eq 'loader') { return $true }
    return $false
}

# Known board / device codenames (ro.product.device style) keyed by loader product (mode-stripped).
# Only high-confidence entries — unknown phones stay unmapped (display = product only).
$known = @{
    # ---- Samsung (board codenames) ----
    'SM-G970U' = 'beyond0q'; 'SM-G973U' = 'beyond1q'; 'SM-G975U' = 'beyond2q'
    'SM-G981U' = 'beyond1x'; 'SM-G986U' = 'beyond2x'; 'SM-G988U' = 'c2s'
    'SM-G991U' = 'o1s';      'SM-G996U' = 'o2s';      'SM-G998U' = 'o3s'
    'SM-G998U1' = 'o3s'
    'SM-S901U' = 's3s';      'SM-S906U' = 's3qs';     'SM-S908U' = 's3sq'
    'SM-S911'  = 'r0s';      'SM-S916U' = 'r1qs';     'SM-S918U' = 'r2s'
    'SM-S918B' = 'r2s';      'SM-S916B' = 'r1qs';     'SM-S918N' = 'r2s'
    'SM-S901E' = 's3s';      'SM-S901J' = 's3s';      'SM-S908E' = 's3sq'
    'SM-S908N' = 's3sq';     'SM-906N'  = 's3qs';     'S926U' = 'r1qs'
    'S928U'    = 'r2s'
    'SM-F916U' = 'bloom';    'SM-F926B' = 'p3s';      'SM-F936U' = 'q4q'
    'SM-F946B' = 'dm3q';     'SM-F996'  = 'q3q';      'SM-F996U' = 'q3q'
    'SM-F711U' = 'o1f';      'SM-F721U' = 'v1f'
    'SM-G770F' = 'a71q';     'SM-G781U' = 'c1q';      'SM-G781V' = 'c1q'
    'SM-G990U' = 'a51q';     'SM-A525F' = 'a52q';     'SM-A526U' = 'a52sxq'
    'SM-A528B' = 'a52sxq'
    'SM-A705F' = 'a70q';     'SM-A716U' = 'a71xq';    'SM-A725F' = 'a72q'
    'SM-A725M' = 'a72q';     'SM-A115F' = 'a11q';     'SM-A115A' = 'a11q'
    'SM-A115U' = 'a11q';     'SM-A015'   = 'a01q';    'SM-A015F' = 'a01q'
    'SM-A015M' = 'a01q';     'SM-A025F' = 'a02q'
    'SM-A057F' = 'a05q';     'SM-A057G' = 'a05q';     'SM-A057M' = 'a05q'
    'SM-A235F' = 'a23q';     'SM-A235M' = 'a23q';     'SM-A236E' = 'a23q'
    'SM-M025F' = 'a02q';     'SM-M115F' = 'a11q';     'SM-M515F' = 'm51q'
    'SM-M526BR' = 'm52q';    'SM-M556E' = 'm55q'
    'SM-J415F' = 'j4primelte'; 'SM-J610F' = 'j6pluslte'
    'SM-N970U' = 'crownlte'; 'SM-N975U' = 'crownqlte'; 'SM-N981U' = 'c1q'
    'N986U'    = 'c2q';      'SM-N986U' = 'c2q'
    'SM-T500'  = 'gta4xlte'; 'SM-T867U' = 'gta7xlte'; 'SM-X210' = 'gta8'
    'SM_X210'  = 'gta8';     'SM-X216B' = 'gta8';     'SM-X906C' = 'gta10'
    'SM-E236B' = 'm23q';     'A70' = 'a70q'; 'A02S' = 'a02q'; 'J4' = 'j4primelte'
    'J6' = 'j6ultelte'; 'M01' = 'a01q'; 'M02' = 'a02q'; 'M11' = 'a11q'; 'S10' = 'beyond1q'
    'W737' = 'w737'

    # ---- Oppo ----
    'RENO_4_4G_CPH2113' = 'CPH2113'
    'RENO_4_2021SEC_CPH2113' = 'CPH2113'
    'RENO_5_4G_CPH2159' = 'CPH2159'
    'RENO_5_CPH2159' = 'CPH2159'
    'A74_CPH2219' = 'CPH2219'
    'A74_CPH2119' = 'CPH2119'
    'F19_CPH2219' = 'CPH2219'
    'A95_CPH2365' = 'CPH2365'
    'RENO_6_LITE_CPH2365' = 'CPH2365'
    'A53_CPH2127_1' = 'CPH2127'
    'A53_CPH2127_2' = 'CPH2127'
    'A53_CPH2127_3' = 'CPH2127'
    'A53_CPH2127_4' = 'CPH2127'
    'A52_CPH2127' = 'CPH2127'
    'A53S_CPH2135' = 'CPH2135'
    'A53S_CPH2139' = 'CPH2139'
    'A57_CPH2139' = 'CPH2139'
    'A53_PD2127' = 'PD2127'
    'A33_CPH2137' = 'CPH2137'
    'RENO_10_PRO_5G_CPH2525' = 'CPH2525'
    'RENO_10-Pro_CPH2525' = 'CPH2525'
    'RENO_12F_4G_CPH2687' = 'CPH2687'
    'RENO_7_CPH2363' = 'CPH2363'
    'RENO_7_4G_CPH2363' = 'CPH2363'
    'F21_PRO_4G_CPH2363' = 'CPH2363'
    'RENO_6_CPH2235' = 'CPH2235'
    'RENO_6_PRO_5G_CPH2247' = 'CPH2247'
    'F19S_CPH2223' = 'CPH2223'
    'A76_CPH2375' = 'CPH2375'
    'A77S_CPH2473' = 'CPH2473'
    'K10_4G_CPH2373' = 'CPH2373'
    'A60_CPH2631' = 'CPH2631'
    'A78_4G_CPH2565' = 'CPH2565'
    'A96_CPH2333' = 'CPH2333'
    'F3_PLUS_CPH1611' = 'CPH1611'
    'R9S_PLUS_CPH1611' = 'CPH1611'
    'F3_Pro_CPH1611' = 'CPH1611'
    'R9S_CPH1607' = 'CPH1607'
    'F17_CPH2095' = 'CPH2095'
    'F17_COH2095' = 'COH2095'
    'RENO_4_PRO_CPH2109' = 'CPH2109'
    'RENO_4_5G_CPH2091' = 'CPH2091'
    'FIND_X2_LITE_CPH2005' = 'CPH2005'
    'FIND_X2_NEO_CPH2009' = 'CPH2009'
    'RENO_3_PRO_CPH2009' = 'CPH2009'
    'RENO_10X_CPH1919' = 'CPH1919'
    'A73_CPH2099' = 'CPH2099'
    'A32_PDVM00' = 'PDVM00'
    'A11S_PDVM00' = 'PDVM00'
    'A36_PESM10' = 'PESM10'
    'K7_5G_PCLM50' = 'PCLM50'
    'RENO_K5_PEGM10' = 'PEGM10'
    'RENO_9_PHM110' = 'PHM110'
    'K9S_PERM10' = 'PERM10'
    'RENO_2_5G' = 'CPH2015'
    'RENO_4_PRO_5G' = 'CPH2091'
    'RENO_5_5G_CPH2145' = 'CPH2145'
    'RENO_5_PRO_ARTIST' = 'PDSE00'
    'RENO_5K_5G' = 'CPH2145'
    'RENO_6_PRO_PLUS_5G' = 'PELM50'
    'RENO_ACE_2' = 'PDHM00'
    'RENO_ACE' = 'PCLM00'
    'FIND_X2_PRO' = 'CPH2025'
    'FIND_X2' = 'CPH2015'
    'FIND_X3_PRO' = 'CPH2173'
    'FIND_X3_LITE' = 'CPH2207'
    'FIND_X3' = 'CPH2181'
    'FIND_X5' = 'PGFM10'
    'FIND_N' = 'CPH2437'
    'FIND_N_5G' = 'CPH2437'
    'A33F' = 'CPH1605'
    'A3S' = 'CPH1803'
    'A5' = 'CPH1819'
    'A7' = 'CPH1805'
    'A9' = 'CPH1937'
    'A71' = 'CPH2015'
    'A72' = 'CPH2135'
    'A73' = 'CPH2121'
    'A77' = 'CPH2263'
    'A78_4G' = 'CPH2565'
    'A57_1' = 'CPH1701'
    'A57_2' = 'CPH1701'
    'A51W_MIRROR5' = 'CPH1613'
    'A51KC' = 'CPH1801'
    'A3X' = 'CPH2477'
    'R11' = 'CPH1707'
    'R11S' = 'CPH1719'
    'R15_NEO' = 'CPH1805'
    'R7S_PLUS' = 'CPH1607'
    'R7SM' = 'CPH1607'
    'R9_PLUS' = 'R9PlusAM'
    'R9_PLUS_MA' = 'R9PlusAM'
    'R9S' = 'R9s'
    'F1' = 'X9006'
    'F1F' = 'X9006'
    'A33' = 'CPH1719'
    'K10_PRO' = 'PERM00'
    'K10_PRO_5G' = 'PGFM10'
    'K11' = 'PJG110'
    'K9_5G' = 'PDYM20'
    'NEO_7' = 'R7c'
    'NEO_9' = 'PHM110'
    'PAD_AIR' = 'OPD2203'
    'PAD' = 'OPD2101'
    'A60' = 'CPH2631'
    'K10_4G' = 'CPH2373'
    'F21_Pro_4G' = 'CPH2363'
    'F21S_PRO_4G' = 'CPH2373'
    'FIND_X2_LITE' = 'CPH2005'

    # ---- Vivo ----
    'X80_PRO_PD2185F' = 'PD2185'
    'X80_PRO' = 'PD2185'
    'X60_PRO_PD2005F' = 'PD2005'
    'X60_PRO' = 'PD2005'
    'X60_PRO_PLUS' = 'PD2045'
    'X60T_PRO' = 'PD2047'
    'X60T_PRO_PLUS' = 'PD2047'
    'X60' = 'PD2048'
    'X50_5G_PD2001F' = 'PD2001'
    'X50_PD2001F' = 'PD2001'
    'X50_PD2006F' = 'PD2006'
    'X50_PRO_PD2005' = 'PD2005'
    'X50_PRO_PD2005F' = 'PD2005'
    'X50_PRO' = 'PD2005'
    'V29_PD2283F' = 'PD2283'
    'Y33S_PD2270F' = 'PD2270'
    'V17_PD1948F' = 'PD1948'
    'V21_PD2107F' = 'PD2107'
    'V21E_PD2107F' = 'PD2107'
    'V21E' = 'PD2107'
    'V9_PD1730F' = 'PD1730'
    'Y91_PD1818BF' = 'PD1818'
    'Y91_PD1818F' = 'PD1818'
    'Y91_PD1818' = 'PD1818'
    'Y91' = 'PD1818'
    'Y93_LITE_PD1818F' = 'PD1818'
    'Y93_PD1818F' = 'PD1818'
    'Y93_PD1818' = 'PD1818'
    'Y93' = 'PD1818'
    'Y93_LITE' = 'PD1818'
    'Y95_PD1818F' = 'PD1818'
    'Y95_PD1818BF' = 'PD1818'
    'Y95_PD1818' = 'PD1818'
    'Y95' = 'PD1818'
    'Y95A_PD1730' = 'PD1730'
    'S1_PD1945GF' = 'PD1945'
    'S1_PRO_PD1945F_CF' = 'PD1945'
    'S1_PRO' = 'PD1945'
    'S1' = 'PD1945'
    'Y9S_PD1945F' = 'PD1945'
    'Z6_5G_PD1963' = 'PD1963'
    'S5_PD1932' = 'PD1932'
    'V11_PRO_PD1814F' = 'PD1814'
    'V11_PRO' = 'PD1814'
    'V15_PRO_PD1832F' = 'PD1832'
    'V15_PRO' = 'PD1832'
    'V19_PD1969F' = 'PD1969'
    'V19' = 'PD1969'
    'V19_NEO_PD1948' = 'PD1948'
    'V19_NEO' = 'PD1948'
    'V20_PD2039F' = 'PD2039'
    'V20_2021_PD2067F' = 'PD2067'
    'V20_PRO_PD2020F' = 'PD2020'
    'V20_SE_PD2038CF' = 'PD2038'
    'V20_SE_PD2038F' = 'PD2038'
    'V20_SE_PD20238CF' = 'PD2038'
    'V20_SE' = 'PD2038'
    'V5_PLUS_PD1624F' = 'PD1624'
    'V5_PLUS' = 'PD1624'
    'V7_PD1718F' = 'PD1718'
    'V7' = 'PD1718'
    'V7_PLUS_PD1708F' = 'PD1708'
    'V7_PLUS' = 'PD1708'
    'V9_YOUTH_PD1730BF' = 'PD1730'
    'V9_YOUTH' = 'PD1730'
    'V30_PD2323' = 'PD2323'
    'Y11_PD1930F' = 'PD1930'
    'Y11' = 'PD1930'
    'Y11S_PD2024' = 'PD2024'
    'Y12A_2021' = 'PD2060'
    'Y12A_PD2060' = 'PD2060'
    'Y12i_PD1930F' = 'PD1930'
    'Y12S_2021' = 'PD2060'
    'Y12S_PD2060' = 'PD2060'
    'Y12S_PD2060F' = 'PD2060'
    'Y12S' = 'PD2060'
    'Y20_PD2034' = 'PD2034'
    'Y20_PD2034F' = 'PD2034'
    'Y20_2020_V2029' = 'V2029'
    'Y20_V2027' = 'V2027'
    'Y20' = 'PD2034'
    'Y20A_PD2060F' = 'PD2060'
    'Y20i_PD2034F' = 'PD2034'
    'Y20i' = 'PD2034'
    'Y20S_PD2034' = 'PD2034'
    'Y20S_PD2034F' = 'PD2034'
    'Y20S' = 'PD2034'
    'Y20T_PD2093F' = 'PD2093'
    'Y21L_PD1309' = 'PD1309'
    'Y21T_PD2142F' = 'PD2142'
    'Y21T_PD2158' = 'PD2158'
    'Y22S_PD2228F' = 'PD2228'
    'Y25_PD1309' = 'PD1309'
    'Y27_PD1410F' = 'PD1410'
    'Y31_PD1410F' = 'PD1410'
    'Y31L_PD1505F' = 'PD1505'
    'Y35_PD1502F' = 'PD1502'
    'Y35_PD2225F' = 'PD2225'
    'Y36_D2280F' = 'D2280'
    'Y38_5G_PD2354F' = 'PD2354'
    'Y39_PD2444F' = 'PD2444'
    'Y50_PD1965F' = 'PD1965'
    'Y51_PD2044F' = 'PD2044'
    'Y51_PD2050F' = 'PD2050'
    'Y51A_PD2050' = 'PD2050'
    'Y51S_PD2050' = 'PD2050'
    'Y51S_PD2050F' = 'PD2050'
    'Y51' = 'PD2050'
    'Y53_PD1628F' = 'PD1628'
    'Y53' = 'PD1628'
    'Y55_PD1613F' = 'PD1613'
    'Y55S_PD1613BF' = 'PD1613'
    'Y55S' = 'PD1613'
    'Y55' = 'PD1613'
    'Y58_5G_PD2354F' = 'PD2354'
    'Y65_PD1621BF' = 'PD1621'
    'Y65' = 'PD1621'
    'Y70_PD2038F' = 'PD2038'
    'Y71_PD1731F' = 'PD1731'
    'Y85A_PD1730' = 'PD1730'
    'Y200_5G_PD2326F' = 'PD2326'
    'Y200E_PD2341F' = 'PD2341'
    'Y300_PLUS_PD2422F' = 'PD2422'
    'T1_44W_PD2201F' = 'PD2201'
    'T1_5G_PD2165F' = 'PD2165'
    'T1_PD2115' = 'PD2115'
    'T1_PD2165F' = 'PD2165'
    'T1_PRO_5G_PD2193F' = 'PD2193'
    'T1X_PD2142F' = 'PD2142'
    'T3X_5G_PD2353F' = 'PD2353'
    'NEX_2_PD1821F' = 'PD1821'
    'X_FOLD_PD2178' = 'PD2178'
    'X20_PLUS' = 'X20Plus'
    'X27_PRO' = 'PD1801'
    'X27' = 'PD1801'
    'X7_PLUS' = 'X7Plus'
    'X9S_PLUS' = 'X9Plus'
    'X9_PLUS' = 'X9Plus'
    'XPLAY_5' = 'X5Play'
    'XPLAY_6' = 'PD1613'
    'Z1_Pro_PD1911F' = 'PD1911'
    'Z1X_PD1921F' = 'PD1921'
    'Z5_PRO' = 'PD1814'
    'Z5X_PD1911' = 'PD1911'
    'V40_LITE' = 'PD1818'
    'V30E' = 'PD2313'
    'V17_PRO_PD1931F' = 'PD1931'
    'V17_PRO' = 'PD1931'
    'V17' = 'PD1948'
    'V17_INDIA' = 'PD1948'
    'V29E_PD2313F' = 'PD2313'
    'V29E_PD2313BF' = 'PD2313'
    'V29E_PD2325F' = 'PD2325'
    'U1' = 'PD1911'
    'U3' = 'PD1913'
    'Y50T' = 'PD1965'
    'Z5S' = 'PD1721'
    'Z3' = 'PD1724'
    'Z5i' = 'PD1724'
    'V40' = 'PD1801'
    'X7' = 'PD1730'
    'X9' = 'PD1624'
    'X9L' = 'PD1624'
    'X9S' = 'PD1624'
    'X9S_L' = 'PD1624'
    'X9S_PL' = 'PD1624'
    'X9_PL' = 'PD1624'
    'X9_PLUS_L' = 'PD1624'
    'X20' = 'PD1613'
    'X21S' = 'PD1818'
    'Y6' = 'PD1613'
    'Y66' = 'PD1613'
    'Y66i' = 'PD1613'
    'Y73' = 'PD1948'
    'Y79' = 'PD1718'
    'Y85' = 'PD1730'
    'Y3' = 'PD1621'
    'Y53C' = 'PD1628'
    'Y53i' = 'PD1628'
    'Y55L' = 'PD1613'
    'V3_MAX' = 'PD1524'
}

# Ensure brand sections exist with correct loader-DB casing
foreach ($brand in @('Oppo', 'Vivo', 'Samsung')) {
    if (-not $codenames.PSObject.Properties[$brand]) {
        $codenames | Add-Member -NotePropertyName $brand -NotePropertyValue ([pscustomobject]@{})
    }
}

$stats = [ordered]@{
    Oppo    = @{ phone = 0; filled = 0; skipped = 0 }
    Vivo    = @{ phone = 0; filled = 0; skipped = 0 }
    Samsung = @{ phone = 0; filled = 0; skipped = 0 }
}

$extract = '(CPH\d{3,4}|PD\d{4}[A-Z]?|RMX\d{3,4}|SM-[A-Z]\d{3}[A-Z]?|PE[A-Z]{2}\d{3}|PH[A-Z]{2}\d{3}|PJ[A-Z]{2}\d{3}|PDYM\d{2}|PDSE\d{2}|PDHM\d{2}|PCLM\d{2}|PGFM\d{2}|OPD\d{4}|PDVM\d{2}|PESM\d{2}|D\d{4}|V\d{4})'

foreach ($brand in @('Oppo', 'Vivo', 'Samsung')) {
    $map = $codenames.$brand
    $models = $loaders.$brand
    foreach ($prop in $models.PSObject.Properties) {
        $product = Strip-Mode $prop.Name
        if (Test-SocKey $product) {
            $stats[$brand].skipped++
            continue
        }
        $stats[$brand].phone++
        $code = $null
        if ($known.ContainsKey($product)) {
            $code = $known[$product]
        } elseif ($product -match $extract) {
            $code = $Matches[1]
        }
        if ($code) {
            if ($map.PSObject.Properties[$product]) {
                $map.$product = $code
            } else {
                $map | Add-Member -NotePropertyName $product -NotePropertyValue $code -Force
            }
            $stats[$brand].filled++
        }
    }
    $sorted = [ordered]@{}
    $map.PSObject.Properties.Name | Sort-Object | ForEach-Object { $sorted[$_] = $map.$_ }
    $codenames.$brand = [pscustomobject]$sorted
}

$final = [ordered]@{}
if ($codenames.PSObject.Properties['Xiaomi']) { $final['Xiaomi'] = $codenames.Xiaomi }
foreach ($b in @('Oppo', 'Vivo', 'Samsung')) { $final[$b] = $codenames.$b }
foreach ($p in $codenames.PSObject.Properties) {
    if ($p.Name -notin @('Xiaomi', 'Oppo', 'Vivo', 'Samsung')) { $final[$p.Name] = $p.Value }
}

$json = [pscustomobject]$final | ConvertTo-Json -Depth 10
Set-Content -LiteralPath $codenamesPath -Value $json -Encoding utf8

Write-Host "Updated: $codenamesPath"
foreach ($b in @('Oppo', 'Vivo', 'Samsung')) {
    $s = $stats[$b]
    Write-Host ("{0}: phone={1} filled={2} soc-skipped={3}" -f $b, $s.phone, $s.filled, $s.skipped)
}

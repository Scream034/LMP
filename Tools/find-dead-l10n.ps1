<#
.SYNOPSIS
    Проверяет локализационные JSON файлы на мёртвые и недостающие ключи.

.PARAMETER Fix
    Удалить мёртвые ключи из JSON файлов автоматически.

.PARAMETER Master
    Мастер-язык (по умолчанию: ru).

.PARAMETER L10nDir
    Путь к папке с JSON файлами относительно корня проекта.

.PARAMETER SourceDirs
    Папки с исходниками через запятую.

.EXAMPLE
    .\Tools\find-dead-l10n.ps1
    .\Tools\find-dead-l10n.ps1 -Fix
    .\Tools\find-dead-l10n.ps1 -Fix -Master en
#>

param(
    [switch]$Fix,
    [string]$Master     = "ru",
    [string]$L10nDir    = "Assets\Localization",
    [string]$SourceDirs = "Core,Features,UI"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Хелперы вывода ───────────────────────────────────────────────────────────

function Write-Dead([string]$location, [string]$key) {
    Write-Host ("  {0,-9} {1,-55} {2}" -f "DEAD", $location, $key) -ForegroundColor DarkYellow
}

function Write-Missing([string]$location, [string]$key) {
    Write-Host ("  {0,-9} {1,-55} {2}" -f "MISSING", $location, $key) -ForegroundColor Red
}

# ── Пути ─────────────────────────────────────────────────────────────────────

$root    = Split-Path $PSScriptRoot -Parent
$l10nDir = Join-Path $root $L10nDir

Write-Host ""
Write-Host "  Project root : $root"    -ForegroundColor Gray
Write-Host "  L10n dir     : $l10nDir" -ForegroundColor Gray
Write-Host "  Master lang  : $Master"  -ForegroundColor Gray

# ── Загрузка JSON ────────────────────────────────────────────────────────────

function Load-Json([string]$path) {
    $json = Get-Content $path -Raw -Encoding UTF8
    $dict = @{}
    $re   = [regex]'"([^"\\]+)"\s*:\s*"((?:[^"\\]|\\.)*)"'
    foreach ($m in $re.Matches($json)) {
        $dict[$m.Groups[1].Value] = $m.Groups[2].Value
    }
    return $dict
}

$jsonFiles = @(Get-ChildItem $l10nDir -Filter "*.json" | Sort-Object Name)
if ($jsonFiles.Count -eq 0) {
    Write-Host "  No JSON files found in $l10nDir" -ForegroundColor Red
    exit 1
}

$langData = @{}
foreach ($f in $jsonFiles) {
    $code            = $f.BaseName
    $langData[$code] = Load-Json $f.FullName
    Write-Host "  Loaded $($f.Name)  ($($langData[$code].Count) keys)" -ForegroundColor Gray
}

if (-not $langData.ContainsKey($Master)) {
    Write-Host "  Master language '$Master' not found" -ForegroundColor Red
    exit 1
}

$masterDict = $langData[$Master]
$allKnownKeys = [System.Collections.Generic.HashSet[string]]::new()
foreach ($k in $masterDict.Keys) { [void]$allKnownKeys.Add($k) }

# ── Сбор исходников ──────────────────────────────────────────────────────────

$allFiles = [System.Collections.Generic.List[string]]::new()
foreach ($dir in ($SourceDirs -split ",")) {
    $full = Join-Path $root $dir.Trim()
    if (Test-Path $full) {
        Get-ChildItem $full -Recurse -Include "*.cs","*.axaml" |
            ForEach-Object { $allFiles.Add($_.FullName) }
    }
}
Get-ChildItem $root -Filter "*.cs"    -File | ForEach-Object { $allFiles.Add($_.FullName) }
Get-ChildItem $root -Filter "*.axaml" -File | ForEach-Object { $allFiles.Add($_.FullName) }

$sourceFiles = $allFiles | Sort-Object -Unique
Write-Host "  Source files : $(@($sourceFiles).Count)  (.cs + .axaml)" -ForegroundColor Gray

# ── Паттерны регулярных выражений ────────────────────────────────────────────

$patterns = @(
    # SL["Key"] / L["Key"] / LocalizationService.Instance["Key"] (включая многострочные пробелы)
    '(?:SL|L|LocalizationService\.Instance)\s*\[\s*"([A-Za-z][A-Za-z0-9_]+)"\s*\]',

    # .Get("Key") / .RawGet("Key") / .GetPlural("Key") (с полной поддержкой переносов строк \s*)
    '\.(?:Get|RawGet|GetPlural)\(\s*"([A-Za-z][A-Za-z0-9_]+)"',

    # {Binding SL[Key]} / {Binding L[Key]} / {Binding Path=SL[Key]}
    '(?:SL|L)\[([A-Za-z][A-Za-z0-9_]+)\]',

    # {l:Loc Key} / {l:Loc Key=Foo}
    '\{l:Loc\s+(?:Key=)?([A-Za-z][A-Za-z0-9_]+)',

    # ShowToastAsync("TitleKey", "MessageKey", ...)
    'ShowToastAsync\(\s*"([A-Za-z][A-Za-z0-9_]+)"\s*,\s*"([A-Za-z][A-Za-z0-9_]+)"',

    # ShowPlaybackErrorAsync("TitleKey", "MessageKey", ...)
    'ShowPlaybackErrorAsync\(\s*"([A-Za-z][A-Za-z0-9_]+)"\s*,\s*"([A-Za-z][A-Za-z0-9_]+)"',

    # titleKey = "Key" / messageKey: "Key" / recommendationKey = "Key"
    '(?i)(?:title|message|recommendation)Key\s*[=:]\s*"([A-Za-z][A-Za-z0-9_]+)"',

    # Одиночные строки, ternary, switch expression
    '^\s*(?:[?:,]|=>)?\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"\s*[,;]?\s*$',
    '[?:]\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"',
    '=>\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"',
    '\(\s*"([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)+)"'
)

$usedKeys = @{}

$dynamicKeys = @(
    "Home_Greeting_Morning", "Home_Greeting_Afternoon", "Home_Greeting_Evening",
    "NetProfile_Low", "NetProfile_Medium", "NetProfile_High", "NetProfile_Ultra",
    "AudioQuality_BestAvailable", "AudioQuality_Standard",
    "Client_AndroidVR", "Client_TV", "Client_Web",
    "Cache_Low", "Cache_Medium", "Cache_High",
    "VolumeCurve_Linear", "VolumeCurve_Quadratic", "VolumeCurve_Logarithmic", "VolumeCurve_Cubic", "VolumeCurve_SpeedOfLight",
    "CloseAction_Exit", "CloseAction_MinimizeToTray", "CloseAction_Ask"
)

foreach ($key in $dynamicKeys) {
    if (-not $usedKeys.ContainsKey($key)) {
        $usedKeys[$key] = "dynamic (whitelist)"
    }
}

function Test-IsL10nKey([string]$key) {
    if (-not $key.Contains('_')) { return $false }
    $upper = $key.ToUpperInvariant()
    if ($key -ceq $upper) { return $false }
    if (-not [char]::IsUpper($key[0])) { return $false }
    return $true
}

$pluralSuffixes = @("_0","_1","_2","_3","_4","_5","_one","_few","_many","_other","_zero")

function Test-PluralSuffix([string]$key) {
    foreach ($s in $pluralSuffixes) {
        if ($key.EndsWith($s)) { return $true }
    }
    return $false
}

# ── Сканирование исходников (Многострочное с поддержкой -Raw) ─────────────────

foreach ($filePath in $sourceFiles) {
    $relPath = $filePath.Substring($root.Length).TrimStart('\')
    
    # Считываем весь файл целиком как единую строку
    $content = Get-Content $filePath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($content)) { continue }

    foreach ($pattern in $patterns) {
        # Применяем регулярные выражения с флагом Multiline
        $matches = [regex]::Matches($content, $pattern, 'Multiline')
        foreach ($m in $matches) {
            # Вычисляем точный номер строки по индексу совпадения в тексте
            $lineNum = [regex]::Matches($content.Substring(0, $m.Index), "\r?\n").Count + 1

            for ($g = 1; $g -lt $m.Groups.Count; $g++) {
                $key = $m.Groups[$g].Value.Trim()
                if ($key.Length -gt 2 -and -not $usedKeys.ContainsKey($key)) {
                    $usedKeys[$key] = "${relPath}:${lineNum}"
                }
            }
        }
    }
}

Write-Host "  Unique keys referenced in code : $($usedKeys.Count)" -ForegroundColor Gray

# ── Анализ ────────────────────────────────────────────────────────────────────

$deadKeys    = [System.Collections.Generic.List[string]]::new()
$missingKeys = [System.Collections.Generic.List[string]]::new()
$pluralFP    = 0

foreach ($key in @($masterDict.Keys)) {
    if (-not $usedKeys.ContainsKey($key) -and -not (Test-PluralSuffix $key)) {
        $deadKeys.Add($key)
    }
}

foreach ($key in $usedKeys.Keys) {
    if (-not $masterDict.ContainsKey($key)) {
        if (Test-PluralSuffix $key) {
            $pluralFP++
        } elseif (Test-IsL10nKey $key) {
            $missingKeys.Add($key)
        }
    }
}

$syncIssues = 0
foreach ($code in $langData.Keys) {
    if ($code -eq $Master) { continue }
    $other = $langData[$code]
    foreach ($key in @($masterDict.Keys)) {
        if (-not $other.ContainsKey($key)) {
            Write-Host "  [!!] $code.json missing key: $key" -ForegroundColor Yellow
            $syncIssues++
        }
    }
    foreach ($key in @($other.Keys)) {
        if (-not $masterDict.ContainsKey($key)) {
            Write-Host "  [!!] $code.json has extra key: $key" -ForegroundColor Yellow
            $syncIssues++
        }
    }
}

# ── Репорт: мёртвые ──────────────────────────────────────────────────────────

if ($deadKeys.Count -gt 0) {
    Write-Host ""
    Write-Host ("-- Dead keys ({0}) " -f $deadKeys.Count).PadRight(60, '-') -ForegroundColor DarkYellow
    Write-Host "   In $Master.json but never referenced in source code." -ForegroundColor Gray
    Write-Host ""
    Write-Host ("  {0,-9} {1,-55} {2}" -f "STATUS", "LOCATION", "KEY") -ForegroundColor DarkGray

    $jsonLines = @(Get-Content (Join-Path $l10nDir "$Master.json") -Encoding UTF8)
    foreach ($key in $deadKeys) {
        $lineNum = 0
        for ($i = 0; $i -lt $jsonLines.Count; $i++) {
            if ($jsonLines[$i] -match ('"' + [regex]::Escape($key) + '"')) {
                $lineNum = $i + 1; break
            }
        }
        $loc = if ($lineNum -gt 0) { "$Master.json:$lineNum" } else { "$Master.json" }
        Write-Dead $loc $key
    }
}

# ── Репорт: недостающие ───────────────────────────────────────────────────────

if ($missingKeys.Count -gt 0) {
    Write-Host ""
    Write-Host ("-- Missing keys ({0}) " -f $missingKeys.Count).PadRight(60, '-') -ForegroundColor Red
    Write-Host "   Referenced in code but absent from $Master.json." -ForegroundColor Gray
    Write-Host ""
    Write-Host ("  {0,-9} {1,-55} {2}" -f "STATUS", "LOCATION", "KEY") -ForegroundColor DarkGray

    foreach ($key in ($missingKeys | Sort-Object)) {
        $loc = if ($usedKeys.ContainsKey($key)) { $usedKeys[$key] } else { "unknown" }
        Write-Missing $loc $key
    }
}

# ── Репорт: plural FP ────────────────────────────────────────────────────────

if ($pluralFP -gt 0) {
    Write-Host ""
    Write-Host ("-- Plural false positives skipped ({0}) " -f $pluralFP).PadRight(60, '-') -ForegroundColor DarkGray
    Write-Host "   Auto-generated GetPlural suffixes (handled by fallback)." -ForegroundColor Gray
}

# ── Fix mode ─────────────────────────────────────────────────────────────────

if ($Fix -and $deadKeys.Count -gt 0) {
    Write-Host ""
    Write-Host "-- Applying fixes " -ForegroundColor Cyan

    foreach ($code in $langData.Keys) {
        $dict    = $langData[$code]
        $removed = 0

        foreach ($key in $deadKeys) {
            if ($dict.ContainsKey($key)) {
                $dict.Remove($key)
                $removed++
            }
        }

        if ($removed -gt 0) {
            $filePath   = Join-Path $l10nDir "$code.json"
            $origLines  = @(Get-Content $filePath -Encoding UTF8)
            $deadSet    = [System.Collections.Generic.HashSet[string]]::new()
            foreach ($k in $deadKeys) { [void]$deadSet.Add($k) }

            $outLines = [System.Collections.Generic.List[string]]::new()
            $prevWasRemoved = $false

            foreach ($line in $origLines) {
                $skip = $false
                foreach ($k in $deadSet) {
                    if ($line -match ('"' + [regex]::Escape($k) + '"\s*:')) {
                        $skip = $true; break
                    }
                }

                if ($skip) {
                    $prevWasRemoved = $true
                    continue
                }

                if ($prevWasRemoved -and $line.Trim() -eq '') {
                    $prevWasRemoved = $false
                    continue
                }

                $prevWasRemoved = $false
                $outLines.Add($line)
            }

            $result = [System.Collections.Generic.List[string]]::new($outLines)
            for ($i = $result.Count - 1; $i -ge 0; $i--) {
                $trimmed = $result[$i].Trim()
                if ($trimmed -eq '}') { continue }
                if ($trimmed -eq '') { continue }
                if ($result[$i] -match ',\s*$') {
                    $result[$i] = $result[$i] -replace ',\s*$', ''
                }
                break
            }

            [System.IO.File]::WriteAllLines($filePath, $result, [System.Text.Encoding]::UTF8)
            Write-Host "  [FIX] Removed $removed dead keys from $code.json" -ForegroundColor Green
        }
    }
}

# ── Итог ─────────────────────────────────────────────────────────────────────

Write-Host ""
if ($syncIssues -eq 0) {
    Write-Host "  [OK] All language files are in sync." -ForegroundColor Green
} else {
    Write-Host "  [!!] Language files are OUT OF SYNC." -ForegroundColor Red
}

Write-Host ""
Write-Host ("-" * 53) -ForegroundColor DarkGray
Write-Host "  Summary"                                                                               -ForegroundColor White
Write-Host "    Master keys  ($Master)  : $($masterDict.Count)"                                     -ForegroundColor Gray
Write-Host "    Keys in code       : $($usedKeys.Count)"                                            -ForegroundColor Gray
Write-Host "    Dead keys          : $($deadKeys.Count)"    -ForegroundColor $(if ($deadKeys.Count    -eq 0) { "Green" } else { "DarkYellow" })
Write-Host "    Missing keys       : $($missingKeys.Count)" -ForegroundColor $(if ($missingKeys.Count -eq 0) { "Green" } else { "Red" })
Write-Host "    Plural FP skipped  : $pluralFP"             -ForegroundColor Gray
Write-Host "    Sync issues        : $syncIssues"           -ForegroundColor $(if ($syncIssues         -eq 0) { "Green" } else { "Red" })
Write-Host ""

exit ($deadKeys.Count + $missingKeys.Count + $syncIssues)
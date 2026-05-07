# find-dead-code.ps1
param(
    [string]$Project  = "",
    [switch]$FailOnAny
)

# ── 1. Найти .csproj ──────────────────────────────────────────────────────────
if (-not $Project) {
    $projectRoot = Split-Path $PSScriptRoot -Parent
    $found = Get-ChildItem -Path $projectRoot -Filter *.csproj | Select-Object -First 1
    if (-not $found) {
        Write-Host "  ERROR: .csproj not found." -ForegroundColor Red
        exit 1
    }
    $Project = $found.FullName
}
Write-Host "`n  Project : $Project" -ForegroundColor DarkGray

# ── 2. Проверить наличие Roslynator CLI ───────────────────────────────────────
if (-not (Get-Command "roslynator" -ErrorAction SilentlyContinue)) {
    Write-Host "`n  Roslynator CLI not found. Installing...`n" -ForegroundColor Yellow
    dotnet tool install -g roslynator.dotnet.cli
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ERROR: Failed to install Roslynator." -ForegroundColor Red
        exit 1
    }
}

# ── 3. Получить путь к NuGet кэшу ────────────────────────────────────────────
$nugetLocalsOutput = dotnet nuget locals global-packages --list 2>$null
$nugetCache = $null
foreach ($line in $nugetLocalsOutput) {
    if ($line -match "global-packages:\s*(.+)") {
        $nugetCache = $Matches[1].Trim().TrimEnd('\').TrimEnd('/')
        break
    }
}
if (-not $nugetCache -or -not (Test-Path $nugetCache)) {
    $nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"
}
Write-Host "  NuGet cache : $nugetCache" -ForegroundColor DarkGray

# ── 4. Найти директорию анализаторов ─────────────────────────────────────────
# Структура: roslynator.analyzers\<ver>\analyzers\dotnet\roslyn4.7\cs\
# Ищем Roslynator.CSharp.Analyzers.dll, берём папку с максимальной версией Roslyn
function Find-AnalyzerDir {
    param([string]$CacheRoot)

    $dlls = Get-ChildItem -Path $CacheRoot -Recurse -Filter "Roslynator.CSharp.Analyzers.dll" `
                -ErrorAction SilentlyContinue

    if (-not $dlls) { return $null }

    # PS 5.1 совместимая сортировка — без ?. и без [0]?.Property
    $best = $dlls | Sort-Object {
        if ($_.FullName -match "roslyn(\d+)\.(\d+)") {
            [int]$Matches[1] * 100 + [int]$Matches[2]
        } else { 0 }
    } -Descending | Select-Object -First 1

    if ($best) { return $best.DirectoryName }
    return $null
}

$analyzerDir = Find-AnalyzerDir $nugetCache

# Если не нашли — скачиваем через временный проект (основной .csproj не трогаем)
if (-not $analyzerDir) {
    Write-Host "`n  Roslynator.Analyzers not in cache. Downloading...`n" -ForegroundColor Yellow

    $tempDir  = Join-Path $env:TEMP ("roslynator-fetch-" + (Get-Random))
    $tempProj = Join-Path $tempDir "fetch.csproj"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    $csprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Roslynator.Analyzers" Version="*" />
  </ItemGroup>
</Project>
"@
    Set-Content -Path $tempProj -Value $csprojContent

    dotnet restore $tempProj 2>$null
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

    $analyzerDir = Find-AnalyzerDir $nugetCache
}

if (-not $analyzerDir) {
    Write-Host "  ERROR: Cannot find Roslynator.CSharp.Analyzers.dll" -ForegroundColor Red
    Write-Host "  Cache: $nugetCache" -ForegroundColor DarkGray
    exit 1
}

Write-Host "  Analyzers   : $analyzerDir" -ForegroundColor DarkGray

# ── 5. Диагностики ────────────────────────────────────────────────────────────
$diagnostics = @("RCS1213", "RCS1170")

Write-Host "`n=== Dead Code Analysis ===`n" -ForegroundColor Cyan
Write-Host "  Diagnostics : $($diagnostics -join ', ')`n" -ForegroundColor DarkGray

# ── 6. Запуск ─────────────────────────────────────────────────────────────────
$roslynatorArgs = @(
    "analyze",
    $Project,
    "--analyzer-assemblies", $analyzerDir,
    "--supported-diagnostics", ($diagnostics -join " "),
    "--severity-level", "info"
)

$rawLines       = & roslynator @roslynatorArgs 2>$null
$roslynatorExit = $LASTEXITCODE

# ── 7. Парсинг stdout ─────────────────────────────────────────────────────────
$issueCount = 0
$issueLines = New-Object System.Collections.ArrayList

foreach ($line in $rawLines) {
    if ($line -match '(warning|error)\s+(RCS\d+|IDE\d+)') {
        [void]$issueLines.Add($line)
        $issueCount++
    }
}

# ── 8. Вывод ──────────────────────────────────────────────────────────────────
if ($issueCount -gt 0) {
    Write-Host "── Issues found ──────────────────────────────────────" -ForegroundColor Red

    foreach ($line in $issueLines) {
        if ($line -match '^(.*?)\((\d+),\d+\):\s*(?:warning|error)\s+(\w+):\s*(.*)$') {
            $filePart = $Matches[1].Trim()
            $lineNum  = $Matches[2]
            $diagId   = $Matches[3]
            $msg      = $Matches[4].Trim()
            Write-Host "  [$diagId] " -ForegroundColor Red -NoNewline
            Write-Host "${filePart}:${lineNum}" -ForegroundColor Cyan -NoNewline
            Write-Host "  $msg" -ForegroundColor Yellow
        } else {
            Write-Host "  $line" -ForegroundColor Yellow
        }
    }

    Write-Host "`n  Found $issueCount dead code issue(s)" -ForegroundColor Red
} else {
    if ($roslynatorExit -ne 0) {
        Write-Host "  Roslynator exited with code $roslynatorExit" -ForegroundColor Yellow
        $rawLines | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    } else {
        Write-Host "  No dead code found!" -ForegroundColor Green
    }
}

Write-Host ""

if ($FailOnAny -and $issueCount -gt 0) { exit 1 }
exit 0
# find-dead-events.ps1
# $PSScriptRoot = .../Tools/ — нужно подняться в корень проекта
$root = Split-Path $PSScriptRoot -Parent

$files = Get-ChildItem -Path $root -Recurse -Include *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|External)\\' }

$allContent = @{}
foreach ($file in $files) {
    $allContent[$file.FullName] = Get-Content $file -Raw
}

$events = @()
foreach ($file in $files) {
    $lines = Get-Content $file
    $relPath = $file.FullName.Substring($root.Length + 1)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '\bevent\s+\S+\s+(\w+)\s*;') {
            $events += [PSCustomObject]@{
                Name = $Matches[1]
                File = $relPath
                Line = $i + 1
            }
        }
    }
}

$events = $events | Sort-Object Name -Unique

Write-Host "`n=== Checking $($events.Count) events ===`n"

$deadCount = 0
foreach ($evt in $events) {
    $subCount = 0
    foreach ($file in $files) {
        $content = $allContent[$file.FullName]
        $subCount += ([regex]::Matches($content, "\b$($evt.Name)\s*\+=")).Count
        $subCount += ([regex]::Matches($content, "\b$($evt.Name)\s*\+=\s*h\b")).Count
    }

    $invokeCount = 0
    foreach ($file in $files) {
        $content = $allContent[$file.FullName]
        $invokeCount += ([regex]::Matches($content, "\b$($evt.Name)\s*\?\.\s*Invoke")).Count
        $invokeCount += ([regex]::Matches($content, "\b$($evt.Name)\s*\(\s*")).Count
    }

    if ($subCount -eq 0) {
        $deadCount++
        Write-Host "  DEAD  " -ForegroundColor Red -NoNewline
        Write-Host "$($evt.File):$($evt.Line)" -ForegroundColor Cyan -NoNewline
        Write-Host "  $($evt.Name)" -ForegroundColor Yellow
    }
}

if ($deadCount -eq 0) {
    Write-Host "  All events have subscribers!" -ForegroundColor Green
} else {
    Write-Host "`n  Found $deadCount dead events" -ForegroundColor Red
}
Write-Host ""

exit $deadCount
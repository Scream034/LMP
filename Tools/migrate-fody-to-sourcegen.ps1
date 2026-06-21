<#
.SYNOPSIS
    Migrates [Reactive] properties from ReactiveUI.Fody to ReactiveUI.SourceGenerators.
.DESCRIPTION
    - Adds 'partial' to all [Reactive] property declarations.
    - Adds 'partial' to the DIRECTLY containing class/record declaration only,
      using brace-depth tracking to avoid false positives on nested types
      (e.g. 'private record struct' inside the target class).
    - Handles combined attributes: [JsonIgnore, Reactive], [Reactive, JsonIgnore].
    - Removes 'using ReactiveUI.Fody.*' directives.
    - Safe to run multiple times (idempotent).
.PARAMETER Root
    Root directory of the solution. Defaults to parent of Tools/.
.PARAMETER DryRun
    Preview changes without modifying files.
.EXAMPLE
    .\Tools\migrate-fody-to-sourcegen.ps1 -DryRun
    .\Tools\migrate-fody-to-sourcegen.ps1
#>
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  Fody -> SourceGenerators Migration Tool     " -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host ""

if ($DryRun) {
    Write-Host "[MODE] Dry Run - no files will be modified" -ForegroundColor Yellow
    Write-Host ""
}

# ── Collect .cs files (no regex character classes - use Split to avoid PS quirk) ──
$files = Get-ChildItem -Path $Root -Filter "*.cs" -Recurse | Where-Object {
    $segments = $_.FullName.Split([System.IO.Path]::DirectorySeparatorChar)
    -not ($segments -contains 'bin' -or $segments -contains 'obj')
} | Where-Object {
    $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
    $content -and ($content -match 'ReactiveUI\.Fody' -or $content -match '\[.*\bReactive\b.*\]\s*public\s')
}

$totalPropertyChanges = 0
$totalClassChanges    = 0
$totalUsingRemovals   = 0
$filesModified        = 0

foreach ($file in $files) {
    $lines   = [System.IO.File]::ReadAllLines($file.FullName)
    $changed = $false

    # ── Build brace-depth map ──
    # lineDepths[i] = nesting depth BEFORE processing line i.
    # File-scoped namespaces (namespace X;) add no depth.
    # Traditional namespace { } blocks add 1.
    # This correctly handles all real-world layouts.
    $lineDepths = [int[]]::new($lines.Count)
    $depth = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $lineDepths[$i] = $depth
        $open  = ([regex]::Matches($lines[$i], '\{')).Count
        $close = ([regex]::Matches($lines[$i], '\}')).Count
        $depth += ($open - $close)
    }

    # ── Collect line indices by category ──
    $classLineIndices    = [System.Collections.Generic.List[int]]::new()
    $reactiveLineIndices = [System.Collections.Generic.List[int]]::new()
    $fodyUsingIndices    = [System.Collections.Generic.List[int]]::new()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        # [Reactive] property — standalone or in combined attribute list
        if ($line -match '\[.*\bReactive\b.*\]\s*public\s') {
            $reactiveLineIndices.Add($i)
        }

        # class / record / struct declarations (any access modifier)
        if ($line -match '^\s*(public|internal|private|protected)\s+.*\b(class|record|struct)\s+\w+') {
            $classLineIndices.Add($i)
        }

        # ReactiveUI.Fody using directives
        if ($line -match '^\s*using\s+ReactiveUI\.Fody') {
            $fodyUsingIndices.Add($i)
        }
    }

    # ── Step 1: Mark Fody using lines for removal ──
    foreach ($i in $fodyUsingIndices) {
        $lines[$i] = $null
        $totalUsingRemovals++
        $changed = $true
    }

    # ── Step 2: Add 'partial' to [Reactive] property declarations ──
    foreach ($i in $reactiveLineIndices) {
        $line = $lines[$i]
        if ($null -ne $line -and $line -match '\[.*\bReactive\b.*\]\s*public\s+(?!partial\b)') {
            $lines[$i] = $line -replace '(\[.*\bReactive\b.*\]\s*public\s+)(?!partial\b)', '$1partial '
            $totalPropertyChanges++
            $changed = $true
        }
    }

    # ── Step 3: Add 'partial' to the directly containing class declaration ──
    #
    # Key invariant: a [Reactive] property at brace-depth D is directly contained by
    # the class/record/struct declaration at brace-depth (D-1).
    #
    # Example (file-scoped namespace, no extra braces):
    #   public sealed class AudioEngine {      <- lineDepth=0, opens { -> depth 1
    #       record struct ContinuationUrlResult(...)  <- lineDepth=1, no body braces
    #       [Reactive] public partial TrackInfo? CurrentTrack  <- lineDepth=1
    #
    # For [Reactive] at depth 1: containingClass must be at depth 1-1=0.
    #   AudioEngine:            lineDepth=0 == 0  -> MATCH (patched)
    #   ContinuationUrlResult:  lineDepth=1 != 0  -> SKIP (correctly ignored)
    #
    # This is robust for: file-scoped namespaces, traditional namespace{} blocks,
    # nested classes, and record structs with primary constructors (no body braces).

    $patchedClasses = [System.Collections.Generic.HashSet[int]]::new()

    foreach ($rLine in $reactiveLineIndices) {
        $reactiveDepth    = $lineDepths[$rLine]
        $targetClassDepth = $reactiveDepth - 1
        $containingIdx    = -1

        foreach ($cLine in $classLineIndices) {
            if ($cLine -lt $rLine -and $lineDepths[$cLine] -eq $targetClassDepth) {
                # Keep iterating to find the NEAREST preceding match
                $containingIdx = $cLine
            }
        }

        if ($containingIdx -ge 0 -and -not $patchedClasses.Contains($containingIdx)) {
            $classDecl = $lines[$containingIdx]
            if ($null -ne $classDecl -and $classDecl -notmatch '\bpartial\b') {
                $lines[$containingIdx] = $classDecl -replace '\b(class|record|struct)\b', 'partial $1'
                $patchedClasses.Add($containingIdx) | Out-Null
                $totalClassChanges++
                $changed = $true
            }
        }
    }

    # ── Write ──
    if ($changed) {
        # Filter out lines marked $null (removed Fody usings)
        $outputLines = $lines | Where-Object { $null -ne $_ }
        $rel = $file.FullName.Substring($Root.Length).TrimStart([char]'\', [char]'/')

        if ($DryRun) {
            Write-Host "  [DRY]  $rel" -ForegroundColor Yellow
        }
        else {
            $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
            [System.IO.File]::WriteAllLines($file.FullName, $outputLines, $utf8NoBom)
            Write-Host "  [OK]   $rel" -ForegroundColor Green
        }
        $filesModified++
    }
}

# ── Summary ──
Write-Host ""
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host "  Files:       $filesModified" -ForegroundColor White
Write-Host "  Properties:  $totalPropertyChanges  (+partial)" -ForegroundColor White
Write-Host "  Classes:     $totalClassChanges  (+partial)" -ForegroundColor White
Write-Host "  Usings:      $totalUsingRemovals  (removed Fody)" -ForegroundColor White
Write-Host "===============================================" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "  Dry run - no files were modified" -ForegroundColor Yellow
}
else {
    Write-Host "  Done. Run dotnet build to verify." -ForegroundColor Green
}
Write-Host ""
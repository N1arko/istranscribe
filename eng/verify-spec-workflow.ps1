[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot ".."))
)

$ErrorActionPreference = "Stop"
$failures = [System.Collections.Generic.List[string]]::new()

function Add-Failure([string]$Message) {
    $failures.Add($Message)
}

function Get-SpecFiles {
    Get-ChildItem -Path (Join-Path $Root "specs") -Recurse -File -Filter "*.md" |
        Where-Object {
            $_.FullName -match '[\\/]specs[\\/](common|modules)[\\/]' -and
            $_.BaseName -match '^(PROP|FEAT|INFRA)-\d+(?:\.[A-Z])?'
        }
}

$specFiles = @(Get-SpecFiles)
$specIds = @{}
foreach ($file in $specFiles) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    if ($content -notmatch '(?s)^---\s*\r?\nstatus:\s*(draft|active|superseded|retired)\s*\r?\n---') {
        Add-Failure "Missing or invalid lifecycle frontmatter: $($file.FullName)"
    }

    $title = [regex]::Match($content, '(?m)^#\s+(?<id>(PROP|FEAT|INFRA)-\d+(?:\.[A-Z])?):')
    if (-not $title.Success) {
        Add-Failure "Missing canonical title and ID: $($file.FullName)"
        continue
    }

    $id = $title.Groups['id'].Value
    if ($specIds.ContainsKey($id)) {
        Add-Failure "Duplicate spec ID $id in $($file.FullName) and $($specIds[$id])"
    } else {
        $specIds[$id] = $file.FullName
    }
}

$specMap = Get-Content -Raw -LiteralPath (Join-Path $Root "specs/SPEC-MAP.md")
foreach ($id in $specIds.Keys) {
    if ($specMap -notmatch [regex]::Escape("[$id]")) {
        Add-Failure "SPEC-MAP does not register $id"
    }
}

$board = Get-Content -Raw -LiteralPath (Join-Path $Root "specs/BOARD.md")
$boardWi = [regex]::Matches($board, '\[WI-(\d+)\]\((?<path>work/(?:archive/\d{4}/)?WI-\d+-[^)]+\.md)\)')
$seenBoardWi = @{}
foreach ($match in $boardWi) {
    $wi = "WI-$($match.Groups[1].Value)"
    if ($seenBoardWi.ContainsKey($wi)) {
        Add-Failure "BOARD lists $wi more than once"
    }
    $seenBoardWi[$wi] = $true
    if (-not (Test-Path -LiteralPath (Join-Path $Root "specs/$($match.Groups['path'].Value)"))) {
        Add-Failure "BOARD points to missing work item $($match.Groups['path'].Value)"
    }
}

$wal = Get-Content -Raw -LiteralPath (Join-Path $Root "specs/WAL.md")
foreach ($match in [regex]::Matches($wal, '(?m)^###\s+(WI-\d+):.*\r?\n- Work: \[WI-\d+\]\((?<path>work/WI-\d+-[^)]+\.md)\)')) {
    $wi = $match.Groups[1].Value
    if (-not $seenBoardWi.ContainsKey($wi)) {
        Add-Failure "WAL checkpoint $wi is absent from BOARD"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Root "specs/$($match.Groups['path'].Value)"))) {
        Add-Failure "WAL points to missing work item $($match.Groups['path'].Value)"
    }
}

$referenceFiles = @(
    Get-ChildItem -Path (Join-Path $Root "src"), (Join-Path $Root "tests"), (Join-Path $Root "specs") -Recurse -File |
        Where-Object { $_.Extension -in ".cs", ".csproj", ".axaml", ".xaml", ".md", ".ps1" -and $_.FullName -notmatch '[\\/]specs[\\/](history|protocols|work)[\\/]' }
)
foreach ($file in $referenceFiles) {
    $content = Get-Content -Raw -LiteralPath $file.FullName
    foreach ($reference in [regex]::Matches($content, 'spec://(?<path>[A-Za-z0-9_./-]+)#(?<anchor>[A-Za-z0-9_.-]+)')) {
        if ($reference.Groups['path'].Value -eq '...') {
            continue
        }
        $target = Join-Path $Root "specs/$($reference.Groups['path'].Value).md"
        if (-not (Test-Path -LiteralPath $target)) {
            Add-Failure "Missing spec target $($reference.Value) from $($file.FullName)"
            continue
        }
        $targetContent = Get-Content -Raw -LiteralPath $target
        if ($targetContent -notmatch [regex]::Escape("{#$($reference.Groups['anchor'].Value)}")) {
            Add-Failure "Missing spec anchor $($reference.Value) from $($file.FullName)"
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Spec workflow verification passed: $($specIds.Count) specs, $($seenBoardWi.Count) BOARD work items."

<#
.SYNOPSIS
Turns a Cobertura coverage report into a Markdown summary.

.DESCRIPTION
Reads the first coverage.cobertura.xml under -Path and writes a Markdown summary:
total and per-assembly line and branch coverage, and the least covered source files.
The summary is a report only; the script never fails the build because of low coverage.

.PARAMETER Path
Folder that holds the test results (searched recursively for coverage.cobertura.xml).

.PARAMETER Out
File the Markdown is appended to. In GitHub Actions pass $env:GITHUB_STEP_SUMMARY.
When empty, the Markdown is written to standard output.

.PARAMETER Lowest
How many of the least covered files to list.

.PARAMETER MinLines
Files with fewer coverable lines than this are left out of the least covered list.

.OUTPUTS
Exit code 0 when a summary was written, 1 when no coverage file was found.
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [string] $Out = '',
    [int] $Lowest = 10,
    [int] $MinLines = 20
)

$ErrorActionPreference = 'Stop'

$file = Get-ChildItem -Path $Path -Recurse -Filter 'coverage.cobertura.xml' | Select-Object -First 1
if (-not $file) {
    Write-Error "No coverage.cobertura.xml under $Path"
    exit 1
}

[xml] $report = Get-Content -LiteralPath $file.FullName -Raw
$coverage = $report.coverage
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Format-Percent([double] $covered, [double] $valid) {
    if ($valid -eq 0) { return 'n/a' }
    return (100.0 * $covered / $valid).ToString('0.0', $invariant) + ' %'
}

function Format-Rate([string] $rate) {
    return (100.0 * [double]::Parse($rate, $invariant)).ToString('0.0', $invariant) + ' %'
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('## Code coverage')
$lines.Add('')
$lines.Add('Report only, no threshold. Unit and in-process end-to-end tests cover the Core and Infrastructure assemblies; the WPF app is checked by the UI scripts instead.')
$lines.Add('')
$lines.Add('| Assembly | Lines | Branches |')
$lines.Add('|---|---:|---:|')
$lines.Add("| **Total** | **$(Format-Percent $coverage.'lines-covered' $coverage.'lines-valid')** ($($coverage.'lines-covered') / $($coverage.'lines-valid')) | **$(Format-Percent $coverage.'branches-covered' $coverage.'branches-valid')** |")
foreach ($package in $coverage.packages.package) {
    $lines.Add("| $($package.name) | $(Format-Rate $package.'line-rate') | $(Format-Rate $package.'branch-rate') |")
}

# A file can hold several classes (nested, partial, compiler-made); count each source line once.
$byFile = @{}
foreach ($class in $coverage.packages.package.classes.class) {
    $name = $class.filename -replace '\\', '/'
    if (-not $byFile.ContainsKey($name)) { $byFile[$name] = @{} }
    foreach ($line in $class.lines.line) {
        $number = [int] $line.number
        $hit = [int64] $line.hits -gt 0
        $byFile[$name][$number] = $byFile[$name][$number] -or $hit
    }
}

$files = foreach ($entry in $byFile.GetEnumerator()) {
    $valid = $entry.Value.Count
    $covered = @($entry.Value.Values | Where-Object { $_ }).Count
    if ($valid -ge $MinLines) {
        [pscustomobject] @{ File = $entry.Key; Covered = $covered; Valid = $valid; Rate = $covered / $valid }
    }
}

$least = @($files | Sort-Object Rate, @{ Expression = 'Valid'; Descending = $true } | Select-Object -First $Lowest)
if ($least.Count -gt 0) {
    $lines.Add('')
    $lines.Add("### Least covered files (at least $MinLines coverable lines)")
    $lines.Add('')
    $lines.Add('| File | Lines | Covered |')
    $lines.Add('|---|---:|---:|')
    foreach ($row in $least) {
        $lines.Add("| ``$($row.File)`` | $(Format-Percent $row.Covered $row.Valid) | $($row.Covered) / $($row.Valid) |")
    }
}

$markdown = ($lines -join "`n") + "`n"
if ($Out) {
    [IO.File]::AppendAllText($Out, $markdown, (New-Object Text.UTF8Encoding $false))
} else {
    $markdown
}
exit 0

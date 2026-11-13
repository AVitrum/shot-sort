param(
    # Also run the slow scenarios: store (CLIP model, ~3 min) and installed-run (self-contained publish, ~1 min).
    [switch]$Slow
)
# Runs the UI scenarios one after another, each in its own PowerShell, and exits with the number of failed scripts.
# Needs a desktop session (UI Automation) and a build: dotnet build ScreenSorter.slnx
$ErrorActionPreference = 'Stop'
$scripts = @('hold', 'history', 'privacy', 'status', 'tray')
if ($Slow) { $scripts += @('store', 'installed-run') }

$failed = @()
foreach ($name in $scripts) {
    Write-Output "=== $name"
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "$name.ps1")
    if ($LASTEXITCODE -ne 0) { $failed += $name }
}
Write-Output ''
if ($failed.Count -eq 0) { Write-Output "all $($scripts.Count) scenarios passed" }
else { Write-Output ("failed: " + ($failed -join ', ')) }
exit $failed.Count

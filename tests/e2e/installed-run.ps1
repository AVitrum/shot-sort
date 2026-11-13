# Publishes the app self-contained, as the installer ships it, and runs that: proves it starts without an installed
# .NET runtime, ships DirectML, creates a missing watch folder and sorts. About 1 minute.
. "$PSScriptRoot\common.ps1"
Stop-App
$publish = Join-Path $E2ERoot 'run\publish'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish -Confirm:$false }
& dotnet publish (Join-Path $RepoRoot 'src\ScreenSorter.App') -c Release -r win-x64 --self-contained true -p:PlatformTarget=x64 -o $publish -v q -nologo 2>&1 | Select-Object -Last 3
$script:Exe = Join-Path $publish 'ScreenSorter.App.exe'
Check 'publish produced an exe' (Test-Path $Exe)
Check 'DirectML shipped' (Test-Path (Join-Path $publish 'DirectML.dll'))
$root = New-Sandbox 'installed'
Remove-Item "$root\shots" -Recurse -Force
$p = Start-App $root
$guide = Find-Window $p.Id 'How ScreenSorter works' 30
Check 'guide opens from the published build' ($null -ne $guide)
Check 'missing watch folder was created' (Wait-Until { Test-Path "$root\shots" })
Invoke-Named $guide 'Skip'
New-Png "$root\shots\blank.png" 0
Check 'sorts a new file' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 1 } 20)
Stop-App
Complete

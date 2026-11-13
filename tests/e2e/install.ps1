param([Parameter(Mandatory)][string]$Setup)
# Silent install to a throwaway folder, a run of the installed app beside any real one, then a silent uninstall.
# Needs an installer built with: installer/build-installer.ps1 -SmokeTest (no shortcuts, Run key or running-app check).
. "$PSScriptRoot\common.ps1"

$root = Join-Path $E2ERoot 'run\install'
if (Test-Path $root) { Remove-Item -Recurse -Force $root -Confirm:$false }
$dir = Join-Path $root 'app'
New-Item -ItemType Directory -Force $root | Out-Null

$runBefore = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').PSObject.Properties.Name -join ','
# A real ScreenSorter install owns this key; a smoke install must neither create nor change it.
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6C2F4C57-1B7E-4E0B-9D35-3B4B7E8A2F11}_is1'
function Get-UninstallEntry { if (Test-Path $uninstallKey) { (Get-ItemProperty $uninstallKey | ConvertTo-Json -Compress) } else { 'none' } }
$entryBefore = Get-UninstallEntry
$p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"", "/LOG=`"$root\setup.log`"" -PassThru -Wait
Check 'installer exit code 0' ($p.ExitCode -eq 0)
$installed = Join-Path $dir 'ScreenSorter.App.exe'
Check 'exe installed' (Test-Path $installed)
foreach ($f in 'DirectML.dll', 'onnxruntime.dll', 'e_sqlite3.dll', 'appsettings.json', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'coreclr.dll', 'PresentationFramework.dll') {
    Check "installed $f" (Test-Path (Join-Path $dir $f))
}
Write-Output ("install size MB: " + [int]((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
Write-Output ("exe version: " + (Get-Item $installed).VersionInfo.ProductVersion)

# The installed build, with its own instance name and sandbox paths, so it runs even beside a real ScreenSorter.
$script:Exe = $installed
$sandbox = New-Sandbox 'install-run'
New-Item -ItemType File "$sandbox\data\guide-seen" | Out-Null
$run = Start-App $sandbox
New-Png "$sandbox\shots\blank.png" 0
Check 'installed app sorts a new file' (Wait-Until { (Get-ChildItem "$sandbox\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 1 } 30)
Stop-App
Check 'installed app stopped' ($run.HasExited)

$uninstaller = Join-Path $dir 'unins000.exe'
Check 'uninstaller present' (Test-Path $uninstaller)
$u = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
Check 'uninstaller exit code 0' ($u.ExitCode -eq 0)
Check 'files removed' (Wait-Until { -not (Test-Path $installed) } 20)
$runAfter = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').PSObject.Properties.Name -join ','
Check 'Run key unchanged' ($runBefore -eq $runAfter)
Check 'uninstall registry entry unchanged' ($entryBefore -eq (Get-UninstallEntry))
Complete

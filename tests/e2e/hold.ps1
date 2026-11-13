. "$PSScriptRoot\common.ps1"
Stop-App

# Fresh install, guide closed with Skip.
$root = New-Sandbox 'hold1'
New-Png "$root\shots\blank.png" 0
$p = Start-App $root
$guide = Find-Window $p.Id 'How ScreenSorter works'
Check 'guide opens on first run' ($null -ne $guide)
Check 'guide says nothing moves yet' ($null -ne (Find-Named $guide 'Nothing moves until you close this guide'))
Start-Sleep -Seconds 6
Check 'file untouched while guide is open' (Test-Path "$root\shots\blank.png")
Check 'marker not written while guide is open' (-not (Test-Path "$root\data\guide-seen"))
Invoke-Named $guide 'Skip'
Check 'file sorted after guide closes' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 1 })
Check 'marker written after guide closes' (Test-Path "$root\data\guide-seen")
Stop-App

# Second start: no guide and no hold.
New-Png "$root\shots\blank2.png" 10
$p = Start-App $root
Check 'second start sorts without a guide' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File).Count -eq 2 })
Check 'second start shows no guide' ($null -eq (Find-Window $p.Id 'How ScreenSorter works' 1))
Stop-App

# Guide to settings to cancel: the hold lasts until the settings window closes.
$root = New-Sandbox 'hold2'
New-Png "$root\shots\blank.png" 0
$p = Start-App $root
$guide = Find-Window $p.Id 'How ScreenSorter works'
1..3 | ForEach-Object { Invoke-Named $guide 'Next'; Start-Sleep -Milliseconds 200 }
Invoke-Named $guide 'Open settings'
$settings = Find-Window $p.Id 'ScreenSorter settings'
Check 'settings open from guide' ($null -ne $settings)
Start-Sleep -Seconds 4
Check 'file untouched while settings are open' (Test-Path "$root\shots\blank.png")
Invoke-Named $settings 'Cancel'
Check 'file sorted after settings close' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 1 })
Stop-App

# Guide to settings to save: nothing sorted before the restart, and no second guide after it.
$root = New-Sandbox 'hold3'
New-Png "$root\shots\blank.png" 0
$p = Start-App $root
$guide = Find-Window $p.Id 'How ScreenSorter works'
1..3 | ForEach-Object { Invoke-Named $guide 'Next'; Start-Sleep -Milliseconds 200 }
Invoke-Named $guide 'Open settings'
$settings = Find-Window $p.Id 'ScreenSorter settings'
Invoke-Named $settings 'Save and restart'
Check 'settings file saved' (Wait-Until { Test-Path "$root\data\settings.json" })
Check 'marker set by saving' (Test-Path "$root\data\guide-seen")
Check 'file sorted after the restart' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 1 } 30)
$new = Get-Process ScreenSorter.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Select-Object -First 1
Check 'restarted app shows no guide' ($new -and ($null -eq (Find-Window $new.Id 'How ScreenSorter works' 2)))
Stop-App

Complete

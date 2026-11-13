. "$PSScriptRoot\common.ps1"
Stop-App
$root = New-Sandbox 'privacy'
New-Item -ItemType File "$root\data\guide-seen" | Out-Null
New-Item -ItemType Directory -Force "$root\data\logs" | Out-Null
$oldLog = "$root\data\logs\screensorter-2020-01-01.log"
Set-Content $oldLog 'old'
(Get-Item $oldLog).LastWriteTime = (Get-Date).AddDays(-60)
$otherLog = "$root\data\logs\keep-me.txt"
Set-Content $otherLog 'other'
(Get-Item $otherLog).LastWriteTime = (Get-Date).AddDays(-60)

# A new file while another window has focus stores a real title, so there is something to erase.
$p = Start-App $root @('--Ui:OpenSettingsOnStart=true')
$settings = Find-Window $p.Id 'ScreenSorter settings'
Check 'settings open' ($null -ne $settings)
Check 'old log deleted at start' (Wait-Until { -not (Test-Path $oldLog) } 10)
Check 'other file left alone' (Test-Path $otherLog)
New-Png "$root\shots\blank.png" 0
Check 'file sorted' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 1 })

Select-Named $settings 'Privacy'
Start-Sleep -Milliseconds 500
Check 'privacy page shows' ($null -ne (Find-Named $settings 'Erase them now'))
Save-Shot $settings 'privacy.png'

$days = Find-Named $settings 'Days to keep them. 0 means they are not stored'
Check 'default is 30 days' ($days.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '30')
Invoke-Named $settings 'Erase them now'
Check 'erase reports a result' (Wait-Until { (Find-Named $settings 'Erased from 1 record.') -or (Find-Named $settings 'Erased from 0 records.') } 5)
$found = Find-Named $settings 'Erased from 1 record.'
Write-Output ("erase said: " + $(if ($found) { '1 record' } else { '0 records' }))

# Saving keeps a new value through the restart.
$days.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('0')
$logs = Find-Named $settings 'Days to keep log files'
$logs.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('0')
Select-Named $settings 'General'
Invoke-Named $settings 'Save and restart'
Start-Sleep -Milliseconds 700
Check 'zero log days is refused' ($null -ne (Find-Named $settings 'Days to keep logs must be a number from 1 to 3650.'))
Select-Named $settings 'Privacy'
Start-Sleep -Milliseconds 300
$logs = Find-Named $settings 'Days to keep log files'
$logs.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('7')
Invoke-Named $settings 'Save and restart'
Check 'settings saved' (Wait-Until { Test-Path "$root\data\settings.json" } 10)
$json = Get-Content "$root\data\settings.json" -Raw | ConvertFrom-Json
Check 'saved values' ($json.Privacy.WindowTitleDays -eq 0 -and $json.Privacy.LogDays -eq 7)
Stop-App
Complete

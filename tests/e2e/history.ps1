. "$PSScriptRoot\common.ps1"
Stop-App
$root = New-Sandbox 'history'
New-Item -ItemType File "$root\data\guide-seen" | Out-Null
# Blank files have no focus rule to depend on: each goes to _Review at once and has a logged decision.
New-Png "$root\shots\blank1.png" 0
New-Png "$root\shots\blank2.png" 20
New-Png "$root\shots\blank3.png" 40
$p = Start-App $root @('--Ui:OpenActivityOnStart=true', '--Corrections:SettleSeconds=1')
$win = Find-Window $p.Id 'ScreenSorter activity'
Check 'window opens' ($null -ne $win)
Check 'three files sorted' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 3 })
Check 'none left at the top' ((Get-ChildItem "$root\shots" -File).Count -eq 0)

Select-Named $win 'History'
Start-Sleep -Milliseconds 800
$grid = Find-Named $win 'Screenshots'
Check 'history grid present' ($null -ne $grid)
$rows = $grid.FindAll([Windows.Automation.TreeScope]::Children, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::DataItem)))
Check 'three rows' ($rows.Count -eq 3)

# Undo the first row: it goes back to the top level and stays there.
$rows[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 500
Check 'reason shown' ($null -ne (Find-Named $win 'The image is blank or could not be read.'))
Save-Shot $win 'history.png'
Invoke-Named $win 'Undo the move'
Check 'file is back at the top level' (Wait-Until { (Get-ChildItem "$root\shots" -File).Count -eq 1 })
Check 'undo message' (Wait-Until { Find-Named $win 'Moved back. The app leaves it alone until you move it yourself.' } 5)
$undone = (Get-ChildItem "$root\shots" -File)[0].FullName
Start-Sleep -Seconds 4
Check 'intake leaves the undone file alone' (Test-Path $undone)
Check 'review keeps the other two' ((Get-ChildItem "$root\shots\_Review" -File).Count -eq 2)
$undoBtn = Find-Named $win 'Undo the move'
Check 'undo is now disabled for it' (-not $undoBtn.Current.IsEnabled)
Stop-App

# A restart must not sort it either.
$p = Start-App $root @('--Ui:OpenActivityOnStart=true')
$win = Find-Window $p.Id 'ScreenSorter activity'
Start-Sleep -Seconds 4
Check 'after a restart it is still left alone' (Test-Path $undone)

# Moving it into a folder by hand brings it back under the app's care.
New-Item -ItemType Directory -Force "$root\shots\Other" | Out-Null
Select-Named $win 'Accuracy'
Start-Sleep -Milliseconds 800
Check 'accuracy table present' ($null -ne (Find-Named $win 'Accuracy by stage'))
Check 'stage is named' ($null -ne (Find-Named $win 'Blank image check'))
Save-Shot $win 'accuracy.png'
Select-Named $win 'History'
Start-Sleep -Milliseconds 800
Save-Shot $win 'history2.png'
Stop-App
Complete

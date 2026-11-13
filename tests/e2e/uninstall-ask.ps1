param([string]$Setup)
# The uninstaller's question, with a smoke installer: its data folder is data-smoke beside the install, never the real one.
. "$PSScriptRoot\common.ps1"

$root = Join-Path $E2ERoot 'run\uninstall'
$dir = Join-Path $root 'app'
$data = Join-Path $root 'data-smoke'
$realData = Join-Path $env:LOCALAPPDATA 'ScreenSorter'
$realBefore = (Get-ChildItem $realData -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count

function Install {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root -Confirm:$false }
    New-Item -ItemType Directory -Force $data | Out-Null
    Set-Content "$data\keep.txt" 'data'
    $p = Start-Process -FilePath $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"" -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "install failed: $($p.ExitCode)" }
}

function Dialogs {
    $all = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $all) { if ($w.Current.Name -match 'Uninstall') { $w } }
}

function Text-Of($w) { ($w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' | ' }

function Button($w, $pattern) {
    $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and ($_.Current.Name -replace '&', '') -match $pattern } | Select-Object -First 1
}

function Uninstall-Answering($answer, $shot) {
    $script:Asked = $false
    $script:NoIsDefault = $false
    Start-Process -FilePath (Join-Path $dir 'unins000.exe') | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in @(Dialogs)) {
            try {
                $text = Text-Of $w
                if ($text -match 'Also delete') {
                    $script:Asked = $true
                    $no = Button $w '^No$'
                    if ($no) { $script:NoIsDefault = $no.Current.HasKeyboardFocus }
                    if ($shot) { Save-Shot $w $shot }
                    (Button $w "^$answer$").GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                }
                elseif ($text -match 'Are you sure') {
                    (Button $w '^Yes$').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                }
                elseif ($text -match 'successfully removed|was removed') {
                    (Button $w '^OK$').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                    return
                }
            }
            catch [Windows.Automation.ElementNotAvailableException] { }
        }
        Start-Sleep -Milliseconds 300
    }
    Write-Output 'FAIL uninstaller did not finish; windows:'
    foreach ($w in @(Dialogs)) { Write-Output ($w.Current.Name + ': ' + (Text-Of $w)) }
    $script:Failures++
}

Install
Uninstall-Answering 'No' 'uninstall-question.png'
Check 'asked about the data' $script:Asked
Check 'No is the default button' $script:NoIsDefault
Check 'answer No: app removed' (Wait-Until { -not (Test-Path "$dir\ScreenSorter.App.exe") } 20)
Check 'answer No: data kept' (Test-Path "$data\keep.txt")

Install
Uninstall-Answering 'Yes' $null
Check 'answer Yes: app removed' (Wait-Until { -not (Test-Path "$dir\ScreenSorter.App.exe") } 20)
Check 'answer Yes: data deleted' (Wait-Until { -not (Test-Path $data) } 20)

Install
$u = Start-Process -FilePath (Join-Path $dir 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
Check 'silent: app removed' (Wait-Until { -not (Test-Path "$dir\ScreenSorter.App.exe") } 20)
Check 'silent: data kept without asking' (Test-Path "$data\keep.txt")

Check 'real data folder untouched' ($realBefore -eq (Get-ChildItem $realData -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count)
Complete

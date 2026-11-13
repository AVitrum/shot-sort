param([switch]$Live)
# The Status page with LM Studio unreachable, and how a waiting file shows there. -Live also reads the real
# LM Studio on localhost:1234 (read only: no model is loaded and nothing is sorted by it).
. "$PSScriptRoot\common.ps1"
Stop-App

$root = New-Sandbox 'status'
New-Item -ItemType File "$root\data\guide-seen" | Out-Null
$p = Start-App $root @('--Status:CheckSeconds=5', '--Ui:OpenActivityOnStart=true')
$win = Find-Window $p.Id 'ScreenSorter activity'
Check 'status window opens' ($null -ne $win)
Check 'LM Studio row, dead URL' (Wait-Until { Find-Named $win 'Not running. Screenshots that need the vision model will wait for it.' })
Check 'LM Studio fix' ($null -ne (Find-Named $win 'Open LM Studio and start its server.'))
Check 'Examples row' ($null -ne (Find-Named $win '0 examples learned.'))
Check 'watch folder ok' ($null -ne (Find-Named $win "$root\shots"))
Check 'nothing waiting' ($null -ne (Find-Named $win 'Running. Nothing is waiting.'))
Save-Shot $win 'status-dead.png'

Stop-App

# A file waiting for the vision model makes LM Studio news. It is placed while the app is off: with no focus history no
# rule applies, so it waits for the model whatever window has focus during the run.
New-Png "$root\shots\noise.png" 0 -Noise
$p = Start-App $root @('--Status:CheckSeconds=5', '--Ui:OpenActivityOnStart=true')
$win = Find-Window $p.Id 'ScreenSorter activity'
Check 'queue count shows in the LM Studio row' (Wait-Until { Find-Named $win 'Not running. 1 screenshot waits for it.' } 25)
Save-Shot $win 'status-waiting.png'
Stop-App

if ($Live) {
    $root = New-Sandbox 'status-live'
    New-Item -ItemType File "$root\data\guide-seen" | Out-Null
    $a = (Get-SandboxArgs $root) | Where-Object { $_ -notlike '--LmStudio:*' }
    $a += '--Ui:OpenActivityOnStart=true'
    $p = Start-Process -FilePath $Exe -ArgumentList ($a | ForEach-Object { '"' + $_ + '"' }) -PassThru
    $win = Find-Window $p.Id 'ScreenSorter activity'
    Check 'LM Studio running' (Wait-Until { Find-Named $win 'Running, with the model google/gemma-4-12b-qat.' })
    Save-Shot $win 'status-live.png'
    Stop-App
}
Complete

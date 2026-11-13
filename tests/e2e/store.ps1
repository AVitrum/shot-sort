param([string]$ModelPath = (Join-Path $env:LOCALAPPDATA 'ScreenSorter\models\clip-vit-b32-vision.onnx'))
# The example stage with the real CLIP model: learn from a file placed by hand, relabel when it moves, rebuild the
# examples from the decision log, sort a near-duplicate by example, forget on a move to _Review. About 3 minutes.
# The model is only read. If it is missing at -ModelPath the app downloads it there (~350 MB, hash-checked); pass a
# path under the work folder to keep that download out of the installed app's data.
. "$PSScriptRoot\common.ps1"
Stop-App

$root = New-Sandbox 'store'
New-Item -ItemType File "$root\data\guide-seen" | Out-Null
$shots = "$root\shots"
$base = @("--Clip:ModelPath=$ModelPath", '--Corrections:SettleSeconds=1', '--Corrections:RescanMinutes=1')

function Wait-Log($pattern, $seconds, $label) {
    Check $label (Wait-Until { (Get-LogText $root) -match $pattern } $seconds)
}

try {
    $app = Start-App $root $base -KeepClip
    Start-Sleep 8
    New-PatternPng "$shots\Games\A\seed.png" 1 8
    Wait-Log 'Learned .*seed|Learned .*Games.A' 60 'learned from a file placed in Games/A'
    $file = (Get-ChildItem "$shots\Games\A" -File | Select-Object -First 1).FullName
    New-Item -ItemType Directory -Force "$shots\Games\B" | Out-Null
    Move-Item $file "$shots\Games\B\"
    Wait-Log 'was Games/A, now Games/B' 30 'relabelled after moving to Games/B'
    Stop-App

    $app = Start-App $root ($base + '--Examples:Collection=rebuilt') -KeepClip
    Wait-Log 'Restored 1 example' 100 'new collection restored the example from the log'
    Stop-App

    # Placed while the app is off: no focus record, so no rule can compete and only the examples decide.
    New-PatternPng "$shots\near.png" 2 8
    $app = Start-App $root ($base + '--Examples:Collection=rebuilt') -KeepClip
    Wait-Log 'Sorted .*near\.png -> .*Games.B.*Example' 40 'near-duplicate sorted into Games/B by the Example stage'
    Check 'near-duplicate is in Games/B' ((Get-ChildItem "$shots\Games\B" -File).Count -ge 2)

    $seed = (Get-ChildItem "$shots\Games\B" -File | Sort-Object LastWriteTime | Select-Object -First 1).FullName
    New-Item -ItemType Directory -Force "$shots\_Review" | Out-Null
    Move-Item $seed "$shots\_Review\"
    Wait-Log 'Forgotten' 30 'moving to _Review forgot the example'
    Stop-App

    $before = ([regex]::Matches((Get-LogText $root), 'Restored \d+ example')).Count
    $app = Start-App $root ($base + '--Examples:Collection=rebuilt') -KeepClip
    Start-Sleep 75
    $after = ([regex]::Matches((Get-LogText $root), 'Restored \d+ example')).Count
    Check 'forgotten example stays forgotten after a restart' ($after -eq $before)
}
finally {
    Stop-App
}
Complete

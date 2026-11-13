# Shared helpers for the end-to-end scripts. Dot-source it first: . "$PSScriptRoot\common.ps1"
#
# Each script runs the app as a real process on throwaway folders and drives its windows through UI Automation. The
# app runs with --App:InstanceName=e2e, so it starts beside an installed ScreenSorter, and every path points under
# $E2ERoot, so the real Screenshots folder, data and settings are never touched.
#
# Environment:
#   SCREENSORTER_E2E_ROOT  work folder (default %TEMP%\ScreenSorterE2E); wiped per scenario
#   SCREENSORTER_E2E_EXE   app to run (default: the Debug build in this repo)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, PresentationCore, WindowsBase

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:E2ERoot = if ($env:SCREENSORTER_E2E_ROOT) { $env:SCREENSORTER_E2E_ROOT } else { Join-Path $env:TEMP 'ScreenSorterE2E' }
$script:Exe = if ($env:SCREENSORTER_E2E_EXE) { $env:SCREENSORTER_E2E_EXE } else { Join-Path $RepoRoot 'src\ScreenSorter.App\bin\Debug\net10.0-windows\ScreenSorter.App.exe' }
$script:UiShots = Join-Path $E2ERoot 'ui'
$script:Failures = 0
# Unreachable on purpose: these scripts test the app without LM Studio or a model download.
$script:DeadUrl = 'http://127.0.0.1:9'
New-Item -ItemType Directory -Force $E2ERoot, $UiShots | Out-Null
if (-not (Test-Path $Exe)) { throw "No app at $Exe. Build first: dotnet build ScreenSorter.slnx" }

function Check($name, $ok) {
    if ($ok) { Write-Output "ok   $name" } else { Write-Output "FAIL $name"; $script:Failures++ }
}

# Prints the result and ends the script with the failure count as exit code.
function Complete {
    Write-Output "failures: $script:Failures"
    exit $script:Failures
}

function New-Png($path, [byte]$gray, [switch]$Noise, [int]$seed = 1) {
    $w = 64; $h = 48; $p = New-Object byte[] ($w * $h)
    if ($Noise) { (New-Object Random($seed)).NextBytes($p) } else { for ($i = 0; $i -lt $p.Length; $i++) { $p[$i] = $gray } }
    $bmp = [System.Windows.Media.Imaging.BitmapSource]::Create($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Gray8, $null, $p, $w)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $fs = [IO.File]::Create($path); $enc.Save($fs); $fs.Close()
}

# A checkerboard with a little per-seed noise: two seeds look alike to CLIP but hash differently.
function New-PatternPng($path, $seed, $noise) {
    $w = 320; $h = 240; $p = New-Object byte[] ($w * $h); $r = New-Object Random $seed
    for ($y = 0; $y -lt $h; $y++) { for ($x = 0; $x -lt $w; $x++) {
        $p[$y * $w + $x] = [byte](((($x / 16) -band 1) * 120 + (($y / 16) -band 1) * 80 + $r.Next(0, $noise)) % 256) } }
    $b = [Windows.Media.Imaging.BitmapSource]::Create($w, $h, 96, 96, [Windows.Media.PixelFormats]::Gray8, $null, $p, $w)
    $e = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $e.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($b))
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $f = [IO.File]::Create($path); $e.Save($f); $f.Close()
}

function New-Sandbox($name) {
    $sandbox = Join-Path $E2ERoot "run\$name"
    if (Test-Path $sandbox) { Remove-Item -Recurse -Force $sandbox -Confirm:$false }
    New-Item -ItemType Directory -Force "$sandbox\shots", "$sandbox\data" | Out-Null
    return $sandbox
}

# Arguments that keep the app inside a sandbox, with LM Studio and the CLIP download unreachable.
function Get-SandboxArgs($sandbox, [switch]$KeepClip) {
    $a = @("--App:InstanceName=e2e",
           "--Paths:WatchFolders:0=$sandbox\shots", "--Paths:SortedRoot=$sandbox\shots", "--Paths:DatabasePath=$sandbox\data\test.db",
           "--Paths:LogFolder=$sandbox\data\logs", "--Paths:UserSettingsPath=$sandbox\data\settings.json",
           "--LmStudio:BaseUrl=$DeadUrl/v1", "--LmStudio:ManagementUrl=$DeadUrl/api/v1", "--LmStudio:AutoStartServer=false",
           "--Updates:Enabled=false", "--Intake:StabilityPollMilliseconds=50")
    if (-not $KeepClip) { $a += @("--Clip:ModelPath=$sandbox\data\none.onnx", "--Clip:ModelUrl=$DeadUrl/none") }
    return $a
}

function Start-App($sandbox, [string[]]$extra = @(), [switch]$KeepClip) {
    $a = (Get-SandboxArgs $sandbox -KeepClip:$KeepClip) + $extra
    $quoted = $a | ForEach-Object { '"' + $_ + '"' }
    return Start-Process -FilePath $Exe -ArgumentList $quoted -PassThru
}

# Stops every copy started from $Exe; an installed ScreenSorter runs from elsewhere and is left alone.
function Stop-App {
    Get-Process ScreenSorter.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force -Confirm:$false
    Start-Sleep -Milliseconds 500
}

function Get-LogText($sandbox) {
    (Get-ChildItem "$sandbox\data\logs" -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
}

function Find-Window($procId, $title, $timeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $cond = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $title)))
    while ((Get-Date) -lt $deadline) {
        $w = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-Named($parent, $name) {
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Named($parent, $name) {
    $e = Find-Named $parent $name
    if (-not $e) { throw "No element named '$name'" }
    $e.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Select-Named($parent, $name) {
    $e = Find-Named $parent $name
    if (-not $e) { throw "No element named '$name'" }
    $e.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Wait-Until([scriptblock]$cond, $timeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $cond) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

# Saves a picture of a window under $UiShots, for a person to look over; never checked by the scripts.
function Save-Shot($element, $name) {
    Add-Type -AssemblyName System.Drawing
    if (-not ('Native.Print' -as [type])) {
        Add-Type -Namespace Native -Name Print -MemberDefinition '[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);'
    }
    $r = $element.Current.BoundingRectangle
    if ($r.Width -le 0 -or $r.Height -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [Native.Print]::PrintWindow([IntPtr]$element.Current.NativeWindowHandle, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc); $g.Dispose()
    $bmp.Save((Join-Path $UiShots $name)); $bmp.Dispose()
}

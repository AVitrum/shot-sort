# The tray icon: every menu item, double-click, pause, Sort now and Exit, through the real NotifyIcon handlers.
. "$PSScriptRoot\common.ps1"
Add-Type -Namespace Native -Name Fg -MemberDefinition @"
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
"@
# A real tray click gives the app the right to take the foreground. F24 does nothing, but makes this script the last
# input source, which lets it hand that right over, as the shell does.
function Grant-Foreground($procId) {
    [Native.Fg]::keybd_event(0x87, 0, 0, [UIntPtr]::Zero)
    [Native.Fg]::keybd_event(0x87, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
    [void][Native.Fg]::AllowSetForegroundWindow($procId)
}
Add-Type -Namespace Native -Name Tray -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
public delegate bool EnumProc(IntPtr h, IntPtr p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
'@

# NotifyIcon's hidden window gets the shell's mouse messages; posting them runs the real handlers without moving the mouse.
$TrayMessage = 0x400 + 1024
function Send-TrayMouse($procId, $message) {
    $handles = New-Object System.Collections.ArrayList
    $cb = [Native.Tray+EnumProc]{ param($h, $p)
        $owner = [uint32]0; [void][Native.Tray]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $procId) {
            $sb = New-Object Text.StringBuilder 256
            [void][Native.Tray]::GetClassName($h, $sb, 256)
            if ($sb.ToString() -like 'WindowsForms*') { [void]$handles.Add($h) }
        }
        return $true }
    [void][Native.Tray]::EnumWindows($cb, [IntPtr]::Zero)
    foreach ($h in $handles) { [void][Native.Tray]::PostMessage($h, $TrayMessage, [IntPtr]::Zero, [IntPtr]$message) }
    return $handles.Count
}
function Find-MenuItem($procId, $name) {
    $cond = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::MenuItem)))
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
}
function Open-Menu($procId) {
    Grant-Foreground $procId
    [void](Send-TrayMouse $procId 0x205)
    return Wait-Until { Find-MenuItem $procId 'Exit' } 5
}
function Pick($procId, $name) {
    if (-not (Open-Menu $procId)) { throw 'menu did not open' }
    (Find-MenuItem $procId $name).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

Stop-App
$root = New-Sandbox 'tray'
New-Item -ItemType File "$root\data\guide-seen" | Out-Null
$p = Start-App $root
Start-Sleep -Seconds 3
$id = $p.Id
Check 'NotifyIcon window found' ((Send-TrayMouse $id 0x200) -gt 0)

# Right-click opens the menu with every item.
Check 'right-click opens the menu' (Open-Menu $id)
foreach ($n in 'Pause', 'Sort now', 'Open review folder', 'Open screenshots folder', 'History', 'Status', 'How it works', 'Settings...', 'Exit') {
    Check "menu has $n" ($null -ne (Find-MenuItem $id $n))
}
Add-Type -AssemblyName System.Windows.Forms
$cond = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $id)),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Menu)))
$menu = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
if ($menu) {
    $r = $menu.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save((Join-Path $UiShots 'tray-menu.png')); $bmp.Dispose()
}
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Check 'Esc closes the menu' (Wait-Until { -not (Find-MenuItem $id 'Exit') } 5)

# Each item does what it says.
Pick $id 'History'
$win = Find-Window $id 'ScreenSorter activity'
Check 'History opens the window' ($null -ne $win)
Check 'on the history page' (Wait-Until { Find-Named $win 'Screenshots' } 5)
Pick $id 'Status'
Check 'Status switches to the status page' (Wait-Until { Find-Named $win 'Check now' } 5)
$win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()

Pick $id 'How it works'
$guide = Find-Window $id 'How ScreenSorter works'
Check 'How it works opens the guide' ($null -ne $guide)
Check 'guide no longer says nothing moves' ($null -eq (Find-Named $guide 'Nothing moves until you close this guide'))
$guide.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()

Pick $id 'Settings...'
$settings = Find-Window $id 'ScreenSorter settings'
Check 'Settings opens the window' ($null -ne $settings)
$settings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
Check 'settings closed' (Wait-Until { $null -eq (Find-Window $id 'ScreenSorter settings' 0) } 5)

# Double-click opens the settings.
[void](Send-TrayMouse $id 0x203)
$settings = Find-Window $id 'ScreenSorter settings'
Check 'double-click opens the settings' ($null -ne $settings)
$settings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()

# Pause stops sorting and the item turns into Resume.
Pick $id 'Pause'
Start-Sleep -Milliseconds 400
Check 'item now says Resume' (Open-Menu $id)
Check 'Resume is listed' ($null -ne (Find-MenuItem $id 'Resume'))
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 300
New-Png "$root\shots\blank.png" 0
Start-Sleep -Seconds 3
Check 'paused: file stays' (Test-Path "$root\shots\blank.png")
Pick $id 'Resume'
Check 'resumed: file sorted' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File -ErrorAction SilentlyContinue).Count -eq 1 })

# Sort now clears a pause too.
Pick $id 'Pause'
Start-Sleep -Milliseconds 300
New-Png "$root\shots\blank2.png" 30
Start-Sleep -Seconds 2
Pick $id 'Sort now'
Check 'Sort now resumes and sorts' (Wait-Until { (Get-ChildItem "$root\shots\_Review" -File).Count -eq 2 })

# Exit ends the process.
Pick $id 'Exit'
Check 'Exit ends the app' ($p.WaitForExit(15000))
Stop-App
Complete

param([string]$OldSetup, [string]$NewSetup)
# Real upgrade with real installers, in a throwaway folder. Needs both built with -SmokeTest -SmokeMutex at 0.0.1 and 0.0.2.
# The installed appsettings.json is rewritten to temp paths, so that the installer's fallback, which starts the old exe
# with no arguments, could never sort the real screenshots folder. A local server stands in for GitHub.
# Releases are signed with a throwaway key made here. The smoke installer starts the new version on paths beside the
# install folder (data-smoke), so this also proves the new version really runs after an update.
. "$PSScriptRoot\common.ps1"

$root = Join-Path $E2ERoot 'run\update'
Get-Process ScreenSorter.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root*" } | Stop-Process -Force -Confirm:$false
if (Test-Path $root) { Remove-Item -Recurse -Force $root -Confirm:$false }
$dir = Join-Path $root 'app'
$exe = Join-Path $dir 'ScreenSorter.App.exe'
New-Item -ItemType Directory -Force "$root\shots", "$root\data", "$root\downloads" | Out-Null
New-Item -ItemType File "$root\data\guide-seen" | Out-Null
New-Item -ItemType Directory -Force "$root\data-smoke" | Out-Null
New-Item -ItemType File "$root\data-smoke\guide-seen" | Out-Null

$tool = Join-Path $RepoRoot 'installer\release-key.cs'
$env:SCREENSORTER_KEY_PASSPHRASE = 'throwaway-update-test'
$publicKey = (& dotnet run $tool -- new --key "$root\throwaway.pem" | Select-Object -Last 1).ToString().Trim()
$name = 'ScreenSorter-0.0.2-setup.exe'
$checksumFile = "$root\$name.sha256"
[IO.File]::WriteAllText($checksumFile, "$((Get-FileHash $NewSetup -Algorithm SHA256).Hash.ToLowerInvariant())  $name`r`n")
& dotnet run $tool -- sign $checksumFile --public-key $publicKey --key "$root\throwaway.pem" | Out-Null
Check 'release signed with a throwaway key' ($LASTEXITCODE -eq 0 -and (Test-Path "$checksumFile.sig"))
Remove-Item Env:\SCREENSORTER_KEY_PASSPHRASE

if (Get-Process ScreenSorter.App -ErrorAction SilentlyContinue) { throw 'A ScreenSorter is running and holds the mutex; this test needs it stopped.' }

$realExe = Join-Path $env:LOCALAPPDATA 'Programs\ScreenSorter\ScreenSorter.App.exe'
$realData = Join-Path $env:LOCALAPPDATA 'ScreenSorter'
function Real-State {
    $exeTime = if (Test-Path $realExe) { (Get-Item $realExe).LastWriteTimeUtc.Ticks } else { 'none' }
    $newest = (Get-ChildItem $realData -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*.log' } | Measure-Object LastWriteTimeUtc -Maximum).Maximum
    "$exeTime|$newest|" + ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').PSObject.Properties.Name -join ',')
}
$realBefore = Real-State

$p = Start-Process -FilePath $OldSetup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$dir`"" -PassThru -Wait
Check 'old version installed' ($p.ExitCode -eq 0 -and (Get-Item $exe).VersionInfo.ProductVersion -like '0.0.1*')

$port = 18765
$settings = Get-Content "$dir\appsettings.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$settings.Paths.WatchFolders = @("$root\shots")
$settings.Paths.SortedRoot = "$root\shots"
$settings.Paths.DatabasePath = "$root\data\test.db"
$settings.Paths.LogFolder = "$root\data\logs"
$settings.Paths | Add-Member -Force NoteProperty UserSettingsPath "$root\data\settings.json"
$settings.LmStudio.BaseUrl = 'http://127.0.0.1:9/v1'
$settings.LmStudio.ManagementUrl = 'http://127.0.0.1:9/api/v1'
$settings.LmStudio.AutoStartServer = $false
$settings.Clip | Add-Member -Force NoteProperty ModelPath "$root\data\none.onnx"
$settings.Clip | Add-Member -Force NoteProperty ModelUrl 'http://127.0.0.1:9/none'
$settings.Idle.NoInputMinutes = 0
$settings.Idle.MaxGpuUtilPercent = 100
$settings.Idle.SkipWhenFullscreen = $false
$settings.Updates.ApiUrl = "http://127.0.0.1:$port"
$settings.Updates.Repository = 'o/r'
$settings.Updates.PublicKey = $publicKey
$settings.Updates.FirstCheckDelaySeconds = 1
$settings.Updates.InstallCheckSeconds = 2
$settings.Updates.DownloadFolder = "$root\downloads"
[IO.File]::WriteAllText("$dir\appsettings.json", ($settings | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))

$server = Start-Job -ArgumentList $port, $NewSetup, $checksumFile -ScriptBlock {
    param($port, $setup, $checksumFile)
    $name = 'ScreenSorter-0.0.2-setup.exe'
    $latest = "{`"tag_name`":`"v0.0.2`",`"assets`":[{`"name`":`"$name`",`"browser_download_url`":`"http://127.0.0.1:$port/setup`"},{`"name`":`"$name.sha256`",`"browser_download_url`":`"http://127.0.0.1:$port/sha`"},{`"name`":`"$name.sha256.sig`",`"browser_download_url`":`"http://127.0.0.1:$port/sig`"}]}"
    $l = New-Object Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$port/")
    $l.Start()
    while ($true) {
        $c = $l.GetContext()
        $path = $c.Request.Url.AbsolutePath
        $body = switch ($path) {
            '/repos/o/r/releases/latest' { [Text.Encoding]::UTF8.GetBytes($latest) }
            '/sha' { [IO.File]::ReadAllBytes($checksumFile) }
            '/sig' { [IO.File]::ReadAllBytes("$checksumFile.sig") }
            '/setup' { [IO.File]::ReadAllBytes($setup) }
            default { $c.Response.StatusCode = 404; [byte[]]@() }
        }
        $c.Response.ContentLength64 = $body.Length
        $c.Response.OutputStream.Write($body, 0, $body.Length)
        $c.Response.Close()
    }
}
try {
    Start-Sleep 2
    $app = Start-Process -FilePath $exe -PassThru
    Check 'old app exits by itself to update' ($app.WaitForExit(120000))
    Check 'new version on disk' (Wait-Until { (Get-Item $exe).VersionInfo.ProductVersion -like '0.0.2*' } 120)
    Check 'new version is running from the install folder' (Wait-Until {
        $running = Get-Process ScreenSorter.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
        $running -and $running.MainModule.FileVersionInfo.ProductVersion -like '0.0.2*' } 30)
    Check 'new version really works: it wrote its log and watches its folder' (Wait-Until {
        $newLog = (Get-ChildItem "$root\data-smoke\logs" -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
        $newLog -match 'Watching' } 30)
    Check 'verified installer was downloaded' (Test-Path "$root\downloads\ScreenSorter-0.0.2-setup.exe")
    Check 'no partial download left' (-not (Test-Path "$root\downloads\ScreenSorter-0.0.2-setup.exe.part"))
    $log = (Get-ChildItem "$root\data\logs" -Filter *.log | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    Check 'log names the new version' ($log -match 'Version 0\.0\.2 is available')
    Check 'real install, data and Run key untouched' ($realBefore -eq (Real-State))
}
finally {
    Stop-Job $server -ErrorAction SilentlyContinue
    Remove-Job $server -Force -ErrorAction SilentlyContinue
    Get-Process ScreenSorter.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root*" } | Stop-Process -Force -Confirm:$false
}
Complete

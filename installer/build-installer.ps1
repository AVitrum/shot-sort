<#
.SYNOPSIS
    Publishes a self-contained build and packs it into a per-user installer.
.DESCRIPTION
    The Inno Setup compiler comes from a pinned NuGet package checked against a pinned SHA-256, so nothing is installed
    on the machine. Everything is written under artifacts\.
.PARAMETER Version
    Version for the app and the file name, such as 1.2.3. Defaults to the version in Directory.Build.props.
.PARAMETER SmokeTest
    Builds an installer without shortcuts, registry entries or the check for a running app, to try on a working machine.
.OUTPUTS
    The path of the installer.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'

$InnoVersion = '6.7.3'
$InnoSha256 = 'f780898e402ff80612cc8d9fcb8c6e02932bd1cb4c900ffdaa31f9341cfb49f4'
$Runtime = 'win-x64'

$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts'
$publish = Join-Path $artifacts 'publish'
$output = Join-Path $artifacts 'installer'
$tools = Join-Path $artifacts "tools\innosetup-$InnoVersion"

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.2.3, not '$Version'."
}

$iscc = Join-Path $tools 'tools\ISCC.exe'
if (-not (Test-Path $iscc)) {
    New-Item -ItemType Directory -Force $tools | Out-Null
    $package = Join-Path $tools 'package.zip'
    Invoke-WebRequest -UseBasicParsing -OutFile $package `
        -Uri "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$InnoVersion/tools.innosetup.$InnoVersion.nupkg"
    $actual = (Get-FileHash $package -Algorithm SHA256).Hash
    if ($actual -ne $InnoSha256) {
        Remove-Item $package
        throw "Inno Setup package has SHA-256 $actual, expected $InnoSha256."
    }
    Expand-Archive $package -DestinationPath $tools -Force
    Remove-Item $package
}

if (Test-Path $publish) {
    Remove-Item -Recurse -Force $publish
}
# The DirectML package copies its DLL only for an explicit platform; without it the app would rely on the Windows copy.
dotnet publish (Join-Path $repo 'src\ScreenSorter.App') -c Release -r $Runtime --self-contained true `
    "-p:Version=$Version" -p:PlatformTarget=x64 -p:PublishReadyToRun=true -o $publish -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$arguments = @("/DAppVersion=$Version", "/DPublishDir=$publish", "/DOutputDir=$output", '/Q')
if ($SmokeTest) { $arguments += '/DSmokeTest=1' }
& $iscc @arguments (Join-Path $PSScriptRoot 'ScreenSorter.iss')
if ($LASTEXITCODE -ne 0) { throw "The installer compiler failed with exit code $LASTEXITCODE." }

Join-Path $output "ScreenSorter-$Version-setup.exe"

<#
.SYNOPSIS
    Signs a draft release's checksum and attaches the signature. Publishing stays a separate step, by hand.
.DESCRIPTION
    Run on the machine that holds the release key, once the draft has been read over: signing says this draft is approved.
    Needs the GitHub CLI, signed in.
.PARAMETER Tag
    The release tag, such as v1.2.3.
.PARAMETER KeyPath
    The private key, if it is not in the default place.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$KeyPath
)

$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v\d+\.\d+\.\d+$') {
    throw "Tag must look like v1.2.3, not '$Tag'."
}
$repo = Split-Path $PSScriptRoot -Parent
$publicKey = (Get-Content (Join-Path $repo 'src\ScreenSorter.App\appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Updates.PublicKey
$name = "ScreenSorter-$($Tag.TrimStart('v'))-setup.exe.sha256"
$work = Join-Path ([IO.Path]::GetTempPath()) "screensorter-sign-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    gh release download $Tag --pattern $name --dir $work --clobber
    if ($LASTEXITCODE -ne 0) { throw "Could not download $name from release $Tag." }

    $arguments = @('run', (Join-Path $PSScriptRoot 'release-key.cs'), '--', 'sign', (Join-Path $work $name), '--public-key', $publicKey)
    if ($KeyPath) { $arguments += '--key', $KeyPath }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed; nothing was uploaded.' }

    gh release upload $Tag (Join-Path $work "$name.sig") --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Could not upload the signature.' }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
"Signed. Publish when ready: gh release edit $Tag --draft=false"

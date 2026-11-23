[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'release'))

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'windows'
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$application = Join-Path $OutputDirectory 'LinkTV.exe'
$manifest = Join-Path $source 'app.manifest'
$sourceFiles = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | ForEach-Object FullName)
$mascot = Join-Path $PSScriptRoot 'assets\linktv_mascot.png'
$icon = Join-Path $PSScriptRoot 'assets\linktv.ico'
$resources = @()
if (Test-Path -LiteralPath $mascot) {
    $resources += "/resource:$mascot,LinkTV.Mascot.png"
}
if (Test-Path -LiteralPath $icon) {
    $resources += "/win32icon:$icon"
    $resources += "/resource:$icon,LinkTV.AppIcon.ico"
}

& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ /codepage:65001 `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    "/win32manifest:$manifest" "/out:$application" @resources @sourceFiles
if ($LASTEXITCODE -ne 0) {
    throw "Windows build failed with exit code $LASTEXITCODE."
}
Write-Output $application

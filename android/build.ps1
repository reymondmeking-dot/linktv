param(
    [string]$Jdk = $env:JAVA_HOME,
    [string]$Sdk = $env:ANDROID_HOME,
    [string]$BuildToolsVersion = '35.0.1',
    [string]$Platform = 'android-35'
)

$ErrorActionPreference = 'Stop'

if (-not $Jdk) {
    $javac = Get-Command javac.exe -ErrorAction SilentlyContinue
    if ($javac) {
        $Jdk = Split-Path -Parent (Split-Path -Parent $javac.Source)
    }
}
if (-not $Jdk) {
    throw 'Set JAVA_HOME or pass -Jdk with the path to the JDK.'
}
if (-not $Sdk) {
    $Sdk = $env:ANDROID_SDK_ROOT
}
if (-not $Sdk) {
    $Sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
}

function Invoke-BuildTool {
    param([string]$Program, [string[]]$ToolArguments)
    & $Program @ToolArguments
    if ($LASTEXITCODE -ne 0) {
        throw "$(Split-Path -Leaf $Program) exited with code $LASTEXITCODE."
    }
}

$toolDir = Join-Path $Sdk "build-tools\$BuildToolsVersion"
$platformJar = Join-Path $Sdk "platforms\$Platform\android.jar"
$work = Join-Path (Join-Path $PSScriptRoot 'build') ('job-' + [Guid]::NewGuid().ToString('N'))
$generated = Join-Path $work 'generated'
$classes = Join-Path $work 'classes'
$dex = Join-Path $work 'dex'
$release = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'
$unsigned = Join-Path $work 'unsigned.apk'
$aligned = Join-Path $work 'aligned.apk'
$classArchive = Join-Path $work 'classes.jar'
$apk = Join-Path $release 'LinkTV-AndroidTV.apk'

New-Item -Path $generated, $classes, $dex, $release -ItemType Directory -Force | Out-Null

$aapt = Join-Path $toolDir 'aapt.exe'
Invoke-BuildTool $aapt @(
    'package', '-f', '-m', '-M', (Join-Path $PSScriptRoot 'AndroidManifest.xml'),
    '-I', $platformJar, '-S', (Join-Path $PSScriptRoot 'res'), '-J', $generated,
    '-F', $unsigned
)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src'), $generated -Recurse -File -Filter '*.java' |
    ForEach-Object FullName)
Invoke-BuildTool (Join-Path $Jdk 'bin\javac.exe') (@(
    '-encoding', 'UTF-8', '-source', '8', '-target', '8', '-bootclasspath', ($platformJar + [IO.Path]::PathSeparator + (Join-Path $toolDir 'core-lambda-stubs.jar')),
    '-d', $classes
) + $sources)
Invoke-BuildTool (Join-Path $Jdk 'bin\jar.exe') @('-cf', $classArchive, '-C', $classes, '.')
Invoke-BuildTool (Join-Path $Jdk 'bin\java.exe') @(
    '-cp', (Join-Path $toolDir 'lib\d8.jar'), 'com.android.tools.r8.D8',
    '--lib', $platformJar, '--min-api', '21', '--output', $dex, $classArchive
)
Push-Location -LiteralPath $dex
try {
    Invoke-BuildTool $aapt @('add', $unsigned, 'classes.dex')
} finally {
    Pop-Location
}
Invoke-BuildTool (Join-Path $toolDir 'zipalign.exe') @('-f', '4', $unsigned, $aligned)

$signingDir = Join-Path $env:LOCALAPPDATA 'LinkTV\android-signing'
$keystore = Join-Path $signingDir 'linktv-tv.p12'
$passwordFile = Join-Path $signingDir 'password.dpapi'
New-Item -Path $signingDir -ItemType Directory -Force | Out-Null
if (-not (Test-Path -LiteralPath $passwordFile)) {
    if (Test-Path -LiteralPath $keystore) {
        throw 'Android signing keystore exists without its saved password.'
    }
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $passwordBytes = [byte[]]::new(24)
    try {
        $random.GetBytes($passwordBytes)
    } finally {
        $random.Dispose()
    }
    $newPassword = [Convert]::ToBase64String($passwordBytes)
    $encryptedPassword = ConvertTo-SecureString $newPassword -AsPlainText -Force |
        ConvertFrom-SecureString
    [IO.File]::WriteAllText($passwordFile, $encryptedPassword)
}
$signingSecret = ConvertTo-SecureString (Get-Content -LiteralPath $passwordFile -Raw)
$env:LINKTV_ANDROID_SIGNING_PASSWORD = [Net.NetworkCredential]::new('', $signingSecret).Password
try {
    if (-not (Test-Path -LiteralPath $keystore)) {
        Invoke-BuildTool (Join-Path $Jdk 'bin\keytool.exe') @(
            '-genkeypair', '-storetype', 'PKCS12', '-keystore', $keystore, '-alias', 'LinkTV',
            '-storepass:env', 'LINKTV_ANDROID_SIGNING_PASSWORD',
            '-keyalg', 'RSA', '-keysize', '2048', '-validity', '3650',
            '-dname', 'CN=LinkTV Android TV'
        )
    }
    $apkSigner = Join-Path $toolDir 'lib\apksigner.jar'
    Invoke-BuildTool (Join-Path $Jdk 'bin\java.exe') @(
        '-jar', $apkSigner, 'sign', '--ks', $keystore, '--ks-key-alias', 'LinkTV',
        '--ks-pass', 'env:LINKTV_ANDROID_SIGNING_PASSWORD',
        '--v1-signing-enabled', 'true', '--v2-signing-enabled', 'true',
        '--out', $apk, $aligned
    )
    Invoke-BuildTool (Join-Path $Jdk 'bin\java.exe') @(
        '-jar', $apkSigner, 'verify', '--verbose', '--min-sdk-version', '21', $apk
    )
} finally {
    Remove-Item Env:LINKTV_ANDROID_SIGNING_PASSWORD -ErrorAction SilentlyContinue
}
Write-Output $apk

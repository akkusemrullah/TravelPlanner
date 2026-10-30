# Builds a release APK (or, with -Aab, the Play Store App Bundle) signed with the upload keystore (docs/RELEASE.md).
# Usage (from anywhere):  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-signed-apk.ps1
# Play Store:             ... -File scripts\build-signed-apk.ps1 -Aab -VersionCode 2
# Optional: -KeyStore <path> (default: travelplanner.keystore in the repo root, which .gitignore keeps out of git).
# -VersionCode overrides ApplicationVersion (Android versionCode); every Play upload needs a higher one than the last.
# Passwords are asked for hidden and only kept in environment variables for the duration of the build.
param(
    [string]$KeyStore = (Join-Path $PSScriptRoot '..\travelplanner.keystore'),
    [switch]$Aab,
    [int]$VersionCode = 0
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$KeyStore = (Resolve-Path $KeyStore).Path

function Read-Secret($prompt) {
    $s = Read-Host $prompt -AsSecureString
    [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
}

# keytool from the JDK that the Android tooling uses (Visual Studio installs it under Program Files\Android).
$kt = Get-ChildItem 'C:\Program Files\Android\openjdk', 'C:\Program Files\Microsoft' -Recurse -Filter keytool.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $kt) { $kt = 'keytool' }

$env:ANDROID_KEYSTORE_PASSWORD = Read-Secret 'Keystore sifresi'
$k = Read-Secret 'Anahtar sifresi (ayni ise bos birak, Enter)'
if ([string]::IsNullOrEmpty($k)) { $k = $env:ANDROID_KEYSTORE_PASSWORD }
$env:ANDROID_KEY_PASSWORD = $k

try {
    # Check the password and find the alias before the long build.
    $list = & $kt -list -keystore $KeyStore -storepass:env ANDROID_KEYSTORE_PASSWORD 2>&1
    if ($LASTEXITCODE -ne 0) { Write-Host 'KEYSTORE SIFRESI YANLIS (veya dosya okunamadi):'; $list | Write-Host; exit 1 }
    $aliases = @($list | Select-String '^([^,]+), .*PrivateKeyEntry' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    if ($aliases -contains 'travelplanner') { $alias = 'travelplanner' }
    elseif ($aliases.Count -eq 1) { $alias = $aliases[0] }
    else { Write-Host "Alias secilemedi: $($aliases -join ', ')"; exit 1 }
    Write-Host "Keystore sifresi dogru, alias: $alias"

    # Clear old Release output: otherwise MSBuild may copy an earlier (e.g. debug-signed) APK instead of re-signing.
    Remove-Item -Recurse -Force bin\Release\net10.0-android, obj\Release\net10.0-android, publish -ErrorAction SilentlyContinue

    $format = if ($Aab) { 'aab' } else { 'apk' }
    $versionArgs = @()
    if ($VersionCode -gt 0) { $versionArgs += "-p:ApplicationVersion=$VersionCode" }

    dotnet publish TravelPlanner.csproj -c Release -f net10.0-android `
        -p:AndroidPackageFormat=$format @versionArgs `
        -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$KeyStore" `
        -p:AndroidSigningKeyAlias=$alias `
        -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD `
        -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD `
        -o publish
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $pkg = Resolve-Path "publish\com.eaksoft.travelplanner-Signed.$format"
    Write-Host ''
    & $kt -printcert -jarfile $pkg | Select-String 'Owner:' | Select-Object -First 1 | Write-Host
    Write-Host "Imzali $($format.ToUpper()): $pkg"
}
finally {
    Remove-Item Env:ANDROID_KEYSTORE_PASSWORD, Env:ANDROID_KEY_PASSWORD -ErrorAction SilentlyContinue
}

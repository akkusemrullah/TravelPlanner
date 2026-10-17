# TravelPlanner - signed Release APK (for direct download; the Play Store build is the .aab, see docs/RELEASE.md)
# Run from the project folder:   powershell -ExecutionPolicy Bypass -File .\build-apk.ps1
# Output: bin\Release\net10.0-android\publish\com.eaksoft.travelplanner-Signed.apk

param(
    [string]$KeyStore = "travelplanner.keystore",
    [string]$Alias = "travelplanner"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path $KeyStore)) { throw "Keystore bulunamadı: $KeyStore" }

# Passwords are asked for, never stored in this file or the shell history.
$store = Read-Host "Keystore şifresi" -AsSecureString
$key = Read-Host "Anahtar şifresi (aynıysa Enter)" -AsSecureString
$env:TP_STORE_PASS = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($store))
$keyPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($key))
$env:TP_KEY_PASS = if ([string]::IsNullOrEmpty($keyPlain)) { $env:TP_STORE_PASS } else { $keyPlain }

try {
    # Old outputs out of the way, so the APK in publish\ is surely the new one.
    if (Test-Path "bin\Release\net10.0-android") { Remove-Item "bin\Release\net10.0-android" -Recurse -Force }

    dotnet publish TravelPlanner.csproj -c Release -f net10.0-android `
        -p:TargetFrameworks=net10.0-android `
        -p:AndroidPackageFormat=apk `
        -p:AndroidKeyStore=true `
        -p:AndroidSigningKeyStore="$((Resolve-Path $KeyStore).Path)" `
        -p:AndroidSigningKeyAlias=$Alias `
        -p:AndroidSigningStorePass=env:TP_STORE_PASS `
        -p:AndroidSigningKeyPass=env:TP_KEY_PASS
    if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız." }

    $apk = Get-ChildItem "bin\Release\net10.0-android\publish\*-Signed.apk" | Select-Object -First 1
    "{0}  ({1:N1} MB)" -f $apk.FullName, ($apk.Length / 1MB)
    explorer.exe /select,"$($apk.FullName)"
}
finally {
    Remove-Item Env:TP_STORE_PASS, Env:TP_KEY_PASS -ErrorAction SilentlyContinue
}

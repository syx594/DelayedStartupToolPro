<#
.SYNOPSIS
    Authenticode sign DelayedStartupTool.exe.

.DESCRIPTION
    - If env SIGN_PFX / SIGN_PWD point to a real CA code-signing .pfx, use it.
      (Recommended: removes "Unknown publisher / SmartScreen" prompts on other PCs.)
    - Otherwise generate a self-signed code-signing cert and sign with it.
      Note: self-signed certs are NOT trusted by other PCs, so SmartScreen may
      still warn there. It only marks the exe as digitally signed locally.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File sign.ps1
      # Use a real cert:
      $env:SIGN_PFX = "D:\certs\my_codesign.pfx"; $env:SIGN_PWD = "password"; powershell -File sign.ps1

    WARNING: the exported datashui_sign.pfx is equivalent to your signing private
    key. Keep it safe; if leaked, others could sign as you.
#>
param(
    [string]$ExePath = "",
    [string]$PfxPath = $env:SIGN_PFX,
    [string]$Password = $env:SIGN_PWD,
    [string]$Subject = "DelayedStartupTool",
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if (-not $ExePath) {
    $ExePath = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64\publish\DelayedStartupTool.exe"
}
if (-not (Test-Path $ExePath)) {
    Write-Error "exe not found: $ExePath"
    exit 1
}

$cert = $null
if ($PfxPath -and (Test-Path $PfxPath)) {
    $sec = ConvertTo-SecureString -String $Password -AsPlainText -Force
    $cert = Get-PfxCertificate -FilePath $PfxPath -Password $sec
    Write-Host "[sign] using PFX cert: $($cert.Subject)"
} else {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("My", "CurrentUser")
    $store.Open("ReadWrite")
    $cert = $store.Certificates | Where-Object { $_.Subject -eq "CN=$Subject" -and (($_.EnhancedKeyUsageList | ForEach-Object { $_.FriendlyName }) -contains "Code Signing") } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigning -Subject "CN=$Subject" -KeyExportPolicy Exportable -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5)
        Write-Host "[sign] generated self-signed code-signing cert: $($cert.Subject)"
    } else {
        Write-Host "[sign] reused self-signed cert: $($cert.Subject)"
    }
    $store.Close()
    $pfxOut = Join-Path $PSScriptRoot "datashui_sign.pfx"
    $exportPwd = ConvertTo-SecureString -String "ChangeMe123!" -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxOut -Password $exportPwd | Out-Null
    Write-Host "[sign] exported PFX backup: $pfxOut (keep safe, equivalent to private key)"
}

try {
    Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -TimestampServer $TimestampServer -HashAlgorithm SHA256 -Force | Out-Null
    Write-Host "[sign] signed with timestamp"
} catch {
    Write-Warning "[sign] timestamped sign failed (no network?), signing without timestamp"
    Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256 -Force | Out-Null
}

$result = Get-AuthenticodeSignature $ExePath
Write-Host "[sign] status: $($result.Status)  signer: $($result.SignerCertificate.Subject)"
if ($result.Status -ne "Valid") { exit 2 }
exit 0

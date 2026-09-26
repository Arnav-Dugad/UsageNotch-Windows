param([string]$Repository = 'Arnav-Dugad/UsageNotch-Windows', [string]$ExePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if (-not $ExePath) { $ExePath = Join-Path $root 'Release\Latest\UsageNotch.exe' }
$exe = Get-Item -LiteralPath $ExePath
$version = [version]$exe.VersionInfo.FileVersion
$tag = 'v' + $version.ToString(3)
$key = [Security.Cryptography.CngKey]::Open('UsageNotch.ReleaseSigning.v1')
try {
    $public = $key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    if ([Convert]::ToBase64String($public) -cne [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'Assets\update-public-key.blob')))) { throw 'Signing key does not match the app trust root.' }
    $manifest = [ordered]@{ Version = $version.ToString(3); Url = "https://github.com/$Repository/releases/download/$tag/UsageNotch.exe"; Sha256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash; Size = $exe.Length }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
    $signer = New-Object Security.Cryptography.ECDsaCng($key)
    $signer.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
    try { $signature = $signer.SignData($bytes) } finally { $signer.Dispose() }
    [IO.File]::WriteAllBytes((Join-Path $root 'Release\update.json'), $bytes)
    [IO.File]::WriteAllText((Join-Path $root 'Release\update.sig'), [Convert]::ToBase64String($signature), (New-Object Text.UTF8Encoding($false)))
    Write-Host "Signed $tag manifest. Upload UsageNotch.exe, update.json and update.sig to the same release."
} finally { $key.Dispose() }

$ErrorActionPreference = 'Stop'
# Non-exportable per-user key in the Windows CNG key store, never in the repository.
$keyName = 'UsageNotch.ReleaseSigning.v1'
if ([Security.Cryptography.CngKey]::Exists($keyName)) {
    $key = [Security.Cryptography.CngKey]::Open($keyName)
} else {
    $options = New-Object Security.Cryptography.CngKeyCreationParameters
    $options.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $options.KeyUsage = [Security.Cryptography.CngKeyUsages]::Signing
    $key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $keyName, $options)
}
try {
    $publicPath = Join-Path (Split-Path $PSScriptRoot) 'Assets\update-public-key.blob'
    $public = $key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    if (Test-Path -LiteralPath $publicPath) {
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($publicPath)) -cne [Convert]::ToBase64String($public)) { throw 'Existing public key differs. Do not silently rotate the update trust root.' }
    } else { [IO.File]::WriteAllBytes($publicPath, $public) }
    Write-Host 'Update signing key ready in Windows CNG. Only its public key is in Assets.'
} finally { $key.Dispose() }

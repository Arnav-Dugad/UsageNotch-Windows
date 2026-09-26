[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9.-]{3,50}$')][string]$Identity,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$PublisherDisplayName,
    [Parameter(Mandatory)][uri]$UpdateBaseUrl,
    [string]$CertificateThumbprint,
    [uri]$TimestampUrl = 'https://timestamp.digicert.com',
    [string]$SdkBin,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($UpdateBaseUrl.Scheme -ne 'https' -or $UpdateBaseUrl.UserInfo -or $UpdateBaseUrl.Query -or $UpdateBaseUrl.Fragment) { throw 'Use an HTTPS update directory URL without credentials, query or fragment.' }
if ($TimestampUrl.Scheme -ne 'https') { throw 'The timestamp service must use HTTPS.' }
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'UsageNotch.Windows.csproj') -Raw)
$version = [version]$project.Project.PropertyGroup.FileVersion
if ($version.Revision -ne 0) { throw 'MSIX release revision must be zero.' }
$substitutions = @{
    '__IDENTITY__' = $Identity
    '__PUBLISHER__' = $Publisher
    '__DISPLAY_PUBLISHER__' = $PublisherDisplayName
    '__VERSION__' = $version.ToString()
    '__BASE_URL__' = $UpdateBaseUrl.AbsoluteUri.TrimEnd('/')
}
function Expand-Template([string]$name) {
    $xmlText = Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw -Encoding UTF8
    foreach ($token in $substitutions.Keys) { $xmlText = $xmlText.Replace($token, [Security.SecurityElement]::Escape($substitutions[$token])) }
    if ($xmlText -match '__[A-Z_]+__') { throw 'Unresolved manifest token.' }
    $document = [xml]$xmlText
    return $document
}
$manifest = Expand-Template 'AppxManifest.template.xml'
$installer = Expand-Template 'AppInstaller.template.xml'
if ($ValidateOnly) {
    Write-Host "Manifest template validation passed for $Identity $version. No package was signed or published."
    return
}
if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Provide the exact thumbprint of your trusted code-signing certificate in CurrentUser\My. No passwords or private keys belong in this project.' }
$certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint)
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw 'A currently valid signing certificate with an accessible private key is required.' }
if ($certificate.Subject -cne $Publisher) { throw 'Publisher must exactly match the signing certificate subject.' }
if (-not ($certificate.EnhancedKeyUsageList.ObjectId.Value -contains '1.3.6.1.5.5.7.3.3')) { throw 'This certificate does not have the code-signing purpose.' }
if (-not $SdkBin) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $SdkBin = Get-ChildItem -LiteralPath $sdkRoot -Directory | Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64' } | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'makeappx.exe') } | Select-Object -First 1
}
if (-not $SdkBin) { throw 'Windows SDK packaging tools are missing. Install the Windows SDK or pass -SdkBin.' }
$makeappx = Join-Path $SdkBin 'makeappx.exe'
$signtool = Join-Path $SdkBin 'signtool.exe'
foreach ($tool in @($makeappx, $signtool)) { if (-not (Test-Path -LiteralPath $tool)) { throw "Missing Windows SDK tool: $tool" } }
$exe = Join-Path $projectRoot 'Release\Latest\UsageNotch.exe'
if (-not (Test-Path -LiteralPath $exe) -or (Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne $version.ToString()) { throw 'Run build.ps1 first; the EXE must match the project version.' }
# Each run gets an isolated staging directory. Existing releases and settings are never deleted.
$stage = Join-Path $projectRoot ('Release\Packaging-Staging\' + [guid]::NewGuid().ToString('N'))
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $stage 'UsageNotch.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot 'HOW-TO-USE.md') -Destination $stage
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))
# Rasterize the existing app logo at Windows' required tile sizes; no new artwork.
Add-Type -AssemblyName System.Drawing
$original = [Drawing.Image]::FromFile((Join-Path $projectRoot 'Assets\UsageNotch-logo.png'))
try {
    foreach ($tile in @(@('StoreLogo.png',50), @('Square44x44Logo.png',44), @('Square150x150Logo.png',150))) {
        $bitmap = New-Object Drawing.Bitmap($tile[1], $tile[1])
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($original, 0, 0, $tile[1], $tile[1])
            $bitmap.Save((Join-Path $assets $tile[0]), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $original.Dispose() }
$out = Join-Path $projectRoot 'Release\Installer'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$packageName = "UsageNotch-$version-x64.msix"
$package = Join-Path $out $packageName
if (Test-Path -LiteralPath $package) { throw 'This package version already exists. Bump the project version; never replace a published version.' }
$stagedPackage = Join-Path (Split-Path $stage -Parent) ((Split-Path $stage -Leaf) + '.msix')
& $signtool sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampUrl.AbsoluteUri /td SHA256 (Join-Path $stage 'UsageNotch.exe')
if ($LASTEXITCODE -ne 0) { throw 'EXE signing failed.' }
& $makeappx pack /d $stage /p $stagedPackage /o
if ($LASTEXITCODE -ne 0) { throw 'MSIX packaging failed.' }
& $signtool sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampUrl.AbsoluteUri /td SHA256 $stagedPackage
if ($LASTEXITCODE -ne 0) { throw 'Package signing failed. Do not distribute the incomplete package.' }
& $signtool verify /pa /all /v $stagedPackage
if ($LASTEXITCODE -ne 0) { throw 'Windows signature trust verification failed. Update metadata will NOT be generated.' }
# Only a verified signed package gets an update descriptor. No self-signed trust installation.
Copy-Item -LiteralPath $stagedPackage -Destination $package
$installer.Save((Join-Path $out 'UsageNotch.appinstaller'))
Get-FileHash -LiteralPath $package -Algorithm SHA256 | Format-List
Write-Host "Signed installer prepared in $out. Nothing has been uploaded or installed."

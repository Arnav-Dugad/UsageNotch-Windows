$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.dotnet-sdk\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$output = Join-Path $projectRoot 'Release\Latest'
$zip = Join-Path $projectRoot 'Release\UsageNotch-win-x64.zip'

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Write-Host 'Publishing UsageNotch for Windows (x64)...'
$runningLatest = Get-Process -Name UsageNotch -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $output 'UsageNotch.exe') }
if ($runningLatest) { throw 'Quit UsageNotch from its dock or tray menu before rebuilding Latest.' }
& $dotnet publish (Join-Path $projectRoot 'UsageNotch.Windows.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $projectRoot 'HOW-TO-USE.md') -Destination (Join-Path $output 'HOW-TO-USE.md') -Force
Compress-Archive -LiteralPath @((Join-Path $output 'UsageNotch.exe'), (Join-Path $output 'HOW-TO-USE.md')) -DestinationPath $zip -CompressionLevel Optimal -Force
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut((Join-Path $projectRoot 'UsageNotch (Latest).lnk'))
$shortcut.TargetPath = Join-Path $output 'UsageNotch.exe'
$shortcut.WorkingDirectory = $output
$shortcut.IconLocation = (Join-Path $output 'UsageNotch.exe') + ',0'
$shortcut.Description = 'Open the latest local UsageNotch build'
$shortcut.Save()
Write-Host ''
Write-Host 'Build complete:'
Write-Host "  $(Join-Path $output 'UsageNotch.exe')"
Write-Host "  $zip"

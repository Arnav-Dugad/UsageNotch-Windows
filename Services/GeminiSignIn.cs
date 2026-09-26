using System.Diagnostics;
using System.Text;

namespace UsageNotch.Services;

public static class GeminiSignIn
{
    public static void Launch()
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            $names = @('GOOGLE_APPLICATION_CREDENTIALS','GOOGLE_CLOUD_PROJECT','GOOGLE_CLOUD_PROJECT_ID','GOOGLE_CLOUD_QUOTA_PROJECT','GOOGLE_CLOUD_ACCESS_TOKEN','GOOGLE_GENAI_USE_GCA','GOOGLE_GENAI_USE_VERTEXAI','GOOGLE_API_KEY','GEMINI_API_KEY')
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
            $signInFolder = Join-Path $env:LOCALAPPDATA 'UsageNotch\GeminiSignIn'
            New-Item -ItemType Directory -Path $signInFolder -Force | Out-Null
            Set-Location -LiteralPath $signInFolder
            $geminiCommand = Join-Path $env:APPDATA 'npm\gemini.cmd'
            if (-not (Test-Path -LiteralPath $geminiCommand)) {
                Write-Host 'Gemini CLI is not installed. Run: npm.cmd install -g @google/gemini-cli' -ForegroundColor Yellow
                return
            }
            Write-Host 'UsageNotch: personal Google sign-in. Windows environment settings are unchanged.' -ForegroundColor Cyan
            Write-Host 'Choose Sign in with Google, using your Google AI Pro account. Then return to UsageNotch and Refresh now.'
            & $geminiCommand
            """;
        Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NoExit -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        { UseShellExecute = true });
    }
}

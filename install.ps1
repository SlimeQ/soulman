param(
    [string]$Configuration = "Release",
    [string]$PublishProfile = "src/Soulman/Properties/PublishProfiles/WinClickOnce.pubxml",
    [int]$ApplicationRevision = 0,
    [switch]$CleanOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$env:ReleaseTag = $null
$repoRoot = $PSScriptRoot

function Write-Step([string]$Message) {
    Write-Host "==> $Message"
}

function Get-NextLocalTag {
    try {
        $headers = @{ "User-Agent" = "Soulman-Installer" }
        $resp = Invoke-RestMethod -UseBasicParsing -Uri "https://api.github.com/repos/SlimeQ/soulman/releases/latest" -Headers $headers -ErrorAction Stop
        $tag = $resp.tag_name
        if (-not [string]::IsNullOrWhiteSpace($tag)) {
            $tag = $tag.Trim()
            if ($tag.StartsWith("v")) { $tag = $tag.Substring(1) }
            $parts = $tag.Split('.')
            if ($parts.Length -ge 3 -and $parts[0] -as [int] -ne $null) {
                $parts[2] = ([int]$parts[2] + 1).ToString()
                return "v{0}.{1}.{2}b" -f $parts[0], $parts[1], $parts[2]
            }
        }
    } catch {}

    $tags = git tag --list 'v*' | Sort-Object -Descending
    if (-not $tags) { return "v0.1.0b" }

    $latest = $tags[0].TrimStart('v')
    $parts = $latest.Split('.')
    if ($parts.Length -lt 3) { return "v0.1.0b" }

    $parts[2] = ([int]$parts[2] + 1).ToString()
    return "v{0}.{1}.{2}b" -f $parts[0], $parts[1], $parts[2]
}

$env:ReleaseTag = Get-NextLocalTag
Write-Step "Using local install tag $($env:ReleaseTag)"

function ConvertTagToApplicationVersion {
    param([string]$Tag)

    if ([string]::IsNullOrWhiteSpace($Tag)) { return "1.0.0.0" }
    $trim = $Tag.Trim().TrimStart('v', 'V')
    $nums = [regex]::Matches($trim, '\d+')
    if ($nums.Count -eq 0) { return "1.0.0.0" }
    $values = @()
    foreach ($m in $nums) { $values += [int]$m.Value }
    while ($values.Count -lt 4) { $values += 0 }
    return "{0}.{1}.{2}.{3}" -f $values[0], $values[1], $values[2], $values[3]
}

$appVersion = ConvertTagToApplicationVersion $env:ReleaseTag

function Build-SingleInstaller {
    param(
        [string]$PublishDir,
        [string]$OutputExe
    )

    $workDir = Join-Path ([IO.Path]::GetTempPath()) ("soulman_bootstrapper_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null

    $payloadZip = Join-Path $workDir 'payload.zip'
    Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $payloadZip -Force

    $programCs = @"
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using System.Linq;
using System.Diagnostics;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "soulman_installer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            MessageBox.Show("Installer payload is missing.", "Soulman Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (Stream? payload = assembly.GetManifestResourceStream(resourceName))
        {
            string zipPath = Path.Combine(tempRoot, "payload.zip");
            using (FileStream fs = File.Create(zipPath))
            {
                payload?.CopyTo(fs);
            }
            ZipFile.ExtractToDirectory(zipPath, tempRoot);
        }

        if (!EnsureNotRunning())
        {
            return;
        }

        string scriptPath = Path.Combine(tempRoot, "Install-ClickOnce.ps1");
        string setupPath = Path.Combine(tempRoot, "setup.exe");
        string manifestPath = Path.Combine(tempRoot, "Soulman.application");

        if (File.Exists(scriptPath))
        {
            var psi = new ProcessStartInfo("powershell")
            {
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -SourcePath \"{tempRoot}\"",
                UseShellExecute = true
            };
            try
            {
            Process.Start(psi);
            return;
        }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to launch installer script: " + ex.Message, "Soulman Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        string target = File.Exists(setupPath) ? setupPath : manifestPath;

        if (!File.Exists(target))
        {
            MessageBox.Show("Installer payload is incomplete.", "Soulman Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var fallback = new ProcessStartInfo(target) { UseShellExecute = true };

        try
        {
            Process.Start(fallback);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to launch installer: " + ex.Message, "Soulman Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool EnsureNotRunning()
    {
        try
        {
            var running = Process.GetProcessesByName("Soulman").ToList();
            if (running.Count == 0)
            {
                return true;
            }

            var names = string.Join(Environment.NewLine, running.Select(p => $"{p.ProcessName} (PID {p.Id})"));
            var result = MessageBox.Show(
                "Soulman is currently running. The installer needs to close it first.\n\n" + names + "\n\nProceed and close these processes?",
                "Soulman Installer",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                return false;
            }

            foreach (var proc in running)
            {
                try { proc.Kill(); proc.WaitForExit(5000); } catch { }
            }

            return true;
        }
        catch
        {
            return true;
        }
    }
}
"@

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="payload.zip" />
  </ItemGroup>
</Project>
"@

    $programPath = Join-Path $workDir 'Program.cs'
    $projPath = Join-Path $workDir 'InstallerBootstrapper.csproj'
    Set-Content -Path $programPath -Value $programCs -NoNewline
    Set-Content -Path $projPath -Value $csproj -NoNewline
    dotnet publish $projPath -c Release -r win-x64 --self-contained true | Out-Null

    $publishedExe = Get-ChildItem -Path $workDir -Recurse -Filter '*.exe' |
        Where-Object { $_.Name -notmatch 'vshost' } |
        Sort-Object Length -Descending |
        Select-Object -First 1

    if (-not $publishedExe) {
        throw "Failed to locate built installer executable."
    }

    Copy-Item -Path $publishedExe.FullName -Destination $OutputExe -Force
    try { Remove-Item -Recurse -Force $workDir } catch {}
    return $OutputExe
}

$publishDir = Join-Path $repoRoot "src\Soulman\bin\$Configuration\net8.0-windows\publish"
$appPublishDir = Join-Path $repoRoot "src\Soulman\bin\$Configuration\net8.0-windows\app.publish"

if ($CleanOutput) {
    Write-Step "Cleaning previous publish output at $publishDir and $appPublishDir"
    foreach ($dir in @($publishDir, $appPublishDir)) {
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force -Path $dir -ErrorAction SilentlyContinue
        }
    }
}

Write-Step "Publishing ClickOnce payload ($Configuration)"
$publishArgs = @{
    Configuration      = $Configuration
    PublishProfile     = $PublishProfile
    ApplicationRevision = $ApplicationRevision
    ReleaseTag         = $env:ReleaseTag
    ApplicationVersion = $appVersion
}
if ($CleanOutput) { $publishArgs.CleanOutput = $true }
& "$repoRoot\Publish-Installer.ps1" @publishArgs

if (-not (Test-Path $publishDir)) {
    throw "Publish directory not found at $publishDir"
}

$artifactsDir = Join-Path $repoRoot "artifacts\install"
$null = New-Item -ItemType Directory -Path $artifactsDir -Force
$installerExe = Join-Path $artifactsDir "soulman_installer.exe"

Write-Step "Bundling installer to $installerExe"
Build-SingleInstaller -PublishDir $publishDir -OutputExe $installerExe | Out-Null

Write-Step "Launching installer"
Start-Process -FilePath $installerExe

Write-Step "Done"

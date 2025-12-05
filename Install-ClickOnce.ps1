param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:LOCALAPPDATA\soulman-clickonce",
    [switch]$SkipCacheClear
)

$ErrorActionPreference = 'Stop'
$logPath = Join-Path $env:TEMP ("soulman_install_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log")
try { Start-Transcript -Path $logPath -Append -Force | Out-Null } catch {}

function Resolve-Source {
    param([string]$PathArg)
    if ($PathArg) {
        return (Resolve-Path $PathArg).Path
    }

    return (Split-Path -Parent $MyInvocation.MyCommand.Path)
}

function Resolve-Mage {
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\mage.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\mage.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    return $null
}

try {
    $payloadRoot = Resolve-Source $SourcePath
    $manifest = Join-Path $payloadRoot 'Soulman.application'
    if (-not (Test-Path $manifest)) {
        throw "Soulman.application not found under $payloadRoot. Point -SourcePath at the published ClickOnce folder."
    }

    Write-Host "[install] Payload root: $payloadRoot" -ForegroundColor Cyan
    Write-Host "[install] Target location: $InstallRoot" -ForegroundColor Cyan

    if (-not $SkipCacheClear) {
        $mage = Resolve-Mage
        if ($mage) {
            Write-Host "[install] Clearing ClickOnce cache (mage -cc) to avoid prior subscription conflicts..." -ForegroundColor Cyan
            & $mage -cc | Out-Null
        } else {
            Write-Warning "mage.exe not found; skipping ClickOnce cache clear. If install complains about a different location, re-run with mage installed or uninstall the previous Soulman entry first."
        }
    }

    if (Test-Path $InstallRoot) {
        Write-Host "[install] Removing previous staged payload at $InstallRoot" -ForegroundColor Cyan
        Remove-Item -Recurse -Force -Path $InstallRoot
    }

    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null

    $robocopyLog = New-TemporaryFile
    try {
        $robocopyArgs = @($payloadRoot, $InstallRoot, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS')
        robocopy @robocopyArgs | Tee-Object -FilePath $robocopyLog | Out-Null
        $robocode = $LASTEXITCODE
        if ($robocode -gt 7) {
            throw "robocopy failed with exit code $robocode. See $robocopyLog for details."
        }
    }
    finally {
        if (Test-Path $robocopyLog) { Remove-Item $robocopyLog -Force }
    }

    $stagedManifest = Join-Path $InstallRoot 'Soulman.application'
    if (-not (Test-Path $stagedManifest)) {
        throw "Staged manifest missing at $stagedManifest"
    }

    $setupExe = Join-Path $InstallRoot 'setup.exe'
    if (Test-Path $setupExe) {
        Write-Host "[install] Launching ClickOnce setup bootstrapper at $setupExe" -ForegroundColor Cyan
        Start-Process -FilePath $setupExe
        Try-EnsureFirewallRule
        return
    }

    Write-Host "[install] setup.exe not found, launching ClickOnce manifest at $stagedManifest" -ForegroundColor Cyan
    Start-Process -FilePath $stagedManifest
    Try-EnsureFirewallRule
}
catch {
    Write-Error $_
    Write-Host "[install] Failed. See log at $logPath" -ForegroundColor Red
    Write-Host "Press Enter to close this window..." -ForegroundColor Yellow
    [void][Console]::ReadLine()
    exit 1
}
finally {
    try { Stop-Transcript | Out-Null } catch {}
    Write-Host "[install] Log saved to $logPath" -ForegroundColor Cyan
}

function Try-EnsureFirewallRule {
    try {
        $ruleName = "Soulman LAN Discovery (UDP 45832)"
        $existing = $null
        if (Get-Command -Name Get-NetFirewallRule -ErrorAction SilentlyContinue) {
            $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
        }

        if (-not $existing) {
            Write-Host "[install] Adding firewall rule '$ruleName' (Private/Domain, UDP 45832 inbound)" -ForegroundColor Cyan
            if (Get-Command -Name New-NetFirewallRule -ErrorAction SilentlyContinue) {
                New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol UDP `
                    -LocalPort 45832 -Profile Private,Domain -ErrorAction Stop | Out-Null
            } else {
                netsh advfirewall firewall add rule name="$ruleName" dir=in action=allow protocol=UDP localport=45832 profile=private,domain | Out-Null
            }
        }
    } catch {
        Write-Warning "Could not add firewall rule for Soulman discovery (UDP 45832). LAN peer discovery may be blocked until allowed manually. Error: $_"
    }
}

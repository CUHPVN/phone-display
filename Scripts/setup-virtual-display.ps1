<#
.SYNOPSIS
    PhoneDisplay Virtual Display Driver & Firewall Setup Helper for Windows 11.
.DESCRIPTION
    Detects Virtual Display Driver (MttVDD / Virtual-Display-Driver / IddSampleDriver),
    adds common Android landscape resolutions (1600x720, 1920x864, 2340x1080, 2400x1080, 1920x1080)
    at 60Hz and 120Hz, restarts the virtual display adapter if modified, and optionally configures
    Windows Firewall rules for TCP ports 8080 (USB/HTTP) and 8443 (Wi-Fi/HTTPS).
#>
[CmdletBinding()]
param(
    [switch]$AddFirewallRules,
    [switch]$SkipRestart
)

$ErrorActionPreference = "Stop"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Ensure-FirewallRules {
    param([int[]]$Ports = @(8080, 8443))

    if (-not (Test-IsAdmin)) {
        Write-Warning "Administrator privileges are required to add Windows Firewall rules."
        Write-Host "Run PowerShell as Administrator and execute: .\Scripts\setup-virtual-display.ps1 -AddFirewallRules" -ForegroundColor Yellow
        return
    }

    foreach ($port in $Ports) {
        $ruleName = "PhoneDisplay TCP $port"
        $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
        if ($null -eq $existing) {
            New-NetFirewallRule `
                -DisplayName $ruleName `
                -Direction Inbound `
                -Protocol TCP `
                -LocalPort $port `
                -Action Allow `
                -Profile Private,Public `
                -Description "Allow PhoneDisplay Host streaming on TCP port $port" | Out-Null
            Write-Host "[OK] Created Windows Firewall rule: $ruleName" -ForegroundColor Green
        }
        else {
            Write-Host "[OK] Firewall rule already exists: $ruleName" -ForegroundColor Cyan
        }
    }
}

function Configure-MttVddXml {
    param([string]$XmlPath)

    Write-Host "Found Virtual Display Driver XML config: $XmlPath" -ForegroundColor Cyan
    [xml]$doc = Get-Content -Path $XmlPath -Raw

    $resolutionsNode = $doc.SelectSingleNode("//resolutions")
    if ($null -eq $resolutionsNode) {
        Write-Warning "Could not locate <resolutions> node inside $XmlPath. Please verify file format."
        return $false
    }

    $desiredModes = @(
        @{ w = "1600"; h = "720";  r = "60" },
        @{ w = "1600"; h = "720";  r = "120" },
        @{ w = "1920"; h = "864";  r = "60" },
        @{ w = "1920"; h = "864";  r = "120" },
        @{ w = "2340"; h = "1080"; r = "60" },
        @{ w = "2340"; h = "1080"; r = "120" },
        @{ w = "2400"; h = "1080"; r = "60" },
        @{ w = "2400"; h = "1080"; r = "120" },
        @{ w = "1920"; h = "1080"; r = "60" },
        @{ w = "1920"; h = "1080"; r = "120" }
    )

    $added = 0
    foreach ($mode in $desiredModes) {
        $exists = $resolutionsNode.resolution | Where-Object {
            $_.width -eq $mode.w -and $_.height -eq $mode.h -and $_.refresh_rate -eq $mode.r
        }
        if ($null -eq $exists) {
            $resEl = $doc.CreateElement("resolution")
            $wEl = $doc.CreateElement("width"); $wEl.InnerText = $mode.w
            $hEl = $doc.CreateElement("height"); $hEl.InnerText = $mode.h
            $rEl = $doc.CreateElement("refresh_rate"); $rEl.InnerText = $mode.r
            $resEl.AppendChild($wEl) | Out-Null
            $resEl.AppendChild($hEl) | Out-Null
            $resEl.AppendChild($rEl) | Out-Null
            $resolutionsNode.AppendChild($resEl) | Out-Null
            $added++
        }
    }

    if ($added -gt 0) {
        if (-not (Test-IsAdmin)) {
            Write-Warning "Administrator rights needed to write $XmlPath. Re-run PowerShell as Administrator."
            return $false
        }
        $backupPath = "$XmlPath.bak"
        Copy-Item -Path $XmlPath -Destination $backupPath -Force
        $doc.Save($XmlPath)
        Write-Host "[OK] Added $added Android landscape modes to $XmlPath (backup: $backupPath)" -ForegroundColor Green
        return $true
    }
    else {
        Write-Host "[OK] All recommended Android resolutions are already present in $XmlPath." -ForegroundColor Green
        return $false
    }
}

function Configure-IddOptionTxt {
    param([string]$TxtPath)

    Write-Host "Found IddSampleDriver config: $TxtPath" -ForegroundColor Cyan
    $lines = Get-Content -Path $TxtPath -ErrorAction SilentlyContinue
    if ($null -eq $lines) { $lines = @() }

    $desired = @(
        "1600, 720, 60",
        "1600, 720, 120",
        "1920, 864, 60",
        "1920, 864, 120",
        "2340, 1080, 60",
        "2340, 1080, 120",
        "2400, 1080, 60",
        "2400, 1080, 120",
        "1920, 1080, 60",
        "1920, 1080, 120"
    )

    $toAdd = @()
    foreach ($entry in $desired) {
        $normEntry = $entry -replace "\s+", ""
        $found = $lines | Where-Object { ($_ -replace "\s+", "") -eq $normEntry }
        if (-not $found) {
            $toAdd += $entry
        }
    }

    if ($toAdd.Count -gt 0) {
        if (-not (Test-IsAdmin)) {
            Write-Warning "Administrator rights needed to write $TxtPath. Re-run PowerShell as Administrator."
            return $false
        }
        Copy-Item -Path $TxtPath -Destination "$TxtPath.bak" -Force
        Add-Content -Path $TxtPath -Value $toAdd
        Write-Host "[OK] Appended $($toAdd.Count) Android resolutions to $TxtPath." -ForegroundColor Green
        return $true
    }
    else {
        Write-Host "[OK] All recommended Android resolutions are already present in $TxtPath." -ForegroundColor Green
        return $false
    }
}

Write-Host "=== PhoneDisplay Virtual Display Setup ===" -ForegroundColor Magenta

if ($AddFirewallRules) {
    Ensure-FirewallRules
}

$displayDevices = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue
$vddDevices = $displayDevices | Where-Object {
    $_.FriendlyName -match "Virtual Display|MttVDD|IddSampleDriver|Indirect Display|USB Mobile Monitor"
}

if ($vddDevices) {
    Write-Host "[OK] Detected virtual display adapter(s):" -ForegroundColor Green
    $vddDevices | Format-Table Status, FriendlyName, InstanceId -AutoSize

    $configModified = $false
    $xmlCandidates = @(
        "C:\VirtualDisplayDriver\vdd_settings.xml",
        "$env:ProgramFiles\VirtualDisplayDriver\vdd_settings.xml",
        "$env:ProgramData\VirtualDisplayDriver\vdd_settings.xml"
    )
    $txtCandidates = @(
        "C:\IddSampleDriver\option.txt"
    )

    $foundConfig = $false
    foreach ($xmlPath in $xmlCandidates) {
        if (Test-Path $xmlPath) {
            $foundConfig = $true
            if (Configure-MttVddXml -XmlPath $xmlPath) {
                $configModified = $true
            }
            break
        }
    }

    if (-not $foundConfig) {
        foreach ($txtPath in $txtCandidates) {
            if (Test-Path $txtPath) {
                $foundConfig = $true
                if (Configure-IddOptionTxt -TxtPath $txtPath) {
                    $configModified = $true
                }
                break
            }
        }
    }

    if (-not $foundConfig) {
        Write-Host "[INFO] Virtual display adapter found, but standard config file was not found in C:\VirtualDisplayDriver or C:\IddSampleDriver." -ForegroundColor Yellow
        Write-Host "       If your driver uses a companion GUI/XML, add 1600x720, 1920x864, 2340x1080, 2400x1080 (60Hz/120Hz)."
    }

    if ($configModified -and -not $SkipRestart) {
        if (Test-IsAdmin) {
            Write-Host "Restarting virtual display adapter(s) to apply new resolutions..." -ForegroundColor Cyan
            foreach ($dev in $vddDevices) {
                Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false
                Start-Sleep -Milliseconds 700
                Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false
            }
            Write-Host "[OK] Virtual display adapter restarted." -ForegroundColor Green
        }
        else {
            Write-Warning "Please restart the Virtual Display adapter in Device Manager (or reboot) for new resolutions to take effect."
        }
    }
}
else {
    Write-Host "[!] No Virtual Display Driver detected on this PC." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To make Windows treat your Android phone as a true extended monitor, install Virtual-Display-Driver (MTT / IddCx):" -ForegroundColor White
    Write-Host "  1. Download the latest release from: https://github.com/itsmikethetech/Virtual-Display-Driver/releases" -ForegroundColor Cyan
    Write-Host "  2. Run the installer or extract to C:\VirtualDisplayDriver and install via Device Manager / included installer." -ForegroundColor Cyan
    Write-Host "  3. Re-run this script as Administrator:" -ForegroundColor Cyan
    Write-Host "       .\Scripts\setup-virtual-display.ps1 -AddFirewallRules" -ForegroundColor Gray
    Write-Host "  4. Press Win+P -> 'Extend' and verify the new monitor appears in Windows Display Settings." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Tip: Even without a virtual driver, PhoneDisplay can still capture and mirror an existing physical monitor." -ForegroundColor DarkGray
}

if (-not $AddFirewallRules) {
    Write-Host ""
    Write-Host "To automatically open Windows Firewall ports 8080 (USB) and 8443 (Wi-Fi HTTPS), run as Admin:" -ForegroundColor DarkCyan
    Write-Host "  .\Scripts\setup-virtual-display.ps1 -AddFirewallRules" -ForegroundColor Gray
}

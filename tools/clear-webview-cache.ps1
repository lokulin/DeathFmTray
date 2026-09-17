<#
    .SYNOPSIS
    Stops Death.FM Player and clears its WebView2 profile (cookies, cache,
    local storage) so the next launch starts as a fresh, logged-out session -
    handy for re-testing login/chat without a real account to log out of.

    .PARAMETER Relaunch
    Start the app back up after clearing (default: on). Pass -Relaunch:$false
    to just clear and leave it closed.

    .EXAMPLE
    .\tools\clear-webview-cache.ps1

    .EXAMPLE
    .\tools\clear-webview-cache.ps1 -Relaunch:$false
#>

param(
    [switch]$Relaunch = $true
)

$profileDir = Join-Path $env:LOCALAPPDATA "DeathFmTray\WebView2"

# Remember where the running instance (if any) was launched from, so the
# same build gets relaunched afterwards rather than guessing.
$running = Get-Process DeathFmTray -ErrorAction SilentlyContinue
$exePath = $running | Select-Object -First 1 -ExpandProperty Path

if ($running) {
    Write-Host "Stopping Death.FM Player..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if (Test-Path $profileDir) {
    Write-Host "Clearing WebView2 profile at $profileDir..."
    Remove-Item $profileDir -Recurse -Force
} else {
    Write-Host "No WebView2 profile found - nothing to clear."
}

if ($Relaunch) {
    if (-not $exePath) {
        # Fall back to the usual install location, then the local dev builds.
        $candidates = @(
            "$env:ProgramFiles\DeathFM player\DeathFmTray.exe",
            (Join-Path $PSScriptRoot "..\bin\Debug\net10.0-windows10.0.19041.0\DeathFmTray.exe"),
            (Join-Path $PSScriptRoot "..\bin\Release\net10.0-windows10.0.19041.0\DeathFmTray.exe")
        )
        $exePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if ($exePath) {
        Write-Host "Relaunching $exePath..."
        Start-Process $exePath
    } else {
        Write-Warning "Couldn't find DeathFmTray.exe to relaunch - start it manually."
    }
}

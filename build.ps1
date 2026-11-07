<#
.SYNOPSIS
    Grepdesk Build & Packaging CLI Wrapper for PowerShell.

.DESCRIPTION
    Automates building, testing, publishing, and creating installers/packages
    for Grepdesk across target operating systems.

.PARAMETER Action
    The action to execute: all, build, test, publish, installer, portable, clean. Default is 'all'.

.PARAMETER OS
    Target operating system: current (default), windows, linux, macos.

.PARAMETER Config
    Configuration: Release (default) or Debug.

.PARAMETER NoTest
    Skip running tests.

.PARAMETER NoInstaller
    Skip building the Inno Setup installer.

.PARAMETER Clean
    Clean build artifacts and temporary files.

.EXAMPLE
    .\build.ps1
    .\build.ps1 publish -OS windows
    .\build.ps1 installer
    .\build.ps1 portable
    .\build.ps1 clean
#>

[CmdletBinding()]
param(
    [ValidateSet("all", "build", "test", "publish", "installer", "portable", "clean")]
    [string]$Action = "all",

    [string]$OS = "current",

    [ValidateSet("Release", "Debug")]
    [string]$Config = "Release",

    [switch]$NoTest,

    [switch]$NoInstaller,

    [switch]$Clean,

    [switch]$DistClean,

    [switch]$Version
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Check if Python is available to run build.py
$pythonCmd = Get-Command python -ErrorAction SilentlyContinue

if ($pythonCmd) {
    $pyArgs = @("$ScriptDir\build.py")
    if ($Clean) {
        $pyArgs += "clean"
    } else {
        $pyArgs += $Action
    }
    $pyArgs += @("--os", $OS)
    $pyArgs += @("--config", $Config)
    if ($NoTest) { $pyArgs += "--no-test" }
    if ($NoInstaller) { $pyArgs += "--no-installer" }
    if ($DistClean) { $pyArgs += "--distclean" }
    if ($Version) { $pyArgs += "--version" }

    & python @pyArgs
    exit $LASTEXITCODE
} else {
    Write-Host "Python was not found on PATH. Please install Python or use dotnet CLI directly." -ForegroundColor Red
    exit 1
}

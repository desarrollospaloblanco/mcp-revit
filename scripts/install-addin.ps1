<#
.SYNOPSIS
    Builds and installs the Revit add-in of this fork for one Revit version, and registers every
    command it ships so they are available without opening the plugin's Settings.

.DESCRIPTION
    - Refuses to run while Revit is open (Revit locks the DLLs it loaded).
    - Optionally builds the add-in (dotnet) and the MCP server (npm).
    - Backs up what is installed to %USERPROFILE%\mcp-revit-backups\<timestamp>\.
    - Copies the .addin manifest and the revit_mcp_plugin folder (plugin + command set) to
      %APPDATA%\Autodesk\Revit\Addins\<version>\.
    - Adds to Commands\commandRegistry.json every command of command.json that is missing, enabled.
      The plugin only loads commands listed in that registry; command.json alone is not enough, and
      a fresh install starts with an empty registry.

    After it runs: open Revit, click "Revit MCP Switch" on the plugin's ribbon panel, and point
    your MCP client at server\build\index.js (see README).

.EXAMPLE
    .\scripts\install-addin.ps1 -RevitVersion 2025 -Build
.EXAMPLE
    .\scripts\install-addin.ps1 -RevitVersion 2026 -DryRun
#>
param(
    [ValidateSet('2023', '2024', '2025', '2026')]
    [string]$RevitVersion = '2025',
    [switch]$Build,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$short = $RevitVersion.Substring(2)
$configuration = "Release R$short"
$source = Join-Path $root "plugin\bin\AddIn $RevitVersion $configuration"
$target = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"

function Step($text) { Write-Host "-> $text" -ForegroundColor Cyan }

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    throw "Revit is running. Close it first: it locks the add-in DLLs."
}

if ($Build) {
    Step "Building the add-in ($configuration)"
    if (-not $DryRun) {
        dotnet build (Join-Path $root 'mcp-servers-for-revit.sln') -c $configuration -v q -nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    }
    Step "Building the MCP server"
    if (-not $DryRun) {
        Push-Location (Join-Path $root 'server')
        npm install --silent
        npm run build
        if ($LASTEXITCODE -ne 0) { Pop-Location; throw "npm run build failed" }
        Pop-Location
    }
}

if (-not (Test-Path (Join-Path $source 'revit_mcp_plugin\RevitMCPPlugin.dll'))) {
    throw "No build found at '$source'. Run with -Build, or build '$configuration' first."
}
$commandSetDll = Join-Path $source "revit_mcp_plugin\Commands\RevitMCPCommandSet\$RevitVersion\RevitMCPCommandSet.dll"
if (-not (Test-Path $commandSetDll)) {
    throw "The command set is missing from the plugin output ($commandSetDll). Build the whole solution."
}

# --- backup ---
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $env:USERPROFILE "mcp-revit-backups\$stamp-$RevitVersion"
if (Test-Path (Join-Path $target 'revit_mcp_plugin')) {
    Step "Backing up the installed add-in to $backup"
    if (-not $DryRun) {
        New-Item -ItemType Directory -Force $backup | Out-Null
        Copy-Item (Join-Path $target 'revit_mcp_plugin') $backup -Recurse
        $manifest = Join-Path $target 'mcp-servers-for-revit.addin'
        if (Test-Path $manifest) { Copy-Item $manifest $backup }
    }
}

# --- copy ---
Step "Installing to $target"
if (-not $DryRun) {
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $source 'mcp-servers-for-revit.addin') $target -Force
    Copy-Item (Join-Path $source 'revit_mcp_plugin') $target -Recurse -Force
    Copy-Item (Join-Path $root 'command.json') (Join-Path $target 'revit_mcp_plugin\Commands\RevitMCPCommandSet\command.json') -Force
}

# --- registry ---
$registryPath = Join-Path $target 'revit_mcp_plugin\Commands\commandRegistry.json'
$commands = (Get-Content (Join-Path $root 'command.json') -Raw | ConvertFrom-Json)
$registry = if (Test-Path $registryPath) { Get-Content $registryPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ Commands = @() } }
if (-not $registry.Commands) { $registry | Add-Member -Force Commands @() }

$present = @($registry.Commands | ForEach-Object { $_.commandName })
$added = @()
$list = [System.Collections.ArrayList]@($registry.Commands)
foreach ($command in $commands.commands) {
    if ($present -contains $command.commandName) { continue }
    [void]$list.Add([pscustomobject]@{
        commandName            = $command.commandName
        assemblyPath           = 'RevitMCPCommandSet\{VERSION}\RevitMCPCommandSet.dll'
        enabled                = $true
        supportedRevitVersions = @($RevitVersion)
        developer              = $commands.developer
        description            = $command.description
    })
    $added += $command.commandName
}
$registry.Commands = $list.ToArray()
Step ("Registering {0} new command(s): {1}" -f $added.Count, ($added -join ', '))
if (-not $DryRun) {
    $registry | ConvertTo-Json -Depth 10 | Set-Content $registryPath -Encoding utf8
}

Write-Host ""
Write-Host ("Done{0}. Open Revit {1}, click 'Revit MCP Switch', and restart your MCP client." -f ($(if ($DryRun) { ' (dry run, nothing changed)' } else { '' }), $RevitVersion)) -ForegroundColor Green

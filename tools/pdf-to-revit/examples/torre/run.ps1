<#
.SYNOPSIS
    Runs the whole extraction for the tower example and writes out\torre_spec.json.

.EXAMPLE
    .\run.ps1 -Tipico "C:\planos\Nivel tipico.pdf" -Sotano "C:\planos\Sótanos.pdf"

    Then build it (Revit open, MCP service switched on):
    node ..\..\..\..\scripts\revit-call.mjs build_model_from_spec '{"specPath":"<full path>\\out\\torre_spec.json","specName":"torre-anteproyecto","documentTitle":"TORRE-ANTEPROYECTO","dryRun":true}' 1800
#>
param(
    [Parameter(Mandatory)][string]$Tipico,
    [Parameter(Mandatory)][string]$Sotano,
    [int]$TowerWallLevels = 22,
    [string]$Python = 'python'
)

$ErrorActionPreference = 'Stop'
$env:PYTHONIOENCODING = 'utf-8'

# Native commands do not stop on $ErrorActionPreference; check every exit code.
function Invoke-Step([string[]]$Arguments) {
    & $Python @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Step failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}

Push-Location $PSScriptRoot
try {
    & $Python -c "import pymupdf, shapely, numpy" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "$Python is missing the extractor's packages: pip install -r $(Resolve-Path ..\..\requirements.txt)"
    }
    New-Item -ItemType Directory -Force out | Out-Null
    $tools = '..\..'
    Invoke-Step "$tools\pdf_walls.py", $Tipico, 'tipico.json', 'out\tipico_walls_raw.json', 'out\tipico_walls_raw.png'
    Invoke-Step "$tools\pdf_walls_refine.py", 'out\tipico_walls_raw.json', 'out\tipico_walls.json'
    Invoke-Step "$tools\pdf_openings.py", $Tipico, 'tipico.json', 'out\tipico_walls.json', 'out\tipico_walls_bridged.json', 'out\tipico_openings.json', 'out\tipico_openings.png'
    Invoke-Step "$tools\pdf_footprint.py", $Tipico, 'tipico.json', 'out\tipico_walls.json', 'out\tipico_outline.json', 'out\tipico_outline.png'
    Invoke-Step "$tools\pdf_footprint.py", $Sotano, 'sotano.json', '-', 'out\sotano_outline.json', 'out\sotano_outline.png'
    Invoke-Step "$tools\pdf_review.py", $Tipico, 'tipico.json', 'out\tipico_walls_bridged.json', 'out\revision'
    Invoke-Step 'build_spec.py', 'out\torre_spec.json', "$TowerWallLevels"
    Write-Host "Spec written to $(Resolve-Path out\torre_spec.json). Review the PNGs in out\ before building." -ForegroundColor Green
}
finally {
    Pop-Location
}

<#
.SYNOPSIS
    Runs the canopy solver's behavioural checks, and - if KSP is installed - fits every
    parachute model in the install.

.DESCRIPTION
    Source/ParaSoft/Core has no Unity dependency, so it compiles straight into a console
    program next to Tools/Tests.cs. The tests drop canopies into scripted conditions -
    steady descent, a pack deployment, vacuum, Duna, a crosswind, reefing, ground, waves,
    an obstacle, a cut, Mach 1.8, a 250 m/s opening - and check the results.

    With a KSP install present it also runs Tools/ModelReport.cs, which reads the stock,
    ReStock and RealChute parachute models, fits them, and checks that every vertex
    reproduces exactly through the embedding. Side-view plots land in build/fits/.

    Needs nothing but the .NET Framework compiler.

.PARAMETER KspDir
    Your KSP install, for the model checks. Defaults to $env:KSP_DIR, then Steam's.

.PARAMETER SkipModels
    Only run the solver tests.

.EXAMPLE
    .\Tools\Test.ps1
#>
[CmdletBinding()]
param(
    [string]$KspDir,
    [switch]$SkipModels
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$core = Get-ChildItem -Path (Join-Path $repo 'Source\ParaSoft\Core') -Filter *.cs | ForEach-Object { $_.FullName }
$build = Join-Path $repo 'build'
if (-not (Test-Path $build)) { New-Item -ItemType Directory -Path $build | Out-Null }

$csc = $null
foreach ($candidate in @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe")) {
    if (Test-Path $candidate) { $csc = $candidate; break }
}
if (-not $csc) { throw 'No C# compiler found. Install the .NET Framework 4.x developer files.' }

# --- solver tests ----------------------------------------------------------------

$exe = Join-Path $build 'tests.exe'
$output = & $csc /nologo /target:exe /optimize+ "/out:$exe" (Join-Path $PSScriptRoot 'Tests.cs') $core 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $output
    throw "Test build failed (exit $LASTEXITCODE)."
}
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Solver checks failed.' }

# --- real models -----------------------------------------------------------------

if ($SkipModels) { return }
if (-not $KspDir) { $KspDir = $env:KSP_DIR }
if (-not $KspDir) { $KspDir = 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }
if (-not (Test-Path (Join-Path $KspDir 'GameData'))) {
    Write-Host "No KSP install at '$KspDir' - skipping the model checks." -ForegroundColor Yellow
    return
}

$report = Join-Path $build 'modelreport.exe'
$output = & $csc /nologo /target:exe /optimize+ /r:System.Drawing.dll "/out:$report" `
    (Join-Path $PSScriptRoot 'ModelReport.cs') (Join-Path $PSScriptRoot 'MuModel.cs') $core 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host $output
    throw "Model report build failed (exit $LASTEXITCODE)."
}
Write-Host ''
Write-Host 'Fitting the parachute models in your install:'
& $report $KspDir (Join-Path $build 'fits')
if ($LASTEXITCODE -ne 0) { throw 'Model checks failed.' }

<#
.SYNOPSIS
    Compiles ParaSoft.dll straight against the KSP assemblies in your install.

.DESCRIPTION
    No SDK, no NuGet restore, no Visual Studio. The C# compiler that ships with the
    .NET Framework is enough - the source targets C# 5 deliberately so this stays true.

    The output lands in ParaSoft/Plugins/, which is the folder that gets zipped into a
    release. Building never touches your GameData; pass -Install to copy the finished
    ParaSoft folder there.

.PARAMETER KspDir
    Your KSP install. Defaults to $env:KSP_DIR, then to the usual Steam location.

.PARAMETER Install
    Also copy the built ParaSoft folder into that install's GameData.

.EXAMPLE
    .\Tools\Build.ps1
    .\Tools\Build.ps1 -KspDir "D:\Games\KSP" -Install
#>
[CmdletBinding()]
param(
    [string]$KspDir,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $repo 'Source\ParaSoft'
$outputDir = Join-Path $repo 'ParaSoft\Plugins'
$outputDll = Join-Path $outputDir 'ParaSoft.dll'

# --- locate KSP ------------------------------------------------------------------

if (-not $KspDir) { $KspDir = $env:KSP_DIR }
if (-not $KspDir) { $KspDir = 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }

$managed = Join-Path $KspDir 'KSP_x64_Data\Managed'
if (-not (Test-Path $managed)) {
    throw "Could not find KSP's managed assemblies at '$managed'. Pass -KspDir or set KSP_DIR."
}
Write-Host "KSP:      $KspDir"

# --- locate a compiler -----------------------------------------------------------

$csc = $null
foreach ($candidate in @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe")) {
    if (Test-Path $candidate) { $csc = $candidate; break }
}
if (-not $csc) { throw 'No C# compiler found. Install the .NET Framework 4.x developer files.' }
Write-Host "Compiler: $csc"

# --- references ------------------------------------------------------------------

$refNames = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'Assembly-CSharp.dll',
    'Assembly-CSharp-firstpass.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.PhysicsModule.dll',
    'UnityEngine.AnimationModule.dll',
    # Part implements a uGUI click handler, so the whole type is unusable without these.
    'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.TextRenderingModule.dll'
)

$references = @()
foreach ($name in $refNames) {
    $path = Join-Path $managed $name
    if (-not (Test-Path $path)) { throw "Missing reference assembly: $path" }
    $references += $path
}

# --- compile ---------------------------------------------------------------------

$sources = Get-ChildItem -Path $sourceDir -Filter *.cs -Recurse | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No .cs files under $sourceDir" }
Write-Host "Sources:  $($sources.Count) files"

if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }

$rsp = Join-Path $env:TEMP "parasoft-build-$PID.rsp"
$lines = @(
    '/target:library',
    '/nostdlib+',
    '/optimize+',
    '/debug-',
    '/warnaserror-',
    '/warn:4',
    "/out:`"$outputDll`""
)
foreach ($r in $references) { $lines += "/reference:`"$r`"" }
foreach ($s in $sources) { $lines += "`"$s`"" }
Set-Content -Path $rsp -Value $lines -Encoding UTF8

try {
    # /noconfig has to be on the command line: csc ignores it inside a response file,
    # and without it the default csc.rsp drags in the framework's own System.dll on top
    # of KSP's, which collides.
    $output = & $csc /noconfig "@$rsp" 2>&1 | Out-String
    $exit = $LASTEXITCODE
} finally {
    Remove-Item $rsp -Force -ErrorAction SilentlyContinue
}

Write-Host $output
if ($exit -ne 0) { throw "Compilation failed (exit $exit)." }

$size = [math]::Round((Get-Item $outputDll).Length / 1KB, 1)
Write-Host "Built:    $outputDll ($size KB)" -ForegroundColor Green

# --- optional install ------------------------------------------------------------

if ($Install) {
    $target = Join-Path $KspDir 'GameData\ParaSoft'
    $sourcePack = Join-Path $repo 'ParaSoft'
    Write-Host "Installing to $target"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $sourcePack $target -Recurse -Force
    Write-Host 'Installed.' -ForegroundColor Green
}

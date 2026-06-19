param(
    [string]$RevitVersion = "2026",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

function Resolve-ProjectRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

$projectRoot = Resolve-ProjectRoot
$addinProject = Join-Path $projectRoot "src\RevitAddin\IrisRevitMcpAddin.csproj"
$addinTemplate = Join-Path $projectRoot "src\RevitAddin\IrisRevitMcp.addin"
$revitInstallDir = "C:\Program Files\Autodesk\Revit $RevitVersion"
$revitApi = Join-Path $revitInstallDir "RevitAPI.dll"
$addinTargetDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$addinTarget = Join-Path $addinTargetDir "IrisRevitMcp.addin"
$builtDll = Join-Path $projectRoot "src\RevitAddin\bin\$Configuration\net8.0-windows\IrisRevitMcpAddin.dll"

Write-Host "Installing IRIS Revit MCP add-in"
Write-Host "Project root:  $projectRoot"
Write-Host "Revit version: $RevitVersion"
Write-Host "Revit API:     $revitApi"
Write-Host ""

if (-not (Test-Path -LiteralPath $revitApi)) {
    [Console]::Error.WriteLine("Install failed: Revit API was not found at '$revitApi'. Pass -RevitVersion with your installed Revit version.")
    exit 1
}

dotnet build $addinProject --configuration $Configuration /p:RevitVersion=$RevitVersion
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $builtDll)) {
    [Console]::Error.WriteLine("Install failed: Built add-in DLL was not found at '$builtDll'.")
    exit 1
}

New-Item -ItemType Directory -Force -Path $addinTargetDir | Out-Null
try {
    Copy-Item -LiteralPath $addinTemplate -Destination $addinTarget -Force
}
catch {
    [Console]::Error.WriteLine("Install failed: could not write '$addinTarget'. Close Revit $RevitVersion and try again. If the file is still locked, delete the existing IrisRevitMcp.addin manifest from that folder and rerun this installer.")
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

[xml]$manifest = Get-Content -LiteralPath $addinTarget
$assemblyNode = $manifest.SelectSingleNode("//Assembly")
if ($null -eq $assemblyNode) {
    [Console]::Error.WriteLine("Install failed: manifest does not contain an Assembly element.")
    exit 1
}

$assemblyNode.InnerText = [string]$builtDll
$manifest.Save($addinTarget)

Write-Host ""
Write-Host "Installed manifest:"
Write-Host $addinTarget
Write-Host ""
Write-Host "Assembly path:"
Write-Host $builtDll
Write-Host ""
Write-Host "Restart Revit $RevitVersion after installing. The named pipe is created only after Revit loads this add-in."

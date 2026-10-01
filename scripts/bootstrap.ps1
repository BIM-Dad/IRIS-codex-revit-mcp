[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& dotnet build (Join-Path $root 'src\RevitAddin\IrisRevitMcpAddin.csproj') -c Release /p:RevitVersion=2026 --nologo --verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'Revit add-in build failed.' }
& node --check (Join-Path $root 'src\mcp-server\server.js')
if ($LASTEXITCODE -ne 0) { throw 'MCP server syntax check failed.' }

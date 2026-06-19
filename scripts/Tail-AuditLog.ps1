param(
    [string]$AuditLogPath,
    [int]$Tail = 10
)

$ErrorActionPreference = "Stop"

function Resolve-ProjectRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

$projectRoot = Resolve-ProjectRoot
if ([string]::IsNullOrWhiteSpace($AuditLogPath)) {
    $AuditLogPath = Join-Path $projectRoot "logs\audit.jsonl"
}

$AuditLogPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AuditLogPath)

Write-Host "IRIS Revit MCP audit log"
Write-Host "Audit log: $AuditLogPath"
Write-Host "Tail:      $Tail"
Write-Host ""

if (-not (Test-Path -LiteralPath $AuditLogPath)) {
    Write-Host "No audit log exists yet. Run scripts\SmokeTest-McpServer.cmd or call an MCP tool first."
    exit 0
}

Get-Content -LiteralPath $AuditLogPath -Tail $Tail

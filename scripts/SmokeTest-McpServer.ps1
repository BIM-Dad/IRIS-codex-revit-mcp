param(
    [string]$NodeExe = "node",
    [string]$ServerScript,
    [string]$PipeName = "IRIS.RevitMcpBridge.v1",
    [string]$AuditLogPath
)

$ErrorActionPreference = "Stop"

function Resolve-ProjectRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Test-Node {
    param([string]$NodeCommand)

    try {
        $null = & $NodeCommand --version
    }
    catch {
        throw "Node.js was not found using '$NodeCommand'. Install Node.js 18+ or pass -NodeExe with the full path to node.exe."
    }
}

function Resolve-NodeExe {
    param([string]$NodeCommand)

    try {
        $null = & $NodeCommand --version
        return $NodeCommand
    }
    catch {
        $candidatePaths = @(
            (Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\nodejs\node.exe"),
            "C:\Program Files\nodejs\node.exe",
            "C:\Program Files (x86)\nodejs\node.exe"
        )

        foreach ($candidatePath in $candidatePaths) {
            if (Test-Path -LiteralPath $candidatePath) {
                try {
                    $null = & $candidatePath --version
                    return $candidatePath
                }
                catch {
                    continue
                }
            }
        }

        throw "Node.js was not found using '$NodeCommand'. Install Node.js 18+ or pass -NodeExe with the full path to node.exe."
    }
}

function Start-McpServer {
    param(
        [string]$NodeCommand,
        [string]$ScriptPath,
        [string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $NodeCommand
    $escapedScriptPath = $ScriptPath.Replace('"', '\"')
    $startInfo.Arguments = "`"$escapedScriptPath`""
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    if (-not $process.Start()) {
        throw "Failed to start MCP server."
    }

    return $process
}

function Invoke-McpRequest {
    param(
        [System.Diagnostics.Process]$Process,
        [hashtable]$Message
    )

    $json = $Message | ConvertTo-Json -Depth 20 -Compress
    $Process.StandardInput.WriteLine($json)
    $Process.StandardInput.Flush()

    $line = $Process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($line)) {
        $stderr = $Process.StandardError.ReadToEnd()
        throw "MCP server returned no response. $stderr"
    }

    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) {
        throw "MCP error for '$($Message.method)': $($response.error.message)"
    }

    return $response
}

function Invoke-McpTool {
    param(
        [System.Diagnostics.Process]$Process,
        [string]$ToolName
    )

    $response = Invoke-McpRequest -Process $Process -Message @{
        jsonrpc = "2.0"
        id = Get-Random
        method = "tools/call"
        params = @{
            name = $ToolName
            arguments = @{}
        }
    }

    if ($response.result.isError -eq $true) {
        $message = ($response.result.content | ForEach-Object { $_.text }) -join [Environment]::NewLine
        if ($message -match "EPERM") {
            throw "The Revit named pipe exists, but Windows refused the connection. Make sure Revit and this smoke test are running under the same Windows user and privilege level, then restart Revit and try again. Server response: $message"
        }

        if ($message -match "Could not connect to Revit bridge|ENOENT|timed out|Timed out") {
            throw "Could not reach the Revit named pipe. For Revit 2026, run scripts\Install-RevitAddin.cmd -RevitVersion 2026, restart Revit, open a model, then run this script again. Server response: $message"
        }

        throw "Tool '$ToolName' failed. Server response: $message"
    }

    $text = ($response.result.content | ForEach-Object { $_.text }) -join [Environment]::NewLine
    try {
        return $text | ConvertFrom-Json
    }
    catch {
        return $text
    }
}

$projectRoot = Resolve-ProjectRoot
if ([string]::IsNullOrWhiteSpace($ServerScript)) {
    $ServerScript = Join-Path $projectRoot "src\mcp-server\server.js"
}

if ([string]::IsNullOrWhiteSpace($AuditLogPath)) {
    $AuditLogPath = Join-Path $projectRoot "logs\audit.jsonl"
}

$ServerScript = (Resolve-Path $ServerScript).Path
$AuditLogPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AuditLogPath)
$pipePath = "\\.\pipe\$PipeName"

Write-Host "IRIS Revit MCP local smoke test"
Write-Host "Project root: $projectRoot"
Write-Host "MCP server:   $ServerScript"
Write-Host "Named pipe:   $pipePath"
Write-Host "Audit log:    $AuditLogPath"
Write-Host ""

$server = $null
$previousPipeEnv = $env:IRIS_REVIT_MCP_PIPE
try {
    $NodeExe = Resolve-NodeExe -NodeCommand $NodeExe
    Test-Node -NodeCommand $NodeExe
    Write-Host "Node.js:      $NodeExe"
    Write-Host ""

    $env:IRIS_REVIT_MCP_PIPE = $pipePath
    $env:IRIS_REVIT_MCP_AUDIT_LOG = $AuditLogPath
    $server = Start-McpServer -NodeCommand $NodeExe -ScriptPath $ServerScript -WorkingDirectory $projectRoot

    $null = Invoke-McpRequest -Process $server -Message @{
        jsonrpc = "2.0"
        id = 1
        method = "initialize"
        params = @{
            protocolVersion = "2024-11-05"
        }
    }

    Write-Host "Calling get_active_document_info..."
    $documentInfo = Invoke-McpTool -Process $server -ToolName "get_active_document_info"
    $documentInfo | ConvertTo-Json -Depth 20
    Write-Host ""

    Write-Host "Calling list_sheets..."
    $sheets = Invoke-McpTool -Process $server -ToolName "list_sheets"
    $sheets | ConvertTo-Json -Depth 20
    Write-Host ""

    $sheetCount = if ($sheets -is [array]) { $sheets.Count } elseif ($null -ne $sheets) { 1 } else { 0 }
    Write-Host "Smoke test passed. Active document responded and list_sheets returned $sheetCount sheet record(s)."
    Write-Host "Audit log updated: $AuditLogPath"
    Write-Host "View recent audit entries with: scripts\Tail-AuditLog.cmd"
}
catch {
    [Console]::Error.WriteLine("Smoke test failed: $($_.Exception.Message)")
    [Console]::Error.WriteLine("Audit log: $AuditLogPath")
    [Console]::Error.WriteLine("View recent audit entries with: scripts\Tail-AuditLog.cmd")
    exit 1
}
finally {
    $env:IRIS_REVIT_MCP_PIPE = $previousPipeEnv

    if ($null -ne $server -and -not $server.HasExited) {
        $server.StandardInput.Close()
        if (-not $server.WaitForExit(2000)) {
            $server.Kill()
        }
        $server.Dispose()
    }
}

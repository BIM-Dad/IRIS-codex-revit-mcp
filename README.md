# Revit MCP Integration Proof of Concept

Status: Phase 1 proof of concept

This candidate explores a local Model Context Protocol (MCP) integration for safe, read-first Revit access. The initial implementation exposes sheet, view, document, and QA/QC inspection tools without modifying the model.

Phase 1 validation is captured in [docs/phase-1-validation.md](docs/phase-1-validation.md).

## Architecture

```text
AI MCP client
  |
  | MCP over stdio
  v
Node MCP server
  |
  | local named pipe: \\.\pipe\IRIS.RevitMcpBridge.v1
  v
Revit add-in
  |
  | ExternalEvent marshaling
  v
Revit API
```

The Revit add-in owns all Revit API access. The MCP server is intentionally thin: it exposes MCP tools, forwards validated requests to Revit, and appends an audit log entry for every tool call.

## Project Layout

```text
src/
  RevitAddin/     Revit-hosted C# add-in and named-pipe bridge
  mcp-server/     Local Node MCP stdio server
examples/         Sample read-only proposal inputs
logs/             Runtime audit logs
```

Keep these two runtime pieces separated:

- `src/RevitAddin` is loaded by Revit and is the only code that references the Revit API.
- `src/mcp-server` is launched by Codex or another MCP client and never references Revit API DLLs directly.

## Safety Model

- Phase 1 tools are read-only.
- No open-ended "execute Revit API code" endpoint exists.
- The MCP server only accepts known tool names.
- The Revit add-in only executes known bridge commands.
- Phase 2 write tools are intentionally not implemented in this proof of concept.
- Future write tools must require explicit approval and run inside a Revit transaction.

## Phase 1 Tools

- `get_active_document_info`
- `list_sheets`
- `list_views`
- `check_duplicate_sheet_numbers`
- `check_missing_titleblock_parameters`
- `check_sheet_standards`
- `propose_sheet_renames_from_csv_or_json`

`propose_sheet_renames_from_csv_or_json` is read-only. It compares active Revit sheets against CSV or JSON proposal data and returns proposed changes without applying them.

`check_sheet_standards` is read-only. It returns a structured QA/QC report for each sheet and checks:

- Duplicate sheet numbers.
- Empty sheet names.
- Placeholder sheets.
- Non-placeholder sheets with no titleblock.
- Sheets with more than one titleblock.
- Missing or blank required titleblock parameters.
- Sheet number format issues.
- Basic sheet name formatting issues such as leading/trailing whitespace, repeated spaces, `Unnamed`, or no uppercase letters.

Default `check_sheet_standards` settings:

```json
{
  "requiredTitleblockParameters": ["Project Number", "Drawn By", "Checked By"],
  "sheetNumberRegex": "^[A-Z]+[0-9]{3}(\\.[0-9]{2})?$",
  "flagPlaceholderSheets": true
}
```

Placeholder sheets are reported when `flagPlaceholderSheets` is true, but missing titleblock is not flagged for placeholder sheets.

Expected proposal fields:

- `currentNumber` or `current_number`
- `newNumber` or `new_number`
- `newName` or `new_name`

## Setup And Test

### Prerequisites

- Windows with Revit installed.
- .NET SDK/build tools compatible with your target Revit version.
- Node.js 18 or newer for the MCP server.

### 1. Build the Revit Add-In

The project defaults to Revit 2026. Pass `RevitVersion` if you are targeting a different installed Revit version.

```powershell
cd opportunities\revit-mcp-integration\src\RevitAddin
dotnet build --configuration Debug /p:RevitVersion=2026
```

The default Debug build output is:

```text
src\RevitAddin\bin\Debug\net8.0-windows\IrisRevitMcpAddin.dll
```

### 2. Load the Add-In in Revit

Recommended for Revit 2026:

```cmd
cd opportunities\revit-mcp-integration
scripts\Install-RevitAddin.cmd -RevitVersion 2026
```

This builds the add-in, copies the `.addin` manifest to the correct Revit add-ins folder, and updates the manifest `Assembly` path.

Manual install: copy `src\RevitAddin\IrisRevitMcp.addin` into your Revit add-ins folder. For Revit 2026:

```text
%APPDATA%\Autodesk\Revit\Addins\2026
```

Update the manifest `Assembly` value to the absolute path of the built DLL. Example:

```xml
<Assembly>C:\Users\GerardoRuiz-King\OneDrive - Symetri\Document\IRIS\opportunities\revit-mcp-integration\src\RevitAddin\bin\Debug\net8.0-windows\IrisRevitMcpAddin.dll</Assembly>
```

Then start Revit and open a model. The add-in starts the local named-pipe bridge automatically during Revit startup:

```text
\\.\pipe\IRIS.RevitMcpBridge.v1
```

### 3. Run the Local MCP Server

From this folder:

```powershell
cd opportunities\revit-mcp-integration
node src\mcp-server\server.js
```

This server uses MCP over stdio. For normal Codex usage, Codex starts it for you, so you usually configure the command instead of running it manually.

Optional environment variables:

- `IRIS_REVIT_MCP_PIPE`: defaults to `\\.\pipe\IRIS.RevitMcpBridge.v1`
- `IRIS_REVIT_MCP_AUDIT_LOG`: defaults to `logs\audit.jsonl`

### 4. Connect Codex to the MCP Server

Codex uses stdio MCP servers from `config.toml`. The server command should launch:

```powershell
node C:\Users\GerardoRuiz-King\OneDrive - Symetri\Document\IRIS\opportunities\revit-mcp-integration\src\mcp-server\server.js
```

Add this block to `~/.codex/config.toml`, or to a trusted project `.codex/config.toml`:

```toml
[mcp_servers.iris_revit]
command = "node"
args = ["C:\\Users\\GerardoRuiz-King\\OneDrive - Symetri\\Document\\IRIS\\opportunities\\revit-mcp-integration\\src\\mcp-server\\server.js"]
cwd = "C:\\Users\\GerardoRuiz-King\\OneDrive - Symetri\\Document\\IRIS\\opportunities\\revit-mcp-integration"
startup_timeout_sec = 20
tool_timeout_sec = 60
enabled = true
enabled_tools = [
  "get_active_document_info",
  "list_sheets",
  "list_views",
  "check_duplicate_sheet_numbers",
  "check_missing_titleblock_parameters",
  "check_sheet_standards",
  "propose_sheet_renames_from_csv_or_json",
]

[mcp_servers.iris_revit.env]
IRIS_REVIT_MCP_PIPE = "\\\\.\\pipe\\IRIS.RevitMcpBridge.v1"
IRIS_REVIT_MCP_AUDIT_LOG = "C:\\Users\\GerardoRuiz-King\\OneDrive - Symetri\\Document\\IRIS\\opportunities\\revit-mcp-integration\\logs\\audit.jsonl"
```

The same example is available at `examples\codex-config.toml`.

If Codex cannot find `node`, replace `command = "node"` with the full path to Node, for example:

```toml
command = "C:\\Users\\GerardoRuiz-King\\.cache\\codex-runtimes\\codex-primary-runtime\\dependencies\\node\\bin\\node.exe"
```

Restart or reload Codex after adding the server entry. In the Codex CLI TUI, use `/mcp` to confirm `iris_revit` is active and tools are visible. The MCP server must be launched by Codex as a stdio process; do not point Codex at a TCP port for this proof of concept.

### Codex Test Prompt

After Revit 2026 is open with a model loaded, start a new Codex thread or reload the current one and run:

```text
Use the iris_revit MCP server.

Call get_active_document_info and summarize the active Revit document.
Then call list_sheets and report the sheet count plus the first 10 sheet numbers and names.
Then call check_duplicate_sheet_numbers and report whether any duplicate sheet numbers exist.
Then call check_sheet_standards with default settings and summarize the issue counts by category.

Do not modify the Revit model.
```

### 5. Test `list_sheets` From an Open Revit Model

1. Open Revit.
2. Open a project with sheets.
3. Confirm the add-in is installed and Revit has fully started.
4. In Codex, ask the connected MCP server to call `list_sheets`.
5. Confirm the response contains sheet IDs, numbers, names, unique IDs, and placeholder status.
6. Review `logs\audit.jsonl` and confirm the call logged `timestamp`, `toolName`, `parameters`, `documentName`, `result`, and `error`.

Expected successful result shape:

```json
[
  {
    "id": 123456,
    "uniqueId": "...",
    "sheetNumber": "A101",
    "name": "Floor Plan - Level 1",
    "isPlaceholder": false
  }
]
```

If `list_sheets` returns a bridge connection error, check that Revit is open and that the add-in was loaded before Codex started the MCP tool call.

### Local Smoke Test Without Codex

After Revit is open with a model loaded, run:

```powershell
cd opportunities\revit-mcp-integration
.\scripts\SmokeTest-McpServer.ps1
```

From Command Prompt instead of PowerShell, run:

```cmd
cd opportunities\revit-mcp-integration
scripts\SmokeTest-McpServer.cmd
```

The script starts `src\mcp-server\server.js`, calls `get_active_document_info`, calls `list_sheets`, calls `check_sheet_standards`, prints the responses, and exits with a helpful error if the Revit named pipe is unavailable.

If `node` is not on `PATH`, pass the full path:

```powershell
.\scripts\SmokeTest-McpServer.ps1 -NodeExe "C:\Path\To\node.exe"
```

From Command Prompt:

```cmd
scripts\SmokeTest-McpServer.cmd -NodeExe "C:\Path\To\node.exe"
```

The script also checks common Node install locations, including the Codex bundled runtime under `%USERPROFILE%\.cache\codex-runtimes`.

To view recent audit entries:

```cmd
scripts\Tail-AuditLog.cmd
```

Or from PowerShell:

```powershell
.\scripts\Tail-AuditLog.ps1 -Tail 10
```

## Troubleshooting Codex MCP

### Revit Not Open

Symptoms:

- `get_active_document_info` fails.
- Audit log records `Could not connect to Revit bridge`.

Fix:

1. Open Revit 2026.
2. Open a project model.
3. Wait until Revit is fully idle.
4. Run `scripts\SmokeTest-McpServer.cmd` before testing through Codex.

### Named Pipe Unavailable

Symptoms:

- Error contains `ENOENT \\.\pipe\IRIS.RevitMcpBridge.v1`.
- `scripts\SmokeTest-McpServer.cmd` cannot reach the bridge.

Fix:

1. Close Revit.
2. Run `scripts\Install-RevitAddin.cmd -RevitVersion 2026`.
3. Confirm `%APPDATA%\Autodesk\Revit\Addins\2026\IrisRevitMcp.addin` points to the built DLL.
4. Reopen Revit and a project.

If the error contains `EPERM`, run Revit and Codex at the same Windows privilege level. For example, do not run Revit as administrator while Codex is running as a normal user.

### MCP Server Starts But Tools Do Not Appear

Symptoms:

- Codex `/mcp` does not show `iris_revit`.
- Tools from this server are unavailable.
- Codex says it does not expose an MCP server named `iris_revit`.
- `tool_search` does not find `get_active_document_info`, `list_sheets`, or `check_duplicate_sheet_numbers`.

Fix:

1. Confirm the config is TOML, not JSON.
2. Confirm the table name is `[mcp_servers.iris_revit]`.
3. Confirm `args` points to the absolute `src\mcp-server\server.js` path.
4. If `node` is not on PATH, use the full `node.exe` path in `command`.
5. Confirm the config was added to the Codex config that the current Codex surface reads. User-level config is usually `%USERPROFILE%\.codex\config.toml`; project config is `.codex\config.toml` and only loads for trusted projects.
6. Fully restart or reload Codex after changing `config.toml`; existing sessions may not pick up newly added MCP servers.
7. Start a new Codex thread from this project and check `/mcp` before running the test prompt.

### Tools Appear But Calls Fail

Symptoms:

- `iris_revit` tools are listed, but calls return errors.
- Audit log records a bridge error.

Fix:

1. Run `scripts\SmokeTest-McpServer.cmd` from Command Prompt.
2. If the smoke test fails, resolve that first; Codex uses the same Node server and named pipe.
3. Confirm Revit has an active document open.
4. Confirm the Revit add-in is installed for the Revit version you are using.

### Audit Log Not Being Written

Symptoms:

- Tool calls happen but `logs\audit.jsonl` is missing or stale.

Fix:

1. Confirm `IRIS_REVIT_MCP_AUDIT_LOG` in the Codex MCP config points to a writable path.
2. If the env var is omitted, the server writes to `logs\audit.jsonl` under the project root.
3. Confirm the `cwd` in the MCP config is the `opportunities\revit-mcp-integration` folder.
4. Run `scripts\Tail-AuditLog.cmd` to inspect the default audit log.

## Minimal Test Workflow

1. Build and install the Revit add-in.
2. Open Revit and load a project.
3. Start or configure the MCP server.
4. Call `get_active_document_info`.
5. Call `list_sheets`.
6. Call `check_duplicate_sheet_numbers`.
7. Call `check_sheet_standards`.
8. Call `propose_sheet_renames_from_csv_or_json` with `examples\sheet-renames.json`.
9. Review `logs\audit.jsonl` and confirm each call logged timestamp, tool name, parameters, result, and errors.

## Phase 2 Notes

The following tools are intentionally deferred:

- `apply_approved_sheet_rename`
- `update_approved_sheet_parameters`

When implemented, each write tool should require an explicit approval token or approval payload from the MCP client, validate the requested element IDs or sheet numbers, run inside a Revit `Transaction`, and log both the approval metadata and final transaction result.

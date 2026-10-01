# Architecture

The Node process in `src/mcp-server` exposes MCP tools over stdio. It communicates through a named pipe with the WPF/Revit add-in in `src/RevitAddin`, which executes work inside Revit's API context. The add-in targets .NET 10 because the installed Revit 2026 API (26.5) references .NET 10.

Audit logs are runtime data and belong under a configured data root such as `C:\DevData\IRIS\RevitMcp`, never in the repository.

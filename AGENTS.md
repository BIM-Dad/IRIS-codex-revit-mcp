# Repository guidance

- Keep the Node MCP server and Revit add-in protocol compatible.
- Build and syntax-check before any installation into Revit or Codex configuration.
- Never commit audit logs, client model data, or machine-specific credentials.
- Use `IRIS_REVIT_MCP_*` environment variables for runtime paths and policy.
- Run `scripts/bootstrap.ps1` for a clean-clone validation.

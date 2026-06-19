#!/usr/bin/env node
import fs from "node:fs/promises";
import net from "node:net";
import path from "node:path";
import process from "node:process";
import readline from "node:readline";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(__dirname, "../..");
const pipePath = process.env.IRIS_REVIT_MCP_PIPE || "\\\\.\\pipe\\IRIS.RevitMcpBridge.v1";
const auditLogPath = process.env.IRIS_REVIT_MCP_AUDIT_LOG || path.join(projectRoot, "logs", "audit.jsonl");
const reportsDir = process.env.IRIS_REVIT_MCP_REPORTS_DIR || path.join(projectRoot, "reports");
const serverInstructions = [
  "Read-only Revit MCP bridge for Phase 1. Use only the listed tools. Do not modify the Revit model.",
  "Natural-language routing: if the user asks to run a standards check, sheet standards check, QA/QC check, or sheet audit on the current/active Revit model, call check_sheet_standards.",
  "If the user asks to save, export, create, or generate a sheet standards report, call export_sheet_standards_report.",
  "If the user mentions sheets without asking for a standards check, prefer list_sheets.",
  "If a tool call fails, check that Revit is open, the IRIS Revit MCP add-in is loaded, and the named pipe is available."
].join(" ");

const tools = [
  {
    name: "get_active_document_info",
    description: "Return basic information about the active Revit document.",
    inputSchema: {
      type: "object",
      properties: {},
      additionalProperties: false
    }
  },
  {
    name: "list_sheets",
    description: "List sheets in the active Revit document.",
    inputSchema: {
      type: "object",
      properties: {},
      additionalProperties: false
    }
  },
  {
    name: "list_views",
    description: "List non-template, non-internal views in the active Revit document.",
    inputSchema: {
      type: "object",
      properties: {},
      additionalProperties: false
    }
  },
  {
    name: "check_duplicate_sheet_numbers",
    description: "Report duplicate sheet numbers in the active Revit document.",
    inputSchema: {
      type: "object",
      properties: {},
      additionalProperties: false
    }
  },
  {
    name: "check_missing_titleblock_parameters",
    description: "Check titleblock instances for missing or blank required parameters.",
    inputSchema: {
      type: "object",
      properties: {
        requiredParameters: {
          type: "array",
          items: { type: "string" },
          description: "Parameter names to require. Defaults to common titleblock parameters."
        }
      },
      additionalProperties: false
    }
  },
  {
    name: "check_sheet_standards",
    description: "Run a read-only sheet standards, QA/QC, or sheet audit check for the current active Revit model and return structured results.",
    inputSchema: {
      type: "object",
      properties: {
        requiredTitleblockParameters: {
          type: "array",
          items: { type: "string" },
          description: "Titleblock parameter names to require. Defaults to Project Number, Drawn By, and Checked By."
        },
        sheetNumberRegex: {
          type: "string",
          description: "Regular expression used to validate sheet numbers. Defaults to ^[A-Z]+[0-9]{3}(\\\\.[0-9]{2})?$."
        },
        flagPlaceholderSheets: {
          type: "boolean",
          description: "Whether placeholder sheets should be reported as issues. Defaults to true."
        },
        excludeSheetNumberPatterns: {
          type: "array",
          items: { type: "string" },
          description: "Regular expression patterns for sheet numbers to exclude from standards checks. Defaults to none."
        },
        excludePlaceholderSheetsFromFailure: {
          type: "boolean",
          description: "Whether placeholder sheets should be excluded from failedSheetCount even when they have error-level issues. Defaults to true."
        },
        severityByIssueCode: {
          type: "object",
          additionalProperties: {
            type: "string",
            enum: ["error", "warning", "info"]
          },
          description: "Optional severity overrides by issue code. Supported severities are error, warning, and info."
        }
      },
      additionalProperties: false
    }
  },
  {
    name: "export_sheet_standards_report",
    description: "Run a read-only sheet standards, QA/QC, or sheet audit check for the current active Revit model and save timestamped JSON and optional CSV report files under the local reports folder.",
    inputSchema: {
      type: "object",
      properties: {
        requiredTitleblockParameters: {
          type: "array",
          items: { type: "string" },
          description: "Titleblock parameter names to require. Defaults to Project Number, Drawn By, and Checked By."
        },
        sheetNumberRegex: {
          type: "string",
          description: "Regular expression used to validate sheet numbers. Defaults to ^[A-Z]+[0-9]{3}(\\\\.[0-9]{2})?$."
        },
        flagPlaceholderSheets: {
          type: "boolean",
          description: "Whether placeholder sheets should be reported as issues. Defaults to true."
        },
        excludeSheetNumberPatterns: {
          type: "array",
          items: { type: "string" },
          description: "Regular expression patterns for sheet numbers to exclude from standards checks. Defaults to none."
        },
        excludePlaceholderSheetsFromFailure: {
          type: "boolean",
          description: "Whether placeholder sheets should be excluded from failedSheetCount even when they have error-level issues. Defaults to true."
        },
        severityByIssueCode: {
          type: "object",
          additionalProperties: {
            type: "string",
            enum: ["error", "warning", "info"]
          },
          description: "Optional severity overrides by issue code. Supported severities are error, warning, and info."
        },
        includeCsv: {
          type: "boolean",
          description: "Whether to also write a CSV issue triage file. Defaults to true."
        }
      },
      additionalProperties: false
    }
  },
  {
    name: "propose_sheet_renames_from_csv_or_json",
    description: "Compare sheet rename proposal data with the active Revit document without applying changes.",
    inputSchema: {
      type: "object",
      properties: {
        proposalFile: {
          type: "string",
          description: "Path to a CSV or JSON file containing proposed sheet renames."
        },
        csvText: {
          type: "string",
          description: "Inline CSV proposal data."
        },
        jsonText: {
          type: "string",
          description: "Inline JSON proposal data."
        },
        proposals: {
          type: "array",
          items: {
            type: "object",
            properties: {
              currentNumber: { type: "string" },
              newNumber: { type: "string" },
              newName: { type: "string" }
            },
            required: ["currentNumber"],
            additionalProperties: true
          }
        }
      },
      additionalProperties: false
    }
  }
];

const toolNames = new Set(tools.map((tool) => tool.name));

const rl = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity
});

rl.on("line", async (line) => {
  if (!line.trim()) {
    return;
  }

  let message;
  try {
    message = JSON.parse(line);
  } catch (error) {
    writeJson({
      jsonrpc: "2.0",
      id: null,
      error: { code: -32700, message: `Parse error: ${error.message}` }
    });
    return;
  }

  if (!Object.prototype.hasOwnProperty.call(message, "id")) {
    return;
  }

  try {
    const result = await dispatch(message.method, message.params || {});
    writeJson({ jsonrpc: "2.0", id: message.id, result });
  } catch (error) {
    writeJson({
      jsonrpc: "2.0",
      id: message.id,
      error: { code: error.code || -32000, message: error.message || String(error) }
    });
  }
});

async function dispatch(method, params) {
  switch (method) {
    case "initialize":
      return {
        protocolVersion: params.protocolVersion || "2024-11-05",
        capabilities: { tools: {} },
        serverInfo: { name: "iris-revit-mcp-server", version: "0.1.0" },
        instructions: serverInstructions
      };
    case "tools/list":
      return { tools };
    case "tools/call":
      return callTool(params);
    case "ping":
      return {};
    default: {
      const error = new Error(`Method not found: ${method}`);
      error.code = -32601;
      throw error;
    }
  }
}

async function callTool(params) {
  const name = params.name;
  const args = params.arguments || {};

  if (!toolNames.has(name)) {
    const error = new Error(`Unknown tool: ${name}`);
    error.code = -32602;
    throw error;
  }

  const startedAt = new Date().toISOString();
  let bridgeResponse;
  let auditError = null;

  try {
    const bridgeParameters = await prepareArguments(name, args);
    const bridgeToolName = name === "export_sheet_standards_report" ? "check_sheet_standards" : name;
    bridgeResponse = await callRevitBridge({ tool: bridgeToolName, parameters: bridgeParameters });

    if (!bridgeResponse.ok) {
      auditError = bridgeResponse.error || "Revit bridge returned an error.";
      return {
        isError: true,
        content: [{ type: "text", text: JSON.stringify(bridgeResponse, null, 2) }]
      };
    }

    if (name === "export_sheet_standards_report") {
      const exportResult = await exportSheetStandardsReport(bridgeResponse.result, args);
      bridgeResponse = {
        ...bridgeResponse,
        result: exportResult
      };
    }

    return {
      content: [{ type: "text", text: JSON.stringify(bridgeResponse.result, null, 2) }]
    };
  } catch (error) {
    auditError = error.message || String(error);
    return {
      isError: true,
      content: [{ type: "text", text: auditError }]
    };
  } finally {
    await appendAuditLog({
      timestamp: startedAt,
      toolName: name,
      parameters: redactLargeInlineData(args),
      documentName: bridgeResponse?.documentName || null,
      result: bridgeResponse?.ok ? bridgeResponse.result : null,
      error: auditError
    });
  }
}

async function prepareArguments(name, args) {
  if (name === "export_sheet_standards_report") {
    const prepared = { ...args };
    delete prepared.includeCsv;
    return prepared;
  }

  if (name !== "propose_sheet_renames_from_csv_or_json" || !args.proposalFile) {
    return args;
  }

  const proposalPath = path.resolve(process.cwd(), args.proposalFile);
  const text = await fs.readFile(proposalPath, "utf8");
  const extension = path.extname(proposalPath).toLowerCase();
  const prepared = { ...args };
  delete prepared.proposalFile;

  if (extension === ".json") {
    prepared.jsonText = text;
  } else if (extension === ".csv") {
    prepared.csvText = text;
  } else {
    throw new Error(`Unsupported proposal file type '${extension}'. Use .csv or .json.`);
  }

  return prepared;
}

function callRevitBridge(request) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipePath);
    let buffer = "";

    socket.setEncoding("utf8");
    socket.setTimeout(30000);

    socket.on("connect", () => {
      socket.write(`${JSON.stringify(request)}\n`);
    });

    socket.on("data", (chunk) => {
      buffer += chunk;
    });

    socket.on("end", () => {
      try {
        resolve(JSON.parse(buffer.trim()));
      } catch (error) {
        reject(new Error(`Invalid response from Revit bridge: ${error.message}`));
      }
    });

    socket.on("timeout", () => {
      socket.destroy(new Error("Timed out waiting for the Revit bridge."));
    });

    socket.on("error", (error) => {
      reject(new Error(`Could not connect to Revit bridge at ${pipePath}: ${error.message}`));
    });
  });
}

async function appendAuditLog(entry) {
  await fs.mkdir(path.dirname(auditLogPath), { recursive: true });
  await fs.appendFile(auditLogPath, `${JSON.stringify(entry)}\n`, "utf8");
}

async function exportSheetStandardsReport(report, args) {
  await fs.mkdir(reportsDir, { recursive: true });

  const timestamp = formatTimestampForFile(new Date());
  const jsonPath = path.join(reportsDir, `sheet-standards-${timestamp}.json`);
  const includeCsv = args.includeCsv !== false;
  const csvPath = includeCsv ? path.join(reportsDir, `sheet-standards-${timestamp}.csv`) : null;

  await fs.writeFile(jsonPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");

  if (includeCsv && csvPath) {
    await fs.writeFile(csvPath, sheetStandardsReportToCsv(report), "utf8");
  }

  return {
    documentName: report.documentName || null,
    checkedAt: report.checkedAt || null,
    reportsDirectory: reportsDir,
    jsonPath,
    csvPath,
    summary: report.summary || null
  };
}

function sheetStandardsReportToCsv(report) {
  const header = [
    "sheetNumber",
    "sheetName",
    "isPlaceholder",
    "isExcluded",
    "exclusionPattern",
    "titleblockCount",
    "highestSeverity",
    "issueCode",
    "issueSeverity",
    "message"
  ];

  const rows = [header];
  for (const sheet of report.sheets || []) {
    const issues = Array.isArray(sheet.issues) && sheet.issues.length > 0 ? sheet.issues : [null];
    for (const issue of issues) {
      rows.push([
        sheet.sheetNumber ?? "",
        sheet.name ?? "",
        sheet.isPlaceholder ?? false,
        sheet.isExcluded ?? false,
        sheet.exclusionPattern ?? "",
        sheet.titleblockCount ?? 0,
        sheet.highestSeverity ?? "",
        issue?.code ?? "",
        issue?.severity ?? "",
        issue?.message ?? ""
      ]);
    }
  }

  return `${rows.map((row) => row.map(csvCell).join(",")).join("\n")}\n`;
}

function csvCell(value) {
  const text = String(value ?? "");
  return /[",\r\n]/.test(text) ? `"${text.replaceAll("\"", "\"\"")}"` : text;
}

function formatTimestampForFile(date) {
  return date.toISOString().replaceAll("-", "").replaceAll(":", "").replace(/\.\d{3}Z$/, "Z");
}

function redactLargeInlineData(args) {
  const copy = { ...args };
  for (const key of ["csvText", "jsonText"]) {
    if (typeof copy[key] === "string" && copy[key].length > 500) {
      copy[key] = `${copy[key].slice(0, 500)}... [truncated ${copy[key].length} chars]`;
    }
  }

  return copy;
}

function writeJson(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

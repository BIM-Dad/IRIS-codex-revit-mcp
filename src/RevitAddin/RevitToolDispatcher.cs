using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Iris.RevitMcp;

public static class RevitToolDispatcher
{
    public static BridgeResponse Execute(UIApplication app, BridgeRequest request)
    {
        var document = app.ActiveUIDocument?.Document;
        if (document is null)
        {
            return BridgeResponse.Failure(null, "No active Revit document.");
        }

        return request.Tool switch
        {
            "get_active_document_info" => BridgeResponse.Success(document.Title, GetActiveDocumentInfo(document)),
            "list_sheets" => BridgeResponse.Success(document.Title, ListSheets(document)),
            "list_views" => BridgeResponse.Success(document.Title, ListViews(document)),
            "check_duplicate_sheet_numbers" => BridgeResponse.Success(document.Title, CheckDuplicateSheetNumbers(document)),
            "check_missing_titleblock_parameters" => BridgeResponse.Success(document.Title, CheckMissingTitleblockParameters(document, request.Parameters)),
            "check_sheet_standards" => BridgeResponse.Success(document.Title, CheckSheetStandards(document, request.Parameters)),
            "propose_sheet_renames_from_csv_or_json" => BridgeResponse.Success(document.Title, ProposeSheetRenames(document, request.Parameters)),
            _ => BridgeResponse.Failure(document.Title, $"Unknown or unsupported tool '{request.Tool}'.")
        };
    }

    private static object GetActiveDocumentInfo(Document document)
    {
        return new
        {
            title = document.Title,
            pathName = document.PathName,
            isWorkshared = document.IsWorkshared,
            isFamilyDocument = document.IsFamilyDocument,
            isModified = document.IsModified,
            activeProjectLocation = document.ActiveProjectLocation?.Name,
            revitBuild = document.Application.VersionBuild,
            revitVersion = document.Application.VersionName
        };
    }

    private static IReadOnlyList<object> ListSheets(Document document)
    {
        return GetSheets(document)
            .OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .Select(sheet => new
            {
                id = sheet.Id.Value,
                uniqueId = sheet.UniqueId,
                sheetNumber = sheet.SheetNumber,
                name = sheet.Name,
                isPlaceholder = sheet.IsPlaceholder
            })
            .Cast<object>()
            .ToList();
    }

    private static IReadOnlyList<object> ListViews(Document document)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => !view.IsTemplate && !view.IsInternalView())
            .OrderBy(view => view.ViewType.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .Select(view => new
            {
                id = view.Id.Value,
                uniqueId = view.UniqueId,
                name = view.Name,
                viewType = view.ViewType.ToString(),
                scale = view.Scale
            })
            .Cast<object>()
            .ToList();
    }

    private static object CheckDuplicateSheetNumbers(Document document)
    {
        var duplicates = GetSheets(document)
            .GroupBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new
            {
                sheetNumber = group.Key,
                count = group.Count(),
                sheets = group.Select(sheet => new
                {
                    id = sheet.Id.Value,
                    uniqueId = sheet.UniqueId,
                    name = sheet.Name
                }).ToList()
            })
            .ToList();

        return new
        {
            duplicateCount = duplicates.Count,
            duplicates
        };
    }

    private static object CheckSheetStandards(Document document, JsonElement parameters)
    {
        var settings = ReadSheetStandardsSettings(parameters);
        var sheetNumberRegex = new Regex(settings.SheetNumberRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var sheets = GetSheets(document)
            .OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateNumbers = sheets
            .GroupBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var sheetResults = new List<object>();
        var issueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failedSheetCount = 0;

        foreach (var sheet in sheets)
        {
            var issues = new List<object>();
            void AddIssue(string code, string severity, string message)
            {
                issues.Add(new { code, severity, message });
                issueCounts[code] = issueCounts.TryGetValue(code, out var count) ? count + 1 : 1;
            }

            var sheetNumber = sheet.SheetNumber ?? string.Empty;
            var sheetName = sheet.Name ?? string.Empty;

            if (duplicateNumbers.TryGetValue(sheetNumber, out var duplicateCount))
            {
                AddIssue("duplicate_sheet_number", "error", $"Sheet number '{sheetNumber}' is used by {duplicateCount} sheets.");
            }

            if (string.IsNullOrWhiteSpace(sheetName))
            {
                AddIssue("empty_sheet_name", "error", "Sheet name is empty or whitespace.");
            }

            if (settings.FlagPlaceholderSheets && sheet.IsPlaceholder)
            {
                AddIssue("placeholder_sheet", "warning", "Sheet is a placeholder sheet.");
            }

            if (!string.IsNullOrWhiteSpace(sheetNumber) && !sheetNumberRegex.IsMatch(sheetNumber))
            {
                AddIssue("sheet_number_format", "warning", $"Sheet number does not match '{settings.SheetNumberRegex}'.");
            }

            foreach (var nameIssue in GetSheetNameFormattingIssues(sheetName))
            {
                AddIssue(nameIssue.Code, nameIssue.Severity, nameIssue.Message);
            }

            var titleblocks = GetTitleblocksOnSheet(document, sheet).ToList();
            if (!sheet.IsPlaceholder && titleblocks.Count == 0)
            {
                AddIssue("missing_titleblock", "error", "Sheet has no titleblock instance.");
            }

            if (titleblocks.Count > 1)
            {
                AddIssue("multiple_titleblocks", "warning", $"Sheet has {titleblocks.Count} titleblock instances.");
            }

            var titleblockResults = new List<object>();
            foreach (var titleblock in titleblocks)
            {
                var missingParameters = settings.RequiredTitleblockParameters
                    .Where(parameterName => IsMissingParameter(titleblock, parameterName))
                    .ToList();

                if (missingParameters.Count > 0)
                {
                    AddIssue("missing_titleblock_parameters", "error", $"Titleblock is missing or has blank required parameters: {string.Join(", ", missingParameters)}.");
                }

                titleblockResults.Add(new
                {
                    id = titleblock.Id.Value,
                    uniqueId = titleblock.UniqueId,
                    name = titleblock.Name,
                    missingParameters
                });
            }

            sheetResults.Add(new
            {
                id = sheet.Id.Value,
                uniqueId = sheet.UniqueId,
                sheetNumber,
                name = sheetName,
                isPlaceholder = sheet.IsPlaceholder,
                titleblockCount = titleblocks.Count,
                titleblocks = titleblockResults,
                issueCount = issues.Count,
                issues
            });

            if (issues.Count > 0)
            {
                failedSheetCount++;
            }
        }

        return new
        {
            documentName = document.Title,
            checkedAt = DateTimeOffset.UtcNow.ToString("O"),
            settings = new
            {
                requiredTitleblockParameters = settings.RequiredTitleblockParameters,
                sheetNumberRegex = settings.SheetNumberRegex,
                flagPlaceholderSheets = settings.FlagPlaceholderSheets,
                note = "Missing titleblock is not flagged for placeholder sheets."
            },
            summary = new
            {
                sheetCount = sheets.Count,
                failedSheetCount,
                issueCount = issueCounts.Values.Sum(),
                issueCounts
            },
            sheets = sheetResults
        };
    }

    private static object CheckMissingTitleblockParameters(Document document, JsonElement parameters)
    {
        var requiredParameters = ReadStringArray(parameters, "requiredParameters")
            .Concat(ReadStringArray(parameters, "required_parameters"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requiredParameters.Count == 0)
        {
            requiredParameters.AddRange(new[] { "Drawn By", "Checked By", "Approved By", "Sheet Issue Date" });
        }

        var issues = new List<object>();
        foreach (var sheet in GetSheets(document).Where(sheet => !sheet.IsPlaceholder))
        {
            var titleblocks = new FilteredElementCollector(document, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            if (titleblocks.Count == 0)
            {
                issues.Add(new
                {
                    sheetId = sheet.Id.Value,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    issue = "No titleblock instance found on sheet.",
                    missingParameters = requiredParameters
                });
                continue;
            }

            foreach (var titleblock in titleblocks)
            {
                var missing = requiredParameters
                    .Where(parameterName => IsMissingParameter(titleblock, parameterName))
                    .ToList();

                if (missing.Count == 0)
                {
                    continue;
                }

                issues.Add(new
                {
                    sheetId = sheet.Id.Value,
                    sheetNumber = sheet.SheetNumber,
                    sheetName = sheet.Name,
                    titleblockId = titleblock.Id.Value,
                    titleblockName = titleblock.Name,
                    issue = "Titleblock has missing or blank required parameters.",
                    missingParameters = missing
                });
            }
        }

        return new
        {
            requiredParameters,
            issueCount = issues.Count,
            issues
        };
    }

    private static object ProposeSheetRenames(Document document, JsonElement parameters)
    {
        var proposals = ReadProposals(parameters);
        var sheetsByNumber = GetSheets(document)
            .GroupBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var proposedChanges = new List<object>();
        var unmatched = new List<object>();
        var conflicts = new List<object>();

        foreach (var proposal in proposals)
        {
            if (string.IsNullOrWhiteSpace(proposal.CurrentNumber))
            {
                unmatched.Add(new { proposal, reason = "Missing current sheet number." });
                continue;
            }

            if (!sheetsByNumber.TryGetValue(proposal.CurrentNumber, out var matches) || matches.Count == 0)
            {
                unmatched.Add(new { proposal, reason = "No matching sheet found in active document." });
                continue;
            }

            if (matches.Count > 1)
            {
                conflicts.Add(new { proposal, reason = "Multiple sheets share the current sheet number." });
                continue;
            }

            var sheet = matches[0];
            proposedChanges.Add(new
            {
                sheetId = sheet.Id.Value,
                uniqueId = sheet.UniqueId,
                currentNumber = sheet.SheetNumber,
                currentName = sheet.Name,
                proposedNumber = proposal.NewNumber,
                proposedName = proposal.NewName,
                numberWillChange = !string.Equals(sheet.SheetNumber, proposal.NewNumber, StringComparison.OrdinalIgnoreCase),
                nameWillChange = !string.Equals(sheet.Name, proposal.NewName, StringComparison.Ordinal)
            });
        }

        return new
        {
            proposedChangeCount = proposedChanges.Count,
            unmatchedCount = unmatched.Count,
            conflictCount = conflicts.Count,
            proposedChanges,
            unmatched,
            conflicts,
            applied = false
        };
    }

    private static IReadOnlyList<ViewSheet> GetSheets(Document document)
    {
        return new FilteredElementCollector(document)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .ToList();
    }

    private static IReadOnlyList<FamilyInstance> GetTitleblocksOnSheet(Document document, ViewSheet sheet)
    {
        if (sheet.IsPlaceholder)
        {
            return Array.Empty<FamilyInstance>();
        }

        return new FilteredElementCollector(document, sheet.Id)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .ToList();
    }

    private static bool IsInternalView(this View view)
    {
        return view.ViewType is ViewType.Internal or ViewType.ProjectBrowser or ViewType.SystemBrowser;
    }

    private static bool IsMissingParameter(Element element, string parameterName)
    {
        var parameter = element.LookupParameter(parameterName);
        if (parameter is null)
        {
            return true;
        }

        return parameter.StorageType switch
        {
            StorageType.String => string.IsNullOrWhiteSpace(parameter.AsString()),
            StorageType.ElementId => parameter.AsElementId() == ElementId.InvalidElementId,
            StorageType.Integer => false,
            StorageType.Double => false,
            _ => string.IsNullOrWhiteSpace(parameter.AsValueString())
        };
    }

    private static IEnumerable<string> ReadStringArray(JsonElement parameters, string propertyName)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static SheetStandardsSettings ReadSheetStandardsSettings(JsonElement parameters)
    {
        var requiredParameters = ReadStringArray(parameters, "requiredTitleblockParameters")
            .Concat(ReadStringArray(parameters, "required_titleblock_parameters"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requiredParameters.Count == 0)
        {
            requiredParameters.AddRange(new[] { "Project Number", "Drawn By", "Checked By" });
        }

        var sheetNumberRegex = ReadStringProperty(parameters, "sheetNumberRegex")
            ?? ReadStringProperty(parameters, "sheet_number_regex")
            ?? "^[A-Z]+[0-9]{3}(\\.[0-9]{2})?$";

        var flagPlaceholderSheets = ReadBoolProperty(parameters, "flagPlaceholderSheets")
            ?? ReadBoolProperty(parameters, "flag_placeholder_sheets")
            ?? true;

        try
        {
            _ = new Regex(sheetNumberRegex);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid sheetNumberRegex: {ex.Message}", ex);
        }

        return new SheetStandardsSettings
        {
            RequiredTitleblockParameters = requiredParameters,
            SheetNumberRegex = sheetNumberRegex,
            FlagPlaceholderSheets = flagPlaceholderSheets
        };
    }

    private static IEnumerable<(string Code, string Severity, string Message)> GetSheetNameFormattingIssues(string sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            yield break;
        }

        if (!string.Equals(sheetName, sheetName.Trim(), StringComparison.Ordinal))
        {
            yield return ("sheet_name_format", "warning", "Sheet name has leading or trailing whitespace.");
        }

        if (sheetName.Contains("  ", StringComparison.Ordinal))
        {
            yield return ("sheet_name_format", "warning", "Sheet name contains repeated spaces.");
        }

        if (string.Equals(sheetName.Trim(), "Unnamed", StringComparison.OrdinalIgnoreCase))
        {
            yield return ("sheet_name_format", "warning", "Sheet name is still set to 'Unnamed'.");
        }

        if (sheetName.Any(char.IsLetter) && !sheetName.Any(char.IsUpper))
        {
            yield return ("sheet_name_format", "warning", "Sheet name does not contain uppercase letters.");
        }
    }

    private static IReadOnlyList<SheetRenameProposal> ReadProposals(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<SheetRenameProposal>();
        }

        if (parameters.TryGetProperty("proposals", out var proposalsElement) &&
            proposalsElement.ValueKind == JsonValueKind.Array)
        {
            return proposalsElement.EnumerateArray().Select(ReadProposal).Where(item => item is not null).Cast<SheetRenameProposal>().ToList();
        }

        if (parameters.TryGetProperty("jsonText", out var jsonTextElement) &&
            jsonTextElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(jsonTextElement.GetString()))
        {
            using var document = JsonDocument.Parse(jsonTextElement.GetString()!);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray().Select(ReadProposal).Where(item => item is not null).Cast<SheetRenameProposal>().ToList();
            }
        }

        if (parameters.TryGetProperty("csvText", out var csvTextElement) &&
            csvTextElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(csvTextElement.GetString()))
        {
            return ParseCsv(csvTextElement.GetString()!);
        }

        return Array.Empty<SheetRenameProposal>();
    }

    private static SheetRenameProposal? ReadProposal(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new SheetRenameProposal
        {
            CurrentNumber = ReadStringProperty(element, "currentNumber") ?? ReadStringProperty(element, "current_number") ?? string.Empty,
            NewNumber = ReadStringProperty(element, "newNumber") ?? ReadStringProperty(element, "new_number") ?? string.Empty,
            NewName = ReadStringProperty(element, "newName") ?? ReadStringProperty(element, "new_name") ?? string.Empty
        };
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? ReadBoolProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            ? property.GetBoolean()
            : null;
    }

    private static IReadOnlyList<SheetRenameProposal> ParseCsv(string csvText)
    {
        var lines = csvText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return Array.Empty<SheetRenameProposal>();
        }

        var headers = SplitCsvLine(lines[0]).Select(NormalizeHeader).ToList();
        var proposals = new List<SheetRenameProposal>();
        for (var i = 1; i < lines.Length; i++)
        {
            var values = SplitCsvLine(lines[i]);
            string ValueFor(params string[] names)
            {
                foreach (var name in names.Select(NormalizeHeader))
                {
                    var index = headers.IndexOf(name);
                    if (index >= 0 && index < values.Count)
                    {
                        return values[index];
                    }
                }

                return string.Empty;
            }

            proposals.Add(new SheetRenameProposal
            {
                CurrentNumber = ValueFor("currentNumber", "current_number"),
                NewNumber = ValueFor("newNumber", "new_number"),
                NewName = ValueFor("newName", "new_name")
            });
        }

        return proposals;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new List<char>();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Add('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(new string(current.ToArray()).Trim());
                current.Clear();
            }
            else
            {
                current.Add(c);
            }
        }

        values.Add(new string(current.ToArray()).Trim());
        return values;
    }

    private static string NormalizeHeader(string value)
    {
        return value.Replace("_", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
    }

    private sealed class SheetRenameProposal
    {
        public string CurrentNumber { get; init; } = string.Empty;
        public string NewNumber { get; init; } = string.Empty;
        public string NewName { get; init; } = string.Empty;
    }

    private sealed class SheetStandardsSettings
    {
        public IReadOnlyList<string> RequiredTitleblockParameters { get; init; } = Array.Empty<string>();
        public string SheetNumberRegex { get; init; } = string.Empty;
        public bool FlagPlaceholderSheets { get; init; }
    }
}

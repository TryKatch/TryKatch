using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

internal static partial class ModuleFieldRenderer
{
    public static IReadOnlyDictionary<string, string> Render(
        string entityName,
        IReadOnlyList<ModuleFieldDefinition> fields)
    {
        ModuleFieldDefinition displayField = fields.FirstOrDefault(field => field.Kind == ModuleFieldKind.String)
            ?? fields[0];
        ModuleFieldDefinition? descriptionField = fields.FirstOrDefault(field =>
            field != displayField && field.Kind == ModuleFieldKind.String && !field.Required);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__FIELD_ENUMS__"] = RenderEnums(entityName, fields),
            ["__DOMAIN_FIELD_PARAMETERS__"] = JoinParameters(fields.Select(field => $"{field.DomainType(entityName)} {field.Name}"), 8),
            ["__DOMAIN_FIELD_ARGUMENTS__"] = string.Join(", ", fields.Select(field => field.Name)),
            ["__DOMAIN_FIELD_ASSIGNMENTS__"] = JoinLines(fields.Select(RenderDomainAssignment), 8),
            ["__DOMAIN_FIELD_PROPERTIES__"] = JoinLines(fields.Select(field => RenderDomainProperty(entityName, field)), 4),
            ["__COMMAND_FIELDS__"] = JoinParameters(fields.Select(field => $"{field.InputType} {field.PropertyName}"), 4),
            ["__DTO_FIELDS__"] = JoinParameters(fields.Select(field => $"{field.DtoType} {field.PropertyName}"), 4),
            ["__COMMAND_TO_DOMAIN_ARGUMENTS__"] = JoinParameters(fields.Select(field => RenderCommandToDomain(entityName, field)), 12),
            ["__REQUEST_ARGUMENTS__"] = string.Join(", ", fields.Select(field => $"request.{field.PropertyName}")),
            ["__DTO_ARGUMENTS__"] = JoinParameters(fields.Select(RenderDtoArgument), 8),
            ["__FIELD_VALIDATION__"] = JoinLines(fields.SelectMany(field => RenderValidation(entityName, field)), 8),
            ["__AUDIT_DISPLAY__"] = RenderAuditDisplay(displayField),
            ["__PAGE_SEARCH_PREDICATE__"] = string.Join(" || ", fields.Where(field => field.Kind == ModuleFieldKind.String)
                .Select(field => $"(record.{field.PropertyName} != null && EF.Functions.ILike(record.{field.PropertyName}, pattern, \"\\\\\"))")) is { Length: > 0 } predicate ? predicate : "false",
            ["__MODEL_FIELD_CONFIGURATION__"] = JoinLines(fields.SelectMany(RenderModelConfiguration), 12),
            ["__MIGRATION_FIELDS__"] = JoinLines(fields.Select(RenderMigrationColumn), 16),
            ["__WEB_FIELD_STATE__"] = JoinLines(fields.Select(RenderWebState), 2),
            ["__WEB_DATETIME_IMPORT__"] = fields.Any(field => field.Kind == ModuleFieldKind.DateTime)
                ? "import { toDateTimeLocal, toUtcDateTime } from './dateTime'"
                : string.Empty,
            ["__WEB_FIELD_RESET__"] = string.Join("; ", fields.Select(RenderWebReset)),
            ["__WEB_FIELD_EDIT__"] = string.Join("; ", fields.Select(RenderWebEdit)),
            ["__WEB_REQUEST_BODY__"] = string.Join(", ", fields.Select(RenderWebRequestValue)),
            ["__WEB_COLUMNS__"] = JoinLines(fields.Select(RenderWebColumn), 4),
            ["__WEB_FORM_FIELDS__"] = JoinLines(fields.Select((field, index) => RenderWebFormField(field, index == 0)), 8),
            ["__WEB_DETAIL_FIELDS__"] = JoinLines(fields.Select(RenderWebDetail), 8),
            ["__WEB_TEST_FIELDS__"] = JoinLines(fields.Select(field => $"{field.Name}: {RenderWebTestValue(field)},"), 2),
            ["__WEB_DISPLAY_VALUE__"] = RenderWebDisplayValue(displayField),
            ["__WEB_VIEWING_DISPLAY_VALUE__"] = RenderWebViewingDisplayValue(displayField),
            ["__WEB_DESCRIPTION_VALUE__"] = descriptionField is null ? "''" : $"record.{descriptionField.Name} ?? ''",
            ["__FIELD_MESSAGES_EN__"] = RenderMessages(fields, french: false),
            ["__FIELD_MESSAGES_FR__"] = RenderMessages(fields, french: true)
        };
    }

    private static string RenderEnums(string entityName, IEnumerable<ModuleFieldDefinition> fields) =>
        string.Join("\n\n", fields.Where(field => field.Kind == ModuleFieldKind.Enum).Select(field =>
            $"public enum {entityName}{field.PropertyName}\n{{\n" +
            string.Join(",\n", field.EnumValues.Select((value, index) => $"    {value} = {index + 1}")) +
            "\n}"));

    private static string RenderDomainAssignment(ModuleFieldDefinition field) => field.Kind switch
    {
        ModuleFieldKind.String => $"{field.PropertyName} = {field.Name}{(field.Required ? string.Empty : "?")}.Trim();",
        ModuleFieldKind.DateTime => $"{field.PropertyName} = {field.Name}{(field.Required ? string.Empty : "?")}.ToUniversalTime();",
        _ => $"{field.PropertyName} = {field.Name};"
    };

    private static string RenderDomainProperty(string entityName, ModuleFieldDefinition field)
    {
        string initializer = field.Kind == ModuleFieldKind.String && field.Required ? " = string.Empty;" : string.Empty;
        return $"public {field.DomainType(entityName)} {field.PropertyName} {{ get; private set; }}{initializer}";
    }

    private static string RenderCommandToDomain(string entityName, ModuleFieldDefinition field)
    {
        if (field.Kind == ModuleFieldKind.Enum)
        {
            string parse = $"Enum.Parse<{entityName}{field.PropertyName}>(command.{field.PropertyName}!, ignoreCase: true)";
            return field.Required
                ? parse
                : $"string.IsNullOrWhiteSpace(command.{field.PropertyName}) ? null : {parse}";
        }

        if (field.Kind == ModuleFieldKind.String)
            return field.Required ? $"command.{field.PropertyName}!" : $"command.{field.PropertyName}";

        if (field.Kind == ModuleFieldKind.Decimal)
        {
            string parse = $"decimal.Parse(command.{field.PropertyName}!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)";
            return field.Required
                ? parse
                : $"string.IsNullOrWhiteSpace(command.{field.PropertyName}) ? null : {parse}";
        }

        if (field.Kind == ModuleFieldKind.Long)
        {
            string parse = $"long.Parse(command.{field.PropertyName}!, NumberStyles.Integer, CultureInfo.InvariantCulture)";
            return field.Required
                ? parse
                : $"string.IsNullOrWhiteSpace(command.{field.PropertyName}) ? null : {parse}";
        }

        return field.Required
            ? $"command.{field.PropertyName}.GetValueOrDefault()"
            : $"command.{field.PropertyName}";
    }

    private static string RenderDtoArgument(ModuleFieldDefinition field) => field.Kind switch
    {
        ModuleFieldKind.Enum => field.Required
            ? $"record.{field.PropertyName}.ToString()"
            : $"record.{field.PropertyName}?.ToString()",
        ModuleFieldKind.Decimal or ModuleFieldKind.Long => field.Required
            ? $"record.{field.PropertyName}.ToString(CultureInfo.InvariantCulture)"
            : $"record.{field.PropertyName}?.ToString(CultureInfo.InvariantCulture)",
        _ => $"record.{field.PropertyName}"
    };

    private static IEnumerable<string> RenderValidation(string entityName, ModuleFieldDefinition field)
    {
        if (field.Kind == ModuleFieldKind.String)
        {
            if (field.Required)
                yield return $"if (string.IsNullOrWhiteSpace(command.{field.PropertyName})) errors.Add(\"{field.PropertyName} is required.\");";
            string prefix = field.Required ? "else " : string.Empty;
            yield return $"{prefix}if (command.{field.PropertyName}?.Trim().Length > {field.MaximumLength}) errors.Add(\"{field.PropertyName} cannot exceed {field.MaximumLength} characters.\");";
            yield break;
        }

        if (field.Kind == ModuleFieldKind.Enum)
        {
            string condition = $"!Enum.GetNames<{entityName}{field.PropertyName}>().Contains(command.{field.PropertyName}, StringComparer.OrdinalIgnoreCase)";
            if (field.Required)
                yield return $"if (string.IsNullOrWhiteSpace(command.{field.PropertyName}) || {condition}) errors.Add(\"{field.PropertyName} must be one of: {string.Join(", ", field.EnumValues)}.\");";
            else
                yield return $"if (!string.IsNullOrWhiteSpace(command.{field.PropertyName}) && {condition}) errors.Add(\"{field.PropertyName} must be one of: {string.Join(", ", field.EnumValues)}.\");";
            yield break;
        }

        if (field.Kind == ModuleFieldKind.Decimal)
        {
            string value = $"command.{field.PropertyName}";
            string parsed = $"parsed{field.PropertyName}";
            if (field.Required)
                yield return $"if (string.IsNullOrWhiteSpace({value})) errors.Add(\"{field.PropertyName} is required.\");";
            string prefix = field.Required ? "else " : string.Empty;
            yield return $"{prefix}if (!string.IsNullOrWhiteSpace({value}) && (!decimal.TryParse({value}, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal {parsed}) || {parsed} <= -10000000000000000m || {parsed} >= 10000000000000000m || {parsed} != decimal.Round({parsed}, 2))) errors.Add(\"{field.PropertyName} must be an invariant decimal with at most 16 integer digits and 2 fractional digits.\");";
            yield break;
        }

        if (field.Kind == ModuleFieldKind.Long)
        {
            string value = $"command.{field.PropertyName}";
            if (field.Required)
                yield return $"if (string.IsNullOrWhiteSpace({value})) errors.Add(\"{field.PropertyName} is required.\");";
            string prefix = field.Required ? "else " : string.Empty;
            yield return $"{prefix}if (!string.IsNullOrWhiteSpace({value}) && !long.TryParse({value}, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) errors.Add(\"{field.PropertyName} must be a 64-bit integer.\");";
            yield break;
        }

        if (field.Required)
        {
            yield return $"if (command.{field.PropertyName} is null) errors.Add(\"{field.PropertyName} is required.\");";
            if (field.Kind == ModuleFieldKind.Guid)
                yield return $"else if (command.{field.PropertyName} == Guid.Empty) errors.Add(\"{field.PropertyName} cannot be an empty GUID.\");";
        }
    }

    private static string RenderAuditDisplay(ModuleFieldDefinition field) => field.Kind == ModuleFieldKind.String
        ? $"NormalizeAuditDisplay(record.{field.PropertyName}, record.Id)"
        : $"NormalizeAuditDisplay(Convert.ToString(record.{field.PropertyName}, CultureInfo.InvariantCulture), record.Id)";

    private static IEnumerable<string> RenderModelConfiguration(ModuleFieldDefinition field)
    {
        string required = field.Required ? ".IsRequired()" : string.Empty;
        switch (field.Kind)
        {
            case ModuleFieldKind.String:
                yield return $"entity.Property(record => record.{field.PropertyName}).HasMaxLength({field.MaximumLength}){required};";
                break;
            case ModuleFieldKind.Decimal:
                yield return $"entity.Property(record => record.{field.PropertyName}).HasPrecision(18, 2){required};";
                break;
            case ModuleFieldKind.Enum:
                int maximum = field.EnumValues.Max(value => value.Length);
                yield return $"entity.Property(record => record.{field.PropertyName}).HasConversion<string>().HasMaxLength({maximum}){required};";
                break;
            default:
                if (field.Required)
                    yield return $"entity.Property(record => record.{field.PropertyName}).IsRequired();";
                break;
        }
    }

    private static string RenderMigrationColumn(ModuleFieldDefinition field)
    {
        string databaseType = field.Kind switch
        {
            ModuleFieldKind.String => $"character varying({field.MaximumLength})",
            ModuleFieldKind.Decimal => "numeric(18, 2)",
            ModuleFieldKind.Integer => "integer",
            ModuleFieldKind.Long => "bigint",
            ModuleFieldKind.Boolean => "boolean",
            ModuleFieldKind.Date => "date",
            ModuleFieldKind.DateTime => "timestamp with time zone",
            ModuleFieldKind.Guid => "uuid",
            ModuleFieldKind.Enum => $"character varying({field.EnumValues.Max(value => value.Length)})",
            _ => throw new InvalidOperationException($"Unsupported field kind '{field.Kind}'.")
        };
        return $"\"{field.PropertyName}\" {databaseType} {(field.Required ? "NOT NULL" : "NULL")},";
    }

    private static string RenderWebState(ModuleFieldDefinition field)
    {
        string initial = field.Kind switch
        {
            ModuleFieldKind.Boolean when field.Required => "false",
            ModuleFieldKind.Enum when field.Required => $"'{field.EnumValues[0]}'",
            _ => "''"
        };
        return $"const [{field.Name}, set{field.PropertyName}] = useState({initial})";
    }

    private static string RenderWebTestValue(ModuleFieldDefinition field) => field.Kind switch
    {
        ModuleFieldKind.String => "'A'",
        ModuleFieldKind.Decimal or ModuleFieldKind.Long => "'1'",
        ModuleFieldKind.Integer => "1",
        ModuleFieldKind.Boolean => "false",
        ModuleFieldKind.Date => "'2026-09-16'",
        ModuleFieldKind.DateTime => "'2026-09-16T12:00:00Z'",
        ModuleFieldKind.Guid => "'0199ca9e-3870-7000-8000-000000000002'",
        ModuleFieldKind.Enum => $"'{field.EnumValues[0]}'",
        _ => throw new InvalidOperationException($"Unsupported field kind '{field.Kind}'.")
    };

    private static string RenderWebReset(ModuleFieldDefinition field)
    {
        string value = field.Kind switch
        {
            ModuleFieldKind.Boolean when field.Required => "false",
            ModuleFieldKind.Enum when field.Required => $"'{field.EnumValues[0]}'",
            _ => "''"
        };
        return $"set{field.PropertyName}({value})";
    }

    private static string RenderWebEdit(ModuleFieldDefinition field)
    {
        string value = field.Kind switch
        {
            ModuleFieldKind.Boolean when field.Required => $"record.{field.Name}",
            ModuleFieldKind.DateTime => $"toDateTimeLocal(record.{field.Name})",
            ModuleFieldKind.String or ModuleFieldKind.Enum or ModuleFieldKind.Date or ModuleFieldKind.Guid => $"record.{field.Name} ?? ''",
            _ => $"record.{field.Name} == null ? '' : String(record.{field.Name})"
        };
        return $"set{field.PropertyName}({value})";
    }

    private static string RenderWebRequestValue(ModuleFieldDefinition field)
    {
        string value = field.Kind switch
        {
            ModuleFieldKind.Integer => field.Required
                ? $"Number({field.Name})"
                : $"{field.Name} === '' ? null : Number({field.Name})",
            ModuleFieldKind.DateTime => field.Required
                ? $"toUtcDateTime({field.Name}, editing?.{field.Name})"
                : $"{field.Name} === '' ? null : toUtcDateTime({field.Name}, editing?.{field.Name})",
            ModuleFieldKind.Boolean when field.Required => field.Name,
            ModuleFieldKind.Boolean => $"{field.Name} === '' ? null : {field.Name} === 'true'",
            _ when field.Required => field.Name,
            _ => $"{field.Name} || null"
        };
        return value == field.Name ? field.Name : $"{field.Name}: {value}";
    }

    private static string RenderWebColumn(ModuleFieldDefinition field)
    {
        string cell = field.Kind == ModuleFieldKind.Boolean
            ? $"record.{field.Name} == null ? t('notSet') : record.{field.Name} ? t('yes') : t('no')"
            : field.Required ? $"String(record.{field.Name})" : $"record.{field.Name} == null ? t('notSet') : String(record.{field.Name})";
        return $"{{ id: '{field.Name}', header: t('field{field.PropertyName}'), cell: (record) => {cell} }},";
    }

    private static string RenderWebFormField(ModuleFieldDefinition field, bool autoFocus)
    {
        string common = $"{(field.Required ? " required" : string.Empty)}{(autoFocus ? " autoFocus" : string.Empty)}";
        string label = $"t('field{field.PropertyName}')";
        string control = field.Kind switch
        {
            ModuleFieldKind.String when field.MaximumLength > 500 =>
                $"<textarea{common} maxLength={{{field.MaximumLength}}} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.String =>
                $"<input{common} maxLength={{{field.MaximumLength}}} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.Decimal =>
                $"<input inputMode=\"decimal\"{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.Integer =>
                $"<input type=\"number\" step=\"1\"{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.Long =>
                $"<input inputMode=\"numeric\" pattern=\"-?[0-9]+\"{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.Date =>
                $"<input type=\"date\"{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.DateTime =>
                $"<input type=\"datetime-local\" step=\"0.001\"{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.Guid =>
                $"<input inputMode=\"text\"{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}} />",
            ModuleFieldKind.Boolean when field.Required =>
                $"<input type=\"checkbox\" checked={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.checked)}} />",
            ModuleFieldKind.Boolean =>
                $"<select{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}}><option value=\"\">{{t('notSet')}}</option><option value=\"true\">{{t('yes')}}</option><option value=\"false\">{{t('no')}}</option></select>",
            ModuleFieldKind.Enum =>
                $"<select{common} value={{{field.Name}}} onChange={{(event) => set{field.PropertyName}(event.target.value)}}>" +
                (field.Required ? string.Empty : "<option value=\"\">{t('notSet')}</option>") +
                string.Join(string.Empty, field.EnumValues.Select(value => $"<option value=\"{value}\">{{t('field{field.PropertyName}{value}')}}</option>")) +
                "</select>",
            _ => throw new InvalidOperationException($"Unsupported field kind '{field.Kind}'.")
        };
        return $"<label>{{{label}}}{control}</label>";
    }

    private static string RenderWebDetail(ModuleFieldDefinition field)
    {
        string value = field.Kind == ModuleFieldKind.Boolean
            ? $"viewing.{field.Name} == null ? t('notSet') : viewing.{field.Name} ? t('yes') : t('no')"
            : field.Required ? $"String(viewing.{field.Name})" : $"viewing.{field.Name} == null ? t('notSet') : String(viewing.{field.Name})";
        return $"<div><dt>{{t('field{field.PropertyName}')}}</dt><dd>{{{value}}}</dd></div>";
    }

    private static string RenderWebDisplayValue(ModuleFieldDefinition field) =>
        field.Required ? $"String(record.{field.Name})" : $"String(record.{field.Name} ?? record.id)";

    private static string RenderWebViewingDisplayValue(ModuleFieldDefinition field) =>
        field.Required ? $"String(viewing.{field.Name})" : $"String(viewing.{field.Name} ?? viewing.id)";

    private static string RenderMessages(IReadOnlyList<ModuleFieldDefinition> fields, bool french)
    {
        List<string> messages = [];
        foreach (ModuleFieldDefinition field in fields)
        {
            messages.Add($"field{field.PropertyName}: '{EscapeTypeScript(Humanize(field.PropertyName, french))}',");
            if (field.Kind == ModuleFieldKind.Enum)
            {
                messages.AddRange(field.EnumValues.Select(value =>
                    $"field{field.PropertyName}{value}: '{EscapeTypeScript(Humanize(value, french))}',"));
            }
        }
        return JoinLines(messages, 4);
    }

    private static string Humanize(string value, bool french)
    {
        string words = WordBoundaryRegex().Replace(value, " $1").Trim().ToLowerInvariant();
        string english = char.ToUpperInvariant(words[0]) + words[1..];
        if (!french) return english;
        return words switch
        {
            "due date" => "Date d’échéance",
            "number" => "Numéro",
            "total" => "Total",
            "status" => "Statut",
            "notes" => "Notes",
            "name" => "Nom",
            "description" => "Description",
            _ => english
        };
    }

    private static string EscapeTypeScript(string value) => value.Replace("'", "\\'", StringComparison.Ordinal);

    private static string JoinParameters(IEnumerable<string> values, int continuationIndent) =>
        string.Join(",\n" + new string(' ', continuationIndent), values);

    private static string JoinLines(IEnumerable<string> lines, int indentation) =>
        string.Join("\n" + new string(' ', indentation), lines);

    [GeneratedRegex("(?<!^)([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex WordBoundaryRegex();
}

using System.Text;
using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

internal enum ModuleFieldKind
{
    String,
    Decimal,
    Integer,
    Long,
    Boolean,
    Date,
    DateTime,
    Guid,
    Enum
}

internal sealed record ModuleFieldDefinition(
    string Name,
    string PropertyName,
    ModuleFieldKind Kind,
    bool Required,
    int? MaximumLength,
    IReadOnlyList<string> EnumValues)
{
    public string DomainType(string entityName)
    {
        string type = Kind switch
        {
            ModuleFieldKind.String => "string",
            ModuleFieldKind.Decimal => "decimal",
            ModuleFieldKind.Integer => "int",
            ModuleFieldKind.Long => "long",
            ModuleFieldKind.Boolean => "bool",
            ModuleFieldKind.Date => "DateOnly",
            ModuleFieldKind.DateTime => "DateTimeOffset",
            ModuleFieldKind.Guid => "Guid",
            ModuleFieldKind.Enum => entityName + PropertyName,
            _ => throw new InvalidOperationException($"Unsupported field kind '{Kind}'.")
        };
        return Required ? type : type + "?";
    }

    public string InputType => Kind switch
    {
        ModuleFieldKind.String or ModuleFieldKind.Enum => "string?",
        ModuleFieldKind.Decimal => "string?",
        ModuleFieldKind.Integer => "int?",
        ModuleFieldKind.Long => "string?",
        ModuleFieldKind.Boolean => "bool?",
        ModuleFieldKind.Date => "DateOnly?",
        ModuleFieldKind.DateTime => "DateTimeOffset?",
        ModuleFieldKind.Guid => "Guid?",
        _ => throw new InvalidOperationException($"Unsupported field kind '{Kind}'.")
    };

    public string DtoType => Kind is ModuleFieldKind.Enum or ModuleFieldKind.Decimal or ModuleFieldKind.Long
        ? Required ? "string" : "string?"
        : DomainType(string.Empty);
}

internal static partial class ModuleFieldContract
{
    private const int MaximumFieldCount = 24;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "organizationId", "createdBy", "createdAt", "updatedAt", "archivedAt", "archivedBy",
        "deletedAt", "deletedBy", "deletionReason", "lifecycle", "lifecycleState", "create", "update",
        "archive", "restore", "requestDeletion", "record", "records", "access", "canManage", "editing",
        "viewing", "actionsFor", "columns", "failure", "activeRecords", "closeEditor", "openCreate",
        "openEdit", "queryClient", "save", "tableLabels", "t", "load", "loadResult", "value", "error",
        "version", "expectedVersion", "ensureVersion", "page", "pageSize", "search", "sort", "refresh", "table", "isConflict",
        "setPage", "setSearch", "setSort",
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "delete", "do", "double",
        "else", "enum", "event", "explicit", "export", "extends", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "function", "goto", "if", "implements", "implicit",
        "import", "in", "instanceof", "int", "interface", "internal", "is", "let", "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "package", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "super", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var",
        "virtual", "void", "volatile", "while", "with", "yield", "await"
    };

    public static IReadOnlyList<ModuleFieldDefinition> Parse(string? specification)
    {
        if (specification is null)
        {
            return
            [
                new("name", "Name", ModuleFieldKind.String, Required: true, 200, []),
                new("description", "Description", ModuleFieldKind.String, Required: false, 2000, [])
            ];
        }

        if (string.IsNullOrWhiteSpace(specification))
            throw new ArgumentException("--fields requires at least one field definition.");

        List<string> definitions = SplitTopLevel(specification, ',');
        if (definitions.Count > MaximumFieldCount)
            throw new ArgumentException($"--fields supports at most {MaximumFieldCount} business fields.");

        List<ModuleFieldDefinition> fields = definitions.Select(ParseField).ToList();
        string? duplicate = fields.GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new ArgumentException($"Field '{duplicate}' is declared more than once.");
        return fields;
    }

    private static ModuleFieldDefinition ParseField(string definition)
    {
        List<string> parts = SplitTopLevel(definition, ':');
        if (parts.Count < 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Invalid field '{definition}'. Use name:type[:required|optional][:max(length)].");

        string name = parts[0].Trim();
        if (!FieldNameRegex().IsMatch(name))
            throw new ArgumentException($"Field name '{name}' must be a lower camelCase identifier.");
        if (name.Length > 63)
            throw new ArgumentException($"Field name '{name}' cannot exceed PostgreSQL's 63-byte identifier limit.");
        if (ReservedNames.Contains(name))
            throw new ArgumentException($"Field name '{name}' is reserved by the generated platform contract.");

        string typeToken = parts[1].Trim();
        ModuleFieldKind kind;
        string[] enumValues = [];
        if (typeToken.StartsWith("enum(", StringComparison.Ordinal) && typeToken.EndsWith(')'))
        {
            kind = ModuleFieldKind.Enum;
            enumValues = typeToken[5..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (enumValues.Length < 2)
                throw new ArgumentException($"Enum field '{name}' requires at least two values.");
            foreach (string value in enumValues)
            {
                if (!DotnetNameRegex().IsMatch(value) || ReservedNames.Contains(value))
                    throw new ArgumentException($"Enum value '{value}' for field '{name}' must be a non-reserved PascalCase identifier.");
            }
            if (enumValues.Distinct(StringComparer.OrdinalIgnoreCase).Count() != enumValues.Length)
                throw new ArgumentException($"Enum field '{name}' contains duplicate values.");
        }
        else
        {
            kind = typeToken switch
            {
                "string" => ModuleFieldKind.String,
                "decimal" => ModuleFieldKind.Decimal,
                "int" => ModuleFieldKind.Integer,
                "long" => ModuleFieldKind.Long,
                "bool" => ModuleFieldKind.Boolean,
                "date" => ModuleFieldKind.Date,
                "datetime" => ModuleFieldKind.DateTime,
                "guid" => ModuleFieldKind.Guid,
                _ => throw new ArgumentException(
                    $"Unsupported type '{typeToken}' for field '{name}'. Supported types: string, decimal, int, long, bool, date, datetime, guid, enum(...).")
            };
        }

        bool? required = null;
        int? maximumLength = null;
        foreach (string rawModifier in parts.Skip(2))
        {
            string modifier = rawModifier.Trim();
            if (modifier is "required" or "optional")
            {
                bool value = modifier == "required";
                if (required.HasValue && required.Value != value)
                    throw new ArgumentException($"Field '{name}' cannot be both required and optional.");
                if (required.HasValue)
                    throw new ArgumentException($"Field '{name}' repeats the '{modifier}' modifier.");
                required = value;
                continue;
            }

            Match maximum = MaximumLengthRegex().Match(modifier);
            if (maximum.Success)
            {
                if (kind != ModuleFieldKind.String)
                    throw new ArgumentException($"Field '{name}' can use max(length) only with the string type.");
                if (maximumLength.HasValue)
                    throw new ArgumentException($"Field '{name}' declares max(length) more than once.");
                maximumLength = int.Parse(maximum.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (maximumLength is < 1 or > 10000)
                    throw new ArgumentException($"Field '{name}' max(length) must be between 1 and 10000.");
                continue;
            }

            throw new ArgumentException($"Unsupported modifier '{modifier}' on field '{name}'.");
        }

        if (kind == ModuleFieldKind.String)
            maximumLength ??= 200;

        return new(name, ToPascalCase(name), kind, required ?? true, maximumLength, enumValues);
    }

    private static List<string> SplitTopLevel(string value, char separator)
    {
        List<string> parts = [];
        StringBuilder current = new();
        int depth = 0;
        foreach (char character in value)
        {
            if (character == '(') depth++;
            if (character == ')')
            {
                depth--;
                if (depth < 0) throw new ArgumentException("--fields contains an unmatched closing parenthesis.");
            }

            if (character == separator && depth == 0)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (depth != 0) throw new ArgumentException("--fields contains an unmatched opening parenthesis.");
        parts.Add(current.ToString().Trim());
        return parts;
    }

    private static string ToPascalCase(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex FieldNameRegex();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DotnetNameRegex();

    [GeneratedRegex("^max\\(([0-9]+)\\)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MaximumLengthRegex();
}

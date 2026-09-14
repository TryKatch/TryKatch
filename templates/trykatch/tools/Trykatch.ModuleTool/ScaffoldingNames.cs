using System.Text.RegularExpressions;

namespace Trykatch.ModuleTool;

internal readonly record struct ModuleName(string Value)
{
    public static ModuleName Parse(string? value) => new(ScaffoldingNameRules.ParsePascalCase(
        value, "Module name", "Invoicing"));
}

internal readonly record struct EntityName(string Value)
{
    public static EntityName Parse(string? value) => new(ScaffoldingNameRules.ParsePascalCase(
        value, "Entity name", "Invoice"));
}

internal readonly record struct ModuleId(string Value)
{
    public static ModuleId From(ModuleName name) => new(ScaffoldingNameRules.ToKebabCase(name.Value));
}

internal readonly record struct ResourceName(string Value)
{
    public static ResourceName Parse(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (!ScaffoldingNameRules.ResourceIdentifier.IsMatch(normalized))
            throw new ArgumentException("--resource must be a lower-case snake_case PostgreSQL identifier, for example 'invoices'.");
        if (ScaffoldingNameRules.PostgreSqlReservedIdentifiers.Contains(normalized))
            throw new ArgumentException($"Resource name '{normalized}' is a PostgreSQL keyword. Choose a descriptive plural name such as '{normalized}_records'.");
        if (normalized.Length > 35)
            throw new ArgumentException("--resource cannot exceed 35 characters because generated PostgreSQL index names are limited to 63 bytes.");
        return new(normalized);
    }
}

internal readonly record struct ApplicationNamespace(string Value)
{
    public static ApplicationNamespace Parse(string value)
    {
        if (!ScaffoldingNameRules.DotnetNamespace.IsMatch(value))
            throw new InvalidOperationException($"Application namespace '{value}' is not a valid .NET namespace.");
        return new(value);
    }
}

internal readonly record struct PublisherId(string Value)
{
    public static PublisherId From(ApplicationNamespace applicationNamespace)
    {
        string value = ScaffoldingNameRules.ToKebabCase(
            applicationNamespace.Value.Replace(".", string.Empty, StringComparison.Ordinal));
        if (!ScaffoldingNameRules.StableId.IsMatch(value))
            throw new InvalidOperationException($"Application namespace '{applicationNamespace.Value}' cannot produce a stable publisher id.");
        return new(value);
    }
}

internal static class ScaffoldingNameRules
{
    internal static readonly Regex ResourceIdentifier = new(
        "^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static readonly Regex DotnetNamespace = new(
        "^[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static readonly Regex StableId = new(
        "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex PascalCaseIdentifier = new(
        "^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static readonly HashSet<string> PostgreSqlReservedIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both",
        "case", "cast", "check", "collate", "column", "constraint", "create", "current_catalog",
        "current_date", "current_role", "current_time", "current_timestamp", "current_user", "default",
        "deferrable", "desc", "distinct", "do", "else", "end", "except", "false", "fetch", "for",
        "foreign", "freeze", "from", "full", "grant", "group", "having", "ilike", "in", "initially",
        "intersect", "into", "is", "isnull", "lateral", "leading", "like", "limit", "localtime",
        "localtimestamp", "natural", "not", "notnull", "null", "offset", "on", "only", "or", "order",
        "placing", "primary", "references", "returning", "select", "session_user", "similar", "some",
        "symmetric", "system_user", "table", "tablesample", "then", "to", "trailing", "true", "union",
        "unique", "user", "using", "variadic", "verbose", "when", "where", "window", "with"
    };

    private static readonly HashSet<string> ReservedPascalCaseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Api", "Application", "ArchitectureTests", "Common", "Domain", "Infrastructure",
        "IntegrationEvents", "Migrator", "Module", "Modules", "Presentation", "Tests", "UnitTests", "Web",
        // Windows device names remain reserved even when an extension is present. Keeping them out of
        // generated directories makes a module portable across every supported development machine.
        "Con", "Prn", "Aux", "Nul", "Clock$",
        "Com1", "Com2", "Com3", "Com4", "Com5", "Com6", "Com7", "Com8", "Com9",
        "Lpt1", "Lpt2", "Lpt3", "Lpt4", "Lpt5", "Lpt6", "Lpt7", "Lpt8", "Lpt9"
    };

    internal static string ParsePascalCase(string? value, string subject, string example)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (!PascalCaseIdentifier.IsMatch(normalized))
            throw new ArgumentException($"{subject} must be a PascalCase .NET identifier, for example '{example}'.");
        if (normalized.Length > 64)
            throw new ArgumentException($"{subject} cannot exceed 64 characters.");
        if (ReservedPascalCaseNames.Contains(normalized))
            throw new ArgumentException($"{subject} '{normalized}' is reserved by the Trykatch host or filesystem.");
        return normalized;
    }

    internal static string ToKebabCase(string value) => Regex.Replace(
        value,
        "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
        "-").ToLowerInvariant();
}

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Trykatch.Modules;

/// <summary>
/// One immutable, forward-only database change owned by a data-capable module.
/// The migrator executes the SQL inside its transaction and records its checksum.
/// </summary>
public sealed record ModuleMigration(string Id, string Sql);

/// <summary>
/// Optional data lifecycle seam implemented by a module that owns SQL outside the
/// host's EF Core migrations. Applied migrations must never be removed or edited.
/// </summary>
public interface IModuleMigrationContributor
{
    IReadOnlyList<ModuleMigration> Migrations { get; }
}

public sealed record AppliedModuleMigration(
    string ModuleId,
    string MigrationId,
    string Checksum);

public sealed record PendingModuleMigration(
    string ModuleId,
    string ModuleVersion,
    string MigrationId,
    string Checksum,
    string Sql);

/// <summary>
/// Validates module migration history and returns the deterministic, pending plan.
/// This is the testable interface; database locking and execution remain migrator details.
/// </summary>
public static partial class ModuleMigrationPlan
{
    public static IReadOnlyList<PendingModuleMigration> Build(
        IEnumerable<IModule> enabledModules,
        IEnumerable<AppliedModuleMigration> appliedMigrations)
    {
        ArgumentNullException.ThrowIfNull(enabledModules);
        ArgumentNullException.ThrowIfNull(appliedMigrations);

        IModule[] modules = enabledModules.ToArray();
        Dictionary<string, AppliedModuleMigration> applied = appliedMigrations
            .ToDictionary(
                migration => Key(migration.ModuleId, migration.MigrationId),
                StringComparer.Ordinal);
        List<PendingModuleMigration> defined = [];

        foreach (IModule module in modules)
        {
            if (module is not IModuleMigrationContributor contributor)
                continue;
            if (!module.Descriptor.Capabilities.HasFlag(ModuleCapabilities.Data))
                throw new InvalidOperationException(
                    $"Trykatch module '{module.Descriptor.Id}' contributes migrations without the Data capability.");

            foreach (ModuleMigration migration in contributor.Migrations)
            {
                if (!MigrationIdPattern().IsMatch(migration.Id))
                    throw new InvalidOperationException(
                        $"Trykatch module '{module.Descriptor.Id}' migration '{migration.Id}' must use 'yyyyMMddHHmm_description'.");
                if (string.IsNullOrWhiteSpace(migration.Sql))
                    throw new InvalidOperationException(
                        $"Trykatch module '{module.Descriptor.Id}' migration '{migration.Id}' has no SQL.");
                if (ContainsTransactionControl(migration.Sql))
                    throw new InvalidOperationException(
                        $"Trykatch module '{module.Descriptor.Id}' migration '{migration.Id}' contains transaction control. The Trykatch migrator owns the transaction.");

                string normalizedSql = NormalizeNewlines(migration.Sql).Trim() + "\n";
                defined.Add(new(
                    module.Descriptor.Id,
                    module.Descriptor.Version,
                    migration.Id,
                    ComputeChecksum(normalizedSql),
                    normalizedSql));
            }


            ValidateDeclaredRelations(module, contributor.Migrations);
        }

        string? duplicate = defined
            .GroupBy(migration => Key(migration.ModuleId, migration.MigrationId), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate Trykatch module migration '{duplicate}'.");

        HashSet<string> definedKeys = defined
            .Select(migration => Key(migration.ModuleId, migration.MigrationId))
            .ToHashSet(StringComparer.Ordinal);
        foreach (KeyValuePair<string, AppliedModuleMigration> historical in applied)
        {
            if (!definedKeys.Contains(historical.Key))
                throw new InvalidOperationException(
                    $"Applied Trykatch module migration '{historical.Value.ModuleId}/{historical.Value.MigrationId}' is missing from the enabled module package.");
        }

        List<PendingModuleMigration> pending = [];
        foreach (PendingModuleMigration migration in defined
                     .OrderBy(item => Array.FindIndex(modules, module => module.Descriptor.Id == item.ModuleId))
                     .ThenBy(item => item.MigrationId, StringComparer.Ordinal))
        {
            if (!applied.TryGetValue(Key(migration.ModuleId, migration.MigrationId), out AppliedModuleMigration? historical))
            {
                pending.Add(migration);
                continue;
            }

            if (!string.Equals(historical.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Applied Trykatch module migration '{migration.ModuleId}/{migration.MigrationId}' was modified after deployment.");
        }

        return pending;
    }

    public static string ComputeChecksum(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
    }

    private static string Key(string moduleId, string migrationId) => $"{moduleId}/{migrationId}";
    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool ContainsTransactionControl(string sql) =>
        TransactionControlPattern().IsMatch(RemoveSqlLiteralsAndComments(sql));

    private static string RemoveSqlLiteralsAndComments(string sql)
    {
        StringBuilder result = new(sql.Length);
        int index = 0;

        while (index < sql.Length)
        {
            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\n') index++;
                result.Append('\n');
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                index += 2;
                int depth = 1;
                while (index < sql.Length && depth > 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }
                result.Append(' ');
                continue;
            }

            if (sql[index] is '\'' or '"')
            {
                char quote = sql[index++];
                while (index < sql.Length)
                {
                    if (sql[index] != quote)
                    {
                        index++;
                        continue;
                    }

                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                    {
                        index += 2;
                        continue;
                    }

                    index++;
                    break;
                }
                result.Append(' ');
                continue;
            }

            if (TryReadDollarQuoteDelimiter(sql, index, out string delimiter))
            {
                int bodyStart = index + delimiter.Length;
                int closing = sql.IndexOf(delimiter, bodyStart, StringComparison.Ordinal);
                if (closing < 0)
                    return result.ToString();
                index = closing + delimiter.Length;
                result.Append(' ');
                continue;
            }

            result.Append(sql[index]);
            index++;
        }

        return result.ToString();
    }

    private static bool TryReadDollarQuoteDelimiter(string sql, int start, out string delimiter)
    {
        delimiter = string.Empty;
        if (sql[start] != '$') return false;

        int index = start + 1;
        if (index < sql.Length && sql[index] == '$')
        {
            delimiter = "$$";
            return true;
        }

        if (index >= sql.Length || !(char.IsAsciiLetter(sql[index]) || sql[index] == '_'))
            return false;

        index++;
        while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] == '_')) index++;
        if (index >= sql.Length || sql[index] != '$') return false;

        delimiter = sql[start..(index + 1)];
        return true;
    }

    private static void ValidateDeclaredRelations(
        IModule module,
        IReadOnlyList<ModuleMigration> migrations)
    {
        HashSet<string> created = migrations
            .SelectMany(migration => CreatedTablePattern().Matches(migration.Sql).Select(match =>
                $"{match.Groups["schema"].Value}.{match.Groups["table"].Value}"))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> declared = module.Descriptor.DataResources
            .Select(resource => $"{resource.Schema}.{resource.Table}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (string relation in created.Except(declared, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Trykatch module '{module.Descriptor.Id}' migration creates undeclared relation '{relation}'.");
        foreach (DataResourceDescriptor resource in module.Descriptor.DataResources)
        {
            string relation = $"{resource.Schema}.{resource.Table}";
            if (!created.Contains(relation))
                throw new InvalidOperationException(
                    $"Trykatch module '{module.Descriptor.Id}' declares persistent relation '{relation}' but its migrations do not create it.");
            if (resource.Ownership == ModuleDataOwnership.Organization)
            {
                string sql = string.Join('\n', migrations.Select(migration => migration.Sql));
                if (!sql.Contains($"ALTER TABLE {relation} ENABLE ROW LEVEL SECURITY", StringComparison.OrdinalIgnoreCase)
                    || !sql.Contains($"ALTER TABLE {relation} FORCE ROW LEVEL SECURITY", StringComparison.OrdinalIgnoreCase)
                    || !sql.Contains("USING", StringComparison.OrdinalIgnoreCase)
                    || !sql.Contains("WITH CHECK", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Trykatch organization relation '{relation}' requires ENABLE/FORCE RLS with USING and WITH CHECK clauses.");
            }
        }
    }

    [GeneratedRegex("^[0-9]{12}_[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MigrationIdPattern();

    [GeneratedRegex("(?:^|[;\\s])(?:begin|commit|rollback)(?:[;\\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TransactionControlPattern();

    [GeneratedRegex("CREATE\\s+TABLE(?:\\s+IF\\s+NOT\\s+EXISTS)?\\s+(?:\"(?<schema>[A-Za-z_][A-Za-z0-9_]*)\"|(?<schema>[a-z][a-z0-9_]*))\\.(?:\"(?<table>[A-Za-z_][A-Za-z0-9_]*)\"|(?<table>[a-z][a-z0-9_]*))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CreatedTablePattern();
}

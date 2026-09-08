using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FlatpackApp.Modules;

/// <summary>
/// One immutable, forward-only database change owned by a data-capable module.
/// The migrator executes the SQL inside its transaction and records its checksum.
/// </summary>
public sealed record FlatpackModuleMigration(string Id, string Sql);

/// <summary>
/// Optional data lifecycle seam implemented by a module that owns SQL outside the
/// host's EF Core migrations. Applied migrations must never be removed or edited.
/// </summary>
public interface IFlatpackModuleMigrationContributor
{
    IReadOnlyList<FlatpackModuleMigration> Migrations { get; }
}

public sealed record AppliedFlatpackModuleMigration(
    string ModuleId,
    string MigrationId,
    string Checksum);

public sealed record PendingFlatpackModuleMigration(
    string ModuleId,
    string ModuleVersion,
    string MigrationId,
    string Checksum,
    string Sql);

/// <summary>
/// Validates module migration history and returns the deterministic, pending plan.
/// This is the testable interface; database locking and execution remain migrator details.
/// </summary>
public static partial class FlatpackModuleMigrationPlan
{
    public static IReadOnlyList<PendingFlatpackModuleMigration> Build(
        IEnumerable<IFlatpackModule> enabledModules,
        IEnumerable<AppliedFlatpackModuleMigration> appliedMigrations)
    {
        ArgumentNullException.ThrowIfNull(enabledModules);
        ArgumentNullException.ThrowIfNull(appliedMigrations);

        IFlatpackModule[] modules = enabledModules.ToArray();
        Dictionary<string, AppliedFlatpackModuleMigration> applied = appliedMigrations
            .ToDictionary(
                migration => Key(migration.ModuleId, migration.MigrationId),
                StringComparer.Ordinal);
        List<PendingFlatpackModuleMigration> defined = [];

        foreach (IFlatpackModule module in modules)
        {
            if (module is not IFlatpackModuleMigrationContributor contributor)
                continue;
            if (!module.Descriptor.Capabilities.HasFlag(FlatpackModuleCapabilities.Data))
                throw new InvalidOperationException(
                    $"Flatpack module '{module.Descriptor.Id}' contributes migrations without the Data capability.");

            foreach (FlatpackModuleMigration migration in contributor.Migrations)
            {
                if (!MigrationIdPattern().IsMatch(migration.Id))
                    throw new InvalidOperationException(
                        $"Flatpack module '{module.Descriptor.Id}' migration '{migration.Id}' must use 'yyyyMMddHHmm_description'.");
                if (string.IsNullOrWhiteSpace(migration.Sql))
                    throw new InvalidOperationException(
                        $"Flatpack module '{module.Descriptor.Id}' migration '{migration.Id}' has no SQL.");
                if (TransactionControlPattern().IsMatch(migration.Sql))
                    throw new InvalidOperationException(
                        $"Flatpack module '{module.Descriptor.Id}' migration '{migration.Id}' contains transaction control. The Flatpack migrator owns the transaction.");

                string normalizedSql = NormalizeNewlines(migration.Sql).Trim() + "\n";
                defined.Add(new(
                    module.Descriptor.Id,
                    module.Descriptor.Version,
                    migration.Id,
                    ComputeChecksum(normalizedSql),
                    normalizedSql));
            }
        }

        string? duplicate = defined
            .GroupBy(migration => Key(migration.ModuleId, migration.MigrationId), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate Flatpack module migration '{duplicate}'.");

        HashSet<string> definedKeys = defined
            .Select(migration => Key(migration.ModuleId, migration.MigrationId))
            .ToHashSet(StringComparer.Ordinal);
        foreach (KeyValuePair<string, AppliedFlatpackModuleMigration> historical in applied)
        {
            if (!definedKeys.Contains(historical.Key))
                throw new InvalidOperationException(
                    $"Applied Flatpack module migration '{historical.Value.ModuleId}/{historical.Value.MigrationId}' is missing from the enabled module package.");
        }

        List<PendingFlatpackModuleMigration> pending = [];
        foreach (PendingFlatpackModuleMigration migration in defined
                     .OrderBy(item => Array.FindIndex(modules, module => module.Descriptor.Id == item.ModuleId))
                     .ThenBy(item => item.MigrationId, StringComparer.Ordinal))
        {
            if (!applied.TryGetValue(Key(migration.ModuleId, migration.MigrationId), out AppliedFlatpackModuleMigration? historical))
            {
                pending.Add(migration);
                continue;
            }

            if (!string.Equals(historical.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Applied Flatpack module migration '{migration.ModuleId}/{migration.MigrationId}' was modified after deployment.");
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

    [GeneratedRegex("^[0-9]{12}_[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MigrationIdPattern();

    [GeneratedRegex("(?:^|[;\\s])(?:begin|commit|rollback)(?:[;\\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TransactionControlPattern();
}

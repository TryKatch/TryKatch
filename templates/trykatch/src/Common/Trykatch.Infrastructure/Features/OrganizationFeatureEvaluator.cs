using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Features;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Features;

internal sealed class OrganizationFeatureEvaluator(OrganizationControlPlaneDbContext db,
    IOrganizationContext organization, ModuleCatalog modules, FeaturePolicySettings settings, TimeProvider clock)
    : IOrganizationFeatureEvaluator, IOrganizationFeatureSnapshotReader
{
    public async Task<bool> IsEnabledAsync(string featureKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ModuleFeatureDescriptor? declaration = modules.Descriptors.SelectMany(module => module.Features)
            .SingleOrDefault(feature => feature.Key == featureKey);
        if (declaration is null || settings.Disabled.Contains(featureKey)) return false;
        PolicyRow row = (await ReadRowsAsync([featureKey], ct)).Single();
        return Evaluate(declaration, row);
    }

    public async Task<OrganizationFeatureSnapshot> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ModuleFeatureDescriptor[] declarations = modules.Descriptors.SelectMany(module => module.Features).ToArray();
        PolicyRow[] rows = await ReadRowsAsync(declarations.Select(feature => feature.Key).ToArray(), ct);
        Dictionary<string, PolicyRow> byKey = rows.ToDictionary(row => row.FeatureKey, StringComparer.Ordinal);
        return new(organization.OrganizationId, clock.GetUtcNow(), declarations.ToFrozenDictionary(
            feature => feature.Key, feature => Evaluate(feature, byKey[feature.Key]), StringComparer.Ordinal));
    }

    private bool Evaluate(ModuleFeatureDescriptor declaration, PolicyRow row) => FeatureAvailabilityPolicy.IsEnabled(
        declaration, settings.Disabled.Contains(declaration.Key), row.ForceDisabled, row.OrganizationEnabled,
        row.EnvironmentDefault, settings.Defaults.TryGetValue(declaration.Key, out bool configured) ? configured : null);

    private async Task<PolicyRow[]> ReadRowsAsync(string[] keys, CancellationToken ct)
    {
        DbTransaction? transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        if (!organization.IsResolved || organization.ActorId == Guid.Empty || organization.OrganizationId == Guid.Empty
            || transaction?.Connection is null || transaction.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Feature evaluation requires an active ReadCommitted organization scope.");
        if (keys.Length == 0) return [];
        Guid actor = organization.ActorId, organizationId = organization.OrganizationId;
        PolicyRow[] rows;
        try
        {
            // Both layers and the trusted SQL scope are read in one statement/snapshot. No enabled decision is cached.
            rows = await db.Database.SqlQuery<PolicyRow>($"""
                SELECT declared."FeatureKey", environment."DefaultEnabled" AS "EnvironmentDefault",
                       COALESCE(environment."ForceDisabled", false) AS "ForceDisabled", overrides."Enabled" AS "OrganizationEnabled"
                FROM unnest({keys}::text[]) AS declared("FeatureKey")
                LEFT JOIN platform.feature_environment_controls AS environment
                  ON environment."Environment" = {settings.Environment} AND environment."FeatureKey" = declared."FeatureKey"
                LEFT JOIN platform.organization_feature_overrides AS overrides
                  ON overrides."OrganizationId" = {organizationId} AND overrides."Environment" = {settings.Environment}
                     AND overrides."FeatureKey" = declared."FeatureKey"
                WHERE current_user = 'trykatch_org_runtime'
                  AND NULLIF(current_setting('app.actor_id', true), '')::uuid = {actor}
                  AND NULLIF(current_setting('app.organization_id', true), '')::uuid = {organizationId}
                """).ToArrayAsync(ct);
        }
        catch (DbException exception)
        {
            ct.ThrowIfCancellationRequested();
            throw new OrganizationFeaturePolicyUnavailableException(exception);
        }
        if (rows.Length != keys.Length)
            throw new InvalidOperationException("Feature evaluation requires its matching organization actor and database profile.");
        return rows;
    }

    private sealed class PolicyRow
    {
        public string FeatureKey { get; set; } = string.Empty;
        public bool? EnvironmentDefault { get; set; }
        public bool ForceDisabled { get; set; }
        public bool? OrganizationEnabled { get; set; }
    }
}

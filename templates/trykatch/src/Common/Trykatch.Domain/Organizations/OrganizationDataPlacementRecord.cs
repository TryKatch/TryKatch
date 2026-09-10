using Trykatch.Domain.Common;

namespace Trykatch.Domain.Organizations;

public enum OrganizationDataPlacementKind
{
    Shared = 1,
    Dedicated = 2
}

public enum OrganizationProvisioningState
{
    Provisioning = 1,
    Ready = 2,
    Failed = 3
}

public sealed class OrganizationDataPlacementRecord : Entity
{
    private OrganizationDataPlacementRecord() : base(Guid.Empty) { }

    private OrganizationDataPlacementRecord(
        Guid organizationId,
        OrganizationDataPlacementKind placement,
        string provider,
        string? regionOrStamp) : base(organizationId)
    {
        OrganizationId = organizationId;
        Placement = placement;
        Provider = provider;
        RegionOrStamp = regionOrStamp;
        State = OrganizationProvisioningState.Provisioning;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid OrganizationId { get; private init; }
    public OrganizationDataPlacementKind Placement { get; private init; }
    public string Provider { get; private init; } = string.Empty;
    public string? RegionOrStamp { get; private init; }
    public string? DatabaseIdentifier { get; private set; }
    public string? SecretReference { get; private set; }
    public string? SchemaVersion { get; private set; }
    public OrganizationProvisioningState State { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ReadyAt { get; private set; }

    public static OrganizationDataPlacementRecord Begin(
        Guid organizationId,
        OrganizationDataPlacementKind placement,
        string provider,
        string? regionOrStamp) =>
        new(organizationId, placement, provider, regionOrStamp);

    public void MarkReady(
        string databaseIdentifier,
        string secretReference,
        string schemaVersion,
        DateTimeOffset now)
    {
        DatabaseIdentifier = databaseIdentifier;
        SecretReference = secretReference;
        SchemaVersion = schemaVersion;
        FailureCode = null;
        State = OrganizationProvisioningState.Ready;
        ReadyAt = now;
        UpdatedAt = now;
    }

    public void MarkFailed(string failureCode, DateTimeOffset now)
    {
        FailureCode = failureCode;
        State = OrganizationProvisioningState.Failed;
        UpdatedAt = now;
    }

    public void BeginRetry(DateTimeOffset now)
    {
        if (State != OrganizationProvisioningState.Failed)
            throw new InvalidOperationException("Only failed data placement can be retried.");
        State = OrganizationProvisioningState.Provisioning;
        FailureCode = null;
        UpdatedAt = now;
    }
}

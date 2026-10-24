namespace Trykatch.Modules;

/// <summary>The current quota operation cannot transition the referenced hold.</summary>
public sealed class OrganizationQuotaConflictException()
    : Exception("The quota reservation conflicts with the current operation.");

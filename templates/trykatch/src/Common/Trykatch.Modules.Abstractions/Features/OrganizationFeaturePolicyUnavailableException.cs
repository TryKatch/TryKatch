namespace Trykatch.Modules;

/// <summary>Authoritative organization feature policy could not be read.</summary>
public sealed class OrganizationFeaturePolicyUnavailableException(Exception innerException)
    : Exception("Organization feature policy is temporarily unavailable.", innerException);

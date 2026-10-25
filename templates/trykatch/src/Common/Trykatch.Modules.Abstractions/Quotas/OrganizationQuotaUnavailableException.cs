namespace Trykatch.Modules;

/// <summary>Quota authority or durable state could not be confirmed; no successful transition is implied.</summary>
public sealed class OrganizationQuotaUnavailableException(Exception? innerException = null)
    : Exception("Organization quota state is temporarily unavailable.", innerException);

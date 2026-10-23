namespace Trykatch.Application.Organizations;

/// <summary>A membership-only use case cannot run with system authority.</summary>
public sealed class OrganizationMembershipRequiredException() : InvalidOperationException(
    "This operation requires a current organization membership.");

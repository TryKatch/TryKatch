namespace Trykatch.Modules;

/// <summary>A declared organization feature is currently unavailable.</summary>
public sealed class OrganizationFeatureUnavailableException()
    : Exception("The requested feature is unavailable in this organization.");

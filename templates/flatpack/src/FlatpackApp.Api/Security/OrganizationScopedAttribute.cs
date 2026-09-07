namespace FlatpackApp.Api.Security;

/// <summary>Marks an endpoint as requiring a validated organization request context.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OrganizationScopedAttribute : Attribute;

using Trykatch.Modules.AspNetCore;

namespace Trykatch.Api.Security;

/// <summary>Marks an endpoint as requiring a validated organization request context.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class OrganizationScopedAttribute : Attribute, IOrganizationScopedMetadata;

/// <summary>Marks an endpoint that reads platform tenancy data and therefore requires actor-scoped PostgreSQL settings.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PlatformDataScopedAttribute : Attribute;

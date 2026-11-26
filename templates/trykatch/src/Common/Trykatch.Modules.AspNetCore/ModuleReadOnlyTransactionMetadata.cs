namespace Trykatch.Modules.AspNetCore;

/// <summary>
/// Explicitly opts a safe organization GET/HEAD endpoint into a PostgreSQL read-only
/// transaction. This is a database constraint, not an authorization grant.
/// </summary>
public sealed class ModuleReadOnlyTransactionMetadata;

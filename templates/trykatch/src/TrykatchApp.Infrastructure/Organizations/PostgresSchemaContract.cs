namespace TrykatchApp.Infrastructure.Organizations;

/// <summary>Shared catalog predicate for persistent, user-inspectable PostgreSQL schemas.</summary>
internal static class PostgresSchemaContract
{
    // PostgreSQL names temporary schemas pg_temp_<backend-id> and their toast
    // companions pg_toast_temp_<backend-id>. Regex anchors and a numeric suffix
    // are intentional: LIKE underscores would also hide legal names such as
    // pgxtemp_private and pgxtempyescape.
    public const string UserSchemaPredicate = """
        n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
        AND n.nspname !~ '^pg_temp_[0-9]+$'
        AND n.nspname !~ '^pg_toast_temp_[0-9]+$'
        """;
}

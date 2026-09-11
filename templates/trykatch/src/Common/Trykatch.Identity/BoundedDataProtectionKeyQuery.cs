using Microsoft.EntityFrameworkCore;

namespace Trykatch.Identity;

internal sealed record BoundedDataProtectionKey(int Id, string? Xml);

internal static class BoundedDataProtectionKeyQuery
{
    internal static IOrderedQueryable<BoundedDataProtectionKey> Read(IdentityDbContext database) =>
        database.Database.SqlQuery<BoundedDataProtectionKey>($"""
            SELECT "Id", CASE
                WHEN bool_and("Xml" IS NOT NULL AND octet_length("Xml") BETWEEN 1 AND {DataProtectionKeyRing.MaximumXmlBytes}) OVER ()
                THEN "Xml" ELSE NULL END AS "Xml"
            FROM identity.data_protection_keys
            """).OrderBy(key => key.Id);
    // The window checks every row in the same SQL snapshot. An invalid ring returns
    // only IDs and null failure sentinels, never oversized text or a partial XML ring.
    // Keep ordering stable for maintenance's row-to-descriptor correlation. FriendlyName
    // is intentionally not selected: it is also unbounded and maintenance never edits it.
}

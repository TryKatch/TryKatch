using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Quotas;

internal static class QuotaResourceLock
{
    internal static Task<bool> TryAcquireAsync(ApplicationDbContext application, Guid organizationId,
        string resourceKey, CancellationToken cancellationToken)
    {
        if (application.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Quota locks require an active organization transaction.");
        long key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(
            Encoding.UTF8.GetBytes($"quota/{organizationId:N}/{resourceKey}")));
        return application.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(cancellationToken);
    }
}

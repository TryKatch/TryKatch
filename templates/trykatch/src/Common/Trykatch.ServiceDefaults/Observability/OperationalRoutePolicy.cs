using Microsoft.AspNetCore.Http;

namespace Trykatch.ServiceDefaults.Observability;

internal static class OperationalRoutePolicy
{
    public static bool IsOperationalPath(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");
}

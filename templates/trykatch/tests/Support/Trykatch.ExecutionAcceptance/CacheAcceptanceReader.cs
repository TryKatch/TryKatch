using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Modules.Projects.Application;

namespace Trykatch.ExecutionAcceptance;

internal sealed record CacheAcceptanceReadCommand(Guid OrganizationId, Guid ActorId, Guid ProjectId);

public sealed class CacheAcceptanceSourceQueryCounter : DbCommandInterceptor
{
    private static readonly Regex ProjectSource = new(
        @"\bFROM\s+(?:""app""|app)\s*\.\s*(?:""projects""|projects)(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private long count;

    public long Count => Interlocked.Read(ref count);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (ProjectSource.IsMatch(command.CommandText)) Interlocked.Increment(ref count);
        return ValueTask.FromResult(result);
    }
}

internal static partial class Program
{
    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web);

    private static async Task<int> RunCacheReaderAsync(IServiceProvider services)
    {
        CacheAcceptanceSourceQueryCounter sourceQueries = services.GetRequiredService<CacheAcceptanceSourceQueryCounter>();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            CacheAcceptanceReadCommand? command;
            try { command = JsonSerializer.Deserialize<CacheAcceptanceReadCommand>(line, CacheJson); }
            catch (JsonException) { command = null; }

            if (command is null || command.OrganizationId == Guid.Empty || command.ActorId == Guid.Empty
                || command.ProjectId == Guid.Empty)
            {
                await WriteCacheResponseAsync(new { kind = "error", code = "invalid-command", processId = Environment.ProcessId });
                continue;
            }

            long before = sourceQueries.Count;
            object response;
            try
            {
                await using (AsyncServiceScope scope = services.CreateAsyncScope())
                {
                    await using IOrganizationDataScope transaction = await scope.ServiceProvider
                        .GetRequiredService<IOrganizationDataScopeFactory>()
                        .BeginReadOnlyAsync(command.OrganizationId, command.ActorId, CancellationToken.None);
                    OrganizationAccess? access = await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
                        .ResolveAsync(command.ActorId, command.OrganizationId, CancellationToken.None);
                    if (access is null)
                    {
                        response = new
                        {
                            kind = "denied",
                            code = "membership_unavailable",
                            processId = Environment.ProcessId,
                            sourceQueryCount = sourceQueries.Count - before
                        };
                    }
                    else
                    {
                        scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);
                        Result<ProjectDto> result = await scope.ServiceProvider.GetRequiredService<ProjectUseCases>()
                            .GetAsync(command.ProjectId, CancellationToken.None);
                        response = new
                        {
                            kind = result.IsSuccess ? "project" : "denied",
                            code = result.IsSuccess ? null : result.ErrorCode,
                            processId = Environment.ProcessId,
                            name = result.IsSuccess ? result.Value!.Name : null,
                            projectId = result.IsSuccess ? result.Value!.Id : (Guid?)null,
                            sourceQueryCount = sourceQueries.Count - before
                        };
                    }
                }
            }
            catch (Exception exception)
            {
                // This process protocol deliberately excludes exception details and configuration values.
                response = new
                {
                    kind = "error",
                    code = exception.GetType().Name,
                    processId = Environment.ProcessId,
                    sourceQueryCount = sourceQueries.Count - before
                };
            }
            await WriteCacheResponseAsync(response);
        }
        return 0;
    }

    private static Task WriteCacheResponseAsync(object response) =>
        Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, CacheJson));
}

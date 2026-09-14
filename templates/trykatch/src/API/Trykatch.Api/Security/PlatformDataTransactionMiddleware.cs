using System.Security.Claims;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Trykatch.Api.Security;

public sealed class AtomicMutationResponseOptions
{
    public const string SectionName = "AtomicMutationResponse";
    public int MaximumBytes { get; init; } = 1_048_576;
}

public sealed partial class PlatformDataTransactionMiddleware(
    RequestDelegate next,
    IOptions<AtomicMutationResponseOptions> responseOptions,
    ILogger<PlatformDataTransactionMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        OrganizationControlPlaneDbContext organizationDbContext,
        IWorkspaceContextCookie workspaceCookie,
        ModuleTransactionCompensation transactionCompensation)
    {
        Endpoint? endpoint = context.GetEndpoint();
        bool usesPlatformData = endpoint?.Metadata.GetMetadata<PlatformDataScopedAttribute>() is not null
            || endpoint?.Metadata.GetMetadata<IOrganizationScopedMetadata>() is not null;
        if (!usesPlatformData || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        string? subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out Guid actorId))
        {
            await next(context);
            return;
        }

        bool platformWorkflow = endpoint!.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy?.StartsWith("platform-permission:", StringComparison.Ordinal) == true);
        if (platformWorkflow && endpoint.Metadata.GetMetadata<IOrganizationScopedMetadata>() is not null)
            throw new InvalidOperationException("An organization endpoint cannot request platform database privileges.");
        DbContext platformDbContext = platformWorkflow
            ? context.RequestServices.GetRequiredService<PlatformDbContext>()
            : organizationDbContext;
        await using IDbContextTransaction transaction = await platformDbContext.Database.BeginTransactionAsync(context.RequestAborted);
        string actor = actorId.ToString();
        Guid organizationId;
        string? organizationClaim = context.User.FindFirstValue("organization_id");
        bool hasOrganization = Guid.TryParse(organizationClaim, out organizationId)
            || workspaceCookie.TryRead(context, out organizationId);
        string organization = !platformWorkflow && hasOrganization ? organizationId.ToString() : string.Empty;
        await platformDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.actor_id', {actor}, true), set_config('app.organization_id', {organization}, true)",
            context.RequestAborted);

        bool mutation = HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method);
        if (!mutation)
        {
            await next(context);
            if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
                await transaction.CommitAsync(context.RequestAborted);
            return;
        }

        bool transactionCommitted = false;
        bool transactionRolledBack = false;
        try
        {
            await AtomicMutationResponse.ExecuteAsync(
                context,
                responseOptions.Value.MaximumBytes,
                next,
                async cancellationToken =>
                {
                    if (context.Response.StatusCode < StatusCodes.Status500InternalServerError)
                    {
                        try
                        {
                            await transaction.CommitAsync(cancellationToken);
                        }
                        catch (PostgresException)
                        {
                            // PostgreSQL returned an explicit commit rejection, so
                            // the transaction is known not to have committed even
                            // though Npgsql now considers it completed.
                            transactionRolledBack = true;
                            try
                            {
                                await transactionCompensation.RollbackAsync(CancellationToken.None);
                            }
                            catch (Exception exception)
                            {
                                LogCompensationFailure(logger, exception);
                            }
                            throw;
                        }
                        transactionCommitted = true;
                        transactionCompensation.Complete();
                    }
                    else
                    {
                        await transactionCompensation.CompensateAfterConfirmedRollbackAsync(
                            async cancellationToken =>
                            {
                                await transaction.RollbackAsync(cancellationToken);
                                transactionRolledBack = true;
                            },
                            CancellationToken.None);
                    }
                });
        }
        catch
        {
            if (!transactionCommitted && !transactionRolledBack)
            {
                await RollbackAfterFailureAsync(transaction, transactionCompensation);
            }
            throw;
        }
    }

    private async Task RollbackAfterFailureAsync(
        IDbContextTransaction transaction,
        ModuleTransactionCompensation transactionCompensation)
    {
        bool rollbackConfirmed = false;
        try
        {
            await transactionCompensation.CompensateAfterConfirmedRollbackAsync(
                async cancellationToken =>
                {
                    await transaction.RollbackAsync(cancellationToken);
                    rollbackConfirmed = true;
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (rollbackConfirmed)
            {
                LogCompensationFailure(logger, exception);
            }
            else
            {
                LogDatabaseRollbackFailure(logger, exception);
                LogCompensationDeferred(logger);
            }
        }
    }

    [LoggerMessage(
        EventId = 4401,
        Level = LogLevel.Error,
        Message = "Database transaction rollback failed after a request error.")]
    private static partial void LogDatabaseRollbackFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4402,
        Level = LogLevel.Error,
        Message = "Module side-effect compensation failed after a request rollback.")]
    private static partial void LogCompensationFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4403,
        Level = LogLevel.Warning,
        Message = "Destructive module compensation was deferred because database rollback could not be confirmed.")]
    private static partial void LogCompensationDeferred(ILogger logger);
}

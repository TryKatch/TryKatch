using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Modules;

namespace Trykatch.Modules.AspNetCore;

public sealed record ModuleMutationIdempotencyMetadata(ModuleMutationPolicy Policy,
    Func<EndpointFilterInvocationContext, MutationAuthority, CancellationToken, Task<bool>> AuthorizeRequestAsync,
    Func<EndpointFilterInvocationContext, MutationAuthority, MutationResourceReference?, CancellationToken, Task<bool>> AuthorizeReplayAsync);

public static class ModuleMutationIdempotency
{
    public static RouteHandlerBuilder WithMutationIdempotency(this RouteHandlerBuilder builder, ModuleMutationPolicy policy,
        Func<EndpointFilterInvocationContext, MutationAuthority, CancellationToken, Task<bool>> authorizeRequest,
        Func<EndpointFilterInvocationContext, MutationAuthority, MutationResourceReference?, CancellationToken, Task<bool>> authorizeReplay)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeRequest);
        ArgumentNullException.ThrowIfNull(authorizeReplay);
        policy.Validate();
        builder.WithMetadata(new ModuleMutationIdempotencyMetadata(policy, authorizeRequest, authorizeReplay))
            .AddEndpointFilter<ModuleMutationIdempotencyFilter>();
        builder.Finally(endpoint => MutationEndpointIdentity.Validate(new(endpoint.Metadata), endpoint.ApplicationServices.GetRequiredService<ModuleCatalog>()));
        return builder;
    }

    public static void BindResource(HttpContext context, MutationResourceReference resource)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        if (context.Features.Get<IModuleMutationResponseCaptureFeature>() is MutationResponseCapture capture)
            capture.Bind(resource);
    }

    /// <summary>Runs after authorization/antiforgery middleware and before endpoint body binding.</summary>
    public static IApplicationBuilder UseModuleMutationIdempotency(this IApplicationBuilder app) =>
        app.UseMiddleware<ModuleMutationPreparationMiddleware>();
}

public sealed class ModuleMutationIdempotencyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        Endpoint endpoint = http.GetEndpoint() ?? throw new InvalidOperationException("Mutation enrollment requires a routed endpoint.");
        ModuleMutationIdempotencyMetadata metadata = endpoint.Metadata.GetMetadata<ModuleMutationIdempotencyMetadata>()
            ?? throw new InvalidOperationException("Mutation enrollment metadata is required.");
        if (!metadata.Policy.KeyRequired && !http.Request.Headers.ContainsKey("Idempotency-Key")) return await next(context);
        ModulePreparedMutationRequest prepared = http.Features.Get<ModulePreparedMutationRequest>()
            ?? throw new InvalidOperationException("Mutation preparation must run before endpoint binding.");
        MutationEndpointIdentity identity = MutationEndpointIdentity.Validate(endpoint.Metadata,
            http.RequestServices.GetRequiredService<ModuleCatalog>());
        IModuleMutationIdempotencyStore store = http.RequestServices.GetRequiredService<IModuleMutationIdempotencyStore>();
        MutationRequest request = new(identity.ModuleId, identity.OperationId, prepared.KeyHash, prepared.RequestHash,
            metadata.Policy, identity.Permissions);
        MutationReservationResult result = await store.ReserveAsync(request,
            (authority, ct) => metadata.AuthorizeRequestAsync(context, authority, ct), http.RequestAborted);
        switch (result.State)
        {
            case MutationReservationState.Reserved:
                Guid token = result.ReservationId is { } value && value != Guid.Empty ? value
                    : throw new InvalidOperationException("The store did not return a mutation reservation.");
                ModuleAtomicMutationResponseLimit limit = http.Features.Get<ModuleAtomicMutationResponseLimit>()
                    ?? throw new InvalidOperationException("Mutation enrollment requires atomic response staging.");
                http.Features.Set<IModuleMutationResponseCaptureFeature>(new MutationResponseCapture(store, token,
                    metadata.Policy, Math.Min(metadata.Policy.MaximumResponseBytes, limit.MaximumBytes)));
                object? response = await next(context);
                if (response is not null && MutationEndpointIdentity.IsStreamingType(response.GetType()))
                    throw new NotSupportedException("Streaming and file results cannot be enrolled in mutation replay.");
                return response;
            case MutationReservationState.Completed:
                if (result.Authority is null || result.Receipt is null)
                    throw new InvalidOperationException("The store did not return an authorized mutation receipt.");
                if (!await metadata.AuthorizeReplayAsync(context, result.Authority, result.Receipt.ResourceReference, http.RequestAborted))
                    return Denied();
                http.RequestAborted.ThrowIfCancellationRequested();
                return new MutationReplayResult(result.Receipt);
            case MutationReservationState.Conflict:
                return Results.Problem(statusCode: 409, title: "idempotency_conflict");
            case MutationReservationState.InProgress:
                http.Response.Headers.RetryAfter = "1";
                return Results.Problem(statusCode: 409, title: "idempotency_in_progress");
            case MutationReservationState.Denied:
                return Denied();
            default:
                throw new InvalidOperationException("The store returned an unsupported mutation outcome.");
        }
    }

    private static IResult Denied() => Results.Problem(statusCode: 403, title: "forbidden");
}

internal sealed class ModuleMutationPreparationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ModuleMutationIdempotencyMetadata? metadata = context.GetEndpoint()?.Metadata.GetMetadata<ModuleMutationIdempotencyMetadata>();
        if (metadata is null || context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
        {
            await next(context);
            return;
        }
        try
        {
            ModulePreparedMutationRequest? request = await ModuleRequestFingerprint.PrepareAsync(context.Request, metadata.Policy, context.RequestAborted);
            if (request is not null) context.Features.Set(request);
        }
        catch (ModuleMutationRequestException exception)
        {
            await Results.Problem(statusCode: exception.StatusCode, title: exception.Code).ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}

internal sealed record MutationEndpointIdentity(string ModuleId, string OperationId, string[] Permissions)
{
    internal static MutationEndpointIdentity Validate(EndpointMetadataCollection metadata, ModuleCatalog catalog)
    {
        ModuleMutationIdempotencyMetadata[] enrollments = metadata.GetOrderedMetadata<ModuleMutationIdempotencyMetadata>().ToArray();
        ModuleEndpointMetadata? owner = metadata.GetMetadata<ModuleEndpointMetadata>();
        string? name = metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        IReadOnlyList<IAuthorizeData> authorization = metadata.GetOrderedMetadata<IAuthorizeData>();
        string[] permissions = authorization.Where(item => item.Policy?.StartsWith("permission:", StringComparison.Ordinal) == true)
            .Select(item => item.Policy!["permission:".Length..]).Distinct(StringComparer.Ordinal).ToArray();
        IReadOnlyList<string>? methods = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        if (enrollments.Length != 1 || owner is null || !catalog.Contains(owner.ModuleId) || string.IsNullOrWhiteSpace(name)
            || metadata.GetMetadata<IOrganizationScopedMetadata>() is null || metadata.GetMetadata<IAllowAnonymous>() is not null
            || authorization.Any(item => item.Policy?.StartsWith("platform-permission:", StringComparison.Ordinal) == true)
            || permissions.Length == 0 || methods is null || methods.Count == 0
            || methods.Any(method => method is not ("POST" or "PUT" or "PATCH" or "DELETE"))
            || metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Any(response =>
                response.ContentTypeIsStreaming() || response.Type is { } type && IsStreamingType(type)))
            throw new InvalidOperationException("Mutation replay requires one installed, named, secured organization mutation with bounded responses.");
        ModuleMutationIdempotencyMetadata enrollment = enrollments[0];
        if (enrollment.AuthorizeRequestAsync is null || enrollment.AuthorizeReplayAsync is null)
            throw new InvalidOperationException("Mutation replay requires explicit request and replay authorization gates.");
        new MutationRequest(owner.ModuleId, name, new string('A', 64), new string('B', 64), enrollment.Policy, permissions).Validate();
        return new(owner.ModuleId, name, permissions);
    }

    internal static bool IsStreamingType(Type type) =>
        typeof(Stream).IsAssignableFrom(type) || typeof(IFileHttpResult).IsAssignableFrom(type)
        || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)
        || type.GetInterfaces().Any(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
}

internal static class MutationResponseMetadata
{
    internal static bool ContentTypeIsStreaming(this IProducesResponseTypeMetadata metadata) =>
        metadata.ContentTypes.Any(type => type.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase));
}

internal sealed class MutationResponseCapture(IModuleMutationIdempotencyStore store, Guid token,
    ModuleMutationPolicy policy, int maximumResponseBytes) : IModuleMutationResponseCaptureFeature
{
    private MutationResourceReference? resource;
    public int MaximumResponseBytes => maximumResponseBytes;
    public bool RequiresRollback { get; private set; }

    internal void Bind(MutationResourceReference reference)
    {
        new MutationReceipt(200, null, null, [], reference).Validate(policy);
        if (resource is not null && resource != reference) throw new InvalidOperationException("A mutation resource cannot be rebound.");
        resource = reference;
    }

    public async Task CaptureAsync(StagedMutationResponse response, CancellationToken cancellationToken)
    {
        if (response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            throw new NotSupportedException("Event streams cannot be captured for mutation replay.");
        if (response.StatusCode is < 200 or > 299)
        {
            RequiresRollback = true;
            return;
        }
        if (response.StatusCode is 204 or 205 && response.Body.Length != 0)
            throw new InvalidOperationException("A bodyless HTTP success cannot persist response bytes.");
        await store.CompleteAsync(token, new(response.StatusCode, response.ContentType, response.Location, response.Body, resource), cancellationToken);
    }
}

internal sealed class MutationReplayResult(MutationReceipt receipt) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode = receipt.StatusCode;
        context.Response.ContentType = receipt.ContentType;
        if (receipt.Location is not null) context.Response.Headers.Location = receipt.Location;
        await context.Response.Body.WriteAsync(receipt.Body, context.RequestAborted);
    }
}

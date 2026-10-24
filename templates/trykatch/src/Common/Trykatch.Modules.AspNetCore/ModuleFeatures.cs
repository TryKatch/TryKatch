using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Trykatch.Modules.AspNetCore;

public sealed record ModuleFeatureRequirement(string FeatureKey);
internal sealed class ModuleFeatureBoundaryMetadata;

public static class ModuleFeatureExtensions
{
    public static RouteHandlerBuilder RequireModuleFeature(this RouteHandlerBuilder builder, string featureKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new ModuleFeatureRequirement(featureKey));
        builder.ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        builder.Finally(Validate);
        return builder;
    }

    internal static void Validate(EndpointBuilder endpoint)
    {
        EndpointMetadataCollection metadata = new(endpoint.Metadata);
        IReadOnlyList<ModuleFeatureRequirement> requirements = metadata.GetOrderedMetadata<ModuleFeatureRequirement>();
        if (requirements.Count == 0) return;
        IReadOnlyList<ModuleEndpointMetadata> owners = metadata.GetOrderedMetadata<ModuleEndpointMetadata>();
        ModuleCatalog catalog = endpoint.ApplicationServices.GetRequiredService<ModuleCatalog>();
        ModuleDescriptor? owner = owners.Count == 1
            ? catalog.Descriptors.SingleOrDefault(module => module.Id == owners[0].ModuleId) : null;
        if (owner is null || metadata.GetMetadata<ModuleFeatureBoundaryMetadata>() is null
            || metadata.GetMetadata<IOrganizationScopedMetadata>() is null
            || metadata.GetMetadata<IAllowAnonymous>() is not null
            || metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0
            || metadata.GetOrderedMetadata<IAuthorizeData>().Any(item => item.Policy?.StartsWith("platform-permission:", StringComparison.Ordinal) == true)
            || requirements.Count > 100
            || requirements.Select(requirement => requirement.FeatureKey).Distinct(StringComparer.Ordinal).Count() != requirements.Count
            || requirements.Any(requirement => !owner.Features.Any(feature => feature.Key == requirement.FeatureKey)))
            throw new InvalidOperationException("Feature requirements must be distinct declarations owned by one installed module within its secured organization boundary.");
    }
}

internal sealed class ModuleFeatureFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        IReadOnlyList<ModuleFeatureRequirement>? requirements = context.HttpContext.GetEndpoint()?.Metadata
            .GetOrderedMetadata<ModuleFeatureRequirement>();
        if (requirements is not { Count: > 0 }) return await next(context);
        IOrganizationFeatureEvaluator evaluator = context.HttpContext.RequestServices.GetRequiredService<IOrganizationFeatureEvaluator>();
        foreach (ModuleFeatureRequirement requirement in requirements)
        {
            if (!await evaluator.IsEnabledAsync(requirement.FeatureKey, context.HttpContext.RequestAborted))
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "feature_unavailable",
                    detail: "The requested feature is unavailable in this organization.",
                    extensions: new Dictionary<string, object?> { ["code"] = "feature_unavailable" });
        }
        return await next(context);
    }
}

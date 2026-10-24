using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.Api.OpenApi;

/// <summary>Adds stable product and installed-module metadata to the API contract.</summary>
public sealed class ModuleOpenApiDocumentTransformer(ModuleCatalog catalog) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Title = "Trykatch API";
        document.Info.Version = "v1";
        document.Info.Description =
            "The versioned HTTP contract for Trykatch's platform and organization workspaces. " +
            "Organization-scoped operations infer their organization from the authenticated workspace context.";

        object[] modules = catalog.Descriptors.Select(descriptor => new
        {
            id = descriptor.Id,
            name = descriptor.Name,
            version = descriptor.Version,
            capabilities = Enum.GetValues<ModuleCapabilities>()
                .Where(capability => capability != ModuleCapabilities.None && descriptor.Capabilities.HasFlag(capability))
                .Select(capability => capability.ToString())
        }).Cast<object>().ToArray();
        document.AddExtension(
            "x-trykatch-modules",
            new JsonNodeExtension(JsonSerializer.SerializeToNode(modules)!));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Identifies module-owned operations and emits only explicitly allowlisted AI
/// tools. The generated contract remains provider-neutral.
/// </summary>
public sealed partial class ModuleOpenApiOperationTransformer(ModuleCatalog catalog) : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Summary ??= HumanizeOperationId(operation.OperationId);
        AddTypeScriptCodeSample(operation, context);

        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
        ModuleMutationIdempotencyMetadata? mutation = endpointMetadata
            .OfType<ModuleMutationIdempotencyMetadata>()
            .FirstOrDefault();
        if (mutation is not null)
            AddMutationIdempotency(operation, mutation);

        string? moduleId = endpointMetadata
            .OfType<ModuleEndpointMetadata>()
            .Select(metadata => metadata.ModuleId)
            .FirstOrDefault()
            ?? endpointMetadata
                .OfType<ModuleAttribute>()
                .Select(attribute => attribute.ModuleId)
                .FirstOrDefault();

        if (moduleId is null)
            return Task.CompletedTask;

        operation.AddExtension("x-trykatch-module", new JsonNodeExtension(JsonSerializer.SerializeToNode(moduleId)!));
        AssistantToolDescriptor? tool = catalog.Descriptors
            .Single(descriptor => descriptor.Id == moduleId)
            .AssistantTools
            .SingleOrDefault(candidate => candidate.OperationId == operation.OperationId);
        if (tool is null)
            return Task.CompletedTask;

        operation.Description ??= tool.Description;
        operation.AddExtension(
            "x-trykatch-assistant-tool",
            new JsonNodeExtension(JsonSerializer.SerializeToNode(new
            {
                name = tool.Name,
                description = tool.Description,
                risk = tool.Risk switch
                {
                    AssistantToolRisk.ReadOnly => "read-only",
                    AssistantToolRisk.Mutating => "mutating",
                    AssistantToolRisk.Destructive => "destructive",
                    _ => throw new InvalidOperationException($"Unsupported assistant tool risk '{tool.Risk}'.")
                },
                requiresHumanConfirmation = tool.RequiresHumanConfirmation
            })!));

        return Task.CompletedTask;
    }

    private static void AddMutationIdempotency(OpenApiOperation operation, ModuleMutationIdempotencyMetadata metadata)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = metadata.Policy.KeyRequired,
            Description = "Reuse this key with the same request intent after an uncertain network outcome.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MinLength = 1,
                MaxLength = 128,
                Pattern = "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$"
            }
        });

        operation.AddExtension(
            "x-trykatch-idempotency",
            new JsonNodeExtension(JsonSerializer.SerializeToNode(new
            {
                keyRequired = metadata.Policy.KeyRequired,
                replayLifetimeSeconds = metadata.Policy.ReplayLifetime.TotalSeconds,
                maximumRequestBytes = metadata.Policy.MaximumRequestBytes,
                maximumResponseBytes = metadata.Policy.MaximumResponseBytes,
                responseLimitAlsoBoundedByAtomicStaging = true
            })!));

        string lifetimeSeconds = metadata.Policy.ReplayLifetime.TotalSeconds.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        string guidance = $"For an uncertain outcome within the replay window, retry with the same key and same request intent. Current authentication, authorization, workspace and organization access, and CSRF checks still apply to replays. A changed request with the same key returns 409 (idempotency_conflict); an in-progress request returns 409 (idempotency_in_progress) with Retry-After: 1. The receipt lifetime is {lifetimeSeconds} seconds (allowed range: 60–86400 seconds), and the response limit is also bounded by the host's atomic response-staging limit. Current policy may shorten an earlier receipt's window; after expiry, the same key may execute a new mutation.";
        operation.Description = string.IsNullOrWhiteSpace(operation.Description)
            ? guidance
            : $"{operation.Description.TrimEnd()}\n\n{guidance}";
    }

    private static void AddTypeScriptCodeSample(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context)
    {
        NormalizeBinaryResponses(operation);
        string method = context.Description.HttpMethod?.ToUpperInvariant() ?? "GET";
        string path = "/" + (context.Description.RelativePath ?? string.Empty).Split('?', 2)[0];
        bool hasRequestBody = operation.RequestBody is not null;
        bool hasMultipartBody = operation.RequestBody?.Content?.ContainsKey("multipart/form-data") == true;
        bool hasBinaryResponse = operation.Responses?.Values.Any(response =>
            response.Content?.Any(content =>
                !content.Key.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                content.Value.Schema?.Format is "binary" or "byte") == true) == true;
        bool requiresAntiforgery = method is not ("GET" or "HEAD" or "OPTIONS");

        List<string> lines = [];
        if (hasMultipartBody)
        {
            lines.Add("const uploadForm = document.querySelector<HTMLFormElement>('#upload-form');");
            lines.Add("if (!uploadForm) throw new Error('Upload form was not found.');");
            lines.Add("const formData = new FormData(uploadForm);");
            lines.Add(string.Empty);
        }
        if (requiresAntiforgery)
        {
            lines.Add("const { token } = await fetch('/api/v1/auth/antiforgery', {");
            lines.Add("  credentials: 'include',");
            lines.Add("}).then((response) => response.json() as Promise<{ token: string }>);");
            lines.Add(string.Empty);
        }

        lines.Add($"const response = await fetch('{path}', {{");
        lines.Add($"  method: '{method}',");
        lines.Add("  credentials: 'include',");
        lines.Add("  headers: {");
        lines.Add(hasBinaryResponse
            ? "    Accept: 'application/octet-stream',"
            : "    Accept: 'application/json',");
        if (hasRequestBody && !hasMultipartBody)
            lines.Add("    'Content-Type': 'application/json',");
        if (requiresAntiforgery)
            lines.Add("    'X-CSRF-TOKEN': token,");
        lines.Add("  },");
        if (hasMultipartBody)
            lines.Add("  body: formData, // The browser supplies the multipart boundary.");
        else if (hasRequestBody)
            lines.Add("  body: JSON.stringify({ /* fields from the request schema */ }),");
        lines.Add("});");
        lines.Add(string.Empty);
        lines.Add("if (!response.ok) throw new Error(`Request failed: ${response.status}`);");
        lines.Add(hasBinaryResponse
            ? "const result: Blob = await response.blob();"
            : "const result: unknown = response.status === 204 ? undefined : await response.json();");
        lines.Add("// Handle result in memory; never log response bodies, which may contain credentials or personal data.");
        lines.Add("console.log({ status: response.status });");

        operation.AddExtension(
            "x-codeSamples",
            new JsonNodeExtension(JsonSerializer.SerializeToNode(new[]
            {
                new
                {
                    lang = "TypeScript",
                    label = "TypeScript · fetch",
                    source = string.Join('\n', lines)
                }
            })!));
    }

    private static void NormalizeBinaryResponses(OpenApiOperation operation)
    {
        if (operation.Responses is null) return;

        foreach (OpenApiResponse response in operation.Responses.Values)
        {
            if (response.Content is null) continue;
            foreach ((string mediaType, OpenApiMediaType content) in response.Content)
            {
                if (!mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)) continue;
                content.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "binary"
                };
            }
        }
    }

    private static string? HumanizeOperationId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            return null;

        string[] parts = operationId.Split('_', 2, StringSplitOptions.RemoveEmptyEntries);
        string resource = HumanizeIdentifier(parts[0]);
        string action = parts.Length == 2 ? HumanizeIdentifier(parts[1]) : resource;
        string summary = parts.Length == 2 ? $"{action} {resource}" : action;
        return summary switch
        {
            { Length: 0 } => operationId,
            string text => char.ToUpperInvariant(text[0]) + text[1..]
        };
    }

    private static string HumanizeIdentifier(string value) =>
        WordBoundary().Replace(value, " $1").Trim().ToLowerInvariant();

    [GeneratedRegex("([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex WordBoundary();
}

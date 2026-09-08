using System.Text.Json;
using System.Text.RegularExpressions;
using FlatpackApp.Modules;
using FlatpackApp.Modules.AspNetCore;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FlatpackApp.Api.OpenApi;

/// <summary>Adds stable product and installed-module metadata to the API contract.</summary>
public sealed class FlatpackOpenApiDocumentTransformer(FlatpackModuleCatalog catalog) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info.Title = "Flatpack API";
        document.Info.Version = "v1";
        document.Info.Description =
            "The versioned HTTP contract for Flatpack's platform and organization workspaces. " +
            "Organization-scoped operations infer their organization from the authenticated workspace context.";

        object[] modules = catalog.Descriptors.Select(descriptor => new
        {
            id = descriptor.Id,
            name = descriptor.Name,
            version = descriptor.Version,
            capabilities = Enum.GetValues<FlatpackModuleCapabilities>()
                .Where(capability => capability != FlatpackModuleCapabilities.None && descriptor.Capabilities.HasFlag(capability))
                .Select(capability => capability.ToString())
        }).Cast<object>().ToArray();
        document.AddExtension(
            "x-flatpack-modules",
            new JsonNodeExtension(JsonSerializer.SerializeToNode(modules)!));

        return Task.CompletedTask;
    }
}

/// <summary>
/// Identifies module-owned operations and emits only explicitly allowlisted AI
/// tools. The generated contract remains provider-neutral.
/// </summary>
public sealed partial class FlatpackOpenApiOperationTransformer(FlatpackModuleCatalog catalog) : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Summary ??= HumanizeOperationId(operation.OperationId);
        AddTypeScriptCodeSample(operation, context);

        string? moduleId = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<FlatpackModuleEndpointMetadata>()
            .Select(metadata => metadata.ModuleId)
            .FirstOrDefault()
            ?? context.Description.ActionDescriptor.EndpointMetadata
                .OfType<FlatpackModuleAttribute>()
                .Select(attribute => attribute.ModuleId)
                .FirstOrDefault();

        if (moduleId is null)
            return Task.CompletedTask;

        operation.AddExtension("x-flatpack-module", new JsonNodeExtension(JsonSerializer.SerializeToNode(moduleId)!));
        FlatpackAssistantToolDescriptor? tool = catalog.Descriptors
            .Single(descriptor => descriptor.Id == moduleId)
            .AssistantTools
            .SingleOrDefault(candidate => candidate.OperationId == operation.OperationId);
        if (tool is null)
            return Task.CompletedTask;

        operation.Description ??= tool.Description;
        operation.AddExtension(
            "x-flatpack-assistant-tool",
            new JsonNodeExtension(JsonSerializer.SerializeToNode(new
            {
                name = tool.Name,
                description = tool.Description,
                risk = tool.Risk switch
                {
                    FlatpackAssistantToolRisk.ReadOnly => "read-only",
                    FlatpackAssistantToolRisk.Mutating => "mutating",
                    FlatpackAssistantToolRisk.Destructive => "destructive",
                    _ => throw new InvalidOperationException($"Unsupported assistant tool risk '{tool.Risk}'.")
                },
                requiresHumanConfirmation = tool.RequiresHumanConfirmation
            })!));

        return Task.CompletedTask;
    }

    private static void AddTypeScriptCodeSample(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context)
    {
        string method = context.Description.HttpMethod?.ToUpperInvariant() ?? "GET";
        string path = "/" + (context.Description.RelativePath ?? string.Empty).Split('?', 2)[0];
        bool hasRequestBody = operation.RequestBody is not null;
        bool requiresAntiforgery = method is not ("GET" or "HEAD" or "OPTIONS");

        List<string> lines = [];
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
        lines.Add("    Accept: 'application/json',");
        if (hasRequestBody)
            lines.Add("    'Content-Type': 'application/json',");
        if (requiresAntiforgery)
            lines.Add("    'X-CSRF-TOKEN': token,");
        lines.Add("  },");
        if (hasRequestBody)
            lines.Add("  body: JSON.stringify({ /* fields from the request schema */ }),");
        lines.Add("});");
        lines.Add(string.Empty);
        lines.Add("if (!response.ok) throw new Error(`Request failed: ${response.status}`);");
        lines.Add("const result: unknown = response.status === 204 ? undefined : await response.json();");
        lines.Add("console.log(result);");

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

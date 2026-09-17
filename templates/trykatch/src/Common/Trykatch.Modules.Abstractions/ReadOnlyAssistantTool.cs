using System.Text.Json;

namespace Trykatch.Modules;

/// <summary>
/// Module-owned, bounded read adapter for an explicitly allowlisted operation.
/// Executed in the caller's organization transaction, never with elevated credentials.
/// Register explicitly; descriptors alone do not make operations executable.
/// </summary>
public interface IReadOnlyAssistantTool
{
    string OperationId { get; }
    string RequiredPermission { get; }
    JsonElement Parameters { get; }
    Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}

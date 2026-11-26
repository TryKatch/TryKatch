using Trykatch.Modules;

namespace Trykatch.Modules.AspNetCore;

/// <summary>Executed response data, captured before its owning transaction finalizes.</summary>
public sealed record StagedMutationResponse(int StatusCode, string? ContentType, string? Location, byte[] Body);

/// <summary>Optional per-request enrollment; capturing must never commit the transaction.</summary>
public interface IModuleMutationResponseCaptureFeature
{
    int MaximumResponseBytes { get; }
    bool RequiresRollback { get; }
    Task CaptureAsync(StagedMutationResponse response, CancellationToken cancellationToken);
}

internal sealed record ModuleMutationReplayResponse(MutationReceipt Receipt);

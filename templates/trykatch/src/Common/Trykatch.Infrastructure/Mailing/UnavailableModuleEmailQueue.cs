using Trykatch.Modules;

namespace Trykatch.Infrastructure.Mailing;

/// <summary>Closed fallback for hosts that do not compose the optional email delivery adapters.</summary>
internal sealed class UnavailableModuleEmailQueue : IModuleEmailQueue
{
    public Task<ModuleWorkReceipt> EnqueueAsync(
        ModuleEmailMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModuleWorkReceipt(
            Guid.Empty,
            ModuleWorkState.Unavailable,
            FailureCode: "email_unavailable"));
    }
}

using FlatpackApp.Application.Identity;

namespace FlatpackApp.Infrastructure.Identity;

internal sealed class NoOpAccountRecoveryNotifier : IAccountRecoveryNotifier
{
    public bool IsConfigured => false;

    public Task SendPasswordResetAsync(
        string recipient,
        string displayName,
        string resetUrl,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

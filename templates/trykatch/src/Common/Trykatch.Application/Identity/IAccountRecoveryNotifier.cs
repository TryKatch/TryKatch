namespace Trykatch.Application.Identity;

public interface IAccountRecoveryNotifier
{
    bool IsConfigured { get; }

    Task SendPasswordResetAsync(
        string recipient,
        string displayName,
        string resetUrl,
        CancellationToken cancellationToken = default);
}

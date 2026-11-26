using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Trykatch.Infrastructure.Modules.Email;

internal enum EmailTransportDisposition
{
    Acknowledged,
    RetryableFailure,
    PermanentFailure,
    Uncertain
}

internal enum EmailTransportStage
{
    None,
    Configuration,
    Connect,
    Authenticate,
    Sender,
    Recipient,
    Send,
    Disconnect
}

internal enum EmailTransportFailureCode
{
    None,
    InvalidConfiguration,
    ConnectionFailed,
    AuthenticationFailed,
    CertificateValidationFailed,
    SmtpRejected,
    ConnectionLost,
    ProtocolFailure
}

internal sealed record EmailTransportResult(
    EmailTransportDisposition Disposition,
    EmailTransportStage Stage = EmailTransportStage.None,
    EmailTransportFailureCode FailureCode = EmailTransportFailureCode.None,
    int? SmtpStatusCode = null);

internal interface IEmailTransport
{
    Task<EmailTransportResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

internal sealed class SmtpEmailTransport(IOptions<SmtpOptions> options, ISmtpClientFactory clients) : IEmailTransport
{
    public async Task<EmailTransportResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SmtpOptions settings = options.Value;
        SecureSocketOptions security;
        MimeMessage mail;
        try
        {
            security = settings.Security switch
            {
                SmtpTransportSecurity.StartTls => SecureSocketOptions.StartTls,
                SmtpTransportSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpTransportSecurity.None => SecureSocketOptions.None,
                _ => throw new InvalidOperationException()
            };
            mail = new MimeMessage();
            mail.From.Add(MailboxAddress.Parse(settings.From));
            mail.To.Add(MailboxAddress.Parse(message.Recipient));
            mail.Subject = message.Subject;
            mail.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        }
        catch (Exception failure) when (failure is FormatException or ArgumentException or InvalidOperationException)
        {
            return Failure(EmailTransportDisposition.PermanentFailure, EmailTransportStage.Configuration, EmailTransportFailureCode.InvalidConfiguration);
        }

        using (mail)
        using (SmtpClient client = clients.CreateClient())
        {
            EmailTransportStage stage = EmailTransportStage.Connect;
            try
            {
                await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
                if (!string.IsNullOrWhiteSpace(settings.Username))
                {
                    stage = EmailTransportStage.Authenticate;
                    await client.AuthenticateAsync(settings.Username, settings.Password!, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                stage = EmailTransportStage.Send;
                try
                {
                    await client.SendAsync(mail, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return Failure(EmailTransportDisposition.Uncertain, EmailTransportStage.Send, EmailTransportFailureCode.ConnectionLost);
                }
                catch (SmtpCommandException failure)
                {
                    return ClassifySmtpCommand(failure, EmailTransportStage.Send);
                }
                catch (Exception failure) when (IsConnectionFailure(failure) || failure is MailKit.ProtocolException)
                {
                    return Failure(EmailTransportDisposition.Uncertain, EmailTransportStage.Send,
                        failure is MailKit.ProtocolException ? EmailTransportFailureCode.ProtocolFailure : EmailTransportFailureCode.ConnectionLost);
                }

                // SendAsync returned the SMTP final success response. Disconnect is cleanup and cannot revoke that acknowledgement.
                stage = EmailTransportStage.Disconnect;
                try { await client.DisconnectAsync(true, cancellationToken); }
                catch (Exception failure) when (IsExpectedCleanupFailure(failure)) { }
                return new EmailTransportResult(EmailTransportDisposition.Acknowledged);
            }
            catch (OperationCanceledException) when (stage != EmailTransportStage.Send)
            {
                throw;
            }
            catch (SmtpCommandException failure)
            {
                return ClassifySmtpCommand(failure, stage);
            }
            catch (Exception failure) when (failure is MailKit.Security.AuthenticationException or System.Security.Authentication.AuthenticationException or SslHandshakeException)
            {
                return Failure(EmailTransportDisposition.PermanentFailure, stage,
                    stage == EmailTransportStage.Authenticate ? EmailTransportFailureCode.AuthenticationFailed : EmailTransportFailureCode.CertificateValidationFailed);
            }
            catch (NotSupportedException)
            {
                return Failure(EmailTransportDisposition.PermanentFailure, stage, EmailTransportFailureCode.ProtocolFailure);
            }
            catch (Exception failure) when (IsConnectionFailure(failure) || failure is MailKit.ProtocolException)
            {
                return Failure(EmailTransportDisposition.RetryableFailure, stage,
                    failure is MailKit.ProtocolException ? EmailTransportFailureCode.ProtocolFailure : EmailTransportFailureCode.ConnectionFailed);
            }
        }
    }

    private static EmailTransportResult ClassifySmtpCommand(SmtpCommandException failure, EmailTransportStage fallbackStage)
    {
        int status = (int)failure.StatusCode;
        EmailTransportDisposition disposition = status is >= 400 and < 500
            ? EmailTransportDisposition.RetryableFailure
            : EmailTransportDisposition.PermanentFailure;
        EmailTransportStage stage = failure.ErrorCode switch
        {
            SmtpErrorCode.SenderNotAccepted => EmailTransportStage.Sender,
            SmtpErrorCode.RecipientNotAccepted => EmailTransportStage.Recipient,
            _ => fallbackStage
        };
        EmailTransportFailureCode code = fallbackStage == EmailTransportStage.Authenticate
            ? EmailTransportFailureCode.AuthenticationFailed
            : EmailTransportFailureCode.SmtpRejected;
        return Failure(disposition, stage, code, status);
    }

    private static bool IsConnectionFailure(Exception failure) =>
        failure is IOException or System.Net.Sockets.SocketException;

    private static bool IsExpectedCleanupFailure(Exception failure) =>
        failure is OperationCanceledException or IOException or System.Net.Sockets.SocketException or
            MailKit.CommandException or MailKit.ProtocolException or NotSupportedException or InvalidOperationException;

    private static EmailTransportResult Failure(EmailTransportDisposition disposition, EmailTransportStage stage,
        EmailTransportFailureCode code, int? status = null) => new(disposition, stage, code, status);
}

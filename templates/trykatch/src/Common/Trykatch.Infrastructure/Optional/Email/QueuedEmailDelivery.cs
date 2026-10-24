using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Trykatch.Application.Mailing;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed class QueuedEmailDispatcher(IServiceScopeFactory scopes, TimeProvider clock)
{
    private static readonly Meter Meter = new("Trykatch.EmailDelivery");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("trykatch.email.delivery.attempts");
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);

    public async Task DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IEnumerable<IEmailDeliverySource> sources = scope.ServiceProvider.GetServices<IEmailDeliverySource>();
        IEmailTransport transport = scope.ServiceProvider.GetRequiredService<IEmailTransport>();

        foreach (IEmailDeliverySource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<EmailDeliveryLease> leases;
            try
            {
                leases = await source.ClaimAsync(1, cancellationToken);
            }
            catch (Exception failure) when (IsTransientSourceFailure(failure))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Count(source.Boundary, "source_unavailable");
                continue;
            }

            if (leases.Count > 1)
                throw new InvalidOperationException("Email delivery source exceeded the requested claim limit.");
            if (leases.Count == 0)
                continue;

            await DispatchLeaseAsync(source, transport, leases[0], cancellationToken);
        }
    }

    private async Task DispatchLeaseAsync(
        IEmailDeliverySource source,
        IEmailTransport transport,
        EmailDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        TimeSpan remainingLease = lease.LeaseUntilUtc - clock.GetUtcNow() - CompletionBudget;
        TimeSpan attemptBudget = remainingLease < EmailDeliveryPolicy.AttemptTimeout
            ? remainingLease
            : EmailDeliveryPolicy.AttemptTimeout;
        if (attemptBudget <= TimeSpan.Zero)
        {
            Count(source.Boundary, "lease_expired");
            return;
        }

        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(attemptBudget);
        EmailAttemptOutcome outcome;
        EmailDeliveryPreparation? preparation;
        try
        {
            preparation = await source.PrepareAsync(lease, attempt.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            outcome = new(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SourceUnavailable);
            Count(source.Boundary, OutcomeTag(outcome));
            await CompleteBoundedAsync(source, lease, outcome);
            return;
        }
        catch (Exception failure) when (IsTransientSourceFailure(failure))
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcome = new(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SourceUnavailable);
            Count(source.Boundary, OutcomeTag(outcome));
            await CompleteBoundedAsync(source, lease, outcome);
            return;
        }

        if (!TryMapPreparation(preparation, out outcome, out EmailPreparedMessage? message))
        {
            Count(source.Boundary, OutcomeTag(outcome));
            await CompleteBoundedAsync(source, lease, outcome);
            return;
        }

        if (outcome.Disposition != EmailAttemptDisposition.Acknowledged || message is null)
        {
            Count(source.Boundary, OutcomeTag(outcome));
            await CompleteBoundedAsync(source, lease, outcome);
            return;
        }

        if (attempt.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            outcome = new(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SourceUnavailable);
            Count(source.Boundary, OutcomeTag(outcome));
            await CompleteBoundedAsync(source, lease, outcome);
            return;
        }

        try
        {
            EmailTransportResult result = await transport.SendAsync(
                new EmailMessage(message.Recipient, message.Subject, message.TextBody, message.HtmlBody), attempt.Token);
            outcome = MapTransport(result);
        }
        catch (OperationCanceledException)
        {
            // SendAsync has started, so interruption cannot prove whether SMTP accepted the message.
            outcome = new(EmailAttemptDisposition.Uncertain, EmailFailureCode.ConnectionLost);
        }

        Count(source.Boundary, OutcomeTag(outcome));
        await CompleteBoundedAsync(source, lease, outcome);
    }

    private static bool TryMapPreparation(
        EmailDeliveryPreparation? preparation,
        out EmailAttemptOutcome outcome,
        out EmailPreparedMessage? message)
    {
        message = null;
        if (preparation is null || !Enum.IsDefined(preparation.Disposition) || !Enum.IsDefined(preparation.FailureCode))
        {
            outcome = new(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.PayloadInvalid);
            return false;
        }

        switch (preparation.Disposition)
        {
            case EmailPreparationDisposition.Ready when preparation.Message is not null
                && preparation.FailureCode == EmailFailureCode.None:
                message = preparation.Message;
                outcome = new(EmailAttemptDisposition.Acknowledged);
                return true;
            case EmailPreparationDisposition.Suppressed when preparation.Message is null
                && preparation.FailureCode is not (EmailFailureCode.None or EmailFailureCode.SmtpRejected):
                outcome = new(EmailAttemptDisposition.Suppressed, preparation.FailureCode);
                return false;
            case EmailPreparationDisposition.RetryableFailure when preparation.Message is null
                && preparation.FailureCode is not (EmailFailureCode.None or EmailFailureCode.SmtpRejected):
                outcome = new(EmailAttemptDisposition.RetryableFailure, preparation.FailureCode);
                return false;
            default:
                outcome = new(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.PayloadInvalid);
                return false;
        }
    }

    private static EmailAttemptOutcome MapTransport(EmailTransportResult? result)
    {
        if (result is null || !Enum.IsDefined(result.Disposition) || !Enum.IsDefined(result.FailureCode))
            return new(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.ProtocolFailure);

        if (result.Disposition == EmailTransportDisposition.Acknowledged)
        {
            if (result.FailureCode != EmailTransportFailureCode.None
                || result.SmtpStatusCode is int acknowledgedStatus && acknowledgedStatus is not (>= 200 and < 300))
                return new(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.ProtocolFailure);
            return new(EmailAttemptDisposition.Acknowledged);
        }

        EmailAttemptDisposition disposition = result.Disposition switch
        {
            EmailTransportDisposition.RetryableFailure => EmailAttemptDisposition.RetryableFailure,
            EmailTransportDisposition.PermanentFailure => EmailAttemptDisposition.PermanentFailure,
            EmailTransportDisposition.Uncertain => EmailAttemptDisposition.Uncertain,
            _ => EmailAttemptDisposition.PermanentFailure
        };
        EmailFailureCode failureCode = MapFailureCode(result.FailureCode);
        if (failureCode == EmailFailureCode.None
            || failureCode == EmailFailureCode.SmtpRejected && result.SmtpStatusCode is null)
            failureCode = EmailFailureCode.ProtocolFailure;
        if (result.SmtpStatusCode is int status && !StatusMatches(disposition, status))
            return new(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.ProtocolFailure);
        return new(disposition, failureCode, result.SmtpStatusCode);
    }

    private static EmailFailureCode MapFailureCode(EmailTransportFailureCode code) => code switch
    {
        EmailTransportFailureCode.InvalidConfiguration => EmailFailureCode.InvalidConfiguration,
        EmailTransportFailureCode.ConnectionFailed => EmailFailureCode.ConnectionFailed,
        EmailTransportFailureCode.AuthenticationFailed => EmailFailureCode.AuthenticationFailed,
        EmailTransportFailureCode.CertificateValidationFailed => EmailFailureCode.CertificateValidationFailed,
        EmailTransportFailureCode.SmtpRejected => EmailFailureCode.SmtpRejected,
        EmailTransportFailureCode.ConnectionLost => EmailFailureCode.ConnectionLost,
        EmailTransportFailureCode.ProtocolFailure => EmailFailureCode.ProtocolFailure,
        _ => EmailFailureCode.None
    };

    private static bool StatusMatches(EmailAttemptDisposition disposition, int status) => disposition switch
    {
        EmailAttemptDisposition.RetryableFailure => status is >= 400 and < 500,
        EmailAttemptDisposition.PermanentFailure => status is >= 500 and < 600,
        _ => false
    };

    private static async Task CompleteBoundedAsync(
        IEmailDeliverySource source,
        EmailDeliveryLease lease,
        EmailAttemptOutcome outcome)
    {
        using CancellationTokenSource completion = new(CompletionBudget);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!await source.CompleteAsync(lease, outcome, completion.Token))
                    Count(source.Boundary, "lease_lost");
                return;
            }
            catch (OperationCanceledException) when (completion.IsCancellationRequested)
            {
                Count(source.Boundary, "completion_unavailable");
                return;
            }
            catch (Exception failure) when (IsTransientSourceFailure(failure))
            {
                if (attempt == 1 || completion.IsCancellationRequested)
                {
                    Count(source.Boundary, "completion_unavailable");
                    return;
                }
            }
        }
    }

    private static bool IsTransientSourceFailure(Exception failure) =>
        failure is DbException or IOException or TimeoutException;

    private static string OutcomeTag(EmailAttemptOutcome outcome) => outcome.Disposition switch
    {
        EmailAttemptDisposition.Acknowledged => "acknowledged",
        EmailAttemptDisposition.RetryableFailure => "retryable_failure",
        EmailAttemptDisposition.PermanentFailure => "permanent_failure",
        EmailAttemptDisposition.Uncertain => "uncertain",
        EmailAttemptDisposition.Suppressed => "suppressed",
        _ => "invalid"
    };

    private static void Count(EmailDeliveryBoundary boundary, string outcome)
    {
        string safeBoundary = boundary switch
        {
            EmailDeliveryBoundary.Organization => "organization",
            EmailDeliveryBoundary.Identity => "identity",
            _ => "unknown"
        };
        Outcomes.Add(1, new KeyValuePair<string, object?>("boundary", safeBoundary),
            new KeyValuePair<string, object?>("outcome", outcome));
    }
}

/// <summary>Optional poller. Hosts opt in by registering this worker after composing delivery sources.</summary>
internal sealed class QueuedEmailDeliveryWorker(QueuedEmailDispatcher dispatcher, TimeProvider clock) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(PollInterval, clock);
        do
        {
            try
            {
                await dispatcher.DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure) when (failure is DbException or IOException or TimeoutException)
            {
                // Transient store faults have no payload-safe diagnostic detail.
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Stop on an unexpected adapter defect, but do not hand its private
                // message, inner exception or Data to BackgroundService host logging.
                throw new QueuedEmailAdapterException();
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

internal sealed class QueuedEmailAdapterException()
    : Exception("Queued email delivery stopped because an adapter failed.");

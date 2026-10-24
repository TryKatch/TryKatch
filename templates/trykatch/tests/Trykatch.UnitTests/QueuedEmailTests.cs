using Shouldly;
using Trykatch.Application.Mailing;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QueuedEmailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AcknowledgedSuppressedAndPermanentOutcomesNeverScheduleAnotherSend()
    {
        EmailDeliveryPolicy.Complete(new(EmailAttemptDisposition.Acknowledged), 1, Now).Status
            .ShouldBe(EmailDeliveryStatus.Delivered);
        EmailDeliveryPolicy.Complete(new(EmailAttemptDisposition.Suppressed, EmailFailureCode.SourceDenied), 1, Now).Status
            .ShouldBe(EmailDeliveryStatus.Suppressed);
        EmailDeliveryTransition permanent = EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.SmtpRejected, 550), 1, Now);
        permanent.Status.ShouldBe(EmailDeliveryStatus.Terminal);
        permanent.NextAttemptAtUtc.ShouldBeNull();
    }

    [TestMethod]
    public void RetryAndUncertaintyUseBoundedBackoffAndStopAtTheAttemptBudget()
    {
        EmailAttemptOutcome retry = new(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SmtpRejected, 450);
        EmailDeliveryTransition first = EmailDeliveryPolicy.Complete(retry, 1, Now);
        first.Status.ShouldBe(EmailDeliveryStatus.Queued);
        first.NextAttemptAtUtc.ShouldBe(Now.AddSeconds(5));
        EmailDeliveryPolicy.Complete(retry, 2, Now).NextAttemptAtUtc.ShouldBe(Now.AddSeconds(10));
        EmailDeliveryPolicy.Complete(retry, EmailDeliveryPolicy.MaximumAttempts, Now).Status
            .ShouldBe(EmailDeliveryStatus.Terminal);
        EmailAttemptOutcome uncertain = new(EmailAttemptDisposition.Uncertain, EmailFailureCode.ConnectionLost);
        EmailDeliveryPolicy.Complete(uncertain, 1, Now).NextAttemptAtUtc.ShouldNotBeNull();
        EmailDeliveryPolicy.Complete(uncertain, EmailDeliveryPolicy.MaximumAttempts, Now).NextAttemptAtUtc.ShouldBeNull();
        EmailDeliveryPolicy.AttemptTimeout.ShouldBeLessThan(EmailDeliveryPolicy.LeaseDuration);
    }

    [TestMethod]
    public void InvalidCompletionCannotManufactureAnAcknowledgementOrUnboundedRetry()
    {
        Should.Throw<ArgumentException>(() => EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.Acknowledged, EmailFailureCode.SourceDenied), 1, Now));
        Should.Throw<ArgumentException>(() => EmailDeliveryPolicy.Complete(
            new((EmailAttemptDisposition)99), 1, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.Acknowledged), 0, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.Acknowledged), EmailDeliveryPolicy.MaximumAttempts + 1, Now));
    }

    [TestMethod]
    public void SmtpStatusMustAgreeWithTheRecordedDisposition()
    {
        Should.Throw<ArgumentException>(() => EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.Acknowledged, SmtpStatusCode: 450), 1, Now));
        Should.Throw<ArgumentException>(() => EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.Suppressed, EmailFailureCode.SourceDenied, 250), 1, Now));
        Should.Throw<ArgumentException>(() => EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.RetryableFailure, EmailFailureCode.SmtpRejected, 550), 1, Now));
        Should.Throw<ArgumentException>(() => EmailDeliveryPolicy.Complete(
            new(EmailAttemptDisposition.PermanentFailure, EmailFailureCode.SmtpRejected, 450), 1, Now));
        EmailDeliveryPolicy.Complete(new(EmailAttemptDisposition.Acknowledged, SmtpStatusCode: 250), 1, Now)
            .Status.ShouldBe(EmailDeliveryStatus.Delivered);
    }

    [TestMethod]
    public void DefaultDiagnosticTextDoesNotRevealPreparedOrProtectedContent()
    {
        const string privateValue = "private-email-body-token-and-address";
        new EmailPreparedMessage(privateValue, privateValue, privateValue, privateValue).ToString()
            .ShouldNotContain(privateValue);
        new PasswordResetEmailIntent(Guid.NewGuid(), Guid.NewGuid(), privateValue, privateValue, privateValue, privateValue)
            .ToString().ShouldNotContain(privateValue);
        new EmailDeliveryLease(new(EmailDeliveryBoundary.Identity, Guid.NewGuid(), Guid.NewGuid(), "core.reset", 1),
            Guid.NewGuid(), 1, Now, privateValue).ToString().ShouldNotContain(privateValue);
    }
}

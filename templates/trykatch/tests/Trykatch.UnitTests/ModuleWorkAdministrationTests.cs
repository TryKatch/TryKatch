using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Jobs;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleWorkAdministrationTests
{
    [TestMethod]
    public void StaleGenerationAndRunningLeaseCannotBeChanged()
    {
        ModuleWorkItem work = new() { Id = Guid.NewGuid(), State = "queued", RecoveryGeneration = 3 };
        ModuleWorkAdministration.ApplyControl(work, "pause", 2).ErrorCode.ShouldBe("request_conflict");
        work.OperatorPaused.ShouldBeFalse();
        work.RecoveryGeneration.ShouldBe(3);
        work.State = "leased";
        ModuleWorkAdministration.ApplyControl(work, "pause", 3).ErrorCode.ShouldBe("state_conflict");
        work.OperatorPaused.ShouldBeFalse();
    }

    [TestMethod]
    public void ManualPauseAndResumePreserveBusinessIdentityAndAttemptHistory()
    {
        Guid id = Guid.NewGuid();
        ModuleWorkItem work = new() { Id = id, Attempts = 2 };
        ModuleWorkAdministration.ApplyControl(work, "pause", 0).Value!.OperatorPaused.ShouldBeTrue();
        ModuleWorkAdministration.ApplyControl(work, "resume", 1).Value!.OperatorPaused.ShouldBeFalse();
        work.Id.ShouldBe(id);
        work.Attempts.ShouldBe(2);
        work.RecoveryGeneration.ShouldBe(2);
    }

    [TestMethod]
    public void RetryResetsOnlyFailureBudgetAndNeverReplaysCompletedEffect()
    {
        Guid id = Guid.NewGuid();
        ModuleWorkItem work = new() { Id = id, State = "terminal", Attempts = 5, LastFailureCode = "event_execution_failed" };
        ModuleWorkAdministration.ApplyControl(work, "retry", 0).IsSuccess.ShouldBeTrue();
        work.Id.ShouldBe(id);
        work.State.ShouldBe("queued");
        work.Attempts.ShouldBe(0);
        work.LastFailureCode.ShouldBeNull();
        work.State = "completed";
        ModuleWorkAdministration.ApplyControl(work, "retry", 1).ErrorCode.ShouldBe("state_conflict");
        work.RecoveryGeneration.ShouldBe(1);
    }

    [TestMethod]
    public void OperatorFailureCodesCannotDiscloseStoredDiagnostics()
    {
        ModuleWorkAdministration.SafeFailureCode(null).ShouldBeNull();
        ModuleWorkAdministration.SafeFailureCode("membership_required").ShouldBe("membership_required");
        ModuleWorkAdministration.SafeFailureCode("password=private-marker").ShouldBe("unclassified");
    }

    [TestMethod]
    public void JobsPermissionsDoNotExpandExistingNonAdministratorRoles()
    {
        PlatformRoles.Find(PlatformRoles.Administrator)!.Permissions.ShouldContain(PlatformPermissions.JobsManage);
        foreach (string role in new[] { PlatformRoles.Operator, PlatformRoles.Auditor })
        {
            PlatformRoles.Find(role)!.Permissions.ShouldNotContain(PlatformPermissions.JobsRead);
            PlatformRoles.Find(role)!.Permissions.ShouldNotContain(PlatformPermissions.JobsManage);
        }
    }
}

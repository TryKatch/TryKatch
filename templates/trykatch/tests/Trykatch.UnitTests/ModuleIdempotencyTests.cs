using Shouldly;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleIdempotencyTests
{
    [TestMethod]
    public void DefaultPolicyIsBoundedAndPreservesClientsWithoutAKey()
    {
        ModuleMutationPolicy policy = ModuleMutationPolicy.Default;
        policy.Validate();
        policy.ReplayLifetime.ShouldBe(TimeSpan.FromHours(24));
        policy.MaximumRequestBytes.ShouldBe(65_536);
        policy.MaximumResponseBytes.ShouldBe(262_144);
        policy.KeyRequired.ShouldBeFalse();
    }

    [TestMethod]
    public void UnsafeReplayLifetimesAndMemoryLimitsAreRejected()
    {
        ModuleMutationPolicy[] invalid =
        [
            new(TimeSpan.FromSeconds(59)), new((TimeSpan.FromHours(24) + TimeSpan.FromTicks(1))),
            new(TimeSpan.FromMinutes(1), MaximumRequestBytes: 0),
            new(TimeSpan.FromMinutes(1), MaximumRequestBytes: 65_537),
            new(TimeSpan.FromMinutes(1), MaximumResponseBytes: 0),
            new(TimeSpan.FromMinutes(1), MaximumResponseBytes: 1_048_577)
        ];
        foreach (ModuleMutationPolicy policy in invalid)
            Should.Throw<ArgumentException>(policy.Validate);
        new ModuleMutationPolicy(TimeSpan.FromMinutes(1), 1, 1, true).Validate();
    }

    [TestMethod]
    public void RequestIdentityRequiresFiniteNamesHashesAndExplicitPermissions()
    {
        MutationRequest valid = Request();
        valid.Validate();
        MutationRequest[] invalid =
        [
            valid with { ModuleId = "" }, valid with { ModuleId = new string('m', 81) },
            valid with { OperationId = "" }, valid with { OperationId = new string('o', 161) },
            valid with { KeyHash = "raw-key" }, valid with { RequestHash = new string('g', 64) },
            valid with { RequiredPermissions = [] }, valid with { RequiredPermissions = [""] },
            valid with { RequiredPermissions = ["projects.manage", "projects.manage"] }
        ];
        foreach (MutationRequest request in invalid)
            Should.Throw<ArgumentException>(request.Validate);
    }

    [TestMethod]
    public void OnlyBoundedSuccessfulResponsesAndRelativeLocationsAreReplayable()
    {
        MutationReceipt valid = new(201, "application/json", "/api/v1/projects/created", [1, 2],
            new("projects.project", Guid.NewGuid()));
        ModuleMutationPolicy policy = new(TimeSpan.FromMinutes(1), MaximumResponseBytes: 2);
        valid.Validate(policy);
        MutationReceipt[] invalid =
        [
            valid with { StatusCode = 199 }, valid with { StatusCode = 300 },
            valid with { StatusCode = 403 }, valid with { StatusCode = 500 },
            valid with { Body = [1, 2, 3] },
            valid with { Location = "https://example.test/private" },
            valid with { Location = "//example.test/private" },
            valid with { Location = "/api/v1/projects\r\nSet-Cookie: injected" },
            valid with { Location = "/" + new string('a', 2048) },
            valid with { ContentType = "application/json\r\nSet-Cookie: injected" },
            valid with { ContentType = new string('a', 257) },
            valid with { ResourceReference = new("", Guid.NewGuid()) },
            valid with { ResourceReference = new("projects.project", Guid.Empty) }
        ];
        foreach (MutationReceipt receipt in invalid)
            Should.Throw<ArgumentException>(() => receipt.Validate(policy));
        new MutationReceipt(204, null, null, []).Validate(policy);
    }

    private static MutationRequest Request() => new("projects", "Projects_Create", new string('A', 64),
        new string('B', 64), ModuleMutationPolicy.Default, ["projects.manage"]);
}

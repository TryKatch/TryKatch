using Shouldly;
using Trykatch.Infrastructure.Caching;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationCacheKeyTests
{
    private static readonly Guid OrganizationId = Guid.Parse("b1b8d510-9e1d-4a98-9e65-8f4fb12d807a");
    private static readonly Guid ActorId = Guid.Parse("a8c1e1c7-c17b-4825-b3c1-dc71d46a5a40");

    [TestMethod]
    public void FrozenContractHasExactModuleNeutralShape()
    {
        Enum.GetNames<ModuleCacheAudience>().ShouldBe(["Organization", "CurrentActor"]);

        typeof(ModuleCacheKey).IsClass.ShouldBeTrue();
        typeof(ModuleCacheKey).IsSealed.ShouldBeTrue();
        typeof(ModuleCacheKey).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name)
            .ShouldBe(["ModuleId", "ReadModelKey", "SchemaVersion", "PolicyVersion", "ParametersHash", "Audience", "TtlPolicyKey"]);

        GetOrCreateCompilesForUnconstrainedType<object>();
        InvalidateCompiles();
    }

    [TestMethod]
    public void LogicalHashIsStableAndLowercaseSha256()
    {
        string hash = Hash(Key(), "trykatch", "Production", OrganizationId, ActorId);

        hash.ShouldBe(Hash(Key(), "trykatch", "Production", OrganizationId, ActorId));
        hash.Length.ShouldBe(64);
        hash.ShouldMatch("^[0-9a-f]{64}$");
    }

    [TestMethod]
    public void OrganizationAudienceIsSharedWithinOrganizationButPartitionedAcrossOrganizations()
    {
        string firstActor = Hash(Key(), "trykatch", "Production", OrganizationId, ActorId);
        string secondActor = Hash(Key(), "trykatch", "Production", OrganizationId, Guid.NewGuid());
        string otherOrganization = Hash(Key(), "trykatch", "Production", Guid.NewGuid(), ActorId);

        secondActor.ShouldBe(firstActor);
        otherOrganization.ShouldNotBe(firstActor);
    }

    [TestMethod]
    public void CurrentActorAudiencePartitionsByOrganizationAndActor()
    {
        ModuleCacheKey key = Key(ModuleCacheAudience.CurrentActor);
        string baseline = Hash(key, "trykatch", "Production", OrganizationId, ActorId);

        Hash(key, "trykatch", "Production", OrganizationId, Guid.NewGuid()).ShouldNotBe(baseline);
        Hash(key, "trykatch", "Production", Guid.NewGuid(), ActorId).ShouldNotBe(baseline);
    }

    [TestMethod]
    public void HashSeparatesEveryKeyAndHostNamespaceField()
    {
        ModuleCacheKey baselineKey = Key();
        string baseline = Hash(baselineKey, "trykatch", "Production", OrganizationId, ActorId);
        ModuleCacheKey[] changedKeys =
        [
            baselineKey with { ModuleId = "documents" },
            baselineKey with { ReadModelKey = "projects.other-detail" },
            baselineKey with { SchemaVersion = baselineKey.SchemaVersion + 1 },
            baselineKey with { PolicyVersion = baselineKey.PolicyVersion + 1 },
            baselineKey with { ParametersHash = new string('b', 64) },
            baselineKey with { Audience = ModuleCacheAudience.CurrentActor },
            baselineKey with { TtlPolicyKey = "short" }
        ];

        foreach (ModuleCacheKey changed in changedKeys)
            Hash(changed, "trykatch", "Production", OrganizationId, ActorId).ShouldNotBe(baseline);
        Hash(baselineKey, "another-app", "Production", OrganizationId, ActorId).ShouldNotBe(baseline);
        Hash(baselineKey, "trykatch", "Staging", OrganizationId, ActorId).ShouldNotBe(baseline);
        Hash(baselineKey, "trykatch", "Production", Guid.NewGuid(), ActorId).ShouldNotBe(baseline);
    }

    [TestMethod]
    public void PhysicalKeyIsOpaqueAndAcceptsEmptyInitialGeneration()
    {
        const string logicalHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Guid generation = Guid.Parse("ce44c7c2-0c4e-4fbe-af8c-efbda81c13a7");

        string physical = CacheKeyPolicy.PhysicalKey(logicalHash, generation);
        physical.ShouldBe($"org-cache:v1:{logicalHash}:{generation:N}");
        physical.Length.ShouldBeLessThanOrEqualTo(1024);
        physical.ShouldNotContain("projects");
        physical.ShouldNotContain("organization");
        CacheKeyPolicy.PhysicalKey(logicalHash, Guid.Empty)
            .ShouldBe($"org-cache:v1:{logicalHash}:{Guid.Empty:N}");
    }

    [TestMethod]
    public void BoundedMaximumLengthIdentifiersAreAccepted()
    {
        ModuleCacheKey key = new(
            "a" + new string('b', 118) + "c",
            new string('a', 60) + "." + new string('b', 59),
            1,
            1,
            new string('a', 64),
            ModuleCacheAudience.Organization,
            new string('a', 64));

        Hash(key, new string('a', 120), new string('b', 120), OrganizationId, ActorId).Length.ShouldBe(64);
    }

    [TestMethod]
    public void InvalidKeyFieldsAndHostContextAreRejectedWithoutEchoingRawValues()
    {
        const string secret = "secret-cache-key-material";
        ModuleCacheKey[] invalidKeys =
        [
            Key() with { ModuleId = "Bad-module" },
            Key() with { ModuleId = new string('a', 121) },
            Key() with { ReadModelKey = "bad/read-model" },
            Key() with { ReadModelKey = new string('a', 121) },
            Key() with { SchemaVersion = 0 },
            Key() with { PolicyVersion = -1 },
            Key() with { ParametersHash = secret },
            Key() with { Audience = (ModuleCacheAudience)99 },
            Key() with { TtlPolicyKey = "Bad-Ttl" },
            Key() with { TtlPolicyKey = new string('a', 65) }
        ];

        foreach (ModuleCacheKey invalid in invalidKeys)
        {
            ArgumentException exception = Should.Throw<ArgumentException>(() =>
                Hash(invalid, "trykatch", "Production", OrganizationId, ActorId));
            exception.Message.ShouldNotContain(secret);
        }

        Should.Throw<ArgumentException>(() => Hash(Key(), "", "Production", OrganizationId, ActorId));
        Should.Throw<ArgumentException>(() => Hash(Key(), "bad namespace", "Production", OrganizationId, ActorId));
        Should.Throw<ArgumentException>(() => Hash(Key(), new string('a', 121), "Production", OrganizationId, ActorId));
        Should.Throw<ArgumentException>(() => Hash(Key(), "trykatch", "", OrganizationId, ActorId));
        Should.Throw<ArgumentException>(() => Hash(Key(), "trykatch", "bad/environment", OrganizationId, ActorId));
        Should.Throw<ArgumentException>(() => Hash(Key(), "trykatch", new string('a', 121), OrganizationId, ActorId));
        Should.Throw<ArgumentException>(() => Hash(Key(), "trykatch", "Production", Guid.Empty, ActorId));
        Should.Throw<ArgumentException>(() => Hash(Key(), "trykatch", "Production", OrganizationId, Guid.Empty));
    }

    [TestMethod]
    public void PhysicalKeyRejectsMalformedLogicalHashWithoutEchoingIt()
    {
        const string raw = "projects/secret query";

        ArgumentException exception = Should.Throw<ArgumentException>(() => CacheKeyPolicy.PhysicalKey(raw, Guid.NewGuid()));

        exception.Message.ShouldNotContain(raw);
    }

    private static ModuleCacheKey Key(ModuleCacheAudience audience = ModuleCacheAudience.Organization) =>
        new("projects", "projects.detail", 2, 3,
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", audience, "standard");

    private static string Hash(
        ModuleCacheKey key,
        string applicationNamespace,
        string environment,
        Guid organizationId,
        Guid actorId) =>
        CacheKeyPolicy.LogicalHash(key, applicationNamespace, environment, organizationId, actorId);

    private static void GetOrCreateCompilesForUnconstrainedType<T>()
    {
        Func<IOrganizationCache, ModuleCacheKey, Func<CancellationToken, Task<T>>, Task<T>> call =
            (cache, key, factory) =>
            cache.GetOrCreateAsync(key, factory);
        _ = call;
    }

    private static void InvalidateCompiles()
    {
        Func<IOrganizationCache, ModuleCacheKey, CancellationToken, Task> call =
            (cache, key, cancellationToken) =>
            cache.InvalidateAsync(key, cancellationToken);
        _ = call;
    }
}

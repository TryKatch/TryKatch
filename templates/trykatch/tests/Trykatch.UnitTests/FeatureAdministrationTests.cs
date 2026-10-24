using Shouldly;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Common;
using Trykatch.Application.Features;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class FeatureAdministrationTests
{
    private static readonly Guid Actor = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Organization TestOrganization = Organization.Create("Example", "example");
    private static readonly Guid OrganizationId = TestOrganization.Id;
    private const string FeatureKey = "projects.reads";
    private static readonly string[] ExpectedKeys = ["projects.reads", "projects.writes"];

    [TestMethod]
    public async Task ReadDenialReturnsForbiddenBeforeReadingOrganizationOrControls()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access();

        var result = await f.Subject.ListAsync(Actor, OrganizationId, default);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("forbidden");
        f.Authority.ReadCount.ShouldBe(1);
        f.Organizations.FindCount.ShouldBe(0);
        f.Store.ReadCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task ManageDenialReturnsForbiddenBeforeTargetOrLockSideEffects()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesRead);

        var result = await f.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("forbidden");
        f.Authority.ReadCount.ShouldBe(1);
        f.Store.LockCount.ShouldBe(0);
        f.Store.SaveEnvironmentCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task ReadAndManagePermissionsRemainIndependent()
    {
        var read = Fixture.Create();
        read.Authority.Access = Access(PlatformPermissions.FeaturesRead);
        (await read.Subject.ListAsync(Actor, null, default)).IsSuccess.ShouldBeTrue();
        (await read.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default)).ErrorCode.ShouldBe("forbidden");

        var manage = Fixture.Create();
        manage.Authority.Access = Access(PlatformPermissions.FeaturesManage);
        (await manage.Subject.ListAsync(Actor, null, default)).ErrorCode.ShouldBe("forbidden");
        (await manage.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default)).IsSuccess.ShouldBeTrue();
    }

    [TestMethod]
    public async Task ActiveAdministratorDefaultsAreReadFromStrictAuthorityAndInactiveAdminIsDenied()
    {
        var f = Fixture.Create();
        f.Authority.Access = new(true, true, PlatformRoles.Administrator, new HashSet<string>(StringComparer.Ordinal));
        (await f.Subject.ListAsync(Actor, null, default)).IsSuccess.ShouldBeTrue();
        (await f.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default)).IsSuccess.ShouldBeTrue();

        f.Authority.Access = new(false, true, PlatformRoles.Administrator, new HashSet<string>(StringComparer.Ordinal));
        (await f.Subject.ListAsync(Actor, null, default)).ErrorCode.ShouldBe("forbidden");
    }

    [TestMethod]
    public async Task UnknownFeatureIsNotFoundBeforeDirectoryOrStoreAccess()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesManage);

        var result = await f.Subject.SaveEnvironmentAsync(Actor, "unknown.feature",
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("not_found");
        f.Organizations.FindCount.ShouldBe(0);
        f.Store.LockCount.ShouldBe(0);
        f.Store.SaveEnvironmentCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task EmptyActorIsForbiddenWithoutAuthorityOrStoreCalls()
    {
        var f = Fixture.Create();

        var result = await f.Subject.ListAsync(Guid.Empty, null, default);

        result.ErrorCode.ShouldBe("forbidden");
        f.Authority.ReadCount.ShouldBe(0);
        f.Store.ReadCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task BusySaveStopsAfterSingleTryLockAndReportsBusy()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesManage);
        f.Store.LockResult = false;

        var result = await f.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("feature_control_busy");
        f.Store.LockCount.ShouldBe(1);
        f.Store.SaveEnvironmentCount.ShouldBe(0);
        f.Authority.ReadCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task RevocationWhileBusyDoesNotRetryTheLockOrSave()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesManage);
        f.Store.LockResult = false;

        var busy = await f.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default);

        busy.ErrorCode.ShouldBe("feature_control_busy");
        f.Authority.Access = Access();
        var retry = await f.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default);

        retry.ErrorCode.ShouldBe("forbidden");
        f.Store.LockCount.ShouldBe(1);
        f.Authority.ReadCount.ShouldBe(2);
        f.Store.SaveEnvironmentCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task RevocationAfterSuccessfulLockPreventsSaveAtSecondAuthorityRead()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesManage);
        f.Authority.OnRead = count =>
        {
            if (count == 2) f.Authority.Access = Access();
        };
        f.Store.OnLock = () => f.Authority.Access = Access();

        var result = await f.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.Empty), default);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("forbidden");
        f.Authority.ReadCount.ShouldBe(2);
        f.Store.LockCount.ShouldBe(1);
        f.Store.SaveEnvironmentCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task SaveConflictMapsToFeatureControlConflict()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesManage);
        f.Store.SaveResult = new(FeatureControlSaveStatus.Conflict);

        var result = await f.Subject.SaveEnvironmentAsync(Actor, FeatureKey,
            new SaveEnvironmentFeatureControl(false, false, Guid.NewGuid()), default);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("feature_control_conflict");
        f.Store.SaveEnvironmentCount.ShouldBe(1);
    }

    [TestMethod]
    public async Task EnvironmentReadUsesTrustedSettingsAndClampsHistoryPaging()
    {
        var f = Fixture.Create(environment: "prod-west");
        f.Authority.Access = Access(PlatformPermissions.FeaturesRead);

        var list = await f.Subject.ListAsync(Actor, null, default);
        list.IsSuccess.ShouldBeTrue();
        f.Store.LastReadEnvironment.ShouldBe("prod-west");
        list.Value!.Environment.ShouldBe("prod-west");
        list.Value.Items.Single().DeploymentDisabled.ShouldBeTrue();

        await f.Subject.HistoryAsync(Actor, new(FeatureKey), int.MaxValue, int.MaxValue, default);
        f.Store.LastHistoryEnvironment.ShouldBe("prod-west");
        f.Store.LastHistoryPage.ShouldBe(10_000);
        f.Store.LastHistoryPageSize.ShouldBe(100);
    }

    [TestMethod]
    public async Task OrganizationTargetsRequireExistingOrganizationAndValidGuid()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesManage);
        f.Organizations.Organization = null;

        var absent = await f.Subject.SaveOrganizationAsync(Actor, new(FeatureKey, OrganizationId),
            new SaveOrganizationFeatureOverride(true, Guid.Empty), default);
        absent.ErrorCode.ShouldBe("not_found");
        f.Store.LockCount.ShouldBe(0);

        var empty = await f.Subject.SaveOrganizationAsync(Actor, new(FeatureKey, Guid.Empty),
            new SaveOrganizationFeatureOverride(true, Guid.Empty), default);
        empty.ErrorCode.ShouldBe("invalid_request");
        f.Organizations.FindCount.ShouldBe(1);
        f.Store.LockCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task OrganizationSavePassesNullableResetCommandAndExactTargetThrough()
    {
        var f = Fixture.Create();
        f.Authority.Access = Access(PlatformPermissions.FeaturesManage);
        var target = new FeatureControlTarget(FeatureKey, OrganizationId);
        var command = new SaveOrganizationFeatureOverride(null, Guid.NewGuid());

        var result = await f.Subject.SaveOrganizationAsync(Actor, target, command, default);

        result.IsSuccess.ShouldBeTrue();
        f.Store.LastOrganizationTarget.ShouldBe(target);
        f.Store.LastOrganizationCommand.ShouldBeSameAs(command);
    }

    [TestMethod]
    public async Task ListSortsInstalledDeclarationsAndDoesNotExposeUninstalledControls()
    {
        var f = Fixture.Create(twoFeatures: true);
        f.Authority.Access = Access(PlatformPermissions.FeaturesRead);
        f.Store.ReadState = target => new(target, true, false, Guid.NewGuid(), DateTimeOffset.UtcNow);

        var result = await f.Subject.ListAsync(Actor, null, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.Select(x => x.Declaration.Key).ShouldBe(ExpectedKeys);
        f.Store.ReadCount.ShouldBe(2);
    }

    private static EffectivePlatformAccess Access(params string[] permissions) => new(
        true, false, "custom", new HashSet<string>(permissions, StringComparer.Ordinal));

    private sealed class Fixture
    {
        private Fixture(FeatureAdministration subject, Authority authority, Store store, Organizations organizations)
        {
            Subject = subject;
            Authority = authority;
            Store = store;
            Organizations = organizations;
        }

        public FeatureAdministration Subject { get; }
        public Authority Authority { get; }
        public Store Store { get; }
        public Organizations Organizations { get; }

        public static Fixture Create(string environment = "test", bool twoFeatures = false)
        {
            var authority = new Authority();
            var store = new Store();
            var organizations = new Organizations { Organization = TestOrganization };
            ModuleFeatureDescriptor[] features = twoFeatures
                ? [new("projects.writes", "Writes", "Write projects", true), new(FeatureKey, "Reads", "Read projects", true)]
                : [new(FeatureKey, "Reads", "Read projects", true)];
            var module = new TestModule(new("projects", "Projects", "1.0.0", "Projects module", [], [],
                ModuleCapabilities.Web, [] ) { Features = features });
            var catalog = new ModuleCatalog([module]);
            var settings = new FeaturePolicySettings(environment,
                new HashSet<string>([FeatureKey], StringComparer.Ordinal),
                new Dictionary<string, bool>(StringComparer.Ordinal) { [FeatureKey] = false });
            return new(new(authority, store, organizations, catalog, settings), authority, store, organizations);
        }
    }

    private sealed class TestModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class Authority : IPlatformAuthorityReader
    {
        public EffectivePlatformAccess Access { get; set; } = EffectivePlatformAccess.None;
        public int ReadCount { get; private set; }
        public Action<int>? OnRead { get; set; }
        public Task<EffectivePlatformAccess> ReadAsync(Guid userId, CancellationToken cancellationToken)
        {
            ReadCount++;
            OnRead?.Invoke(ReadCount);
            return Task.FromResult(Access);
        }
        public Task<bool> HasOtherActiveAdministratorAsync(Guid exceptUserId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Organizations : IOrganizationDirectory
    {
        public Organization? Organization { get; set; }
        public int FindCount { get; private set; }
        public Task<Organization?> FindAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            FindCount++;
            return Task.FromResult(Organization?.Id == organizationId ? Organization : null);
        }
        public Task<PagedResult<Organization>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid> GetOwnerRoleIdAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OrganizationCreationPreparationResult> PrepareCreationAsync(string name, string slug, Guid initiatingActorId, string administratorEmail, OrganizationDataPlacementKind placement, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddAsync(Organization organization, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OrganizationRoleSeeds> SeedRolesAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddMembershipAsync(Membership membership, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddInvitationAsync(Invitation invitation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store : IFeatureControlStore
    {
        public int ReadCount { get; private set; }
        public int LockCount { get; private set; }
        public int SaveEnvironmentCount { get; private set; }
        public bool LockResult { get; set; } = true;
        public FeatureControlSaveResult SaveResult { get; set; } = new(FeatureControlSaveStatus.Saved, new(new(FeatureKey), false, false, Guid.NewGuid(), DateTimeOffset.UtcNow));
        public Func<FeatureControlTarget, FeatureControlState>? ReadState { get; set; }
        public Action? OnLock { get; set; }
        public string? LastReadEnvironment { get; private set; }
        public string? LastHistoryEnvironment { get; private set; }
        public int LastHistoryPage { get; private set; }
        public int LastHistoryPageSize { get; private set; }
        public FeatureControlTarget? LastOrganizationTarget { get; private set; }
        public SaveOrganizationFeatureOverride? LastOrganizationCommand { get; private set; }
        public Task<FeatureControlState> ReadAsync(Guid actorId, string environment, FeatureControlTarget target, CancellationToken ct)
        {
            ReadCount++;
            LastReadEnvironment = environment;
            return Task.FromResult(ReadState?.Invoke(target) ?? new(target, null, false, Guid.Empty, null));
        }
        public Task<bool> TryLockAsync(Guid actorId, string environment, FeatureControlTarget target, CancellationToken ct)
        {
            LockCount++;
            OnLock?.Invoke();
            return Task.FromResult(LockResult);
        }
        public Task<FeatureControlSaveResult> SaveEnvironmentAsync(Guid actorId, string environment, string featureKey, SaveEnvironmentFeatureControl command, CancellationToken ct)
        {
            SaveEnvironmentCount++;
            return Task.FromResult(SaveResult);
        }
        public Task<FeatureControlSaveResult> SaveOrganizationAsync(Guid actorId, string environment, FeatureControlTarget target, SaveOrganizationFeatureOverride command, CancellationToken ct)
        {
            LastOrganizationTarget = target;
            LastOrganizationCommand = command;
            return Task.FromResult(SaveResult);
        }
        public Task<PagedResult<FeatureControlHistoryEntry>> HistoryAsync(Guid actorId, string environment, FeatureControlTarget target, int page, int pageSize, CancellationToken ct)
        {
            LastHistoryEnvironment = environment;
            LastHistoryPage = page;
            LastHistoryPageSize = pageSize;
            return Task.FromResult(new PagedResult<FeatureControlHistoryEntry>([], page, pageSize, 0));
        }
    }
}

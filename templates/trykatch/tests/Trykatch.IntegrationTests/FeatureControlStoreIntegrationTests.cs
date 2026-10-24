using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Common;
using Trykatch.Application.Features;
using Trykatch.Infrastructure.Features;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class FeatureControlStoreIntegrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private const string EnvironmentName = "production";
    private const string FeatureKey = "projects.reads";

    [TestMethod]
    public async Task EnvironmentControlUsesCasAndWritesTypedHistoryIncludingRetainedNullReset()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string owner = postgres.GetConnectionString();
        string runtime = (await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner)).Platform;
        Guid actor = Guid.NewGuid();
        FeatureControlTarget target = new(FeatureKey);

        FeatureControlSaveResult created = await SaveEnvironmentAsync(runtime, actor, target,
            new SaveEnvironmentFeatureControl(true, false, Guid.Empty));
        created.Status.ShouldBe(FeatureControlSaveStatus.Saved);
        created.State!.Version.ShouldNotBe(Guid.Empty);
        created.State.Enabled.ShouldBe(true);
        created.State.ForceDisabled.ShouldBe(false);

        FeatureControlState read = await ReadAsync(runtime, actor, target);
        read.ShouldBe(created.State);

        Guid nextActor = Guid.NewGuid();
        FeatureControlSaveResult updated = await SaveEnvironmentAsync(runtime, nextActor, target,
            new SaveEnvironmentFeatureControl(false, true, created.State.Version));
        updated.Status.ShouldBe(FeatureControlSaveStatus.Saved);
        updated.State!.Version.ShouldNotBe(created.State.Version);
        updated.State.Enabled.ShouldBe(false);
        updated.State.ForceDisabled.ShouldBe(true);

        FeatureControlSaveResult reset = await SaveEnvironmentAsync(runtime, actor, target,
            new SaveEnvironmentFeatureControl(null, false, updated.State.Version));
        reset.Status.ShouldBe(FeatureControlSaveStatus.Saved);
        reset.State!.Enabled.ShouldBeNull();
        reset.State.Version.ShouldNotBe(updated.State.Version);

        FeatureControlSaveResult stale = await SaveEnvironmentAsync(runtime, nextActor, target,
            new SaveEnvironmentFeatureControl(true, false, created.State.Version));
        stale.Status.ShouldBe(FeatureControlSaveStatus.Conflict);
        FeatureControlSaveResult emptyVersion = await SaveEnvironmentAsync(runtime, actor, target,
            new SaveEnvironmentFeatureControl(true, false, Guid.Empty));
        emptyVersion.Status.ShouldBe(FeatureControlSaveStatus.Conflict);

        PagedResult<FeatureControlHistoryEntry> history = await HistoryAsync(runtime, actor, target, 1, 10);
        history.TotalCount.ShouldBe(3);
        history.Items.Select(entry => entry.Version).Distinct().Count().ShouldBe(3);
        history.Items.ShouldContain(entry => entry.ActorId == actor && entry.PreviousVersion == null && entry.Enabled == true);
        history.Items.ShouldContain(entry => entry.ActorId == nextActor && entry.PreviousEnabled == true && entry.Enabled == false);
        history.Items.ShouldContain(entry => entry.PreviousEnabled == false && entry.Enabled == null);
        history.Items.ShouldAllBe(entry => entry.Target == target);
        history.Items.ShouldContain(entry => entry.Version == updated.State.Version
            && entry.PreviousVersion == created.State.Version && entry.PreviousForceDisabled == false && entry.ForceDisabled == true);
        var firstPage = await HistoryAsync(runtime, actor, target, 1, 1);
        var secondPage = await HistoryAsync(runtime, actor, target, 2, 1);
        firstPage.Items.Single().Id.ShouldNotBe(secondPage.Items.Single().Id);
        var bounded = await HistoryAsync(runtime, actor, target, 0, 250);
        bounded.Page.ShouldBe(1);
        bounded.PageSize.ShouldBe(100);
    }

    [TestMethod]
    public async Task OrganizationOverrideIsTargetedByOrganizationAndEnvironmentAndRetainsNullReset()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string owner = postgres.GetConnectionString();
        string runtime = (await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner)).Platform;
        Guid organization = await CreateOrganizationAsync(owner);
        Guid actor = Guid.NewGuid();
        FeatureControlTarget target = new(FeatureKey, organization);

        FeatureControlSaveResult initial = await SaveOrganizationAsync(runtime, actor, target,
            new SaveOrganizationFeatureOverride(true, Guid.Empty));
        initial.Status.ShouldBe(FeatureControlSaveStatus.Saved);
        FeatureControlState actual = await ReadAsync(runtime, actor, target);
        actual.Enabled.ShouldBe(true);
        actual.Target.OrganizationId.ShouldBe(organization);

        FeatureControlSaveResult reset = await SaveOrganizationAsync(runtime, actor, target,
            new SaveOrganizationFeatureOverride(null, initial.State!.Version));
        reset.Status.ShouldBe(FeatureControlSaveStatus.Saved);
        reset.State!.Enabled.ShouldBeNull();
        reset.State.Version.ShouldNotBe(initial.State.Version);

        FeatureControlState otherEnvironment = await ReadAsync(runtime, actor, target, "staging");
        otherEnvironment.Enabled.ShouldBeNull();
        otherEnvironment.Version.ShouldBe(Guid.Empty);
        (await ReadAsync(runtime, actor, new FeatureControlTarget(FeatureKey))).Version.ShouldBe(Guid.Empty);
        Guid otherOrganization = await CreateOrganizationAsync(owner);
        (await ReadAsync(runtime, actor, new FeatureControlTarget(FeatureKey, otherOrganization))).Version.ShouldBe(Guid.Empty);
        PagedResult<FeatureControlHistoryEntry> history = await HistoryAsync(runtime, actor, target, 1, 10);
        history.TotalCount.ShouldBe(2);
        history.Items.ShouldAllBe(entry => entry.Target.OrganizationId == organization);
    }

    [TestMethod]
    public async Task TryLockIsImmediateAndScopedToTheFullTarget()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string runtime = (await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(postgres.GetConnectionString())).Platform;
        Guid actor = Guid.NewGuid();
        FeatureControlTarget target = new(FeatureKey);
        await using PlatformDbContext first = NewContext(runtime);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction firstTransaction = await BeginAsync(first, actor);
        var firstStore = new FeatureControlStore(first, TimeProvider.System);
        (await firstStore.TryLockAsync(actor, EnvironmentName, target, CancellationToken.None)).ShouldBeTrue();

        Guid otherActor = Guid.NewGuid();
        await using PlatformDbContext second = NewContext(runtime);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction secondTransaction = await BeginAsync(second, otherActor);
        var secondStore = new FeatureControlStore(second, TimeProvider.System);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        (await secondStore.TryLockAsync(otherActor, EnvironmentName, target, timeout.Token)).ShouldBeFalse();
        timeout.IsCancellationRequested.ShouldBeFalse();
        (await new FeatureControlStore(second, TimeProvider.System).TryLockAsync(otherActor, "staging", target, timeout.Token)).ShouldBeTrue();
        (await new FeatureControlStore(second, TimeProvider.System).TryLockAsync(otherActor, EnvironmentName,
            new FeatureControlTarget("projects.writes"), timeout.Token)).ShouldBeTrue();
        (await new FeatureControlStore(second, TimeProvider.System).TryLockAsync(otherActor, EnvironmentName,
            new FeatureControlTarget(FeatureKey, Guid.NewGuid()), timeout.Token)).ShouldBeTrue();

        (await firstStore.SaveEnvironmentAsync(actor, EnvironmentName, FeatureKey,
            new SaveEnvironmentFeatureControl(true, false, Guid.Empty), CancellationToken.None))
            .Status.ShouldBe(FeatureControlSaveStatus.Saved);
        await firstTransaction.CommitAsync();
        await secondTransaction.RollbackAsync();

        FeatureControlSaveResult retry = await SaveEnvironmentAsync(runtime, actor, target,
            new SaveEnvironmentFeatureControl(true, false, Guid.Empty));
        retry.Status.ShouldBe(FeatureControlSaveStatus.Conflict);
        await AssertCountsAsync(postgres.GetConnectionString(), target, expectedControl: 1, expectedHistory: 1);
    }

    [TestMethod]
    public async Task OperationsRequireMatchingActorActiveReadCommittedPlatformTransactionAndAcquiredTarget()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string owner = postgres.GetConnectionString();
        string runtime = (await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner)).Platform;
        Guid actor = Guid.NewGuid();
        FeatureControlTarget target = new(FeatureKey);
        using CancellationTokenSource cancel = new();

        await using (PlatformDbContext noTransaction = NewContext(runtime))
        {
            var store = new FeatureControlStore(noTransaction, TimeProvider.System);
            await Should.ThrowAsync<InvalidOperationException>(() => store.ReadAsync(actor, EnvironmentName, target, cancel.Token));
            await Should.ThrowAsync<InvalidOperationException>(() => store.TryLockAsync(actor, EnvironmentName, target, cancel.Token));
            await Should.ThrowAsync<InvalidOperationException>(() => store.HistoryAsync(actor, EnvironmentName, target, 1, 10, cancel.Token));
        }

        await using (PlatformDbContext wrongActor = NewContext(runtime))
        await using (var transaction = await BeginAsync(wrongActor, actor))
        {
            var store = new FeatureControlStore(wrongActor, TimeProvider.System);
            await Should.ThrowAsync<InvalidOperationException>(() => store.TryLockAsync(Guid.NewGuid(), EnvironmentName, target, cancel.Token));
            await transaction.RollbackAsync();
        }

        await using (PlatformDbContext mismatch = NewContext(runtime))
        await using (var transaction = await BeginAsync(mismatch, actor))
        {
            var store = new FeatureControlStore(mismatch, TimeProvider.System);
            (await store.TryLockAsync(actor, EnvironmentName, target, cancel.Token)).ShouldBeTrue();
            await Should.ThrowAsync<InvalidOperationException>(() => store.SaveEnvironmentAsync(actor, EnvironmentName,
                "projects.writes", new SaveEnvironmentFeatureControl(true, false, Guid.Empty), cancel.Token));
            await transaction.RollbackAsync();
        }

        await using (PlatformDbContext noLock = NewContext(runtime))
        await using (var transaction = await BeginAsync(noLock, actor))
        {
            var store = new FeatureControlStore(noLock, TimeProvider.System);
            await Should.ThrowAsync<InvalidOperationException>(() => store.SaveEnvironmentAsync(actor, EnvironmentName,
                FeatureKey, new SaveEnvironmentFeatureControl(true, false, Guid.Empty), cancel.Token));
            await transaction.RollbackAsync();
        }

        await using (PlatformDbContext wrongIsolation = NewContext(runtime))
        await using (var transaction = await BeginAsync(wrongIsolation, actor, IsolationLevel.RepeatableRead))
        {
            var store = new FeatureControlStore(wrongIsolation, TimeProvider.System);
            await Should.ThrowAsync<InvalidOperationException>(() => store.TryLockAsync(actor, EnvironmentName, target, cancel.Token));
            await transaction.RollbackAsync();
        }

        await using (PlatformDbContext changedActor = NewContext(runtime))
        await using (var transaction = await BeginAsync(changedActor, actor))
        {
            var store = new FeatureControlStore(changedActor, TimeProvider.System);
            (await store.TryLockAsync(actor, EnvironmentName, target, cancel.Token)).ShouldBeTrue();
            Guid replacementActor = Guid.NewGuid();
            await changedActor.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.actor_id', {replacementActor.ToString()}, true)");
            await Should.ThrowAsync<InvalidOperationException>(() => store.SaveEnvironmentAsync(replacementActor, EnvironmentName,
                FeatureKey, new SaveEnvironmentFeatureControl(true, false, Guid.Empty), cancel.Token));
            await transaction.RollbackAsync();
        }

        await using (PlatformDbContext changedTransaction = NewContext(runtime))
        {
            var store = new FeatureControlStore(changedTransaction, TimeProvider.System);
            await using (var first = await BeginAsync(changedTransaction, actor))
            {
                (await store.TryLockAsync(actor, EnvironmentName, target, cancel.Token)).ShouldBeTrue();
                await first.CommitAsync();
            }
            await using (var replacement = await BeginAsync(changedTransaction, actor))
            {
                await Should.ThrowAsync<InvalidOperationException>(() => store.SaveEnvironmentAsync(actor, EnvironmentName,
                    FeatureKey, new SaveEnvironmentFeatureControl(true, false, Guid.Empty), cancel.Token));
                await replacement.RollbackAsync();
            }
        }

        await using (PlatformDbContext actorless = NewContext(runtime))
        await using (var transaction = await BeginAsync(actorless, Guid.Empty))
        {
            var store = new FeatureControlStore(actorless, TimeProvider.System);
            await Should.ThrowAsync<InvalidOperationException>(() => store.TryLockAsync(Guid.Empty, EnvironmentName, target, cancel.Token));
            await transaction.RollbackAsync();
        }

        await using (PlatformDbContext organizationProfile = NewContext((await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner)).Organization))
        await using (var transaction = await BeginAsync(organizationProfile, actor))
        {
            var store = new FeatureControlStore(organizationProfile, TimeProvider.System);
            await Should.ThrowAsync<InvalidOperationException>(() => store.TryLockAsync(actor, EnvironmentName, target, cancel.Token));
            await transaction.RollbackAsync();
        }

        await AssertCountsAsync(owner, target, expectedControl: 0, expectedHistory: 0);
    }

    [TestMethod]
    public async Task FailedHistoryInsertionAndDeferredCommitLeaveNoControlOrHistoryRows()
    {
        await using PostgreSqlContainer postgres = await MigratedDatabaseAsync();
        string owner = postgres.GetConnectionString();
        string runtime = (await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner)).Platform;
        FeatureControlTarget beforeInsertTarget = new("projects.before-insert-failure");
        await CreateFailureTriggerAsync(owner, beforeInsertTarget, deferred: false);
        await using (PlatformDbContext context = NewContext(runtime))
        await using (var transaction = await BeginAsync(context, Guid.NewGuid()))
        {
            var store = new FeatureControlStore(context, TimeProvider.System);
            Guid actor = await CurrentActorAsync(context);
            await store.TryLockAsync(actor, EnvironmentName, beforeInsertTarget, CancellationToken.None);
            Exception failure = await Should.ThrowAsync<Exception>(() => store.SaveEnvironmentAsync(actor, EnvironmentName,
                beforeInsertTarget.FeatureKey, new SaveEnvironmentFeatureControl(true, false, Guid.Empty), CancellationToken.None));
            await transaction.RollbackAsync();
            (failure is PostgresException || failure.InnerException is PostgresException).ShouldBeTrue();
        }
        await AssertCountsAsync(owner, beforeInsertTarget, 0, 0);

        FeatureControlTarget updateTarget = new("projects.before-update-failure");
        Guid updateActor = Guid.NewGuid();
        FeatureControlSaveResult baseline = await SaveEnvironmentAsync(runtime, updateActor, updateTarget,
            new SaveEnvironmentFeatureControl(true, false, Guid.Empty));
        baseline.Status.ShouldBe(FeatureControlSaveStatus.Saved);
        await CreateFailureTriggerAsync(owner, updateTarget, deferred: false);
        await using (PlatformDbContext context = NewContext(runtime))
        await using (var transaction = await BeginAsync(context, updateActor))
        {
            var store = new FeatureControlStore(context, TimeProvider.System);
            (await store.TryLockAsync(updateActor, EnvironmentName, updateTarget, CancellationToken.None)).ShouldBeTrue();
            await Should.ThrowAsync<PostgresException>(() => store.SaveEnvironmentAsync(updateActor, EnvironmentName,
                updateTarget.FeatureKey, new SaveEnvironmentFeatureControl(false, true, baseline.State!.Version), CancellationToken.None));
            await transaction.RollbackAsync();
        }
        (await ReadAsync(runtime, updateActor, updateTarget)).ShouldBe(baseline.State);
        await AssertCountsAsync(owner, updateTarget, 1, 1);

        FeatureControlTarget deferredTarget = new("projects.deferred-failure");
        await CreateFailureTriggerAsync(owner, deferredTarget, deferred: true);
        await using (PlatformDbContext context = NewContext(runtime))
        await using (var transaction = await BeginAsync(context, Guid.NewGuid()))
        {
            var store = new FeatureControlStore(context, TimeProvider.System);
            Guid actor = await CurrentActorAsync(context);
            await store.TryLockAsync(actor, EnvironmentName, deferredTarget, CancellationToken.None);
            FeatureControlSaveResult save = await store.SaveEnvironmentAsync(actor, EnvironmentName, deferredTarget.FeatureKey,
                new SaveEnvironmentFeatureControl(true, false, Guid.Empty), CancellationToken.None);
            save.Status.ShouldBe(FeatureControlSaveStatus.Saved);
            PostgresException failure = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
            failure.MessageText.ShouldContain("forced deferred feature history failure");
        }
        await AssertCountsAsync(owner, deferredTarget, 0, 0);
    }

    private static async Task<PostgreSqlContainer> MigratedDatabaseAsync()
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(postgres.GetConnectionString());
        (int Exit, string Output, string Error) migration = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(postgres.GetConnectionString());
        migration.Exit.ShouldBe(0, migration.Error + migration.Output);
        return postgres;
    }

    private static PlatformDbContext NewContext(string connection) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connection).Options);

    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginAsync(PlatformDbContext context, Guid actor,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(isolation);
        await using NpgsqlCommand command = new("SELECT set_config('app.actor_id', @actor, true)",
            (NpgsqlConnection)context.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
        command.Parameters.AddWithValue("actor", actor == Guid.Empty ? string.Empty : actor.ToString());
        await command.ExecuteScalarAsync();
        return transaction;
    }

    private static async Task<Guid> CurrentActorAsync(PlatformDbContext context)
    {
        await using NpgsqlCommand command = new("SELECT current_setting('app.actor_id')::uuid",
            (NpgsqlConnection)context.Database.GetDbConnection(), (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction());
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<FeatureControlSaveResult> SaveEnvironmentAsync(string connection, Guid actor,
        FeatureControlTarget target, SaveEnvironmentFeatureControl command)
    {
        await using PlatformDbContext context = NewContext(connection);
        await using var transaction = await BeginAsync(context, actor);
        var store = new FeatureControlStore(context, TimeProvider.System);
        (await store.TryLockAsync(actor, EnvironmentName, target, CancellationToken.None)).ShouldBeTrue();
        FeatureControlSaveResult result = await store.SaveEnvironmentAsync(actor, EnvironmentName, target.FeatureKey, command, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<FeatureControlSaveResult> SaveOrganizationAsync(string connection, Guid actor,
        FeatureControlTarget target, SaveOrganizationFeatureOverride command)
    {
        await using PlatformDbContext context = NewContext(connection);
        await using var transaction = await BeginAsync(context, actor);
        var store = new FeatureControlStore(context, TimeProvider.System);
        (await store.TryLockAsync(actor, EnvironmentName, target, CancellationToken.None)).ShouldBeTrue();
        FeatureControlSaveResult result = await store.SaveOrganizationAsync(actor, EnvironmentName, target, command, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<FeatureControlState> ReadAsync(string connection, Guid actor, FeatureControlTarget target,
        string environment = EnvironmentName)
    {
        await using PlatformDbContext context = NewContext(connection);
        await using var transaction = await BeginAsync(context, actor);
        FeatureControlState state = await new FeatureControlStore(context, TimeProvider.System)
            .ReadAsync(actor, environment, target, CancellationToken.None);
        await transaction.CommitAsync();
        return state;
    }

    private static async Task<PagedResult<FeatureControlHistoryEntry>> HistoryAsync(string connection, Guid actor,
        FeatureControlTarget target, int page, int pageSize)
    {
        await using PlatformDbContext context = NewContext(connection);
        await using var transaction = await BeginAsync(context, actor);
        PagedResult<FeatureControlHistoryEntry> result = await new FeatureControlStore(context, TimeProvider.System)
            .HistoryAsync(actor, EnvironmentName, target, page, pageSize, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<Guid> CreateOrganizationAsync(string owner)
    {
        Guid id = Guid.NewGuid();
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO platform.organizations ("Id", "Name", "Slug", "IsActive", "CreatedAt")
            VALUES (@id, 'Feature test organization', @slug, true, CURRENT_TIMESTAMP)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("slug", $"feature-{id:N}"[..16]);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task AssertCountsAsync(string owner, FeatureControlTarget target, int expectedControl, int expectedHistory)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT (SELECT count(*) FROM platform.feature_environment_controls WHERE "Environment" = @environment AND "FeatureKey" = @feature),
                   (SELECT count(*) FROM platform.feature_control_history WHERE "Environment" = @environment AND "FeatureKey" = @feature)
            """, connection);
        command.Parameters.AddWithValue("environment", EnvironmentName);
        command.Parameters.AddWithValue("feature", target.FeatureKey);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetInt64(0).ShouldBe((long)expectedControl);
        reader.GetInt64(1).ShouldBe((long)expectedHistory);
    }

    private static async Task CreateFailureTriggerAsync(string owner, FeatureControlTarget target, bool deferred)
    {
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        string trigger = $"feature_history_failure_{Guid.NewGuid():N}";
        string timing = deferred ? "AFTER INSERT" : "BEFORE INSERT";
        string createTrigger = deferred
            ? $"CREATE CONSTRAINT TRIGGER {trigger} {timing} ON platform.feature_control_history DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION platform.fail_feature_history_test('{target.FeatureKey}')"
            : $"CREATE TRIGGER {trigger} {timing} ON platform.feature_control_history FOR EACH ROW EXECUTE FUNCTION platform.fail_feature_history_test('{target.FeatureKey}')";
        await using NpgsqlCommand command = new("""
            CREATE OR REPLACE FUNCTION platform.fail_feature_history_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."FeatureKey" = TG_ARGV[0] THEN RAISE EXCEPTION 'forced deferred feature history failure'; END IF;
              RETURN NEW;
            END $$;
            """, connection);
        await command.ExecuteNonQueryAsync();
        await using NpgsqlCommand create = new(createTrigger, connection);
        await create.ExecuteNonQueryAsync();
    }
}

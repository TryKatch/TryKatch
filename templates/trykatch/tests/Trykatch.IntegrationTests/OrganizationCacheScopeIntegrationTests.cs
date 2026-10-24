using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed partial class OrganizationCacheScopeIntegrationTests
{
    private const string PostgresImage =
        "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";

    [TestMethod]
    public async Task ReadOnlyScopeUsesReadCommittedReadOnlyTransactionAndActorScopedRuntimeRole()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IOrganizationContext organizationContext = scope.ServiceProvider.GetRequiredService<IOrganizationContext>();
        organizationContext.IsResolved.ShouldBeFalse();

        Guid organizationId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();
        IOrganizationDataScope dataScope = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginReadOnlyAsync(organizationId, actorId, CancellationToken.None);
        await using (dataScope)
        {
            OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
            ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            IDbContextTransaction controlTransaction = control.Database.CurrentTransaction!;
            IDbContextTransaction applicationTransaction = application.Database.CurrentTransaction!;
            ReferenceEquals(controlTransaction.GetDbTransaction(), applicationTransaction.GetDbTransaction()).ShouldBeTrue();
            ReferenceEquals(control.Database.GetDbConnection(), application.Database.GetDbConnection()).ShouldBeTrue();

            TransactionState state = await ReadTransactionStateAsync(control, controlTransaction.GetDbTransaction());
            state.ReadOnly.ShouldBe("on");
            state.Isolation.ShouldBe("read committed");
            state.Role.ShouldBe(PostgresRuntimeRoleFixture.OrganizationRole);
            state.Organization.ShouldBe(organizationId.ToString());
            state.Actor.ShouldBe(actorId.ToString());
        }
    }

    [TestMethod]
    public async Task ExistingReadWriteScopeRemainsReadWriteAndEnlistsApplicationOnlyWhenRequested()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IOrganizationDataScope dataScope = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await using (dataScope)
        {
            application.Database.CurrentTransaction.ShouldBeNull();
            Guid organizationId = Guid.NewGuid();
            Guid actorId = Guid.NewGuid();
            IDbContextTransaction enlisted = await OrganizationTransactionEnlistment.EnlistAsync(
                control, application, organizationId, actorId, CancellationToken.None);
            ReferenceEquals(
                control.Database.CurrentTransaction!.GetDbTransaction(),
                enlisted.GetDbTransaction()).ShouldBeTrue();

            TransactionState state = await ReadTransactionStateAsync(control, enlisted.GetDbTransaction());
            state.ReadOnly.ShouldBe("off");
            state.Isolation.ShouldBe("read committed");
            state.Role.ShouldBe(PostgresRuntimeRoleFixture.OrganizationRole);
            state.Organization.ShouldBe(organizationId.ToString());
            state.Actor.ShouldBe(actorId.ToString());
        }
        application.Database.CurrentTransaction.ShouldBeNull();
    }

    [TestMethod]
    public async Task DisposedImplicitRollbackClearsEnlistedApplicationWrapperBeforeNextScope()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IOrganizationDataScopeFactory factory = scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>();
        Guid firstOrganizationId = Guid.NewGuid();
        Guid firstActorId = Guid.NewGuid();
        await using (IOrganizationDataScope firstScope = await factory.BeginAsync(
                         firstOrganizationId, firstActorId, CancellationToken.None))
        {
            IDbContextTransaction enlisted = await OrganizationTransactionEnlistment.EnlistAsync(
                control, application, firstOrganizationId, firstActorId, CancellationToken.None);
            enlisted.GetDbTransaction().ShouldBeSameAs(control.Database.CurrentTransaction!.GetDbTransaction());
        }
        application.Database.CurrentTransaction.ShouldBeNull();
        control.Database.CurrentTransaction.ShouldBeNull();

        Guid secondOrganizationId = Guid.NewGuid();
        Guid secondActorId = Guid.NewGuid();
        await using IOrganizationDataScope secondScope = await factory.BeginAsync(
            secondOrganizationId, secondActorId, CancellationToken.None);
        IDbContextTransaction secondEnlistment = await OrganizationTransactionEnlistment.EnlistAsync(
            control, application, secondOrganizationId, secondActorId, CancellationToken.None);
        DbTransaction secondTransaction = secondEnlistment.GetDbTransaction();
        secondTransaction.ShouldBeSameAs(control.Database.CurrentTransaction!.GetDbTransaction());

        TransactionState state = await ReadTransactionStateAsync(control, secondTransaction);
        state.ReadOnly.ShouldBe("off");
        state.Organization.ShouldBe(secondOrganizationId.ToString());
        state.Actor.ShouldBe(secondActorId.ToString());
    }

    [TestMethod]
    public async Task ReadOnlyScopeRejectsEmptyOrganizationOrActorBeforeOpeningTransaction()
    {
        await using PostgreSqlContainer postgres = await StartPostgresAsync();
        using ServiceProvider provider = BuildProvider(postgres.GetConnectionString());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        IOrganizationDataScopeFactory factory = scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>();

        await Should.ThrowAsync<ArgumentException>(() => factory.BeginReadOnlyAsync(Guid.Empty, Guid.NewGuid(), CancellationToken.None));
        control.Database.CurrentTransaction.ShouldBeNull();
        await Should.ThrowAsync<ArgumentException>(() => factory.BeginReadOnlyAsync(Guid.NewGuid(), Guid.Empty, CancellationToken.None));
        control.Database.CurrentTransaction.ShouldBeNull();
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(postgres.GetConnectionString());
        return postgres;
    }

    private static ServiceProvider BuildProvider(string ownerConnection, params IModule[] modules)
    {
        NpgsqlConnectionStringBuilder owner = new(ownerConnection);
        Dictionary<string, string?> settings = new()
        {
            [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Organization}"] = RoleConnection(
                owner, PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.OrganizationPassword),
            [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Platform}"] = RoleConnection(
                owner, PostgresRuntimeRoleFixture.PlatformRole, PostgresRuntimeRoleFixture.PlatformPassword),
            [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Identity}"] = RoleConnection(
                owner, PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.IdentityPassword),
            [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Outbox}"] = RoleConnection(
                owner, PostgresRuntimeRoleFixture.OutboxRole, PostgresRuntimeRoleFixture.OutboxPassword),
            [$"ConnectionStrings:{RuntimeDatabaseConnectionContract.WorkCoordinator}"] = RoleConnection(
                owner, PostgresRuntimeRoleFixture.WorkCoordinatorRole, PostgresRuntimeRoleFixture.WorkCoordinatorPassword)
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddApplication();
        services.AddModules(configuration, modules);
        services.AddInfrastructure(configuration, isOpenApiGeneration: true);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string RoleConnection(NpgsqlConnectionStringBuilder owner, string role, string password) =>
        new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = role, Password = password }.ConnectionString;

    private static async Task<TransactionState> ReadTransactionStateAsync(
        DbContext context,
        DbTransaction transaction)
    {
        await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT current_setting('transaction_read_only'), current_setting('transaction_isolation'),
                   current_user, current_setting('app.organization_id', true), current_setting('app.actor_id', true)
            """;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private sealed record TransactionState(string ReadOnly, string Isolation, string Role, string Organization, string Actor);
}

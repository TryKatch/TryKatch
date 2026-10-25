using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task OrganizationRequestAdmissionConcurrentReplicasShareTheFinalSlots()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        const int limit = 2;
        const int replicas = 8;
        List<MutationScope> scopes = [];
        try
        {
            for (int index = 0; index < replicas; index++)
                scopes.Add(await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user));

            Task<OrganizationRequestAdmissionResult>[] admissions = scopes
                .Select(scope => CreateRequestAdmission(scope.Services, limit, windowSeconds: 60)
                    .TryAdmitAsync())
                .ToArray();
            OrganizationRequestAdmissionResult[] results = await Task.WhenAll(admissions)
                .WaitAsync(TimeSpan.FromSeconds(10));

            results.Count(result => result.Disposition == OrganizationRequestAdmissionDisposition.Allowed).ShouldBe(limit);
            results.Count(result => result.Disposition == OrganizationRequestAdmissionDisposition.Exhausted).ShouldBe(replicas - limit);
            (await fixture.ReadRequestAdmissionAttemptsAsync(user.OrganizationId)).ShouldBe(limit);
        }
        finally
        {
            foreach (MutationScope scope in scopes)
                await scope.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionKeepsTheSameCapturedDatabaseAsTheAuthorizedHost()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment(registrations =>
            registrations.AddSingleton(OrganizationRequestLimitSettings.Create(CreateRequestAdmissionConfiguration(
                null, enabled: true, permitLimit: 1, windowSeconds: 60))));
        await using MutationScope request = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IConfiguration configuration = environment.Services.GetRequiredService<IConfiguration>();
        string key = $"ConnectionStrings:{RuntimeDatabaseConnectionContract.Organization}";
        string? original = configuration[key];
        try
        {
            configuration[key] = "Host=127.0.0.1;Port=1;Database=changed;Username=changed;Password=changed;Timeout=1;Pooling=false";
            IOrganizationRequestAdmission admission = request.Services.GetRequiredService<IOrganizationRequestAdmission>();
            (await admission.TryAdmitAsync()).Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
            (await admission.TryAdmitAsync()).Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Exhausted);
            (await fixture.ReadRequestAdmissionAttemptsAsync(user.OrganizationId)).ShouldBe(1);
        }
        finally { configuration[key] = original; }
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionAllowanceIsIndependentPerOrganization()
    {
        ProjectsSeededUser first = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        ProjectsSeededUser second = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope firstScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, first);
        await using MutationScope secondScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, second);

        OrganizationRequestAdmissionResult firstResult = await CreateRequestAdmission(firstScope.Services, 1, 60).TryAdmitAsync();
        OrganizationRequestAdmissionResult secondResult = await CreateRequestAdmission(secondScope.Services, 1, 60).TryAdmitAsync();

        firstResult.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
        secondResult.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
        (await fixture.ReadRequestAdmissionAttemptsAsync(first.OrganizationId)).ShouldBe(1);
        (await fixture.ReadRequestAdmissionAttemptsAsync(second.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionPersistsWhenTheParentBusinessTransactionRollsBack()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            OrganizationRequestAdmissionResult admitted = await CreateRequestAdmission(parent.Services, 1, 60).TryAdmitAsync();
            admitted.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
            // The scoped request transaction is disposed without commit.
        }

        await using MutationScope retry = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        OrganizationRequestAdmissionResult exhausted = await CreateRequestAdmission(retry.Services, 1, 60).TryAdmitAsync();
        exhausted.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Exhausted);
        (await fixture.ReadRequestAdmissionAttemptsAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionUsesDatabaseWindowTimeAndAllowsAfterRollover()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        OrganizationRequestAdmission admission = CreateRequestAdmission(scope.Services, 1, windowSeconds: 60);

        (await admission.TryAdmitAsync()).Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
        (await admission.TryAdmitAsync()).Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Exhausted);
        await fixture.AgeRequestAdmissionWindowAsync(user.OrganizationId, TimeSpan.FromMinutes(2));

        OrganizationRequestAdmissionResult afterRollover = await admission.TryAdmitAsync();
        afterRollover.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
        (await fixture.ReadRequestAdmissionAttemptsAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionDisabledByDefaultDoesNotRequireOrganizationContextOrDatabase()
    {
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using AsyncServiceScope scope = environment.Services.CreateAsyncScope();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        OrganizationRequestLimitSettings settings = OrganizationRequestLimitSettings.Create(configuration);
        OrganizationRequestAdmission admission = ActivatorUtilities.CreateInstance<OrganizationRequestAdmission>(
            scope.ServiceProvider, settings, string.Empty);

        OrganizationRequestAdmissionResult result = await admission.TryAdmitAsync();

        result.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionUnresolvedOrganizationFailsClosed()
    {
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using AsyncServiceScope scope = environment.Services.CreateAsyncScope();

        OrganizationRequestAdmissionResult result = await CreateRequestAdmission(scope.ServiceProvider, 1, 60)
            .TryAdmitAsync();

        result.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Unavailable);
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionCallerCancellationDoesNotConsumeAnAttempt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => CreateRequestAdmission(scope.Services, 1, 60)
            .TryAdmitAsync(cancellation.Token));
        (await fixture.ReadRequestAdmissionAttemptsAsync(user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionDatabaseOutageReturnsUnavailableAndRetainsTheCount()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        (await CreateRequestAdmission(scope.Services, 1, 60).TryAdmitAsync()).Disposition
            .ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);

        IConfiguration brokenConfiguration = CreateRequestAdmissionConfiguration(
            environment.Services.GetRequiredService<IConfiguration>(),
            enabled: true,
            permitLimit: 1,
            windowSeconds: 60,
            organizationConnection: "Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Password=unavailable;Timeout=1;Pooling=false");
        OrganizationRequestLimitSettings settings = OrganizationRequestLimitSettings.Create(brokenConfiguration);
        OrganizationRequestAdmission outageAdmission = ActivatorUtilities.CreateInstance<OrganizationRequestAdmission>(
            scope.Services, settings, RuntimeDatabaseConnectionContract.Get(brokenConfiguration, RuntimeDatabaseConnectionContract.Organization));

        OrganizationRequestAdmissionResult unavailable = await outageAdmission.TryAdmitAsync();

        unavailable.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Unavailable);
        (await fixture.ReadRequestAdmissionAttemptsAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task OrganizationRequestAdmissionRowLockIsBoundedAndDoesNotFallBackLocally()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["projects.manage"]);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        (await CreateRequestAdmission(scope.Services, 2, 60).TryAdmitAsync()).Disposition
            .ShouldBe(OrganizationRequestAdmissionDisposition.Allowed);

        await using NpgsqlConnection lockConnection = new(fixture.OwnerConnectionForRequestAdmission);
        await lockConnection.OpenAsync();
        await using NpgsqlTransaction transaction = await lockConnection.BeginTransactionAsync();
        await using (NpgsqlCommand lockRow = new("""
            SELECT 1 FROM app.organization_request_limits
            WHERE "OrganizationId" = @organization AND "Policy" = 'organization-api'
            FOR UPDATE
            """, lockConnection, transaction))
        {
            lockRow.Parameters.AddWithValue("organization", user.OrganizationId);
            (await lockRow.ExecuteScalarAsync()).ShouldNotBeNull();
        }

        OrganizationRequestAdmissionResult unavailable = await CreateRequestAdmission(scope.Services, 2, 60)
            .TryAdmitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        unavailable.Disposition.ShouldBe(OrganizationRequestAdmissionDisposition.Unavailable);
        await transaction.RollbackAsync();
        (await fixture.ReadRequestAdmissionAttemptsAsync(user.OrganizationId)).ShouldBe(1);
    }

    private static OrganizationRequestAdmission CreateRequestAdmission(IServiceProvider services, int permitLimit, int windowSeconds)
    {
        IConfiguration configuration = CreateRequestAdmissionConfiguration(
            services.GetRequiredService<IConfiguration>(), enabled: true, permitLimit, windowSeconds);
        OrganizationRequestLimitSettings settings = OrganizationRequestLimitSettings.Create(configuration);
        return ActivatorUtilities.CreateInstance<OrganizationRequestAdmission>(services, settings,
            RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.Organization));
    }

    private static IConfiguration CreateRequestAdmissionConfiguration(
        IConfiguration? baseConfiguration,
        bool enabled,
        int permitLimit,
        int windowSeconds,
        string? organizationConnection = null)
    {
        Dictionary<string, string?> values = new()
        {
            ["OrganizationRequestLimits:Enabled"] = enabled.ToString(),
            ["OrganizationRequestLimits:PermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture),
            ["OrganizationRequestLimits:WindowSeconds"] = windowSeconds.ToString(CultureInfo.InvariantCulture)
        };
        if (organizationConnection is not null)
            values[$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Organization}"] = organizationConnection;
        ConfigurationBuilder builder = new();
        if (baseConfiguration is not null)
            builder.AddConfiguration(baseConfiguration);
        return builder.AddInMemoryCollection(values).Build();
    }

    private sealed partial class ExecutionFixture
    {
        public string OwnerConnectionForRequestAdmission => owner;

        public async Task<int> ReadRequestAdmissionAttemptsAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "Attempts" FROM app.organization_request_limits
                WHERE "OrganizationId" = @organization AND "Policy" = 'organization-api'
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            object? value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        public async Task AgeRequestAdmissionWindowAsync(Guid organizationId, TimeSpan age)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                UPDATE app.organization_request_limits
                SET "WindowStartUtc" = "WindowStartUtc" - @age
                WHERE "OrganizationId" = @organization AND "Policy" = 'organization-api'
                """, connection);
            command.Parameters.AddWithValue("age", age);
            command.Parameters.AddWithValue("organization", organizationId);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
    }
}

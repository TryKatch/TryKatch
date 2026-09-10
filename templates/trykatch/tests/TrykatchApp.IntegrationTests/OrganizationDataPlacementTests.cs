using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Domain.Organizations;
using TrykatchApp.Infrastructure.Organizations;
using TrykatchApp.Infrastructure.Persistence;

namespace TrykatchApp.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class OrganizationDataPlacementTests
{
    [TestMethod]
    public async Task ProvisioningRecoversFromFailureAndSerializesConcurrentRetries()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string connectionString = postgres.GetConnectionString();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);
        await using (PlatformDbContext migration = CreateContext(connectionString))
            await migration.Database.MigrateAsync();

        Guid retryOrganization = Guid.CreateVersion7();
        RecordingAdapter retryAdapter = new(failFirstAttempt: true);
        OrganizationDataPlacementResult failed;
        await using (PlatformDbContext firstContext = CreateContext(connectionString))
        {
            OrganizationDataPlacement placement = new(firstContext, [retryAdapter], TimeProvider.System);
            failed = await placement.ProvisionAsync(Request(retryOrganization), CancellationToken.None);
        }
        failed.State.ShouldBe(OrganizationProvisioningState.Failed);
        await using (PlatformDbContext durableContext = CreateContext(connectionString))
        {
            var durable = await durableContext.OrganizationDataPlacements.AsNoTracking()
                .SingleAsync(item => item.OrganizationId == retryOrganization);
            durable.State.ShouldBe(OrganizationProvisioningState.Failed);
            durable.FailureCode.ShouldBe("provisioning_failed");
        }

        await using (PlatformDbContext retryContext = CreateContext(connectionString))
        {
            OrganizationDataPlacement placement = new(retryContext, [retryAdapter], TimeProvider.System);
            (await placement.ProvisionAsync(Request(retryOrganization), CancellationToken.None)).IsReady.ShouldBeTrue();
        }
        await using (PlatformDbContext idempotentContext = CreateContext(connectionString))
        {
            OrganizationDataPlacement placement = new(idempotentContext, [retryAdapter], TimeProvider.System);
            (await placement.ProvisionAsync(Request(retryOrganization), CancellationToken.None)).IsReady.ShouldBeTrue();
        }
        retryAdapter.Attempts.ShouldBe(2, "a ready placement must not invoke its adapter again");

        Guid concurrentOrganization = Guid.CreateVersion7();
        RecordingAdapter concurrentAdapter = new(delay: TimeSpan.FromMilliseconds(150));
        await using PlatformDbContext leftContext = CreateContext(connectionString);
        await using PlatformDbContext rightContext = CreateContext(connectionString);
        OrganizationDataPlacement left = new(leftContext, [concurrentAdapter], TimeProvider.System);
        OrganizationDataPlacement right = new(rightContext, [concurrentAdapter], TimeProvider.System);

        OrganizationDataPlacementResult[] results = await Task.WhenAll(
            left.ProvisionAsync(Request(concurrentOrganization), CancellationToken.None),
            right.ProvisionAsync(Request(concurrentOrganization), CancellationToken.None));

        results.ShouldAllBe(result => result.IsReady);
        concurrentAdapter.Attempts.ShouldBe(1, "the organization advisory lock must collapse concurrent provisioning into one adapter call");
    }

    private static PlatformDbContext CreateContext(string connectionString) => new(
        new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);

    private static OrganizationDataPlacementRequest Request(Guid organizationId) =>
        new(organizationId, OrganizationDataPlacementKind.Shared);

    private sealed class RecordingAdapter(bool failFirstAttempt = false, TimeSpan? delay = null) : IOrganizationDataPlacementAdapter
    {
        private int attempts;
        public int Attempts => attempts;
        public OrganizationDataPlacementKind Kind => OrganizationDataPlacementKind.Shared;

        public async Task<OrganizationDataRoute> ProvisionAsync(
            OrganizationDataPlacementRequest request,
            CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref attempts);
            if (delay is not null)
                await Task.Delay(delay.Value, cancellationToken);
            if (failFirstAttempt && attempt == 1)
                throw new InvalidOperationException("simulated failure");
            return new(request.OrganizationId, Kind, "postgres", "test", "test", "1.0.0");
        }
    }
}

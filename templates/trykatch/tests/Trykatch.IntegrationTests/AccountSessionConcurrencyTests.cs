using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Identity;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration")]
public sealed class AccountSessionConcurrencyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingRenewalOrWorkspaceBindingCannotReviveAConcurrentlyRevokedRow(bool binding)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = await host.SignInAsync();
        await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(host.OwnerConnection).Options);
        ApplicationUser user = await identity.Users.AsNoTracking().SingleAsync(value => value.Email == SecurityHost.Email);
        string applicationName = "session-race-" + Guid.NewGuid().ToString("N");
        IdentitySessionRegistry registry = new(new NpgsqlConnectionStringBuilder(host.OwnerConnection)
        {
            Username = PostgresRuntimeRoleFixture.IdentityRole,
            Password = PostgresRuntimeRoleFixture.IdentityPassword,
            ApplicationName = applicationName
        }.ConnectionString, host.Clock);
        AccountSessionProof proof = await registry.IssueAsync(user.Id, new string('a', 64), user.SecurityStamp!, host.Clock.GetUtcNow().AddHours(1));
        await using NpgsqlConnection owner = new(host.OwnerConnection);
        await owner.OpenAsync();
        await using NpgsqlTransaction transaction = await owner.BeginTransactionAsync();
        await using (NpgsqlCommand revoke = new("UPDATE identity.account_sessions SET \"RevokedAt\" = @now WHERE \"SessionHash\" = @hash", owner, transaction))
        {
            revoke.Parameters.AddWithValue("now", host.Clock.GetUtcNow());
            revoke.Parameters.AddWithValue("hash", proof.SessionHash);
            (await revoke.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        Task<AccountSessionState?> pending = binding
            ? registry.BindWorkspaceAsync(proof, Guid.NewGuid(), deadline.Token)
            : registry.RenewAsync(proof, host.Clock.GetUtcNow().AddHours(2), deadline.Token);
        bool committed = false;
        try
        {
            await WaitForBlockedStatementAsync(host.OwnerConnection, applicationName, deadline.Token);
            await transaction.CommitAsync(deadline.Token);
            committed = true;
            (await pending).ShouldBeNull();
            (await registry.ValidateAsync(proof)).ShouldBeNull();
        }
        finally
        {
            await deadline.CancelAsync();
            if (!committed) await transaction.RollbackAsync();
            try { await pending; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    private static async Task WaitForBlockedStatementAsync(string connectionString, string applicationName, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        while (true)
        {
            await using NpgsqlCommand blocked = new("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @application AND wait_event_type = 'Lock')", connection);
            blocked.Parameters.AddWithValue("application", applicationName);
            if ((bool)(await blocked.ExecuteScalarAsync(cancellationToken))!) return;
            await Task.Delay(25, cancellationToken);
        }
    }
}

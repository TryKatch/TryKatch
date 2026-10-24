using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Identity;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class IdentitySessionRegistryTests
{
    [TestMethod]
    public async Task IssuingReplacementRevokesOnlyThatBrowserSession()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);

        AccountSessionProof browser = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));
        AccountSessionProof otherDevice = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));
        AccountSessionProof replacement = await registry.IssueAsync(actorId, NewHash(), stamp,
            host.Clock.GetUtcNow().AddHours(2), browser);

        (await registry.ValidateAsync(browser)).ShouldBeNull();
        (await registry.ValidateAsync(otherDevice)).ShouldNotBeNull();
        (await registry.ValidateAsync(replacement)).ShouldNotBeNull();
        (await ReadSessionAsync(host, browser.SessionHash)).RevokedAt.ShouldNotBeNull();
        (await ReadSessionAsync(host, otherDevice.SessionHash)).RevokedAt.ShouldBeNull();
        (await ReadSessionAsync(host, replacement.SessionHash)).RevokedAt.ShouldBeNull();
    }

    [TestMethod]
    public async Task WorkspaceBindingRotatesGenerationForEverySelectionAndClear()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        AccountSessionProof proof = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));
        Guid organizationA = Guid.NewGuid();
        Guid organizationB = Guid.NewGuid();

        AccountSessionState firstA = (await registry.BindWorkspaceAsync(proof, organizationA))!;
        AccountSessionState boundB = (await registry.BindWorkspaceAsync(proof, organizationB))!;
        AccountSessionState secondA = (await registry.BindWorkspaceAsync(proof, organizationA))!;
        AccountSessionState reselectedA = (await registry.BindWorkspaceAsync(proof, organizationA))!;
        AccountSessionState cleared = (await registry.BindWorkspaceAsync(proof, null))!;

        firstA.OrganizationId.ShouldBe(organizationA);
        boundB.OrganizationId.ShouldBe(organizationB);
        secondA.OrganizationId.ShouldBe(organizationA);
        reselectedA.OrganizationId.ShouldBe(organizationA);
        cleared.OrganizationId.ShouldBeNull();
        new[] { firstA.WorkspaceGeneration, boundB.WorkspaceGeneration, secondA.WorkspaceGeneration,
            reselectedA.WorkspaceGeneration, cleared.WorkspaceGeneration }.Distinct().Count().ShouldBe(5);
    }

    [TestMethod]
    public async Task BindingRejectsEmptyOrganizationId()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        AccountSessionProof proof = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));

        (await registry.BindWorkspaceAsync(proof, Guid.Empty)).ShouldBeNull();
        (await ReadSessionAsync(host, proof.SessionHash)).OrganizationId.ShouldBeNull();
    }

    [TestMethod]
    public async Task InvalidDurableSessionsCannotValidateRenewOrBind()
    {
        await AssertInvalidSessionAsync("missing");
        await AssertInvalidSessionAsync("expired-row");
        await AssertInvalidSessionAsync("revoked");
        await AssertInvalidSessionAsync("changed-stamp");
    }

    [TestMethod]
    public async Task RenewedDurableExpiryDoesNotExtendCapturedTicket()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        AccountSessionProof proof = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddMinutes(10));
        DateTimeOffset renewedExpiry = host.Clock.GetUtcNow().AddHours(2);

        AccountSessionState renewed = (await registry.RenewAsync(proof, renewedExpiry))!;
        AssertTimestampNear(renewed.ExpiresAt, renewedExpiry);
        host.Clock.Advance(TimeSpan.FromMinutes(11));

        (await registry.ValidateAsync(proof)).ShouldBeNull();
        (await registry.RenewAsync(proof, host.Clock.GetUtcNow().AddHours(3))).ShouldBeNull();
        AssertTimestampNear((await ReadSessionAsync(host, proof.SessionHash)).ExpiresAt, renewedExpiry);
    }

    [TestMethod]
    public async Task ShorterRenewalNeverShrinksDurableExpiry()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        AccountSessionProof proof = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));
        DateTimeOffset longer = host.Clock.GetUtcNow().AddHours(3);
        DateTimeOffset shorter = host.Clock.GetUtcNow().AddHours(1);

        AssertTimestampNear((await registry.RenewAsync(proof, longer))!.ExpiresAt, longer);
        AssertTimestampNear((await registry.RenewAsync(proof, shorter))!.ExpiresAt, longer);
        AssertTimestampNear((await ReadSessionAsync(host, proof.SessionHash)).ExpiresAt, longer);
    }

    [TestMethod]
    public async Task ValidationThrottlesLastSeenUpdatesToOneMinute()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        AccountSessionProof proof = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));
        DateTimeOffset initial = (await ReadSessionAsync(host, proof.SessionHash)).LastSeenAt;

        host.Clock.Advance(TimeSpan.FromSeconds(59));
        (await registry.ValidateAsync(proof)).ShouldNotBeNull();
        (await ReadSessionAsync(host, proof.SessionHash)).LastSeenAt.ShouldBe(initial);

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        (await registry.ValidateAsync(proof)).ShouldNotBeNull();
        AssertTimestampNear((await ReadSessionAsync(host, proof.SessionHash)).LastSeenAt, host.Clock.GetUtcNow());
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await registry.RenewAsync(proof, host.Clock.GetUtcNow().AddHours(3))).ShouldNotBeNull();
        DateTimeOffset renewalSeen = (await ReadSessionAsync(host, proof.SessionHash)).LastSeenAt;
        AssertTimestampNear(renewalSeen, host.Clock.GetUtcNow());
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        (await registry.RenewAsync(proof, host.Clock.GetUtcNow().AddHours(3))).ShouldNotBeNull();
        (await ReadSessionAsync(host, proof.SessionHash)).LastSeenAt.ShouldBe(renewalSeen);
    }

    [TestMethod]
    public async Task IssueRejectsMissingActorOrStaleSecurityStampWithoutCreatingRows()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        DateTimeOffset expiry = host.Clock.GetUtcNow().AddHours(2);
        string staleHash = NewHash();
        string missingHash = NewHash();
        await SetSecurityStampAsync(host, actorId, Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<InvalidOperationException>(() => registry.IssueAsync(actorId, staleHash, stamp, expiry));
        await Should.ThrowAsync<InvalidOperationException>(() => registry.IssueAsync(Guid.NewGuid(), missingHash, stamp, expiry));

        (await SessionExistsAsync(host, staleHash)).ShouldBeFalse();
        (await SessionExistsAsync(host, missingHash)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task RevokeIsIdempotentAndDoesNotRevokeAnotherDevice()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        AccountSessionProof target = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddMinutes(1));
        AccountSessionProof otherDevice = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        await SetSecurityStampAsync(host, actorId, Guid.NewGuid().ToString("N"));

        await registry.RevokeAsync(target);
        await registry.RevokeAsync(target);

        (await SessionExistsAsync(host, target.SessionHash)).ShouldBeTrue();
        (await ReadSessionAsync(host, target.SessionHash)).RevokedAt.ShouldNotBeNull();
        (await ReadSessionAsync(host, otherDevice.SessionHash)).RevokedAt.ShouldBeNull();
    }

    [TestMethod]
    public async Task RegistryReadsFreshStampWithoutSavingOtherTrackedIdentityChanges()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        AccountSessionProof proof = await IssueAsync(registry, actorId, stamp, host.Clock.GetUtcNow().AddHours(2));
        await using IdentityDbContext tracked = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(host.OwnerConnection).Options);
        ApplicationUser stale = await tracked.Users.SingleAsync(user => user.Id == actorId);
        string originalName = stale.DisplayName;
        stale.DisplayName = "Uncommitted unrelated change";
        await SetSecurityStampAsync(host, actorId, Guid.NewGuid().ToString("N"));

        (await registry.ValidateAsync(proof)).ShouldBeNull();

        (await ReadUserAsync(host, actorId)).DisplayName.ShouldBe(originalName);
    }

    [TestMethod]
    public void SessionProofAndStateToStringDoNotExposeFields()
    {
        string hash = NewHash();
        string stamp = Guid.NewGuid().ToString("N");
        AccountSessionProof proof = new(Guid.NewGuid(), hash, stamp, DateTimeOffset.UtcNow);
        AccountSessionState state = new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        proof.ToString().ShouldBe(nameof(AccountSessionProof));
        proof.ToString().ShouldNotContain(hash);
        proof.ToString().ShouldNotContain(stamp);
        state.ToString().ShouldBe(nameof(AccountSessionState));
    }

    private static async Task<AccountSessionProof> IssueAsync(IdentitySessionRegistry registry, Guid actorId, string stamp, DateTimeOffset expiresAt) =>
        await registry.IssueAsync(actorId, NewHash(), stamp, expiresAt);

    private static async Task AssertInvalidSessionAsync(string invalidation)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient _ = await host.SignInAsync();
        (Guid actorId, string stamp) = await GetActorAsync(host);
        IdentitySessionRegistry registry = CreateRegistry(host);
        DateTimeOffset longExpiry = host.Clock.GetUtcNow().AddHours(2);
        AccountSessionProof proof;

        if (invalidation == "missing")
        {
            proof = new(actorId, NewHash(), stamp, longExpiry);
        }
        else
        {
            DateTimeOffset rowExpiry = invalidation == "expired-row" ? host.Clock.GetUtcNow().AddMinutes(1) : longExpiry;
            proof = await IssueAsync(registry, actorId, stamp, rowExpiry);
            if (invalidation == "expired-row")
            {
                proof = proof with { TicketExpiresAt = longExpiry };
                host.Clock.Advance(TimeSpan.FromMinutes(2));
            }
            else if (invalidation == "revoked")
            {
                await registry.RevokeAsync(proof);
            }
            else if (invalidation == "changed-stamp")
            {
                await SetSecurityStampAsync(host, actorId, Guid.NewGuid().ToString("N"));
            }
        }

        (await registry.ValidateAsync(proof)).ShouldBeNull();
        (await registry.RenewAsync(proof, host.Clock.GetUtcNow().AddHours(4))).ShouldBeNull();
        (await registry.BindWorkspaceAsync(proof, Guid.NewGuid())).ShouldBeNull();
        if (invalidation == "missing")
        {
            (await SessionExistsAsync(host, proof.SessionHash)).ShouldBeFalse();
        }
        else
        {
            DateTimeOffset? revokedAt = (await ReadSessionAsync(host, proof.SessionHash)).RevokedAt;
            if (invalidation == "revoked") revokedAt.ShouldNotBeNull();
            else revokedAt.ShouldBeNull();
        }
    }

    private static void AssertTimestampNear(DateTimeOffset actual, DateTimeOffset expected) =>
        (Math.Abs(actual.UtcTicks - expected.UtcTicks) <= TimeSpan.TicksPerMicrosecond).ShouldBeTrue(
            $"Expected {actual:O} to be within one microsecond of {expected:O}.");

    private static IdentitySessionRegistry CreateRegistry(SecurityHost host)
    {
        NpgsqlConnectionStringBuilder connection = new(host.OwnerConnection)
        {
            Username = PostgresRuntimeRoleFixture.IdentityRole,
            Password = PostgresRuntimeRoleFixture.IdentityPassword
        };
        return new(connection.ConnectionString, host.Clock);
    }

    private static async Task<(Guid ActorId, string SecurityStamp)> GetActorAsync(SecurityHost host)
    {
        await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(host.OwnerConnection).Options);
        ApplicationUser user = await identity.Users.AsNoTracking().SingleAsync(value => value.Email == SecurityHost.Email);
        return (user.Id, user.SecurityStamp!);
    }

    private static async Task SetSecurityStampAsync(SecurityHost host, Guid actorId, string stamp)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("UPDATE identity.\"AspNetUsers\" SET \"SecurityStamp\" = @stamp WHERE \"Id\" = @actorId", connection);
        command.Parameters.AddWithValue("stamp", stamp);
        command.Parameters.AddWithValue("actorId", actorId);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private static async Task<(string DisplayName, string SecurityStamp)> ReadUserAsync(SecurityHost host, Guid actorId)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT \"DisplayName\", \"SecurityStamp\" FROM identity.\"AspNetUsers\" WHERE \"Id\" = @actorId", connection);
        command.Parameters.AddWithValue("actorId", actorId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<SessionRow> ReadSessionAsync(SecurityHost host, string sessionHash)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "OrganizationId", "WorkspaceGeneration", "ExpiresAt", "LastSeenAt", "RevokedAt"
            FROM identity.account_sessions WHERE "SessionHash" = @sessionHash
            """, connection);
        command.Parameters.AddWithValue("sessionHash", sessionHash);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        Guid? organizationId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
        DateTimeOffset? revokedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);
        return new(organizationId, reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetFieldValue<DateTimeOffset>(3), revokedAt);
    }

    private static async Task<bool> SessionExistsAsync(SecurityHost host, string sessionHash)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT EXISTS (SELECT 1 FROM identity.account_sessions WHERE \"SessionHash\" = @sessionHash)", connection);
        command.Parameters.AddWithValue("sessionHash", sessionHash);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static string NewHash() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private sealed record SessionRow(Guid? OrganizationId, Guid WorkspaceGeneration, DateTimeOffset ExpiresAt,
        DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt);
}

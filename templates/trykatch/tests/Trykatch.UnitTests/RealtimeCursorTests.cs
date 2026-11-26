using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;
using Trykatch.Application.Realtime;
using Trykatch.Infrastructure.Realtime;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class RealtimeCursorTests
{
    private static RealtimeHistoryScope Scope() => new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), Guid.NewGuid());

    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(long.MaxValue)]
    public void PositionsRoundTripInBoundedOpaqueTokens(long position)
    {
        RealtimeCursorProtector protector = new(new EphemeralDataProtectionProvider());
        RealtimeHistoryScope scope = Scope();
        string cursor = protector.Protect(scope, position);
        cursor.Length.ShouldBeLessThanOrEqualTo(2048);
        cursor.ShouldNotContain(scope.OrganizationId.ToString());
        cursor.ShouldNotContain(scope.ActorId.ToString());
        cursor.ShouldNotContain(scope.SessionHash);
        scope.ToString().ShouldNotContain(scope.SessionHash);
        protector.TryRead(cursor, scope, out long actual).ShouldBeTrue();
        actual.ShouldBe(position);
    }

    [TestMethod]
    [DataRow("organization")]
    [DataRow("actor")]
    [DataRow("session")]
    [DataRow("workspace")]
    public void ForeignScopeCannotReadACursor(string change)
    {
        RealtimeCursorProtector protector = new(new EphemeralDataProtectionProvider());
        RealtimeHistoryScope original = Scope();
        string cursor = protector.Protect(original, 42);
        RealtimeHistoryScope foreign = change switch
        {
            "organization" => original with { OrganizationId = Guid.NewGuid() },
            "actor" => original with { ActorId = Guid.NewGuid() },
            "session" => original with { SessionHash = new string('b', 64) },
            _ => original with { WorkspaceGeneration = Guid.NewGuid() }
        };
        protector.TryRead(cursor, foreign, out long position).ShouldBeFalse();
        position.ShouldBe(0L);
    }

    [TestMethod]
    public void InvalidAndTamperedInputsFailClosedWithoutARecoveredPosition()
    {
        RealtimeCursorProtector protector = new(new EphemeralDataProtectionProvider());
        RealtimeHistoryScope scope = Scope();
        string valid = protector.Protect(scope, 42);
        string tampered = (valid[0] == 'A' ? "B" : "A") + valid[1..];
        foreach (string? invalid in new[] { null, "", "null", " ", new string('A', 2049), tampered, valid + "=" })
        {
            protector.TryRead(invalid, scope, out long position).ShouldBeFalse();
            position.ShouldBe(0L);
        }
    }

    [TestMethod]
    public void WorkspaceRoundTripDoesNotReviveAnOldGeneration()
    {
        RealtimeCursorProtector protector = new(new EphemeralDataProtectionProvider());
        RealtimeHistoryScope first = Scope();
        string cursor = protector.Protect(first, 42);
        RealtimeHistoryScope backToSameOrganization = first with { WorkspaceGeneration = Guid.NewGuid() };
        protector.TryRead(cursor, backToSameOrganization, out _).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("organization")]
    [DataRow("actor")]
    [DataRow("workspace")]
    [DataRow("missing-hash")]
    [DataRow("short-hash")]
    [DataRow("invalid-hash")]
    public void InvalidHostScopeCannotIssueOrReadACursor(string change)
    {
        RealtimeCursorProtector protector = new(new EphemeralDataProtectionProvider());
        RealtimeHistoryScope valid = Scope();
        string cursor = protector.Protect(valid, 42);
        RealtimeHistoryScope invalid = change switch
        {
            "organization" => valid with { OrganizationId = Guid.Empty },
            "actor" => valid with { ActorId = Guid.Empty },
            "workspace" => valid with { WorkspaceGeneration = Guid.Empty },
            "missing-hash" => valid with { SessionHash = null! },
            "short-hash" => valid with { SessionHash = "a" },
            _ => valid with { SessionHash = new string('x', 64) }
        };
        Should.Throw<ArgumentException>(() => protector.Protect(invalid, 0));
        protector.TryRead(cursor, invalid, out long position).ShouldBeFalse();
        position.ShouldBe(0L);
    }

    [TestMethod]
    public void SharedProtectionProviderSupportsAnotherCodecButRejectsAnotherPurposeOrFormat()
    {
        EphemeralDataProtectionProvider provider = new();
        RealtimeCursorProtector first = new(provider), second = new(provider);
        RealtimeHistoryScope scope = Scope();
        second.TryRead(first.Protect(scope, 42), scope, out long position).ShouldBeTrue();
        position.ShouldBe(42L);
        byte[] incompatible = [1, 2, 3];
        string foreignPurpose = WebEncoders.Base64UrlEncode(provider.CreateProtector("OtherPurpose").Protect(incompatible));
        second.TryRead(foreignPurpose, scope, out _).ShouldBeFalse();
        string wrongFormat = WebEncoders.Base64UrlEncode(provider.CreateProtector("Trykatch.Realtime.Cursor.v1").Protect(incompatible));
        second.TryRead(wrongFormat, scope, out _).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => first.Protect(scope, -1));
    }
}

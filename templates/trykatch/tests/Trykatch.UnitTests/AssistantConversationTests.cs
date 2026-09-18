using Microsoft.AspNetCore.DataProtection;
using Shouldly;
using Trykatch.Modules.AspNetCore.Assistant;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class AssistantConversationTests
{
    private readonly TestClock _clock = new();
    private readonly AssistantConversationScope _scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "permissions-and-model-a");
    private AssistantConversationTokens Tokens() => new(new EphemeralDataProtectionProvider(), _clock);

    [TestMethod]
    public void ContinuationIsEncryptedAndRetainsOnlyCompletedText()
    {
        AssistantConversationTokens tokens = Tokens();
        string token = tokens.Continue(tokens.Read(null, _scope), "private question", "private answer");
        token.ShouldNotContain("private question");
        token.ShouldNotContain("private answer");
        tokens.Read(token, _scope).Exchanges.ShouldBe([new AssistantExchange("private question", "private answer")]);
    }

    [TestMethod]
    [DataRow("actor")]
    [DataRow("organization")]
    [DataRow("membership")]
    [DataRow("access")]
    public void ContinuationCannotCrossIdentityOrAccessScope(string changed)
    {
        AssistantConversationTokens tokens = Tokens();
        string token = tokens.Continue(tokens.Read(null, _scope), "question", "answer");
        AssistantConversationScope different = changed switch
        {
            "actor" => _scope with { ActorId = Guid.NewGuid() },
            "organization" => _scope with { OrganizationId = Guid.NewGuid() },
            "membership" => _scope with { MembershipId = Guid.NewGuid() },
            _ => _scope with { AccessFingerprint = "revoked-or-changed" }
        };
        Should.Throw<AssistantException>(() => tokens.Read(token, different)).Code.ShouldBe("invalid_conversation");
    }

    [TestMethod]
    public void ExpirationIsAbsoluteAndDoesNotSlideOnFollowUp()
    {
        AssistantConversationTokens tokens = Tokens();
        string token = tokens.Continue(tokens.Read(null, _scope), "first", "answer");
        _clock.Now = _clock.Now.AddMinutes(19);
        token = tokens.Continue(tokens.Read(token, _scope), "follow-up", "answer");
        _clock.Now = _clock.Now.AddMinutes(1);
        Should.Throw<AssistantException>(() => tokens.Read(token, _scope)).Code.ShouldBe("invalid_conversation");
    }

    [TestMethod]
    public void TamperedMalformedAndOversizedTokensAreRejected()
    {
        AssistantConversationTokens tokens = Tokens();
        byte[] bytes = Convert.FromBase64String(tokens.Continue(tokens.Read(null, _scope), "question", "answer"));
        bytes[bytes.Length / 2] ^= 1;
        foreach (string token in new[] { Convert.ToBase64String(bytes), "not-base64!", "", new string('a', AssistantConversationTokens.MaxTokenLength + 1) })
            Should.Throw<AssistantException>(() => tokens.Read(token, _scope)).Code.ShouldBe("invalid_conversation");
    }

    [TestMethod]
    public void OnlyFourRecentExchangesAreRetained()
    {
        AssistantConversationTokens tokens = Tokens();
        AssistantConversation state = tokens.Read(null, _scope);
        for (int index = 0; index < 10; index++) state = tokens.Read(tokens.Continue(state, $"question {index}", "answer"), _scope);
        state.Exchanges.Length.ShouldBe(4);
        state.Exchanges[0].Question.ShouldBe("question 6");
    }

    [TestMethod]
    public void UnicodeAtValidMessageLimitsFitsAndOldExchangesArePrunedByBytes()
    {
        AssistantConversationTokens tokens = Tokens();
        AssistantConversation state = tokens.Read(null, _scope);
        string question = new('问', 2_000);
        string answer = new('答', 8_000);
        for (int index = 0; index < 3; index++) state = tokens.Read(tokens.Continue(state, question, answer), _scope);
        state.Exchanges.Length.ShouldBe(1);
        state.Exchanges[0].Answer.ShouldBe(answer);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

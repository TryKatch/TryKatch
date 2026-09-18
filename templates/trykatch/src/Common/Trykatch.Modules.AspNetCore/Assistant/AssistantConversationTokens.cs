using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Trykatch.Modules.AspNetCore.Assistant;

public sealed record AssistantExchange(string Question, string Answer);
public sealed record AssistantConversationScope(Guid ActorId, Guid OrganizationId, Guid MembershipId, string AccessFingerprint);
public sealed record AssistantConversation(AssistantConversationScope Scope, DateTimeOffset ExpiresAt, AssistantExchange[] Exchanges);

/// <summary>Only authenticated, scope-bound, bounded text can reenter the runtime as conversation context.</summary>
public sealed class AssistantConversationTokens(IDataProtectionProvider protection, TimeProvider clock)
{
    public const int MaxTokenLength = 96_000;
    private const int MaxBytes = 64 * 1024;
    private readonly IDataProtector _protector = protection.CreateProtector("Trykatch.Assistant.Conversation.v1");

    public AssistantConversation Read(string? token, AssistantConversationScope scope)
    {
        if (token is null) return new(scope, clock.GetUtcNow().AddMinutes(20), []);
        if (token.Length is 0 or > MaxTokenLength) throw new AssistantException("invalid_conversation");
        try
        {
            byte[] bytes = _protector.Unprotect(Convert.FromBase64String(token));
            if (bytes.Length > MaxBytes) throw new AssistantException("invalid_conversation");
            AssistantConversation? state = JsonSerializer.Deserialize<AssistantConversation>(bytes);
            if (state is null || state.Scope != scope || state.ExpiresAt <= clock.GetUtcNow()
                || state.Exchanges is null || state.Exchanges.Length > 4
                || state.Exchanges.Any(exchange => exchange is null || string.IsNullOrWhiteSpace(exchange.Question)
                    || exchange.Question.Length > 2_000 || string.IsNullOrWhiteSpace(exchange.Answer) || exchange.Answer.Length > 8_000))
                throw new AssistantException("invalid_conversation");
            return state;
        }
        catch (Exception failure) when (failure is CryptographicException or FormatException or JsonException)
        {
            throw new AssistantException("invalid_conversation");
        }
    }

    public string Continue(AssistantConversation state, string question, string answer)
    {
        List<AssistantExchange> exchanges = [.. state.Exchanges, new(question.Trim(), answer)];
        byte[] bytes;
        do
        {
            if (exchanges.Count > 4) exchanges.RemoveAt(0);
            bytes = JsonSerializer.SerializeToUtf8Bytes(state with { Exchanges = exchanges.ToArray() });
            if (bytes.Length <= MaxBytes) break;
            exchanges.RemoveAt(0);
        } while (exchanges.Count > 0);
        if (exchanges.Count == 0) throw new AssistantException("context_limit");
        string token = Convert.ToBase64String(_protector.Protect(bytes));
        if (token.Length > MaxTokenLength) throw new AssistantException("context_limit");
        return token;
    }
}

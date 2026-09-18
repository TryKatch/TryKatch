using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Trykatch.Modules.AspNetCore.Assistant;

public static class AssistantProviders
{
    public static bool IsValidTimeout(int timeoutMs) => timeoutMs is >= 1_000 and <= 60_000;

    public static bool IsValid(AssistantOptions options)
    {
        if (!options.Enabled) return true;
        if (!IsValidTimeout(options.TimeoutMs)) return false;
        if (string.IsNullOrWhiteSpace(options.Model) || options.Model.Length > 120) return false;
        if (options.ReasoningEffort.Length > 0 && (options.Provider != "chat-completions"
            || options.ReasoningEffort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))) return false;
        return options.Provider switch
        {
            "openai" => !string.IsNullOrWhiteSpace(options.ApiKey) && string.IsNullOrEmpty(options.Endpoint),
            "ollama" => IsValidEndpoint(options.Endpoint),
            "chat-completions" => !string.IsNullOrWhiteSpace(options.ApiKey) && IsValidEndpoint(options.Endpoint, allowVersionPath: true),
            _ => false
        };
    }

    public static IChatClient Create(HttpClient http, IOptions<AssistantOptions> options)
    {
        if (!options.Value.Enabled) return new DisabledChatClient(http);
        if (!IsValid(options.Value)) { http.Dispose(); throw new AssistantException("invalid_provider_configuration"); }
        return options.Value.Provider switch
        {
            "openai" => new OpenAiAssistantModel(http, options),
            "ollama" => new OllamaAssistantModel(http, options),
            "chat-completions" => new ChatCompletionsAssistantModel(http, options),
            _ => throw new AssistantException("invalid_provider_configuration")
        };
    }

    // Operators select platform destinations or enroll tenant destinations for the authorized Settings UI.
    // Prompts and tool arguments never select destinations; tenant policy is stricter than this adapter.
    // Root URLs, or /v1 for compatible chat endpoints. Cleartext is loopback-only.
    private static bool IsValidEndpoint(string endpoint, bool allowVersionPath = false) => Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
        && !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && (uri.AbsolutePath == "/" || (allowVersionPath && uri.AbsolutePath is "/v1" or "/v1/"))
        && (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback));

    private sealed class DisabledChatClient(HttpClient http) : AssistantChatClient(http)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new AssistantException("assistant_disabled");
    }
}

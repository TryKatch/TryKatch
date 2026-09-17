using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Trykatch.Modules.AspNetCore.Assistant;

/// <summary>Configurable Chat Completions protocol adapter, including DeepSeek-compatible endpoints.</summary>
public sealed class ChatCompletionsAssistantModel(HttpClient http, IOptions<AssistantOptions> settings) : AssistantChatClient(http)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<object> input = [new { role = "system", content = options?.Instructions ?? AssistantProtocol.Instructions }];
        foreach (ChatMessage message in messages)
        {
            if (message.RawRepresentation is Continuation continuation) { input.Add(continuation.Message); continue; }
            FunctionCallContent[] calls = message.Contents.OfType<FunctionCallContent>().ToArray();
            if (calls.Length > 0)
                input.Add(new { role = "assistant", content = message.Text, tool_calls = calls.Select(call => new
                { id = call.CallId, type = "function", function = new { name = call.Name, arguments = AssistantProtocol.ArgumentsJson(call) } }) });
            else foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text: input.Add(new { role = message.Role.Value, content = text.Text }); break;
                    case FunctionResultContent result:
                        input.Add(new { role = "tool", tool_call_id = result.CallId, content = AssistantProtocol.ResultJson(result) }); break;
                    default: throw new AssistantException("invalid_model_response");
                }
            }
        }
        Uri endpoint = new(new Uri(settings.Value.Endpoint.TrimEnd('/') + "/"), "chat/completions");
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.ApiKey);
        Dictionary<string, object?> payload = new()
        {
            ["model"] = options?.ModelId ?? settings.Value.Model, ["stream"] = false, ["messages"] = input,
            ["max_tokens"] = Math.Min(options?.MaxOutputTokens ?? 1_024, 1_024)
        };
        if (options?.Tools is { Count: > 0 }) payload["tools"] = options.Tools.Cast<AIFunctionDeclaration>().Select(tool => new
            { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.JsonSchema } });
        // Optional and operator-owned: vendors differ on reasoning controls. Never forward arbitrary options.
        if (settings.Value.ReasoningEffort.Length > 0) payload["reasoning_effort"] = settings.Value.ReasoningEffort;
        using JsonDocument document = await SendAsync(request, payload, cancellationToken);
        try
        {
            JsonElement choices = document.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() != 1) throw new AssistantException("invalid_model_response");
            JsonElement choice = choices[0];
            string? finish = choice.GetProperty("finish_reason").GetString();
            if (finish == "length") throw new AssistantException("response_limit");
            if (finish is not ("stop" or "tool_calls")) throw new AssistantException("invalid_model_response");
            JsonElement message = choice.GetProperty("message");
            if (message.GetProperty("role").GetString() != "assistant") throw new AssistantException("invalid_model_response");
            if (message.TryGetProperty("refusal", out JsonElement refusal) && refusal.ValueKind != JsonValueKind.Null
                && !string.IsNullOrEmpty(refusal.GetString())) throw new AssistantException("invalid_model_response");
            List<AIContent> contents = [new TextContent(message.GetProperty("content").GetString())];
            Dictionary<string, object?> retained = new() { ["role"] = "assistant", ["content"] = message.GetProperty("content").Clone() };
            if (message.TryGetProperty("reasoning_content", out JsonElement reasoning))
            {
                _ = reasoning.GetString(); // Validate the optional private continuation; never expose it as answer text.
                retained["reasoning_content"] = reasoning.Clone();
            }
            int callCount = 0;
            if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
            {
                retained["tool_calls"] = toolCalls.Clone();
                foreach (JsonElement call in toolCalls.EnumerateArray())
                {
                    if (call.GetProperty("type").GetString() != "function") throw new AssistantException("invalid_model_response");
                    JsonElement function = call.GetProperty("function");
                    contents.Add(AssistantProtocol.Call(call.GetProperty("id").GetString()!,
                        function.GetProperty("name").GetString()!, function.GetProperty("arguments").GetString()!));
                    callCount++;
                }
            }
            if ((finish == "tool_calls") != (callCount > 0)) throw new AssistantException("invalid_model_response");
            return new(new ChatMessage(ChatRole.Assistant, contents) { RawRepresentation = new Continuation(retained) });
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new AssistantException("invalid_model_response"); }
    }

    private sealed record Continuation(Dictionary<string, object?> Message);
}

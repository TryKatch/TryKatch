using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Trykatch.Modules.AspNetCore.Assistant;

/// <summary>OpenAI-specific Responses translation stays behind the provider-neutral IChatClient seam.</summary>
public sealed class OpenAiAssistantModel(HttpClient http, IOptions<AssistantOptions> settings) : AssistantChatClient(http)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<object> input = [];
        foreach (ChatMessage message in messages)
        {
            if (message.RawRepresentation is Continuation continuation) { input.AddRange(continuation.Output.Cast<object>()); continue; }
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        input.Add(new { role = message.Role.Value, content = text.Text }); break;
                    case FunctionCallContent call:
                        input.Add(new { type = "function_call", call_id = call.CallId, name = call.Name, arguments = AssistantProtocol.ArgumentsJson(call) }); break;
                    case FunctionResultContent result:
                        input.Add(new { type = "function_call_output", call_id = result.CallId, output = AssistantProtocol.ResultJson(result) }); break;
                    default: throw new AssistantException("invalid_model_response");
                }
            }
        }
        using HttpRequestMessage request = new(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.ApiKey);
        using JsonDocument document = await SendAsync(request, new
        {
            model = options?.ModelId ?? settings.Value.Model, store = false,
            instructions = options?.Instructions ?? AssistantProtocol.Instructions, input,
            tools = (options?.Tools ?? []).Cast<AIFunctionDeclaration>().Select(tool => new
            { type = "function", name = tool.Name, description = tool.Description, strict = true, parameters = tool.JsonSchema }),
            parallel_tool_calls = false, max_output_tokens = Math.Min(options?.MaxOutputTokens ?? 1_024, 1_024)
        }, cancellationToken);
        try
        {
            JsonElement root = document.RootElement;
            if (root.GetProperty("status").GetString() == "incomplete"
                && root.TryGetProperty("incomplete_details", out JsonElement details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reason", out JsonElement reason)
                && reason.GetString() == "max_output_tokens")
                throw new AssistantException("response_limit");
            if (root.GetProperty("status").GetString() != "completed") throw new AssistantException("invalid_model_response");
            JsonElement[] output = root.GetProperty("output").EnumerateArray().Select(item => item.Clone()).ToArray();
            List<AIContent> contents = [];
            foreach (JsonElement item in output)
            {
                string? type = item.GetProperty("type").GetString();
                if (type == "function_call") contents.Add(AssistantProtocol.Call(
                    item.GetProperty("call_id").GetString()!, item.GetProperty("name").GetString()!, item.GetProperty("arguments").GetString()!));
                else if (type == "message")
                {
                    if (item.TryGetProperty("role", out JsonElement role) && role.GetString() != "assistant")
                        throw new AssistantException("invalid_model_response");
                    contents.AddRange(item.GetProperty("content").EnumerateArray()
                        .Where(part => part.GetProperty("type").GetString() == "output_text")
                        .Select(part => new TextContent(part.GetProperty("text").GetString())));
                }
            }
            // Preserve reasoning/opaque provider items without teaching the runtime their wire format.
            return new(new ChatMessage(ChatRole.Assistant, contents) { RawRepresentation = new Continuation(output) });
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new AssistantException("invalid_model_response"); }
    }

    private sealed record Continuation(JsonElement[] Output);
}

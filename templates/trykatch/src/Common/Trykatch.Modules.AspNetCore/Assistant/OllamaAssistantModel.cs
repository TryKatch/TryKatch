using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Trykatch.Modules.AspNetCore.Assistant;

/// <summary>Native Ollama chat adapter: no OpenAI protocol, SDK, or mandatory cloud key.</summary>
public sealed class OllamaAssistantModel(HttpClient http, IOptions<AssistantOptions> settings) : AssistantChatClient(http)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage[] history = messages.ToArray();
        Dictionary<string, string> names = history.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .ToDictionary(call => call.CallId, call => call.Name, StringComparer.Ordinal);
        List<object> input = [new { role = "system", content = options?.Instructions ?? AssistantProtocol.Instructions }];
        foreach (ChatMessage message in history)
        {
            if (message.RawRepresentation is Continuation continuation) { input.Add(continuation.Message); continue; }
            FunctionCallContent[] calls = message.Contents.OfType<FunctionCallContent>().ToArray();
            if (calls.Length > 0)
                input.Add(new { role = "assistant", content = message.Text, tool_calls = calls.Select(call => new
                { function = new { name = call.Name, arguments = JsonSerializer.Deserialize<JsonElement>(AssistantProtocol.ArgumentsJson(call)) } }) });
            else foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text: input.Add(new { role = message.Role.Value, content = text.Text }); break;
                    case FunctionResultContent result:
                        input.Add(new { role = "tool", content = AssistantProtocol.ResultJson(result), tool_name = names[result.CallId] }); break;
                    default: throw new AssistantException("invalid_model_response");
                }
            }
        }
        Uri endpoint = new(new Uri(settings.Value.Endpoint.TrimEnd('/') + "/"), "api/chat");
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(settings.Value.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Value.ApiKey);
        using JsonDocument document = await SendAsync(request, new
        {
            model = options?.ModelId ?? settings.Value.Model, stream = false, messages = input,
            tools = (options?.Tools ?? []).Cast<AIFunctionDeclaration>().Select(tool => new
            { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.JsonSchema } }),
            options = new { num_predict = Math.Min(options?.MaxOutputTokens ?? 1_024, 1_024) }
        }, cancellationToken);
        try
        {
            JsonElement root = document.RootElement;
            if (!root.GetProperty("done").GetBoolean()) throw new AssistantException("invalid_model_response");
            if (root.TryGetProperty("done_reason", out JsonElement reason) && reason.GetString() == "length")
                throw new AssistantException("response_limit");
            JsonElement message = root.GetProperty("message");
            if (message.GetProperty("role").GetString() != "assistant") throw new AssistantException("invalid_model_response");
            List<AIContent> contents = [new TextContent(message.GetProperty("content").GetString())];
            if (message.TryGetProperty("tool_calls", out JsonElement toolCalls))
                foreach (JsonElement call in toolCalls.EnumerateArray())
                {
                    JsonElement function = call.GetProperty("function");
                    contents.Add(AssistantProtocol.Call(Guid.NewGuid().ToString("N"),
                        function.GetProperty("name").GetString()!, function.GetProperty("arguments").GetRawText()));
                }
            return new(new ChatMessage(ChatRole.Assistant, contents) { RawRepresentation = new Continuation(message.Clone()) });
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new AssistantException("invalid_model_response"); }
    }

    private sealed record Continuation(JsonElement Message);
}

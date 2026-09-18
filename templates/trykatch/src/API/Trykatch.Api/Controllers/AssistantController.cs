using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Trykatch.Api.Security;
using Trykatch.Application.Organizations;
using Trykatch.Modules.AspNetCore.Assistant;

namespace Trykatch.Api.Controllers;

[ApiController]
[Authorize]
[OrganizationScoped]
[Route("api/v1/assistant")]
public sealed class AssistantController(AssistantRuntime runtime, AssistantConversationTokens conversations, AssistantKnowledge knowledge, IOrganizationContext organization,
    IOptions<AssistantOptions> options, ILogger<AssistantController> logger) : ControllerBase
{
    [HttpGet("status", Name = "Assistant_Status")]
    public async Task<ActionResult<AssistantStatus>> Status(CancellationToken cancellationToken)
    {
        bool enabled = await runtime.IsEnabledAsync(cancellationToken);
        return Ok(new AssistantStatus(enabled, true,
            enabled ? (await runtime.AvailableToolsAsync(cancellationToken)).Select(tool => tool.Name).ToArray() : [],
            enabled && runtime.HelpAvailable));
    }

    [HttpGet("guides", Name = "Assistant_Guides")]
    public ActionResult<AssistantGuideSource[]> Guides() => Ok(knowledge.List());

    [HttpGet("guides/{id}", Name = "Assistant_Guide")]
    public ActionResult<AssistantGuide> Guide(string id) => knowledge.Get(id) is { } guide ? Ok(guide) : NotFound();

    [CookieAntiforgery]
    [EnableRateLimiting("assistant")]
    [RequestSizeLimit(131_072)]
    [HttpPost("ask", Name = "Assistant_Ask")]
    public async Task<ActionResult<AssistantAnswer>> Ask(AssistantRequest request, CancellationToken cancellationToken)
    {
        try
        {
            string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                permissions = organization.Permissions.Order(StringComparer.Ordinal).ToArray(),
                tools = (await runtime.AvailableToolsAsync(cancellationToken)).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(),
                options.Value.Provider, options.Value.Model, options.Value.Endpoint, options.Value.ReasoningEffort, runtime.KnowledgeRevision,
                activationVersion = await runtime.ActivationVersionAsync(cancellationToken)
            })));
            AssistantConversation state = conversations.Read(request.ConversationToken,
                new(organization.ActorId, organization.OrganizationId, organization.MembershipId, fingerprint));
            AssistantAnswer answer = await runtime.AskAsync(request.Message, state.Exchanges, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Ok(answer with { ConversationToken = conversations.Continue(state, request.Message, answer.Answer) });
        }
        catch (AssistantException failure)
        {
            int status = failure.Code switch
            {
                "assistant_disabled" => 503,
                "no_authorized_tools" or "tool_forbidden" => 403,
                "invalid_message" => 400,
                "invalid_conversation" => 409,
                "assistant_timeout" => 504,
                "response_limit" => 502,
                _ => 502
            };
            return Problem(statusCode: status, title: failure.Code, detail: "The assistant could not complete this request. No changes were made.");
        }
        catch (HttpRequestException)
        {
            return Problem(statusCode: 502, title: "provider_unavailable", detail: "The assistant provider is unavailable. No changes were made.");
        }
        catch (UnauthorizedAccessException)
        {
            return Problem(statusCode: 403, title: "tool_forbidden", detail: "The requested information is not available to this membership.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never log prompts, arguments, retrieved records, provider bodies or API keys.
            LogAssistantFailure(logger, null);
            return Problem(statusCode: 500, title: "assistant_failed", detail: "The assistant could not complete this request. No changes were made.");
        }
    }

    private static readonly Action<ILogger, Exception?> LogAssistantFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(7301, "AssistantFailed"), "Assistant request failed; sensitive request and provider data omitted.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssistantRequest([Required, StringLength(2_000, MinimumLength = 1)] string Message,
    [StringLength(AssistantConversationTokens.MaxTokenLength)] string? ConversationToken = null);
public sealed record AssistantStatus(bool Enabled, bool ReadOnly, string[] Tools, bool HelpAvailable = false);

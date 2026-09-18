using System.Text.Json.Serialization;
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
[Route("api/v1/organization-settings/ai")]
public sealed class OrganizationSettingsController(OrganizationAssistantSettings settings, IOptions<AssistantOptions> provider,
    IOrganizationAssistantProviderPolicy policy, IOrganizationAssistantConnectionProbe probe) : ControllerBase
{
    [HttpGet(Name = "OrganizationSettings_AiGet")]
    public async Task<ActionResult<OrganizationAiConfiguration>> Get(CancellationToken cancellationToken)
    {
        var result = await settings.GetAsync(cancellationToken);
        return result.IsSuccess ? Ok(Configuration(result.Value!)) : Problem(statusCode: 403, title: result.ErrorCode, detail: result.ErrorMessage);
    }

    [HttpPut(Name = "OrganizationSettings_AiUpdate")]
    [CookieAntiforgery]
    public async Task<ActionResult<OrganizationAiConfiguration>> Update(UpdateOrganizationAiConfiguration request, CancellationToken cancellationToken)
    {
        var result = await settings.SaveAsync(new(request.Enabled, request.ExpectedVersion, request.Provider, request.Model,
            request.Endpoint, request.ApiKey, request.RemoveApiKey, request.TimeoutMs), cancellationToken);
        return result.IsSuccess ? Ok(Configuration(result.Value!)) : Problem(statusCode: ErrorStatus(result.ErrorCode),
            title: result.ErrorCode, detail: result.ErrorMessage);
    }

    [HttpPost("test", Name = "OrganizationSettings_AiTest")]
    [CookieAntiforgery]
    [EnableRateLimiting("assistant")]
    public async Task<ActionResult<OrganizationAiConnectionResult>> Test(TestOrganizationAiConfiguration request, CancellationToken cancellationToken)
    {
        var result = await settings.TestConnectionAsync(request.ExpectedVersion, probe, cancellationToken);
        return result.IsSuccess ? Ok(new OrganizationAiConnectionResult(true)) : Problem(statusCode: ErrorStatus(result.ErrorCode), title: result.ErrorCode, detail: result.ErrorMessage);
    }

    private static int ErrorStatus(string? code) => code switch { "conflict" => 409, "validation" => 400, "provider_unavailable" => 502, _ => 403 };

    private OrganizationAiConfiguration Configuration(OrganizationAssistantSettingDto setting) =>
        new(setting.Enabled, setting.Version,
            setting.Provider.Length > 0 ? policy.IsValid(setting.Provider, setting.Model, setting.Endpoint, setting.HasApiKey, setting.TimeoutMs) : provider.Value.Enabled,
            setting.Provider.Length > 0 ? setting.Provider : provider.Value.Provider,
            setting.Provider.Length > 0 ? setting.Model : provider.Value.Model,
            setting.Provider.Length > 0 ? setting.Endpoint : provider.Value.Endpoint, setting.HasApiKey, setting.TimeoutMs, setting.Provider.Length > 0,
            provider.Value.AllowTenantConfiguration, policy.AllowedEndpoints);
}

public sealed record OrganizationAiConfiguration(bool Enabled, Guid Version, bool ProviderAvailable, string Provider, string Model,
    string Endpoint, bool HasApiKey, int TimeoutMs, bool UsesTenantProvider, bool CanConfigureProvider, IReadOnlyList<string> AllowedEndpoints);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateOrganizationAiConfiguration(bool Enabled, Guid ExpectedVersion, string? Provider = null, string? Model = null,
    string? Endpoint = null, string? ApiKey = null, bool RemoveApiKey = false, int? TimeoutMs = null)
{
    public override string ToString() => "UpdateOrganizationAiConfiguration { sensitive values omitted }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TestOrganizationAiConfiguration(Guid ExpectedVersion);
public sealed record OrganizationAiConnectionResult(bool Connected);

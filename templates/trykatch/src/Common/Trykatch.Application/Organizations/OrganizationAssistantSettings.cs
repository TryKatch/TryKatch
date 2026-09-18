using Trykatch.Application.Auditing;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Domain.Organizations;
using Trykatch.Modules;

namespace Trykatch.Application.Organizations;

public sealed record OrganizationAssistantSettingDto(bool Enabled, Guid Version, string Provider = "", string Model = "", string Endpoint = "", bool HasApiKey = false, int TimeoutMs = 30_000);
public sealed record SaveOrganizationAssistantSetting(bool Enabled, Guid ExpectedVersion,
    string? Provider = null, string? Model = null, string? Endpoint = null, string? ApiKey = null, bool RemoveApiKey = false, int? TimeoutMs = null)
{
    public override string ToString() => "SaveOrganizationAssistantSetting { sensitive values omitted }";
}

public interface IOrganizationAssistantKeyProtector
{
    string Protect(Guid organizationId, string key);
    string Unprotect(Guid organizationId, string protectedKey);
}

public interface IOrganizationAssistantProviderPolicy
{
    IReadOnlyList<string> AllowedEndpoints { get; }
    bool IsValid(string provider, string model, string endpoint, bool hasKey, int timeoutMs);
}

public interface IOrganizationAssistantConnectionProbe
{
    Task<bool> TestAsync(CancellationToken cancellationToken);
}

public interface IOrganizationAssistantSettingStore
{
    Task<OrganizationAssistantSetting?> FindAsync(Guid organizationId, CancellationToken cancellationToken);
    Task AddAsync(OrganizationAssistantSetting setting, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class OrganizationAssistantSettings(
    IOrganizationAssistantSettingStore store, IOrganizationContext context,
    IPermissionAuthorizer permissions, OrganizationManagementAuthorization management,
    IAuditIntentWriter audit, IOrganizationAssistantKeyProtector keys,
    IOrganizationAssistantProviderPolicy providerPolicy) : IOrganizationAssistantActivation
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        context.IsResolved && (await store.FindAsync(context.OrganizationId, cancellationToken))?.Enabled == true;

    public async Task<Guid> VersionAsync(CancellationToken cancellationToken) =>
        context.IsResolved ? (await store.FindAsync(context.OrganizationId, cancellationToken))?.Version ?? Guid.Empty : Guid.Empty;

    public async Task<Result<OrganizationAssistantSettingDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (!context.IsResolved || (!await permissions.HasPermissionAsync(Permissions.OrganizationsRead, cancellationToken)
            && !await permissions.HasPermissionAsync(Permissions.OrganizationsManage, cancellationToken)))
            return Result.Failure<OrganizationAssistantSettingDto>("forbidden", "Organization settings cannot be viewed by this membership.");
        OrganizationAssistantSetting? setting = await store.FindAsync(context.OrganizationId, cancellationToken);
        return Result.Success(ToDto(setting));
    }

    public async Task<Result<OrganizationAssistantSettingDto>> SaveAsync(SaveOrganizationAssistantSetting command, CancellationToken cancellationToken)
    {
        Result<OrganizationManagementAuthority> authority = await management.BeginAsync(Permissions.OrganizationsManage, cancellationToken);
        if (!authority.IsSuccess) return Result.Failure<OrganizationAssistantSettingDto>(authority.ErrorCode!, authority.ErrorMessage!);
        OrganizationAssistantSetting? setting = await store.FindAsync(context.OrganizationId, cancellationToken);
        if (command.ExpectedVersion != (setting?.Version ?? Guid.Empty))
            return Result.Failure<OrganizationAssistantSettingDto>("conflict", "Settings changed. Reload the current settings before saving.");
        bool configure = command.Provider is not null || command.Model is not null || command.Endpoint is not null
            || command.ApiKey is not null || command.RemoveApiKey || command.TimeoutMs is not null;
        string provider = command.Provider ?? setting?.Provider ?? "";
        string model = command.Model ?? setting?.Model ?? "";
        string endpoint = command.Endpoint ?? setting?.Endpoint ?? "";
        int timeout = command.TimeoutMs ?? setting?.TimeoutMs ?? 30_000;
        string protectedKey = setting?.ProtectedApiKey ?? "";
        if (configure)
        {
            if (command.ApiKey is not null && (string.IsNullOrWhiteSpace(command.ApiKey) || command.ApiKey.Length > 2048
                || command.ApiKey.Any(char.IsControl) || command.RemoveApiKey)) return Invalid();
            bool destinationChanged = provider != (setting?.Provider ?? "") || endpoint != (setting?.Endpoint ?? "");
            if (command.RemoveApiKey || destinationChanged) protectedKey = "";
            bool hasKey = command.ApiKey is not null || protectedKey.Length > 0;
            if (!providerPolicy.IsValid(provider, model, endpoint, hasKey || !command.Enabled, timeout)) return Invalid();
            if (command.ApiKey is not null) protectedKey = keys.Protect(context.OrganizationId, command.ApiKey);
        }
        if (command.Enabled && provider.Length > 0 && !providerPolicy.IsValid(provider, model, endpoint, protectedKey.Length > 0, timeout)) return Invalid();
        if (setting is null)
        {
            setting = OrganizationAssistantSetting.Create(context.OrganizationId, command.Enabled);
            await store.AddAsync(setting, cancellationToken);
        }
        else setting.SetEnabled(command.Enabled);
        if (configure) setting.ConfigureProvider(provider, model, endpoint, protectedKey, timeout);
        audit.Record(AuditActions.OrganizationAiUpdated, new AuditTarget("Organization", context.OrganizationId.ToString(), context.OrganizationSlug),
            new Dictionary<string, string?> { ["status"] = command.Enabled ? "Enabled" : "Disabled" });
        await store.SaveChangesAsync(cancellationToken);
        return Result.Success(ToDto(setting));
    }

    private static OrganizationAssistantSettingDto ToDto(OrganizationAssistantSetting? setting) => new(
        setting?.Enabled ?? false, setting?.Version ?? Guid.Empty, setting?.Provider ?? "", setting?.Model ?? "",
        setting?.Endpoint ?? "", setting is not null && setting.ProtectedApiKey.Length > 0, setting?.TimeoutMs ?? 30_000);
    private static Result<OrganizationAssistantSettingDto> Invalid() => Result.Failure<OrganizationAssistantSettingDto>(
        "validation", "Choose a supported provider, approved endpoint, model, valid timeout and required API key. Changing provider or endpoint requires a replacement key.");

    public async Task<Result<bool>> TestConnectionAsync(Guid expectedVersion, IOrganizationAssistantConnectionProbe probe, CancellationToken cancellationToken)
    {
        Result<OrganizationManagementAuthority> authority = await management.BeginAsync(Permissions.OrganizationsManage, cancellationToken);
        if (!authority.IsSuccess) return Result.Failure<bool>(authority.ErrorCode!, authority.ErrorMessage!);
        OrganizationAssistantSetting? setting = await store.FindAsync(context.OrganizationId, cancellationToken);
        if (setting is null || setting.Version != expectedVersion)
            return Result.Failure<bool>("conflict", "Save or reload the current settings before testing.");
        if (!providerPolicy.IsValid(setting.Provider, setting.Model, setting.Endpoint, setting.ProtectedApiKey.Length > 0, setting.TimeoutMs))
            return Result.Failure<bool>("validation", "Save a valid provider configuration and API key before testing.");
        bool connected = await probe.TestAsync(cancellationToken);
        return connected ? Result.Success(true) : Result.Failure<bool>("provider_unavailable", "The provider could not be reached or rejected the configuration. Check the endpoint, model and key.");
    }
}

namespace Trykatch.Domain.Organizations;

/// <summary>Organization opt-in and provider metadata; only protected credentials, never permission grants.</summary>
public sealed class OrganizationAssistantSetting
{
    private OrganizationAssistantSetting() { }
    public Guid OrganizationId { get; private init; }
    public bool Enabled { get; private set; }
    public Guid Version { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string Endpoint { get; private set; } = string.Empty;
    public string ProtectedApiKey { get; private set; } = string.Empty;
    public int TimeoutMs { get; private set; } = 30_000;

    public void ConfigureProvider(string provider, string model, string endpoint, string protectedApiKey, int timeoutMs)
    {
        if (provider.Length > 40 || model.Length is 0 or > 120 || endpoint.Length > 500
            || protectedApiKey.Length > 8192 || timeoutMs is < 1000 or > 60_000)
            throw new ArgumentException("Invalid provider settings.");
        Provider = provider; Model = model; Endpoint = endpoint; ProtectedApiKey = protectedApiKey; TimeoutMs = timeoutMs;
        Version = Guid.CreateVersion7();
    }

    public static OrganizationAssistantSetting Create(Guid organizationId, bool enabled)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("Organization is required.", nameof(organizationId));
        return new() { OrganizationId = organizationId, Enabled = enabled, Version = Guid.CreateVersion7() };
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Version = Guid.CreateVersion7();
    }
}

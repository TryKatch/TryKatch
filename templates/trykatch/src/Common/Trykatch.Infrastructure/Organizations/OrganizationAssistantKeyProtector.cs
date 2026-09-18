using Microsoft.AspNetCore.DataProtection;
using Trykatch.Application.Organizations;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class OrganizationAssistantKeyProtector(IDataProtectionProvider protection) : IOrganizationAssistantKeyProtector
{
    private IDataProtector For(Guid organizationId) => protection.CreateProtector("Trykatch.OrganizationAssistant.ApiKey.v1", organizationId.ToString("D"));
    public string Protect(Guid organizationId, string key) => For(organizationId).Protect(key);
    public string Unprotect(Guid organizationId, string protectedKey) => For(organizationId).Unprotect(protectedKey);
}

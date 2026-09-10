using Microsoft.AspNetCore.DataProtection;
using Trykatch.Application.Organizations;

namespace Trykatch.Infrastructure.Organizations;

internal sealed class OrganizationInvitationTokenProtector(
    IDataProtectionProvider dataProtectionProvider) : IOrganizationInvitationTokenProtector
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "Trykatch.OrganizationCreation.InvitationToken.v1");

    public string Protect(string token) => protector.Protect(token);

    public string Unprotect(string protectedToken) => protector.Unprotect(protectedToken);
}

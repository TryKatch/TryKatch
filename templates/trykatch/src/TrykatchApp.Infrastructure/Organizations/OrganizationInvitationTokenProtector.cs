using Microsoft.AspNetCore.DataProtection;
using TrykatchApp.Application.Organizations;

namespace TrykatchApp.Infrastructure.Organizations;

internal sealed class OrganizationInvitationTokenProtector(
    IDataProtectionProvider dataProtectionProvider) : IOrganizationInvitationTokenProtector
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "TrykatchApp.OrganizationCreation.InvitationToken.v1");

    public string Protect(string token) => protector.Protect(token);

    public string Unprotect(string protectedToken) => protector.Unprotect(protectedToken);
}

using FlatpackApp.Application.Common;
using FlatpackApp.Domain.Organizations;
using FluentValidation;
using System.Security.Cryptography;
using System.Text;

namespace FlatpackApp.Application.Organizations;

public sealed record CreateOrganizationCommand(string Name, string Slug, string AdministratorEmail);
public sealed record OrganizationDto(Guid Id, string Name, string Slug, bool IsActive, DateTimeOffset CreatedAt);
public sealed record CreateOrganizationResult(OrganizationDto Organization, string AdministratorEmail, string InvitationToken);

public sealed class CreateOrganizationValidator : AbstractValidator<CreateOrganizationCommand>
{
    public CreateOrganizationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(160);
        RuleFor(x => x.Slug).Must(SlugRules.IsValid).WithMessage("Use 2-63 lowercase letters, numbers, or single hyphens.");
        RuleFor(x => x.AdministratorEmail).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public sealed class CreateOrganization(
    IOrganizationDirectory directory,
    IValidator<CreateOrganizationCommand> validator)
{
    public async Task<Result<CreateOrganizationResult>> HandleAsync(CreateOrganizationCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<CreateOrganizationResult>("validation", validation.Errors[0].ErrorMessage);
        }

        if (await directory.SlugExistsAsync(command.Slug, cancellationToken))
        {
            return Result.Failure<CreateOrganizationResult>("slug_conflict", "An organization already uses this slug.");
        }

        Organization organization = Organization.Create(command.Name, command.Slug);
        await directory.AddAsync(organization, cancellationToken);
        OrganizationRoleSeeds roleSeeds = await directory.SeedRolesAsync(organization.Id, cancellationToken);
        string invitationToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Invitation invitation = Invitation.Create(
            organization.Id,
            roleSeeds.OwnerRoleId,
            command.AdministratorEmail,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(invitationToken))),
            DateTimeOffset.UtcNow.AddDays(7));
        await directory.AddInvitationAsync(invitation, cancellationToken);
        await directory.SaveChangesAsync(cancellationToken);

        OrganizationDto dto = new(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.IsActive,
            organization.CreatedAt);
        return Result.Success(new CreateOrganizationResult(dto, invitation.Email, invitationToken));
    }
}

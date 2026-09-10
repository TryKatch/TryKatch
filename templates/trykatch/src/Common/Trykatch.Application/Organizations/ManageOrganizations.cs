using Trykatch.Application.Common;
using Trykatch.Domain.Organizations;
using FluentValidation;

namespace Trykatch.Application.Organizations;

public sealed record UpdateOrganizationCommand(Guid Id, string Name);

public sealed class UpdateOrganizationValidator : AbstractValidator<UpdateOrganizationCommand>
{
    public UpdateOrganizationValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(160);
    }
}

public sealed class ManageOrganizations(
    IOrganizationDirectory directory,
    IValidator<UpdateOrganizationCommand> validator)
{
    public async Task<Result<OrganizationDto>> GetAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        Organization? organization = await directory.FindAsync(organizationId, cancellationToken);
        return organization is null
            ? Result.Failure<OrganizationDto>("not_found", "Tenant was not found.")
            : Result.Success(ToDto(organization));
    }

    public async Task<Result<OrganizationDto>> UpdateAsync(UpdateOrganizationCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<OrganizationDto>("validation", validation.Errors[0].ErrorMessage);
        }

        Organization? organization = await directory.FindAsync(command.Id, cancellationToken);
        if (organization is null)
        {
            return Result.Failure<OrganizationDto>("not_found", "Tenant was not found.");
        }

        organization.Rename(command.Name);
        await directory.SaveChangesAsync(cancellationToken);
        return Result.Success(ToDto(organization));
    }

    public async Task<Result<OrganizationDto>> SetStatusAsync(Guid organizationId, bool isActive, CancellationToken cancellationToken)
    {
        Organization? organization = await directory.FindAsync(organizationId, cancellationToken);
        if (organization is null)
        {
            return Result.Failure<OrganizationDto>("not_found", "Tenant was not found.");
        }

        if (isActive)
        {
            organization.Reactivate();
        }
        else
        {
            organization.Deactivate();
        }

        await directory.SaveChangesAsync(cancellationToken);
        return Result.Success(ToDto(organization));
    }

    private static OrganizationDto ToDto(Organization organization) =>
        new(organization.Id, organization.Name, organization.Slug, organization.IsActive, organization.CreatedAt);
}

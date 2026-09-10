using Trykatch.Application.Common;
using Trykatch.Domain.Organizations;
using FluentValidation;
using System.Security.Cryptography;
using System.Text;

namespace Trykatch.Application.Organizations;

public sealed record CreateOrganizationCommand(
    string Name,
    string Slug,
    string AdministratorEmail,
    Guid InitiatingActorId);
public sealed record OrganizationDto(Guid Id, string Name, string Slug, bool IsActive, DateTimeOffset CreatedAt);
public sealed record CreateOrganizationResult(OrganizationDto Organization, string AdministratorEmail, string InvitationToken);

public sealed class CreateOrganizationValidator : AbstractValidator<CreateOrganizationCommand>
{
    public CreateOrganizationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(160);
        RuleFor(x => x.Slug).Must(SlugRules.IsValid).WithMessage("Use 2-63 lowercase letters, numbers, or single hyphens.");
        RuleFor(x => x.AdministratorEmail).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.InitiatingActorId).NotEmpty();
    }
}

public sealed class CreateOrganization(
    IOrganizationDirectory directory,
    IOrganizationDataPlacement dataPlacement,
    IOrganizationInvitationTokenProtector tokenProtector,
    TimeProvider timeProvider,
    IValidator<CreateOrganizationCommand> validator)
{
    public async Task<Result<CreateOrganizationResult>> HandleAsync(CreateOrganizationCommand command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<CreateOrganizationResult>("validation", validation.Errors[0].ErrorMessage);
        }

        OrganizationCreationPreparationResult prepared = await directory.PrepareCreationAsync(
            command.Name,
            command.Slug,
            command.InitiatingActorId,
            command.AdministratorEmail,
            OrganizationDataPlacementKind.Shared,
            cancellationToken);
        if (!prepared.IsPrepared || prepared.Preparation is null)
            return Result.Failure<CreateOrganizationResult>(
                prepared.ErrorCode ?? "creation_conflict",
                prepared.ErrorMessage ?? "Organization creation could not be prepared.");

        Organization organization = prepared.Preparation.Organization;
        OrganizationCreationIntent intent = prepared.Preparation.Intent;
        Guid ownerRoleId = prepared.Preparation.OwnerRoleId;

        OrganizationDataPlacementResult placement = await dataPlacement.ProvisionAsync(
            new(organization.Id, OrganizationDataPlacementKind.Shared),
            cancellationToken);
        if (!placement.IsReady)
            return Result.Failure<CreateOrganizationResult>(
                placement.FailureCode ?? "provisioning_failed",
                "Organization data placement is not ready. No owner invitation was issued; retrying this request is safe.");

        string invitationToken;
        string administratorEmail;
        if (intent.IsCompleted)
        {
            invitationToken = tokenProtector.Unprotect(intent.ProtectedInvitationToken!);
            administratorEmail = intent.AdministratorEmail.ToLowerInvariant();
        }
        else
        {
            invitationToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            DateTimeOffset now = timeProvider.GetUtcNow();
            Invitation invitation = Invitation.Create(
                organization.Id,
                ownerRoleId,
                intent.AdministratorEmail,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(invitationToken))),
                now.AddDays(7));
            intent.MarkInvitationIssued(invitation.Id, tokenProtector.Protect(invitationToken), now);
            await directory.AddInvitationAsync(invitation, cancellationToken);
            await directory.SaveChangesAsync(cancellationToken);
            administratorEmail = invitation.Email;
        }

        OrganizationDto dto = new(
            organization.Id,
            organization.Name,
            organization.Slug,
            organization.IsActive,
            organization.CreatedAt);
        return Result.Success(new CreateOrganizationResult(dto, administratorEmail, invitationToken));
    }
}

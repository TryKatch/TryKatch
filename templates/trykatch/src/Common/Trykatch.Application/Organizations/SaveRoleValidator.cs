using FluentValidation;

namespace Trykatch.Application.Organizations;

public sealed class SaveRoleValidator : AbstractValidator<SaveRoleCommand>
{
    public SaveRoleValidator()
    {
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 80)
            .WithMessage("Role names must contain 1-80 characters.");
        RuleFor(command => command.Description)
            .Must(description => (description?.Trim().Length ?? 0) <= 240)
            .WithMessage("Role descriptions cannot exceed 240 characters.");
        RuleFor(command => command.Permissions)
            .Must(permissions => permissions is null || permissions.Count == permissions.Distinct(StringComparer.Ordinal).Count())
            .WithMessage("Permission grants must be unique.");
        RuleForEach(command => command.Permissions).NotEmpty()
            .WithMessage("The role contains an unknown permission.");
    }
}

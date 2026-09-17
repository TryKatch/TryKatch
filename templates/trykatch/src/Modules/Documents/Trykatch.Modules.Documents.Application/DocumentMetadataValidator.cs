using FluentValidation;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.Application;

public sealed class DocumentMetadataValidator : AbstractValidator<UpdateDocumentCommand>
{
    public DocumentMetadataValidator()
    {
        RuleFor(command => command.Title)
            .Must(title => !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= 200)
            .WithMessage("Title is required and cannot exceed 200 characters.");
        RuleFor(command => command.Description)
            .Must(description => (description?.Trim().Length ?? 0) <= 2_000)
            .WithMessage("Description cannot exceed 2,000 characters.");
        RuleFor(command => command.DocumentType)
            .Must(type => type is null || DocumentTypes.IsValid(type))
            .WithMessage("Choose a supported document type.");
    }
}

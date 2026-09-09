using TrykatchApp.Domain.Common;

namespace TrykatchApp.Domain.Organizations;

public sealed class Organization : Entity
{
    private Organization(Guid id, string name, string slug) : base(id)
    {
        Name = name;
        Slug = slug;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private Organization() : base(Guid.Empty) { }

    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private init; }

    public static Organization Create(string name, string slug)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Organization name is required.");
        }

        if (!SlugRules.IsValid(slug))
        {
            throw new DomainException("Organization slug must contain lowercase letters, numbers, and single hyphens only.");
        }

        return new Organization(Guid.CreateVersion7(), name.Trim(), slug);
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Organization name is required.");
        }

        Name = name.Trim();
    }

    public void Deactivate() => IsActive = false;
    public void Reactivate() => IsActive = true;
}


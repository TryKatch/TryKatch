using Microsoft.AspNetCore.Identity;

namespace TrykatchApp.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsPlatformAdministrator { get; set; }
    public bool IsPlatformAccessSuspended { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSignedInAt { get; set; }
}

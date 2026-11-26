namespace Trykatch.Application.Mailing;

/// <summary>Renders the canonical password-reset intent without owning recipient selection.</summary>
public interface IPasswordResetEmailRenderer
{
    EmailPreparedMessage Render(PasswordResetEmailIntent intent);
}

using TrykatchApp.Domain.Common;

namespace TrykatchApp.Application.Common;

public sealed record RecordLifecycleDto(
    string Status,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    string? DeletionReason);

public sealed record DeleteRecordCommand(string Reason);

public enum RecordLifecycleFilter
{
    Active,
    Archived,
    Deleted,
    Recoverable,
    All
}

public static class RecordLifecycle
{
    public static RecordLifecycleDto ToDto(RecoverableEntity entity) => new(
        entity.LifecycleState.ToString(),
        entity.ArchivedAt,
        entity.DeletedAt,
        entity.DeletionReason);

    public static bool TryParseFilter(string? value, out RecordLifecycleFilter filter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            filter = RecordLifecycleFilter.Active;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out filter);
    }

    public static string? ValidateDeletionReason(string? reason) =>
        reason?.Trim().Length is >= 10 and <= 500
            ? null
            : "A deletion reason containing 10-500 characters is required.";
}

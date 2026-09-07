namespace FlatpackApp.Application.Common;

public sealed record Result<T>(T? Value, bool IsSuccess, string? ErrorCode = null, string? ErrorMessage = null);

public static class Result
{
    public static Result<T> Success<T>(T value) => new(value, true);
    public static Result<T> Failure<T>(string code, string message) => new(default, false, code, message);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);

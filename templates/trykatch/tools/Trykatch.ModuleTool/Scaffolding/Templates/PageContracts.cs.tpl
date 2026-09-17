using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Application;

public sealed record __ENTITY__Page<T>(T[] Items, int Page, int PageSize, bool HasMore);

public sealed record __ENTITY__PageQuery(string Lifecycle = "active", int Page = 1, int PageSize = 25,
    string? Search = null, string Sort = "newest")
{
    public __ENTITY__QueryScope Scope => Lifecycle.ToLowerInvariant() switch
    {
        "active" => __ENTITY__QueryScope.Active,
        "recoverable" => __ENTITY__QueryScope.Recoverable,
        _ => throw new ArgumentException("Lifecycle must be active or recoverable.", nameof(Lifecycle))
    };

    public void Validate()
    {
        _ = Scope;
        if (Page is < 1 or > 100000) throw new ArgumentException("Page must be between 1 and 100000.", nameof(Page));
        if (PageSize is < 1 or > 100) throw new ArgumentException("Page size must be between 1 and 100.", nameof(PageSize));
        if (Search?.Length > 200) throw new ArgumentException("Search cannot exceed 200 characters.", nameof(Search));
        if (Sort is not ("newest" or "oldest")) throw new ArgumentException("Sort must be newest or oldest.", nameof(Sort));
    }
}

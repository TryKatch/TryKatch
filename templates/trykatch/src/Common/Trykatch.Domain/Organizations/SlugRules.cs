using System.Text.RegularExpressions;

namespace Trykatch.Domain.Organizations;

public static partial class SlugRules
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public static bool IsValid(string? slug) =>
        slug is { Length: >= 2 and <= 63 } && SlugPattern().IsMatch(slug);
}


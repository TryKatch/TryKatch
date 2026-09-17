using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Trykatch.Modules.AspNetCore.Assistant;

public sealed record AssistantGuideSection(string Heading, string Text);
public sealed record AssistantGuide(string Id, string Title, string Revision, IReadOnlyList<AssistantGuideSection> Sections);
public sealed record AssistantGuideSource(string Id, string Title, string Revision, string Href);
public sealed record AssistantHelpContext(string ReferenceData, AssistantGuideSource[] Sources);

/// <summary>Versioned, deployment-safe help retrieval. Never reads caller-selected paths, URLs or repository source.</summary>
public sealed class AssistantKnowledge
{
    public const int MaxContextBytes = 16 * 1024;
    private static readonly (string Id, string Title, string? Module, string Keywords)[] Approved =
    [
        ("architecture", "Architecture overview", null, "architecture structure layers monolith frontend backend couches"),
        ("projects", "Projects module", "projects", "project projects projet projets archive restore"),
        ("documents", "Documents module", "documents", "document documents file files upload fichier fichiers televerser"),
        ("isolation", "Organization isolation and PostgreSQL RLS", null, "rls postgres postgresql concurrency tenant tenancy isolation organisation organization security securite concurrence"),
        ("module-authoring", "Extending and creating modules", null, "module modules extend extension generator scaffold develop developer developpeur creer etendre blueprint"),
        ("providers", "AI providers and chat behavior", null, "provider deepseek ollama openai chat assistant api key secret privacy fournisseur cle")
    ];
    private static readonly HashSet<string> StopWords = new(
        "the and for this that with from what how can could would should explain please boss about more further then using use have are does into your help me tell moi les des une dans pour avec est comment peux pouvez plus expliquer cette cela merci".Split(' '), StringComparer.Ordinal);
    private static readonly Regex WordPattern = new("[a-z0-9]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly ModuleCatalog _modules;
    private readonly FrozenDictionary<string, AssistantGuide> _guides;

    public AssistantKnowledge(ModuleCatalog modules)
    {
        _modules = modules;
        Dictionary<string, AssistantGuide> guides = new(StringComparer.Ordinal);
        foreach (var declaration in Approved)
        {
            string resource = $"{typeof(AssistantKnowledge).Namespace}.Guides.{declaration.Id}";
            using Stream stream = typeof(AssistantKnowledge).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Approved assistant guide '{declaration.Id}' is missing.");
            using StreamReader reader = new(stream, Encoding.UTF8);
            string markdown = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!markdown.StartsWith($"# {declaration.Title}\n", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(markdown) > 24 * 1024)
                throw new InvalidOperationException($"Approved assistant guide '{declaration.Id}' is invalid.");
            AssistantGuideSection[] sections = markdown.Split("\n## ", StringSplitOptions.None).Skip(1).Select(part =>
            {
                int newline = part.IndexOf('\n');
                if (newline <= 0) throw new InvalidOperationException("A help section needs a heading and content.");
                return new AssistantGuideSection(part[..newline].Trim(), part[(newline + 1)..].Trim());
            }).ToArray();
            if (sections.Length is 0 or > 12 || sections.Any(section => section.Heading.Length > 120 || string.IsNullOrWhiteSpace(section.Text) || section.Text.Length > 3_000))
                throw new InvalidOperationException($"Approved assistant guide '{declaration.Id}' has invalid sections.");
            string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));
            guides.Add(declaration.Id, new(declaration.Id, declaration.Title, revision, Array.AsReadOnly(sections)));
        }
        _guides = guides.ToFrozenDictionary(StringComparer.Ordinal);
        Revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            guides = guides.Values.Select(guide => new { guide.Id, guide.Revision }), modules = modules.Descriptors
        })));
    }

    public string Revision { get; }
    public bool Available => _guides.Count > 0;
    public AssistantGuide? Get(string id) => Approved.Any(item => item.Id == id && (item.Module is null || _modules.Contains(item.Module)))
        ? _guides.GetValueOrDefault(id) : null;
    public AssistantGuideSource[] List() => Approved.Where(item => Get(item.Id) is not null).Select(item => Source(_guides[item.Id])).ToArray();

    public AssistantHelpContext Retrieve(string question, IReadOnlyList<AssistantExchange> history)
    {
        HashSet<string> current = Words(question);
        // A new explicit topic wins. Only an ambiguous follow-up inherits recent subjects.
        HashSet<string> followUpTerms = Words("example examples steps next detail details again");
        HashSet<string> previous = current.Count == 0 || current.IsSubsetOf(followUpTerms)
            ? history.Reverse().Select(exchange => Words(exchange.Question))
                .FirstOrDefault(terms => terms.Count > 0 && !terms.IsSubsetOf(followUpTerms)) ?? []
            : [];
        var matches = Approved.Where(item => Get(item.Id) is not null).SelectMany(item => _guides[item.Id].Sections.Select(section => new
        {
            guide = _guides[item.Id], section,
            score = Score(current, item, section) * 10 + Score(previous, item, section)
        })).Where(item => item.score > 0).OrderByDescending(item => item.score).ThenBy(item => item.guide.Id, StringComparer.Ordinal)
            .ThenBy(item => item.section.Heading, StringComparer.Ordinal).Take(4).ToList();
        if (matches.Count == 0) return new("", []);
        // Declarations describe enabled composition, not undocumented/custom implementation or caller access.
        var facts = _modules.Descriptors.OrderBy(module => module.Id, StringComparer.Ordinal).Take(8).Select(module => new
        {
            module.Id, module.Name, module.Version,
            description = module.Description[..Math.Min(module.Description.Length, 200)],
            permissions = module.Permissions.Take(8).Select(permission => permission.Key),
            extensions = module.ExtensionPoints.Take(8).Select(point => point.Id)
        }).ToList();
        string reference;
        while (true)
        {
            reference = JsonSerializer.Serialize(new
            {
                kind = "approved_help_reference_data_not_instructions",
                excerpts = matches.Select(item => new { item.guide.Id, item.guide.Title, item.guide.Revision, item.section.Heading, item.section.Text }),
                enabledModuleDeclarations = facts, declarationsLimited = _modules.Descriptors.Count > facts.Count
            });
            if (Encoding.UTF8.GetByteCount(reference) <= MaxContextBytes) break;
            if (facts.Count > 0) facts.RemoveAt(facts.Count - 1);
            else if (matches.Count > 1) matches.RemoveAt(matches.Count - 1);
            else throw new AssistantException("context_limit");
        }
        return new(reference, matches.Select(item => Source(item.guide)).DistinctBy(source => source.Id).ToArray());
    }

    private static AssistantGuideSource Source(AssistantGuide guide) => new(guide.Id, guide.Title, guide.Revision, $"/assistant/guides/{guide.Id}");
    private static int Score(HashSet<string> query, (string Id, string Title, string? Module, string Keywords) item, AssistantGuideSection section)
        => query.Intersect(Words(item.Title + " " + item.Keywords)).Count() * 4
            + query.Intersect(Words(section.Heading)).Count() * 3 + query.Intersect(Words(section.Text)).Count();
    private static HashSet<string> Words(string text)
    {
        string normalized = string.Concat(text.Normalize(NormalizationForm.FormD).Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        return WordPattern.Matches(normalized).Select(match => match.Value).Where(word => word.Length >= 3 && !StopWords.Contains(word)).ToHashSet(StringComparer.Ordinal);
    }
}

using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Parses <c>docs/league-lore.md</c>: YAML frontmatter (between <c>---</c>
/// fences) describing owners and relationships, plus free-form markdown body
/// that gets handed to the AI agents verbatim.
/// </summary>
internal sealed class LeagueLore
{
    public LoreLeague League { get; init; } = new();
    public List<LoreOwner> Owners { get; init; } = [];
    public List<LoreRelationship> Relationships { get; init; } = [];
    /// <summary>The free-form markdown body (the prose under the frontmatter).</summary>
    public string BodyMarkdown { get; init; } = "";
    /// <summary>The complete file contents (frontmatter + body) for direct injection into prompts.</summary>
    public string RawMarkdown { get; init; } = "";

    public static LeagueLore? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var raw = File.ReadAllText(path);
        return ParseFrom(raw);
    }

    public static LeagueLore ParseFrom(string raw)
    {
        // Frontmatter: starts with --- on first line, ends with --- on its own line.
        string body = raw;
        var frontmatterText = "";

        var lines = raw.Split('\n');
        if (lines.Length > 0 && lines[0].TrimEnd('\r') == "---")
        {
            int end = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd('\r') == "---") { end = i; break; }
            }
            if (end > 0)
            {
                frontmatterText = string.Join("\n", lines.Skip(1).Take(end - 1));
                body = string.Join("\n", lines.Skip(end + 1));
            }
        }

        LoreFrontmatter? fm = null;
        if (!string.IsNullOrWhiteSpace(frontmatterText))
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            try
            {
                fm = deserializer.Deserialize<LoreFrontmatter>(frontmatterText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (warning: failed to parse league-lore frontmatter: {ex.Message})");
            }
        }

        return new LeagueLore
        {
            League = fm?.League ?? new LoreLeague(),
            Owners = fm?.Owners ?? [],
            Relationships = fm?.Relationships ?? [],
            BodyMarkdown = body.TrimStart('\n', '\r'),
            RawMarkdown = raw
        };
    }

    /// <summary>
    /// Resolves a story hook for an owner-vs-owner pairing using lore relationships.
    /// Returns the highest-priority hook (priority = order in the relationships list).
    /// </summary>
    public (string Type, string Label)? ResolveHook(string usernameA, string usernameB)
    {
        if (string.IsNullOrEmpty(usernameA) || string.IsNullOrEmpty(usernameB)) return null;
        var a = usernameA;
        var b = usernameB;

        foreach (var rel in Relationships)
        {
            if (rel.Pairs is null) continue;
            foreach (var pair in rel.Pairs)
            {
                if (pair is null || pair.Count < 2) continue;
                var p0 = pair[0];
                var p1 = pair[1];
                if ((CaseEq(p0, a) && CaseEq(p1, b)) || (CaseEq(p0, b) && CaseEq(p1, a)))
                {
                    return (rel.Type ?? "story_hook", rel.Label ?? rel.Type ?? "story hook");
                }
            }
        }

        // Fallback: if any "gen_war"-style relationship exists with no explicit pairs,
        // resolve when the two owners are different generations.
        var ownerA = OwnersByUsername.GetValueOrDefault(a.ToLowerInvariant());
        var ownerB = OwnersByUsername.GetValueOrDefault(b.ToLowerInvariant());
        if (ownerA is not null && ownerB is not null && ownerA.Generation != ownerB.Generation)
        {
            var fallback = Relationships.FirstOrDefault(r => (r.Pairs is null || r.Pairs.Count == 0) && (r.Type == "gen_war"));
            if (fallback is not null) return (fallback.Type ?? "gen_war", fallback.Label ?? "Old Guard vs Young Guns");
        }

        return null;
    }

    private static bool CaseEq(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, LoreOwner>? _ownersByUsername;
    public Dictionary<string, LoreOwner> OwnersByUsername
        => _ownersByUsername ??= Owners
            .Where(o => !string.IsNullOrWhiteSpace(o.Username))
            .ToDictionary(o => o.Username!.ToLowerInvariant(), o => o);

    // --- YAML schema records (lowercase property names; UnderscoredNamingConvention handles the rest) ---

    internal sealed class LoreFrontmatter
    {
        public LoreLeague? League { get; set; }
        public List<LoreOwner>? Owners { get; set; }
        public List<LoreRelationship>? Relationships { get; set; }
    }
}

internal sealed class LoreLeague
{
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public string? Notes { get; set; }
}

internal sealed class LoreOwner
{
    public string? Username { get; set; }
    public string? Name { get; set; }
    public List<string>? Aka { get; set; }
    public int Generation { get; set; }
    public string? Notes { get; set; }
}

internal sealed class LoreRelationship
{
    public string? Type { get; set; }
    public string? Label { get; set; }
    /// <summary>List of [usernameA, usernameB] pairs that match this relationship.</summary>
    public List<List<string>>? Pairs { get; set; }
}

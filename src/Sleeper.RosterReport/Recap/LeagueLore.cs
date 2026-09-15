using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Parses and merges league-lore Markdown files. YAML frontmatter describes
/// owners and relationships; free-form Markdown is handed to the AI agents.
/// Later layers override structured facts while prose accumulates.
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
    /// <summary>Files included in this lore set, in merge order.</summary>
    public List<string> Sources { get; init; } = [];

    public static LeagueLore? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var raw = File.ReadAllText(path);
        var lore = ParseFrom(raw);
        return new LeagueLore
        {
            League = lore.League,
            Owners = lore.Owners,
            Relationships = lore.Relationships,
            BodyMarkdown = lore.BodyMarkdown,
            RawMarkdown = lore.RawMarkdown,
            Sources = [path]
        };
    }

    /// <summary>
    /// Loads lore from general to specific:
    /// legacy file, league, owners, history, season, then week.
    /// Missing layers are ignored.
    /// </summary>
    public static LeagueLore? TryLoadLayers(
        string legacyPath,
        string loreDirectory,
        int season,
        int? week = null)
    {
        var candidates = new List<string>
        {
            legacyPath,
            Path.Combine(loreDirectory, "league.md"),
            Path.Combine(loreDirectory, "owners.md"),
            Path.Combine(loreDirectory, "history.md"),
            Path.Combine(loreDirectory, "seasons", $"{season}.md")
        };

        if (week is not null)
            candidates.Add(Path.Combine(loreDirectory, "weeks", $"{season}-{week.Value:00}.md"));

        var layers = candidates
            .Where(File.Exists)
            .Select(path => (Path: path, Lore: ParseFrom(File.ReadAllText(path))))
            .ToList();

        if (layers.Count == 0)
            return null;

        return Merge(layers, loreDirectory);
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

    private static LeagueLore Merge(
        IReadOnlyList<(string Path, LeagueLore Lore)> layers,
        string loreDirectory)
    {
        var league = new LoreLeague();
        var owners = new List<LoreOwner>();
        var ownerIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var relationships = new List<LoreRelationship>();
        var relationshipIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, layer) in layers)
        {
            league = MergeLeague(league, layer.League);

            foreach (var overlay in layer.Owners.Where(o => !string.IsNullOrWhiteSpace(o.Username)))
            {
                var key = overlay.Username!;
                if (ownerIndexes.TryGetValue(key, out var index))
                    owners[index] = MergeOwner(owners[index], overlay);
                else
                {
                    ownerIndexes[key] = owners.Count;
                    owners.Add(CloneOwner(overlay));
                }
            }

            foreach (var overlay in layer.Relationships)
            {
                var key = overlay.Type;
                if (!string.IsNullOrWhiteSpace(key) && relationshipIndexes.TryGetValue(key, out var index))
                    relationships[index] = CloneRelationship(overlay);
                else
                {
                    if (!string.IsNullOrWhiteSpace(key))
                        relationshipIndexes[key] = relationships.Count;
                    relationships.Add(CloneRelationship(overlay));
                }
            }
        }

        var sourceRoot = Directory.GetParent(loreDirectory)?.FullName ?? loreDirectory;
        var raw = new StringBuilder();
        var body = new StringBuilder();
        foreach (var (path, layer) in layers)
        {
            var source = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
            raw.AppendLine($"<!-- lore-source: {source} -->");
            raw.AppendLine(layer.RawMarkdown.Trim());
            raw.AppendLine();

            if (!string.IsNullOrWhiteSpace(layer.BodyMarkdown))
            {
                body.AppendLine($"<!-- lore-source: {source} -->");
                body.AppendLine(layer.BodyMarkdown.Trim());
                body.AppendLine();
            }
        }

        return new LeagueLore
        {
            League = league,
            Owners = owners,
            Relationships = relationships,
            BodyMarkdown = body.ToString().Trim(),
            RawMarkdown = raw.ToString().Trim(),
            Sources = layers.Select(layer => layer.Path).ToList()
        };
    }

    private static LoreLeague MergeLeague(LoreLeague current, LoreLeague overlay)
        => new()
        {
            Name = overlay.Name ?? current.Name,
            Notes = Combine(current.Notes, overlay.Notes)
        };

    private static LoreOwner MergeOwner(LoreOwner current, LoreOwner overlay)
        => new()
        {
            Username = current.Username,
            Name = overlay.Name ?? current.Name,
            Aka = (current.Aka ?? [])
                .Concat(overlay.Aka ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Generation = overlay.Generation ?? current.Generation,
            Notes = Combine(current.Notes, overlay.Notes)
        };

    private static LoreOwner CloneOwner(LoreOwner owner)
        => new()
        {
            Username = owner.Username,
            Name = owner.Name,
            Aka = owner.Aka?.ToList(),
            Generation = owner.Generation,
            Notes = owner.Notes
        };

    private static LoreRelationship CloneRelationship(LoreRelationship relationship)
        => new()
        {
            Type = relationship.Type,
            Label = relationship.Label,
            Pairs = relationship.Pairs?.Select(pair => pair.ToList()).ToList()
        };

    private static string? Combine(string? current, string? overlay)
    {
        if (string.IsNullOrWhiteSpace(current)) return overlay;
        if (string.IsNullOrWhiteSpace(overlay)) return current;
        return $"{current.Trim()} {overlay.Trim()}";
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
        if (ownerA?.Generation is not null &&
            ownerB?.Generation is not null &&
            ownerA.Generation != ownerB.Generation)
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
    public string? Notes { get; set; }
}

internal sealed class LoreOwner
{
    public string? Username { get; set; }
    public string? Name { get; set; }
    public List<string>? Aka { get; set; }
    public int? Generation { get; set; }
    public string? Notes { get; set; }
}

internal sealed class LoreRelationship
{
    public string? Type { get; set; }
    public string? Label { get; set; }
    /// <summary>List of [usernameA, usernameB] pairs that match this relationship.</summary>
    public List<List<string>>? Pairs { get; set; }
}

using System.Text.RegularExpressions;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Tests;

/// <summary>
/// Hard privacy gate for anything the league publishes. Owner surnames and Sleeper
/// usernames must never reach generated markdown, exported JSON, or the site build.
/// Lore frontmatter and test fixtures are exempt: usernames there are internal join
/// keys that the envelope builder maps to first names before any rendering happens.
/// </summary>
public class PrivacyGuardTests
{
    // Sleeper usernames are the join keys. Most of them embed the surname, which is
    // exactly why none of them may ever be rendered.
    private static readonly string[] ForbiddenTokens =
    [
        "Foulkrod",
        "FoulknFootball",
        "robfoulk",
        "Dbfoulkrod",
        "mafoulk",
        "jfoulkrod",
        "asmartaleck1",
        "ebmookie",
        "Evenkeel75",
        "NOTDoda",
        "Von937"
    ];

    /// <summary>Directories holding published or generated artifacts, relative to the repo root.</summary>
    private static readonly string[] ScannedDirectories = ["recaps", "site"];

    private static readonly string[] ScannedExtensions = [".md", ".json", ".svg", ".html", ".pdf.txt"];

    public static TheoryData<string> PublishedFiles()
    {
        var data = new TheoryData<string>();
        var root = RecapPaths.WorkspaceRoot;

        foreach (var relativeDir in ScannedDirectories)
        {
            var dir = Path.Combine(root, relativeDir);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")) continue;
                if (!ScannedExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) continue;
                data.Add(Path.GetRelativePath(root, file));
            }
        }

        // An empty TheoryData makes xUnit fail the whole class, so seed a sentinel
        // that trivially passes when nothing has been generated yet.
        if (data.Count == 0) data.Add("");
        return data;
    }

    [Theory]
    [MemberData(nameof(PublishedFiles))]
    public void PublishedArtifactsContainNoIdentifyingTokens(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;

        var fullPath = Path.Combine(RecapPaths.WorkspaceRoot, relativePath);
        var text = File.ReadAllText(fullPath);

        var hits = ForbiddenTokens
            .Where(token => text.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            hits.Count == 0,
            $"{relativePath} leaks identifying token(s): {string.Join(", ", hits)}. " +
            "Published output must use first names only.");
    }

    [Fact]
    public void PublishedMarkdownContainsNoFamilyRelationshipFraming()
    {
        // Rivalries in this league are earned from results, never inherited.
        // Singular forms are included: "Rob's son" and "Jake's cousin" are exactly
        // the framing the mandate forbids. "Old Guard vs Young Guns" was the
        // generational (Gen 1 vs Gen 2) hook label, not a football descriptor.
        var framing = new Regex(
            @"\b(father|fathers|son|sons|brother|brothers|cousin|cousins|nephew|nephews|" +
            @"uncle|uncles|sibling|siblings|dad|dads|family|families|generational|" +
            @"old guard|young guns)\b",
            RegexOptions.IgnoreCase);

        var root = RecapPaths.WorkspaceRoot;
        var recapDir = Path.Combine(root, "recaps");
        if (!Directory.Exists(recapDir)) return;

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(recapDir, "*.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // "Kirk Cousins" is an NFL player, not a relationship.
            text = text.Replace("Kirk Cousins", "Kirk C.", StringComparison.OrdinalIgnoreCase);

            var hits = framing.Matches(text).Select(m => m.Value).Distinct().ToList();
            if (hits.Count > 0)
                offenders.Add($"{Path.GetRelativePath(root, file)} ({string.Join(", ", hits)})");
        }

        Assert.True(
            offenders.Count == 0,
            $"Relationship framing found in: {string.Join("; ", offenders)}");
    }

    [Fact]
    public void PublishedMarkdownContainsNoModelRefusals()
    {
        // Two committed recaps had their game bodies replaced by a model refusal.
        var root = RecapPaths.WorkspaceRoot;
        var recapDir = Path.Combine(root, "recaps");
        if (!Directory.Exists(recapDir)) return;

        var offenders = Directory
            .EnumerateFiles(recapDir, "*.md", SearchOption.AllDirectories)
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("cannot assist", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("I'm sorry, but", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("I\u2019m sorry, but", StringComparison.OrdinalIgnoreCase);
            })
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        Assert.True(offenders.Count == 0, $"Model refusal text found in: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The scanning tests skip when they find nothing, so this asserts the corpus they
    /// depend on actually exists. Without it, deleting recaps/ would make the guard pass.
    /// </summary>
    [Fact]
    public void PublishedRecapCorpusIsPresent()
    {
        var recapDir = Path.Combine(RecapPaths.WorkspaceRoot, "recaps");
        Assert.True(Directory.Exists(recapDir), $"Expected published recaps at {recapDir}.");

        var markdown = Directory.GetFiles(recapDir, "*.md", SearchOption.AllDirectories);
        Assert.True(
            markdown.Length >= 30,
            $"Expected the full 2024+2025 recap corpus; found only {markdown.Length} markdown files. " +
            "The privacy scans skip empty trees, so a missing corpus would hide real leaks.");
    }

    [Fact]
    public void PublishedArtifactsContainNoGenerationField()
    {
        // Generation encoded the Gen 1 / Gen 2 split — the data needed to reconstruct
        // forbidden generational framing. It must not survive in published sidecars.
        var recapDir = Path.Combine(RecapPaths.WorkspaceRoot, "recaps");
        if (!Directory.Exists(recapDir)) return;

        var offenders = Directory
            .EnumerateFiles(recapDir, "*.json", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("\"Generation\"", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(RecapPaths.WorkspaceRoot, f))
            .ToList();

        Assert.True(offenders.Count == 0, $"Generation field present in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OwnerRefDoesNotSerializePlatformHandles()
    {
        // Username/DisplayName are in-memory join keys only; most embed a surname.
        var owner = new OwnerRef(
            UserId: "u1",
            Username: "someusername",
            DisplayName: "somedisplayname",
            TeamName: "Team",
            RosterId: 1,
            RealName: "Rob",
            LoreNotes: null);

        var json = System.Text.Json.JsonSerializer.Serialize(owner);

        Assert.DoesNotContain("someusername", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("somedisplayname", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rob", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFamilyLoreFileIsGone()
    {
        Assert.False(
            File.Exists(RecapPaths.LegacyLorePath),
            "docs/league-lore.md defined family relationship hooks and a surname. It must stay deleted.");
    }

    [Fact]
    public void LoreResolvesTheScrubbedLeagueName()
    {
        var lore = LeagueLore.TryLoadLayers(
            RecapPaths.LegacyLorePath,
            RecapPaths.LoreDirectory,
            season: 2026);

        Assert.NotNull(lore);
        Assert.Equal("The League", lore!.League.Name);
    }

    [Fact]
    public void LoreDefinesNoRelationshipHooks()
    {
        var lore = LeagueLore.TryLoadLayers(
            RecapPaths.LegacyLorePath,
            RecapPaths.LoreDirectory,
            season: 2026);

        Assert.NotNull(lore);
        Assert.Empty(lore!.Relationships);
    }
}

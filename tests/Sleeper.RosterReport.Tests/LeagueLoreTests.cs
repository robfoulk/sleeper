using FluentAssertions;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Tests;

public class LeagueLoreTests
{
    [Fact]
    public void TryLoadLayers_MergesGeneralToSpecific_AndAccumulatesProse()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sleeper-lore-{Guid.NewGuid():N}");
        var loreDirectory = Path.Combine(root, "lore");

        try
        {
            Directory.CreateDirectory(Path.Combine(loreDirectory, "seasons"));
            Directory.CreateDirectory(Path.Combine(loreDirectory, "weeks"));

            var legacyPath = Path.Combine(root, "league-lore.md");
            File.WriteAllText(legacyPath, """
                ---
                league:
                  name: Base League
                owners:
                  - username: owner1
                    name: Original Name
                    generation: 1
                    notes: Base owner note.
                relationships:
                  - type: rivalry
                    label: Base Rivalry
                    pairs:
                      - [owner1, owner2]
                ---
                Base prose.
                """);

            File.WriteAllText(Path.Combine(loreDirectory, "owners.md"), """
                ---
                owners:
                  - username: owner1
                    aka: [One]
                    notes: Permanent owner note.
                ---
                Owner prose.
                """);

            File.WriteAllText(Path.Combine(loreDirectory, "seasons", "2026.md"), """
                ---
                league:
                  name: Current League
                owners:
                  - username: owner1
                    name: Current Name
                  - username: newcomer
                    name: New Owner
                    notes: Joined this season.
                relationships:
                  - type: rivalry
                    label: Current Rivalry
                    pairs:
                      - [owner1, newcomer]
                ---
                Season prose.
                """);

            File.WriteAllText(Path.Combine(loreDirectory, "weeks", "2026-03.md"), """
                ---
                owners:
                  - username: owner1
                    generation: 2
                    notes: Week-specific note.
                ---
                Week prose.
                """);

            var lore = LeagueLore.TryLoadLayers(legacyPath, loreDirectory, 2026, 3);

            lore.Should().NotBeNull();
            lore!.Sources.Should().HaveCount(4);
            lore.League.Name.Should().Be("Current League");
            lore.Owners.Should().HaveCount(2);

            var owner = lore.OwnersByUsername["owner1"];
            owner.Name.Should().Be("Current Name");
            owner.Generation.Should().Be(2);
            owner.Aka.Should().ContainSingle().Which.Should().Be("One");
            owner.Notes.Should().Be("Base owner note. Permanent owner note. Week-specific note.");

            lore.ResolveHook("owner1", "newcomer").Should().Be(("rivalry", "Current Rivalry"));
            lore.ResolveHook("owner1", "owner2").Should().BeNull();
            lore.RawMarkdown.Should().Contain("Base prose.")
                .And.Contain("Owner prose.")
                .And.Contain("Season prose.")
                .And.Contain("Week prose.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveHook_DoesNotInferGenerationWar_WhenGenerationIsUnknown()
    {
        var lore = LeagueLore.ParseFrom("""
            ---
            owners:
              - username: elder
                generation: 1
              - username: newcomer
            relationships:
              - type: gen_war
                label: Old Guard vs Young Guns
            ---
            """);

        lore.ResolveHook("elder", "newcomer").Should().BeNull();
    }

    [Fact]
    public void TryLoadLayers_ReturnsNull_WhenNoLayersExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sleeper-lore-{Guid.NewGuid():N}");

        LeagueLore.TryLoadLayers(
                Path.Combine(root, "league-lore.md"),
                Path.Combine(root, "lore"),
                2026,
                1)
            .Should().BeNull();
    }

    [Fact]
    public void RecapPathScope_RedirectsRecapsAndRestoresCanonicalPath()
    {
        var canonical = RecapPaths.RecapFile(2025, 1);
        var temporary = Path.Combine(Path.GetTempPath(), $"sleeper-replay-{Guid.NewGuid():N}");

        using (RecapPaths.UseRecapDirectory(temporary))
        {
            RecapPaths.RecapFile(2025, 1).Should().Be(Path.Combine(temporary, "week-01.md"));
            RecapPaths.PowerHistory(2025).Should().Be(Path.Combine(temporary, "power-history.json"));
        }

        RecapPaths.RecapFile(2025, 1).Should().Be(canonical);
    }
}

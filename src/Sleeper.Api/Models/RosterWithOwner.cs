namespace Sleeper.Api.Models;

public record RosterWithOwner(
    Roster Roster,
    string? OwnerDisplayName,
    string? OwnerUsername,
    string? TeamName
);

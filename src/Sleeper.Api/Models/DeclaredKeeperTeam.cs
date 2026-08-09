namespace Sleeper.Api.Models;

/// <summary>
/// Represents a team's declared keeper selections for the upcoming season,
/// as recorded on the Sleeper roster. The <see cref="Keepers"/> list is empty
/// when the owner has not declared any keepers yet (which is the case for
/// most of the year, until the keeper deadline).
/// </summary>
public record DeclaredKeeperTeam(
    int RosterId,
    string? OwnerId,
    string? Username,
    string? DisplayName,
    string? TeamName,
    List<Player> Keepers
);

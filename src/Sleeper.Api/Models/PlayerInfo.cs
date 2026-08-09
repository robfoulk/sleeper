namespace Sleeper.Api.Models;

public record PlayerInfo(
    Player Player,
    bool IsStarter,
    bool IsReserve
);

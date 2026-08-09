namespace Sleeper.Api.Injuries;

public sealed class InjuryStoreOptions
{
    public string DatabasePath { get; set; } = Path.Combine(".sleeper-data", "injuries.db");
}

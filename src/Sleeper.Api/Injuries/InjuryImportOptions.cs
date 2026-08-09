namespace Sleeper.Api.Injuries;

public sealed class InjuryImportOptions
{
    public string NflverseUrlTemplate { get; set; } =
        "https://github.com/nflverse/nflverse-data/releases/download/injuries/injuries_{0}.csv";
}

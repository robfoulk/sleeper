using Microsoft.Extensions.Configuration;

namespace Sleeper.RosterReport;

internal sealed record FoundryAgentSettings(
    string? ProjectEndpoint,
    string ModelDeployment,
    string? SeasonModelDeployment,
    bool AutoCreateMissingAgents,
    FoundryAgentNames Agents)
{
    public const string SectionName = "Foundry";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectEndpoint);

    public string MissingConfigurationMessage
        => $"set {SectionName}:ProjectEndpoint in user secrets, appsettings.Development.json, or environment variables; see docs/foundry-agent-configuration.md";

    public static FoundryAgentSettings FromConfiguration(IConfiguration configuration)
    {
        var foundry = configuration.GetSection(SectionName);
        var agents = foundry.GetSection("Agents");

        return new FoundryAgentSettings(
            ProjectEndpoint: First(
                foundry["ProjectEndpoint"],
                configuration["AZURE_AI_PROJECT_ENDPOINT"],
                configuration["AZURE_OPENAI_ENDPOINT"],
                configuration["FOUNDRY_PROJECT_ENDPOINT"]),
            ModelDeployment: First(
                foundry["ModelDeployment"],
                foundry["DeploymentName"],
                configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]) ?? "gpt-4o-mini",
            SeasonModelDeployment: First(
                foundry["SeasonModelDeployment"],
                agents.GetSection("SeasonAnalyst")["ModelDeployment"],
                configuration["AZURE_OPENAI_SEASON_DEPLOYMENT_NAME"]),
            AutoCreateMissingAgents: BoolOrDefault(foundry["AutoCreateMissingAgents"], defaultValue: true),
            Agents: new FoundryAgentNames(
                KeeperSecondOpinion: AgentName(agents, "KeeperSecondOpinion", "sleeper-keeper-second-opinion"),
                TeamDraftOutlook: AgentName(agents, "TeamDraftOutlook", "sleeper-team-draft-outlook"),
                WeeklyGameAnalyst: AgentName(agents, "WeeklyGameAnalyst", "sleeper-weekly-game-analyst"),
                WeeklyLeagueAnalyst: AgentName(agents, "WeeklyLeagueAnalyst", "sleeper-weekly-league-analyst"),
                SeasonAnalyst: AgentName(agents, "SeasonAnalyst", "sleeper-season-analyst")));
    }

    public string AgentNameFor(FoundryAgentRole role)
        => role switch
        {
            FoundryAgentRole.KeeperSecondOpinion => Agents.KeeperSecondOpinion,
            FoundryAgentRole.TeamDraftOutlook => Agents.TeamDraftOutlook,
            FoundryAgentRole.WeeklyGameAnalyst => Agents.WeeklyGameAnalyst,
            FoundryAgentRole.WeeklyLeagueAnalyst => Agents.WeeklyLeagueAnalyst,
            FoundryAgentRole.SeasonAnalyst => Agents.SeasonAnalyst,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };

    public string ModelDeploymentFor(FoundryAgentRole role)
        => role == FoundryAgentRole.SeasonAnalyst && !string.IsNullOrWhiteSpace(SeasonModelDeployment)
            ? SeasonModelDeployment!
            : ModelDeployment;

    public static string LabelFor(FoundryAgentRole role)
        => role switch
        {
            FoundryAgentRole.KeeperSecondOpinion => "Keeper Second Opinion",
            FoundryAgentRole.TeamDraftOutlook => "Team Draft Outlook",
            FoundryAgentRole.WeeklyGameAnalyst => "Weekly Game Analyst",
            FoundryAgentRole.WeeklyLeagueAnalyst => "Weekly League Analyst",
            FoundryAgentRole.SeasonAnalyst => "Season Analyst",
            _ => role.ToString()
        };

    public static string DescriptionFor(FoundryAgentRole role)
        => role switch
        {
            FoundryAgentRole.KeeperSecondOpinion => "Fantasy football keeper second-opinion analyst with web search grounding.",
            FoundryAgentRole.TeamDraftOutlook => "Fantasy football draft outlook analyst with web search grounding.",
            FoundryAgentRole.WeeklyGameAnalyst => "Weekly fantasy football matchup recap columnist.",
            FoundryAgentRole.WeeklyLeagueAnalyst => "Weekly fantasy football league-wide recap columnist.",
            FoundryAgentRole.SeasonAnalyst => "Fantasy football season-in-review columnist.",
            _ => "Sleeper roster report agent."
        };

    private static string AgentName(IConfigurationSection agents, string key, string fallback)
        => First(agents.GetSection(key)["Name"], agents[key]) ?? fallback;

    private static bool BoolOrDefault(string? value, bool defaultValue)
        => bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static string? First(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

internal sealed record FoundryAgentNames(
    string KeeperSecondOpinion,
    string TeamDraftOutlook,
    string WeeklyGameAnalyst,
    string WeeklyLeagueAnalyst,
    string SeasonAnalyst);

internal enum FoundryAgentRole
{
    KeeperSecondOpinion,
    TeamDraftOutlook,
    WeeklyGameAnalyst,
    WeeklyLeagueAnalyst,
    SeasonAnalyst
}

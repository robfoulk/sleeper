using Sleeper.RosterReport.Agents;
using Sleeper.RosterReport.Recap;

namespace Sleeper.RosterReport.Copilot;

/// <summary>
/// Builds the recap agents against Copilot, falling back to Foundry.
/// </summary>
/// <remarks>
/// Copilot is preferred because it needs no provisioned Foundry resource — the
/// signed-in CLI user is the credential. Foundry remains supported for anyone who
/// still has it configured. When neither is available the caller writes a
/// data-only dump rather than failing the run.
/// </remarks>
internal sealed class ReportAgentProvider : IAsyncDisposable
{
    /// <summary>Fast, cheap model used only to check drafts against the fact cards.</summary>
    private const string ProofreaderModel = "gpt-5-mini";

    private const string ProofreaderInstructions =
        "You are a strict, fast factual proofreader for a fantasy football recap newsletter. " +
        "Compare the supplied draft markdown recap against the game data and COMPUTED MATCHUP FACTS. " +
        "Check for: incorrect scores, wrong winner/loser, wrong owner or team names, mathematical mistakes, " +
        "roster misattributions, and ungrounded claims. " +
        "Return ONLY valid JSON matching: {\"Pass\": true/false, \"Defects\": [\"...\"]}. " +
        "No markdown fences or commentary outside the JSON.";

    private readonly CopilotAgentHost? _host;

    public string Backend { get; }
    public CopilotUsageLedger Usage { get; }

    private ReportAgentProvider(CopilotAgentHost? host, CopilotUsageLedger usage, string backend)
    {
        _host = host;
        Usage = usage;
        Backend = backend;
    }

    public static async Task<ReportAgentProvider> CreateAsync(
        CopilotAgentSettings copilotSettings,
        CancellationToken ct = default)
    {
        var usage = new CopilotUsageLedger();
        try
        {
            var host = await CopilotAgentHost.StartAsync(copilotSettings, usage, ct).ConfigureAwait(false);
            Console.WriteLine($"  (Copilot agents online: writer '{copilotSettings.WriterModel}', proofreader '{ProofreaderModel}')");
            return new ReportAgentProvider(host, usage, "copilot");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (Copilot unavailable: {ex.Message})");
            return new ReportAgentProvider(null, usage, "none");
        }
    }

    /// <summary>Returns null when no backend is available, so the caller can dump data instead.</summary>
    public async Task<RecapAgent?> TryCreateRecapAgentAsync(
        FoundryAgentSettings foundrySettings,
        CancellationToken ct = default)
    {
        if (_host is not null)
        {
            return RecapAgent.Create(
                _host.CreateWriter(FoundryAgentInstructions.WeeklyGameAnalyst),
                _host.CreateWriter(FoundryAgentInstructions.WeeklyLeagueAnalyst),
                "copilot",
                _host.CreateProofreader(ProofreaderInstructions, ProofreaderModel));
        }

        return await RecapAgent.TryCreateAsync(foundrySettings, ct).ConfigureAwait(false);
    }

    /// <summary>Returns null when no backend is available, so the caller can skip the prose pass.</summary>
    public async Task<SeasonAgent?> TryCreateSeasonAgentAsync(
        FoundryAgentSettings foundrySettings,
        CancellationToken ct = default)
    {
        if (_host is not null)
            return SeasonAgent.Create(_host.CreateWriter(FoundryAgentInstructions.SeasonAnalyst), "copilot");

        return await SeasonAgent.TryCreateAsync(foundrySettings, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync().ConfigureAwait(false);
    }
}

using Microsoft.Extensions.Configuration;

namespace Sleeper.RosterReport.Copilot;

internal sealed record CopilotAgentSettings(
    string WriterModel,
    string EvaluatorModel,
    string? ReasoningEffort,
    int TimeoutSeconds,
    int MaxConcurrency)
{
    public const string SectionName = "Copilot";

    public static CopilotAgentSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return new CopilotAgentSettings(
            WriterModel: section["WriterModel"] ?? "claude-sonnet-5",
            EvaluatorModel: section["EvaluatorModel"] ?? "gpt-5.6-sol",
            ReasoningEffort: section["ReasoningEffort"] ?? "medium",
            TimeoutSeconds: PositiveInt(section["TimeoutSeconds"], 180),
            MaxConcurrency: PositiveInt(section["MaxConcurrency"], 3));
    }

    private static int PositiveInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}

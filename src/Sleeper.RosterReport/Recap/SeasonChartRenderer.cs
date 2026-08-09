using System.Globalization;
using System.Text;

namespace Sleeper.RosterReport.Recap;

/// <summary>
/// Hand-emits SVG line charts for the season recap. Pure-string output so we
/// never bring a charting library into the project. Each chart carries a
/// machine-readable comment naming its data source so the SVGs can be
/// re-rendered from <c>season-aggregate.json</c> without scraping prose.
/// </summary>
internal static class SeasonChartRenderer
{
    private const int Width = 880;
    private const int Height = 440;
    private const int Pad = 64;       // chart area padding (left/top/right/bottom)
    private const int LegendH = 80;   // extra space below the chart for the legend

    /// <summary>
    /// Deterministic 8-color palette keyed off RosterId so an owner is the
    /// same color across every chart in the season recap. Hex chosen for
    /// good contrast on white background.
    /// </summary>
    private static readonly string[] Palette =
    {
        "#1f77b4", // blue
        "#d62728", // red
        "#2ca02c", // green
        "#ff7f0e", // orange
        "#9467bd", // purple
        "#8c564b", // brown
        "#17becf", // teal
        "#e377c2"  // pink
    };

    public static void WriteAll(int season, SeasonAggregate agg)
    {
        Directory.CreateDirectory(RecapPaths.SeasonChartsDir(season));
        File.WriteAllText(RecapPaths.SeasonChartFile(season, "power-rank-trajectory"),
            RenderPowerRankTrajectory(agg));
        File.WriteAllText(RecapPaths.SeasonChartFile(season, "cumulative-points-differential"),
            RenderCumulativePointsDifferential(agg));
        File.WriteAllText(RecapPaths.SeasonChartFile(season, "cumulative-record"),
            RenderCumulativeRecord(agg));
        File.WriteAllText(RecapPaths.SeasonChartFile(season, "weekly-score-rank"),
            RenderWeeklyScoreRank(agg));
    }

    // ---------------- Chart builders ----------------

    public static string RenderPowerRankTrajectory(SeasonAggregate agg)
    {
        // Y axis is rank (1 = top, N = bottom). Invert so #1 is at the top.
        int n = Math.Max(1, agg.Teams.Count);
        var series = agg.Teams
            .OrderBy(t => t.RosterId)
            .Select(t => (Team: t, Points: t.Weekly
                .Where(e => e.PowerRank is not null)
                .Select(e => ((double)e.Week, (double)e.PowerRank!.Value))
                .ToList()))
            .ToList();

        return BuildChart(
            title: $"Power-rank trajectory — {agg.Season} {agg.LeagueName}",
            xLabel: "Week",
            yLabel: "Power rank (1 = top)",
            xTicks: Range(1, agg.Schedule.ChampionshipWeek),
            yTicks: Range(1, n).Select(i => (double)i).ToList(),
            yMin: 0.5,
            yMax: n + 0.5,
            invertY: true,
            series: series,
            dataSource: "season-aggregate.json#Teams[].Weekly[].PowerRank");
    }

    public static string RenderCumulativePointsDifferential(SeasonAggregate agg)
    {
        // Running total of (PointsFor - PointsAgainst) through the regular season.
        // A line that climbs steadily from W1→W15 = a team that consistently outscored
        // its weekly opponent. A line that drifts down = the opposite. Much easier to
        // read than the zigzag of single-week scores: position on the y-axis at any
        // week IS the cumulative story to that point.
        var series = agg.Teams
            .OrderBy(t => t.RosterId)
            .Select(t => (Team: t, Points: t.Weekly
                .Where(e => e.Week <= agg.Schedule.RegularSeasonLastWeek)
                .Select(e => ((double)e.Week, (double)e.CumulativePointsDifferential))
                .ToList()))
            .ToList();

        var allY = series.SelectMany(s => s.Points.Select(p => p.Item2)).ToList();
        double rawMin = allY.Count == 0 ? -100 : allY.Min();
        double rawMax = allY.Count == 0 ? 100 : allY.Max();
        // Symmetric round to the nearest 50, then pad.
        double yMin = Math.Floor(rawMin / 50) * 50;
        double yMax = Math.Ceiling(rawMax / 50) * 50;
        if (yMax - yMin < 100) { yMin -= 50; yMax += 50; }

        return BuildChart(
            title: $"Cumulative points differential — {agg.Season} {agg.LeagueName} (regular season)",
            xLabel: "Week",
            yLabel: "Σ (Points For − Points Against)",
            xTicks: Range(1, agg.Schedule.RegularSeasonLastWeek),
            yTicks: TicksBetween(yMin, yMax, 50),
            yMin: yMin,
            yMax: yMax,
            invertY: false,
            series: series,
            dataSource: "season-aggregate.json#Teams[].Weekly[].CumulativePointsDifferential");
    }

    public static string RenderCumulativeRecord(SeasonAggregate agg)
    {
        // Plot wins minus losses through the regular season.
        var series = agg.Teams
            .OrderBy(t => t.RosterId)
            .Select(t => (Team: t, Points: t.Weekly
                .Where(e => e.Week <= agg.Schedule.RegularSeasonLastWeek)
                .Select(e => ((double)e.Week, (double)(e.CumulativeWins - e.CumulativeLosses)))
                .ToList()))
            .ToList();

        var allY = series.SelectMany(s => s.Points.Select(p => p.Item2)).ToList();
        double yMin = allY.Count == 0 ? -5 : Math.Min(-1, Math.Floor(allY.Min()));
        double yMax = allY.Count == 0 ? 5 : Math.Max(1, Math.Ceiling(allY.Max()));

        return BuildChart(
            title: $"Cumulative wins minus losses — {agg.Season} {agg.LeagueName} (regular season)",
            xLabel: "Week",
            yLabel: "Wins − Losses",
            xTicks: Range(1, agg.Schedule.RegularSeasonLastWeek),
            yTicks: TicksBetween(yMin, yMax, 1),
            yMin: yMin - 0.5,
            yMax: yMax + 0.5,
            invertY: false,
            series: series,
            dataSource: "season-aggregate.json#Teams[].Weekly[].(CumulativeWins-CumulativeLosses)");
    }

    public static string RenderWeeklyScoreRank(SeasonAggregate agg)
    {
        // Score-rank: 1 = highest scorer of the week. Inverted Y like power rank.
        int n = Math.Max(1, agg.Teams.Count);
        var series = agg.Teams
            .OrderBy(t => t.RosterId)
            .Select(t => (Team: t, Points: t.Weekly
                .Where(e => e.ScoreRank is not null)
                .Select(e => ((double)e.Week, (double)e.ScoreRank!.Value))
                .ToList()))
            .ToList();

        return BuildChart(
            title: $"Weekly score rank — {agg.Season} {agg.LeagueName}",
            xLabel: "Week",
            yLabel: "Score rank (1 = highest scorer)",
            xTicks: Range(1, agg.Schedule.ChampionshipWeek),
            yTicks: Range(1, n).Select(i => (double)i).ToList(),
            yMin: 0.5,
            yMax: n + 0.5,
            invertY: true,
            series: series,
            dataSource: "season-aggregate.json#Teams[].Weekly[].ScoreRank");
    }

    // ---------------- Generic chart builder ----------------

    private static string BuildChart(
        string title,
        string xLabel,
        string yLabel,
        IReadOnlyList<int> xTicks,
        IReadOnlyList<double> yTicks,
        double yMin,
        double yMax,
        bool invertY,
        List<(SeasonTeamSeries Team, List<(double X, double Y)> Points)> series,
        string dataSource)
    {
        double xMin = xTicks.Count == 0 ? 0 : xTicks.Min() - 0.5;
        double xMax = xTicks.Count == 0 ? 1 : xTicks.Max() + 0.5;

        int plotL = Pad;
        int plotT = Pad;
        int plotR = Width - Pad / 2;
        int plotB = Height - Pad;
        int plotW = plotR - plotL;
        int plotH = plotB - plotT;

        double XScale(double x) => plotL + (x - xMin) / (xMax - xMin) * plotW;
        double YScale(double y)
        {
            // SVG Y grows downward, so the cartesian default places larger values at the
            // top by flipping `t`. `invertY: true` is for rank charts (1 = best, render at
            // top): in that case we want the smaller value at the top, which means NOT
            // flipping `t`. The two branches below were previously swapped.
            double t = (y - yMin) / (yMax - yMin);
            if (!invertY) t = 1 - t;
            return plotT + t * plotH;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<!-- data-source: {dataSource} -->");
        sb.AppendLine($"<!-- generator: SeasonChartRenderer -->");
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Width} {Height + LegendH}\" font-family=\"-apple-system, Segoe UI, Helvetica, Arial, sans-serif\" font-size=\"12\">");
        // Background
        sb.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{Width}\" height=\"{Height + LegendH}\" fill=\"#ffffff\"/>");
        // Title
        sb.AppendLine($"  <text x=\"{Width / 2}\" y=\"22\" text-anchor=\"middle\" font-size=\"14\" font-weight=\"600\">{Escape(title)}</text>");
        // Axes
        sb.AppendLine($"  <line x1=\"{plotL}\" y1=\"{plotT}\" x2=\"{plotL}\" y2=\"{plotB}\" stroke=\"#333\" stroke-width=\"1\"/>");
        sb.AppendLine($"  <line x1=\"{plotL}\" y1=\"{plotB}\" x2=\"{plotR}\" y2=\"{plotB}\" stroke=\"#333\" stroke-width=\"1\"/>");
        // Y-axis label
        sb.AppendLine($"  <text x=\"{plotL - 48}\" y=\"{plotT + plotH / 2}\" text-anchor=\"middle\" transform=\"rotate(-90 {plotL - 48} {plotT + plotH / 2})\">{Escape(yLabel)}</text>");
        // X-axis label
        sb.AppendLine($"  <text x=\"{plotL + plotW / 2}\" y=\"{plotB + 36}\" text-anchor=\"middle\">{Escape(xLabel)}</text>");

        // Grid + tick labels (Y)
        foreach (var y in yTicks)
        {
            double py = YScale(y);
            sb.AppendLine($"  <line x1=\"{plotL}\" y1=\"{F(py)}\" x2=\"{plotR}\" y2=\"{F(py)}\" stroke=\"#eee\" stroke-width=\"1\"/>");
            sb.AppendLine($"  <text x=\"{plotL - 8}\" y=\"{F(py + 4)}\" text-anchor=\"end\" fill=\"#555\">{F(y)}</text>");
        }
        // Grid + tick labels (X)
        foreach (var x in xTicks)
        {
            double px = XScale(x);
            sb.AppendLine($"  <line x1=\"{F(px)}\" y1=\"{plotT}\" x2=\"{F(px)}\" y2=\"{plotB}\" stroke=\"#f5f5f5\" stroke-width=\"1\"/>");
            sb.AppendLine($"  <text x=\"{F(px)}\" y=\"{plotB + 16}\" text-anchor=\"middle\" fill=\"#555\">{x}</text>");
        }

        // Series — one polyline per team.
        for (int i = 0; i < series.Count; i++)
        {
            var (team, pts) = series[i];
            if (pts.Count < 2) continue;
            string color = ColorFor(team.RosterId, i);
            var poly = string.Join(' ', pts.Select(p => $"{F(XScale(p.X))},{F(YScale(p.Y))}"));
            sb.AppendLine($"  <polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"2.25\" stroke-linejoin=\"round\" stroke-linecap=\"round\" points=\"{poly}\"/>");
            // Dots at each data point.
            foreach (var (x, y) in pts)
                sb.AppendLine($"  <circle cx=\"{F(XScale(x))}\" cy=\"{F(YScale(y))}\" r=\"2.5\" fill=\"{color}\"/>");
        }

        // Legend (2 rows of 4).
        int legY = Height + 4;
        for (int i = 0; i < series.Count; i++)
        {
            int row = i / 4;
            int col = i % 4;
            int lx = 32 + col * (Width - 64) / 4;
            int ly = legY + 18 + row * 28;
            string color = ColorFor(series[i].Team.RosterId, i);
            sb.AppendLine($"  <rect x=\"{lx}\" y=\"{ly - 10}\" width=\"14\" height=\"14\" fill=\"{color}\"/>");
            string label = $"{series[i].Team.OwnerRealName} — {series[i].Team.FinalTeamName}";
            sb.AppendLine($"  <text x=\"{lx + 20}\" y=\"{ly + 2}\" fill=\"#222\">{Escape(label)}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string ColorFor(int rosterId, int fallbackIdx)
    {
        // Stable per-roster color: rosterId mod palette size, with the fallback
        // index used to break ties when rosters happen to collide on the modulus.
        int idx = ((rosterId - 1) % Palette.Length + Palette.Length) % Palette.Length;
        return Palette[idx];
    }

    private static List<int> Range(int from, int to)
    {
        var list = new List<int>();
        for (int i = from; i <= to; i++) list.Add(i);
        return list;
    }

    private static List<double> TicksBetween(double min, double max, double step)
    {
        var list = new List<double>();
        for (double v = min; v <= max + 1e-9; v += step) list.Add(Math.Round(v, 2));
        return list;
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string F(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}

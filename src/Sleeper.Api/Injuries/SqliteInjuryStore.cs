using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Sleeper.Api.Injuries;

public sealed class SqliteInjuryStore : IInjuryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public SqliteInjuryStore(IOptions<InjuryStoreOptions> options)
    {
        var path = ResolveDatabasePath(options.Value.DatabasePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task<InjuryObservation> RecordAsync(InjuryObservationInput input, CancellationToken ct = default)
    {
        var observations = await RecordBatchAsync([input], ct).ConfigureAwait(false);
        return observations.Observations[0];
    }

    public async Task<InjuryBatchWriteResult> RecordBatchAsync(
        IReadOnlyList<InjuryObservationInput> inputs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (var input in inputs)
            ValidateInput(input);

        if (inputs.Count == 0)
            return new InjuryBatchWriteResult([], 0, 0);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var observations = new List<InjuryObservation>(inputs.Count);
        var insertedCount = 0;
        foreach (var input in inputs)
        {
            var write = await RecordCoreAsync(connection, transaction, input, ct).ConfigureAwait(false);
            observations.Add(write.Observation);
            if (write.Inserted)
                insertedCount++;
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new InjuryBatchWriteResult(observations, insertedCount, inputs.Count - insertedCount);
    }

    public async Task<CurrentInjury?> GetCurrentAsync(string sleeperId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sleeper_id, status, practice_status, primary_injury, secondary_injury,
                   notes, observed_at, effective_at, resolved_at, source, source_url, confidence,
                   authority, expires_at
            FROM injury_current_sources
            WHERE sleeper_id = $sleeper_id
              AND (expires_at IS NULL OR expires_at > $now)
            ORDER BY COALESCE(effective_at, observed_at) DESC, authority DESC, observed_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sleeper_id", sleeperId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadCurrent(reader) : null;
    }

    public async Task<IReadOnlyList<InjuryObservation>> GetTimelineAsync(
        string sleeperId,
        int limit = 50,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, sleeper_id, observed_at, effective_at, source, source_url, status,
                   practice_status, primary_injury, secondary_injury, notes, confidence,
                   observation_scope, authority, expires_at, season, week, season_type
            FROM injury_observations
            WHERE sleeper_id = $sleeper_id
            ORDER BY CASE observation_scope WHEN 'current' THEN 1 ELSE 0 END DESC,
                     season DESC, week DESC,
                     COALESCE(effective_at, observed_at) DESC, observed_at DESC, id DESC
            LIMIT {limit};
            """;
        command.Parameters.AddWithValue("$sleeper_id", sleeperId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<InjuryObservation>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadObservation(reader));
        return results;
    }

    public async Task<IReadOnlyList<InjuryObservation>> GetHistoricalTimelineAsync(
        string sleeperId,
        int limit = 50,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, sleeper_id, observed_at, effective_at, source, source_url, status,
                   practice_status, primary_injury, secondary_injury, notes, confidence,
                   observation_scope, authority, expires_at, season, week, season_type
            FROM injury_observations
            WHERE sleeper_id = $sleeper_id
              AND observation_scope = 'historical'
            ORDER BY season DESC, week DESC, observed_at DESC, id DESC
            LIMIT {limit};
            """;
        command.Parameters.AddWithValue("$sleeper_id", sleeperId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<InjuryObservation>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadObservation(reader));
        return results;
    }

    public async Task<InjuryHistorySummary> GetHistorySummaryAsync(
        string sleeperId,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COUNT(DISTINCT season),
                   MIN(season),
                   MAX(season),
                   SUM(CASE WHEN lower(status) = 'out' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN lower(status) = 'doubtful' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN lower(status) = 'questionable' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN lower(practice_status) LIKE '%limited%' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN lower(practice_status) LIKE '%did not%' OR lower(practice_status) = 'dnp' THEN 1 ELSE 0 END),
                   MAX(effective_at)
            FROM injury_observations
            WHERE sleeper_id = $sleeper_id
              AND observation_scope = 'historical';
            """;
        command.Parameters.AddWithValue("$sleeper_id", sleeperId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new InjuryHistorySummary(
            sleeperId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            ReadNullableDate(reader, 9));
    }

    public async Task ReplaceCohortAsync(
        IReadOnlyList<InjuryCohortPlayer> players,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (players.Select(player => player.SleeperId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != players.Count)
            throw new ArgumentException("The injury cohort contains duplicate Sleeper player IDs.", nameof(players));

        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM injury_cohort;";
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var player in players.OrderBy(player => player.Rank))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO injury_cohort
                    (sleeper_id, rank, name, position, team, gsis_id, ranking_source, ranked_at)
                VALUES
                    ($sleeper_id, $rank, $name, $position, $team, $gsis_id, $ranking_source, $ranked_at);
                """;
            insert.Parameters.AddWithValue("$sleeper_id", player.SleeperId);
            insert.Parameters.AddWithValue("$rank", player.Rank);
            insert.Parameters.AddWithValue("$name", player.Name);
            insert.Parameters.AddWithValue("$position", player.Position);
            insert.Parameters.AddWithValue("$team", (object?)player.Team ?? DBNull.Value);
            insert.Parameters.AddWithValue("$gsis_id", (object?)player.GsisId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ranking_source", player.RankingSource);
            insert.Parameters.AddWithValue("$ranked_at", player.RankedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InjuryCohortPlayer>> GetCohortAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sleeper_id, rank, name, position, team, gsis_id, ranking_source, ranked_at
            FROM injury_cohort
            ORDER BY rank;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var players = new List<InjuryCohortPlayer>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            players.Add(new InjuryCohortPlayer(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                reader.GetString(6),
                ParseDate(reader.GetString(7))));
        }
        return players;
    }

    public Task<InjuryObservation> ResolveAsync(
        string sleeperId,
        string source,
        string? sourceUrl,
        string? notes,
        CancellationToken ct = default) =>
        RecordAsync(CreateResolutionInput(sleeperId, source, sourceUrl, notes), ct);

    private static InjuryObservationInput CreateResolutionInput(
        string sleeperId,
        string source,
        string? sourceUrl,
        string? notes)
    {
        var observedAt = DateTimeOffset.UtcNow;
        return new InjuryObservationInput(
            sleeperId,
            source,
            sourceUrl,
            "healthy",
            "full",
            null,
            null,
            notes ?? "Availability cleared.",
            "official",
            observedAt,
            observedAt,
            InjuryObservationScope.Current,
            Authority: 90,
            ExpiresAt: observedAt.AddDays(7));
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady)
            return;

        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaReady)
                return;

            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS injury_observations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sleeper_id TEXT NOT NULL,
                    observed_at TEXT NOT NULL,
                    effective_at TEXT,
                    source TEXT NOT NULL,
                    source_url TEXT,
                    status TEXT NOT NULL,
                    practice_status TEXT,
                    primary_injury TEXT,
                    secondary_injury TEXT,
                    notes TEXT,
                    confidence TEXT NOT NULL,
                    content_hash TEXT NOT NULL UNIQUE,
                    observation_scope TEXT NOT NULL DEFAULT 'current',
                    authority INTEGER NOT NULL DEFAULT 50,
                    expires_at TEXT,
                    season INTEGER,
                    week INTEGER,
                    season_type TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_injury_observations_player_time
                    ON injury_observations (sleeper_id, observed_at DESC);
                CREATE TABLE IF NOT EXISTS injury_current (
                    sleeper_id TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    practice_status TEXT,
                    primary_injury TEXT,
                    secondary_injury TEXT,
                    notes TEXT,
                    observed_at TEXT NOT NULL,
                    effective_at TEXT,
                    resolved_at TEXT,
                    source TEXT NOT NULL,
                    source_url TEXT,
                    confidence TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS injury_current_sources (
                    sleeper_id TEXT NOT NULL,
                    source TEXT NOT NULL,
                    status TEXT NOT NULL,
                    practice_status TEXT,
                    primary_injury TEXT,
                    secondary_injury TEXT,
                    notes TEXT,
                    observed_at TEXT NOT NULL,
                    effective_at TEXT,
                    resolved_at TEXT,
                    source_url TEXT,
                    confidence TEXT NOT NULL,
                    authority INTEGER NOT NULL,
                    expires_at TEXT,
                    PRIMARY KEY (sleeper_id, source)
                );
                CREATE INDEX IF NOT EXISTS ix_injury_current_sources_player_time
                    ON injury_current_sources (sleeper_id, effective_at DESC, observed_at DESC);
                CREATE TABLE IF NOT EXISTS injury_cohort (
                    sleeper_id TEXT PRIMARY KEY,
                    rank INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    position TEXT NOT NULL,
                    team TEXT,
                    gsis_id TEXT,
                    ranking_source TEXT NOT NULL,
                    ranked_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "injury_current", "effective_at", "TEXT", ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "injury_observations", "observation_scope", "TEXT NOT NULL DEFAULT 'current'", ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "injury_observations", "authority", "INTEGER NOT NULL DEFAULT 50", ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "injury_observations", "expires_at", "TEXT", ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "injury_observations", "season", "INTEGER", ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "injury_observations", "week", "INTEGER", ct).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "injury_observations", "season_type", "TEXT", ct).ConfigureAwait(false);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static string ResolveDatabasePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;

        var dataRoot = Environment.GetEnvironmentVariable("SLEEPER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataRoot))
            return Path.Combine(dataRoot, Path.GetFileName(configuredPath));

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || Directory.Exists(Path.Combine(directory.FullName, "src")))
                {
                    return Path.Combine(directory.FullName, configuredPath);
                }

                directory = directory.Parent;
            }
        }

        return Path.GetFullPath(configuredPath);
    }

    private static void AddParameters(
        SqliteCommand command,
        InjuryObservationInput input,
        DateTimeOffset observedAt,
        string hash)
    {
        command.Parameters.AddWithValue("$sleeper_id", input.SleeperId);
        command.Parameters.AddWithValue("$observed_at", observedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$effective_at", (object?)input.EffectiveAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", input.Source);
        command.Parameters.AddWithValue("$source_url", (object?)input.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", input.Status);
        command.Parameters.AddWithValue("$practice_status", (object?)input.PracticeStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$primary_injury", (object?)input.PrimaryInjury ?? DBNull.Value);
        command.Parameters.AddWithValue("$secondary_injury", (object?)input.SecondaryInjury ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)input.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", input.Confidence);
        command.Parameters.AddWithValue("$content_hash", hash);
        command.Parameters.AddWithValue("$observation_scope", ScopeValue(input.Scope));
        command.Parameters.AddWithValue("$authority", input.Authority);
        command.Parameters.AddWithValue("$expires_at", FormatNullableDate(input.ExpiresAt));
        command.Parameters.AddWithValue("$season", (object?)input.Season ?? DBNull.Value);
        command.Parameters.AddWithValue("$week", (object?)input.Week ?? DBNull.Value);
        command.Parameters.AddWithValue("$season_type", (object?)input.SeasonType ?? DBNull.Value);
    }

    private static async Task<(InjuryObservation Observation, bool Inserted)> RecordCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InjuryObservationInput input,
        CancellationToken ct)
    {
        var observedAt = (input.ObservedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var normalizedInput = input with
        {
            Source = input.Source.Trim().ToLowerInvariant(),
            Status = input.Status.Trim().ToLowerInvariant(),
            ObservedAt = observedAt,
            EffectiveAt = input.EffectiveAt?.ToUniversalTime(),
            ExpiresAt = input.ExpiresAt?.ToUniversalTime()
        };
        var hash = BuildHash(normalizedInput, observedAt);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO injury_observations
                (sleeper_id, observed_at, effective_at, source, source_url, status,
                 practice_status, primary_injury, secondary_injury, notes, confidence, content_hash,
                 observation_scope, authority, expires_at, season, week, season_type)
            VALUES
                ($sleeper_id, $observed_at, $effective_at, $source, $source_url, $status,
                 $practice_status, $primary_injury, $secondary_injury, $notes, $confidence, $content_hash,
                 $observation_scope, $authority, $expires_at, $season, $week, $season_type)
            ON CONFLICT(content_hash) DO NOTHING
            RETURNING id;
            """;
        AddParameters(command, normalizedInput, observedAt, hash);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var inserted = result is not null and not DBNull;
        var observation = !inserted
            ? await FindByHashAsync(connection, transaction, hash, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The injury observation was not persisted.")
            : new InjuryObservation(
                Convert.ToInt64(result, CultureInfo.InvariantCulture),
                normalizedInput.SleeperId,
                observedAt,
                normalizedInput.EffectiveAt,
                normalizedInput.Source,
                normalizedInput.SourceUrl,
                normalizedInput.Status,
                normalizedInput.PracticeStatus,
                normalizedInput.PrimaryInjury,
                normalizedInput.SecondaryInjury,
                normalizedInput.Notes,
                normalizedInput.Confidence,
                normalizedInput.Scope,
                normalizedInput.Authority,
                normalizedInput.ExpiresAt,
                normalizedInput.Season,
                normalizedInput.Week,
                normalizedInput.SeasonType);

        if (observation.Scope == InjuryObservationScope.Current)
            await RefreshCurrentSourceAsync(connection, transaction, observation, ct).ConfigureAwait(false);
        return (observation, inserted);
    }

    private static async Task<InjuryObservation?> FindByHashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hash,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, sleeper_id, observed_at, effective_at, source, source_url, status,
                   practice_status, primary_injury, secondary_injury, notes, confidence,
                   observation_scope, authority, expires_at, season, week, season_type
            FROM injury_observations WHERE content_hash = $content_hash;
            """;
        command.Parameters.AddWithValue("$content_hash", hash);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadObservation(reader) : null;
    }

    private static async Task RefreshCurrentSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InjuryObservation observation,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO injury_current_sources
                (sleeper_id, status, practice_status, primary_injury, secondary_injury, notes,
                 observed_at, effective_at, resolved_at, source, source_url, confidence, authority, expires_at)
            VALUES
                ($sleeper_id, $status, $practice_status, $primary_injury, $secondary_injury, $notes,
                 $observed_at, $effective_at, $resolved_at, $source, $source_url, $confidence, $authority, $expires_at)
            ON CONFLICT(sleeper_id, source) DO UPDATE SET
                status = excluded.status,
                practice_status = excluded.practice_status,
                primary_injury = excluded.primary_injury,
                secondary_injury = excluded.secondary_injury,
                notes = excluded.notes,
                observed_at = excluded.observed_at,
                effective_at = excluded.effective_at,
                resolved_at = excluded.resolved_at,
                source = excluded.source,
                source_url = excluded.source_url,
                confidence = excluded.confidence,
                authority = excluded.authority,
                expires_at = excluded.expires_at
            WHERE COALESCE(excluded.effective_at, excluded.observed_at)
                >= COALESCE(injury_current_sources.effective_at, injury_current_sources.observed_at);
            """;
        command.Parameters.AddWithValue("$sleeper_id", observation.SleeperId);
        command.Parameters.AddWithValue("$status", observation.Status);
        command.Parameters.AddWithValue("$practice_status", (object?)observation.PracticeStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$primary_injury", (object?)observation.PrimaryInjury ?? DBNull.Value);
        command.Parameters.AddWithValue("$secondary_injury", (object?)observation.SecondaryInjury ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)observation.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed_at", observation.ObservedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$effective_at", (object?)observation.EffectiveAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolved_at", observation.Status is "healthy" or "active"
            ? observation.ObservedAt.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        command.Parameters.AddWithValue("$source", observation.Source);
        command.Parameters.AddWithValue("$source_url", (object?)observation.SourceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", observation.Confidence);
        command.Parameters.AddWithValue("$authority", observation.Authority);
        command.Parameters.AddWithValue("$expires_at", FormatNullableDate(observation.ExpiresAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static InjuryObservation ReadObservation(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            ParseDate(reader.GetString(2)),
            ReadNullableDate(reader, 3),
            reader.GetString(4),
            ReadNullableString(reader, 5),
            reader.GetString(6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            reader.GetString(11),
            ParseScope(reader.GetString(12)),
            reader.GetInt32(13),
            ReadNullableDate(reader, 14),
            reader.IsDBNull(15) ? null : reader.GetInt32(15),
            reader.IsDBNull(16) ? null : reader.GetInt32(16),
            ReadNullableString(reader, 17));

    private static CurrentInjury ReadCurrent(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ParseDate(reader.GetString(6)),
            ReadNullableDate(reader, 7),
            ReadNullableDate(reader, 8),
            reader.GetString(9),
            ReadNullableString(reader, 10),
            reader.GetString(11),
            reader.GetInt32(12),
            ReadNullableDate(reader, 13));

    private static string? ReadNullableString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : ParseDate(reader.GetString(index));

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string BuildHash(InjuryObservationInput input, DateTimeOffset observedAt)
    {
        var observationIdentity = input.Scope == InjuryObservationScope.Historical
            ? $"{input.Season?.ToString(CultureInfo.InvariantCulture)}:{input.SeasonType}:{input.Week?.ToString(CultureInfo.InvariantCulture)}"
            : observedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var value = string.Join('|',
            input.SleeperId, input.Source, input.SourceUrl, input.Status, input.PracticeStatus,
            input.PrimaryInjury, input.SecondaryInjury, input.Notes, input.Confidence,
            ScopeValue(input.Scope), input.Authority, observationIdentity);
        value = string.Join('|', value,
            input.EffectiveAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            input.ExpiresAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            input.Season, input.Week, input.SeasonType);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static void ValidateInput(InjuryObservationInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SleeperId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Status);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Confidence);
        if (input.Authority is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(input), "Injury source authority must be between 0 and 100.");
    }

    private static string ScopeValue(InjuryObservationScope scope) =>
        scope == InjuryObservationScope.Historical ? "historical" : "current";

    private static InjuryObservationScope ParseScope(string value) =>
        string.Equals(value, "historical", StringComparison.OrdinalIgnoreCase)
            ? InjuryObservationScope.Historical
            : InjuryObservationScope.Current;

    private static object FormatNullableDate(DateTimeOffset? value) =>
        (object?)value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value;

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

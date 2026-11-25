using CloudSelfService.Models;
using Npgsql;

namespace CloudSelfService.Services.Data;

/// <summary>
/// Open → Completing → Completed, or Open → Aborted. Every transition out of Open is a conditional update, so the
/// expiry sweep and a user's Complete or Abort can never both act on the same session.
/// </summary>
public static class UploadStatus
{
    public const string Open = "Open";
    public const string Completing = "Completing";
    public const string Completed = "Completed";
    public const string Aborted = "Aborted";
}

/// <summary>
/// ClaimedAt is when the session last entered Completing, so the sweep can spot a commit that never finished.
/// LastActivityAt is when a chunk last landed (idle expiry, and how long a blob upload holds the file name).
/// TargetKind is the target's kind at Begin; cleanup refuses to touch storage if the target has changed kind since.
/// ExistedAtBegin records that Begin found a file to replace, so Complete audits Overwrite rather than Upload.
/// StagingRemoved is true once nothing of this session is left in storage; until then the row is kept so cleanup
/// can retry. Blob sessions start true because uncommitted blocks need no cleanup.
/// StagingSize is the logical size the Files staging file has been grown to: 0 until the first piece arrives, then the
/// session's full size (R32, R34).
/// CleanupRefused is set when cleanup refused to touch storage for this session (target gone or changed kind, or a
/// staging path that isn't the session's own); the refusal is logged once and the sweep stops retrying it.
/// FirstPieceMd5 is the base64 MD5 of the first 4 MiB the client declared at Begin, so a different file of the same name and size
/// is not resumed into this session; null when the client sent none (or the file is empty).
/// </summary>
public sealed record UploadSession(Guid Id, Guid UserId, Guid TargetId, string FinalPath, string StagingPath,
    long Size, int ChunkSize, bool Overwrite, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status,
    DateTimeOffset? ClaimedAt = null, DateTimeOffset? LastActivityAt = null, StorageKind TargetKind = StorageKind.FileShare,
    bool ExistedAtBegin = false, bool StagingRemoved = false, long StagingSize = 0, bool CleanupRefused = false,
    string? FirstPieceMd5 = null);

/// <summary>A purged row whose staging data was never confirmed removed, for a staff clean-up log line.</summary>
public sealed record AbandonedStaging(Guid SessionId, Guid TargetId, string StagingPath);

public interface IUploadSessionStore
{
    Task CreateAsync(UploadSession s, CancellationToken ct = default);
    Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The newest Open, unexpired session for exactly this user, target, final path and size.</summary>
    Task<UploadSession?> FindResumableAsync(Guid userId, Guid targetId, string finalPath, long size, DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Idempotent: marking a received chunk twice is not an error. Also stamps LastActivityAt with <paramref name="at"/>
    /// and, for an Open session only, moves ExpiresAt to <paramref name="expiresAt"/> if that is later (it never moves back).
    /// </summary>
    Task MarkChunkAsync(Guid id, int index, DateTimeOffset at, DateTimeOffset expiresAt, CancellationToken ct = default);
    Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default);
    Task SetStatusAsync(Guid id, string status, CancellationToken ct = default);

    /// <summary>
    /// Moves the session to <paramref name="next"/> only if it is still <paramref name="expected"/>. A non-null
    /// <paramref name="claimedAt"/> is stored as the claim time (set when entering Completing). When
    /// <paramref name="requireClaim"/> is true the update also requires the stored claim time to equal
    /// <paramref name="expectedClaim"/>, so a sweep never acts on a session re-claimed since it was listed.
    /// </summary>
    Task<bool> TrySetStatusAsync(Guid id, string expected, string next, DateTimeOffset? claimedAt = null, CancellationToken ct = default,
        bool requireClaim = false, DateTimeOffset? expectedClaim = null);

    /// <summary>Open or Completing, unexpired sessions on this exact target and final path, for any user.</summary>
    Task<IReadOnlyList<UploadSession>> ListActiveForPathAsync(Guid targetId, string finalPath, DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Open sessions of this user on this exact target and final path (any size, expired or not).</summary>
    Task<IReadOnlyList<UploadSession>> ListOpenForUserPathAsync(Guid userId, Guid targetId, string finalPath, CancellationToken ct = default);

    /// <summary>This user's Open, unexpired sessions on any target, most recent activity (or creation) first.</summary>
    Task<IReadOnlyList<UploadSession>> ListOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Sessions still Completing whose claim is older than <paramref name="claimedBefore"/>.</summary>
    Task<IReadOnlyList<UploadSession>> ListStuckCompletingAsync(DateTimeOffset claimedBefore, CancellationToken ct = default);
    Task SetOverwriteAsync(Guid id, bool overwrite, bool existedAtBegin, CancellationToken ct = default);

    /// <summary>
    /// Open sessions that have expired, or that never received a chunk and were created before <paramref name="idleBefore"/>.
    /// A session with any chunk keeps its full lifetime, so an overnight resume works (R33).
    /// </summary>
    Task<IReadOnlyList<UploadSession>> ListSweepableOpenAsync(DateTimeOffset now, DateTimeOffset idleBefore, CancellationToken ct = default);

    /// <summary>Aborted sessions whose staging data has not been confirmed removed and whose cleanup wasn't refused.</summary>
    Task<IReadOnlyList<UploadSession>> ListAbortedWithStagingAsync(CancellationToken ct = default);
    Task SetStagingRemovedAsync(Guid id, CancellationToken ct = default);
    Task SetCleanupRefusedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Open and Completing sessions of this user, unexpired.</summary>
    Task<int> CountOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Total declared size of this user's Open and Completing, unexpired sessions.</summary>
    Task<long> SumOpenSizeForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="grow"/> only if the recorded StagingSize is below <paramref name="size"/>, then records it.
    /// Calls for one session are serialised (a row lock in PostgreSQL), so parallel chunks can never resize the staging
    /// file out of order and shrink it under a piece another chunk just wrote.
    /// </summary>
    Task EnsureStagingSizeAsync(Guid id, long size, Func<CancellationToken, Task> grow, CancellationToken ct = default);

    /// <summary>
    /// Deletes settled sessions (and their chunk rows) that expired before <paramref name="cutoff"/> and have no staging
    /// left (Completed, or Aborted with StagingRemoved), plus any settled session that expired before
    /// <paramref name="hardCutoff"/> regardless. Returns the rows
    /// removed by the hard cutoff whose staging was never confirmed gone (excluding refused ones, already logged).
    /// </summary>
    Task<(int Deleted, IReadOnlyList<AbandonedStaging> Abandoned)> PurgeAsync(DateTimeOffset cutoff, DateTimeOffset hardCutoff,
        CancellationToken ct = default);
}

public sealed class InMemoryUploadSessionStore : IUploadSessionStore
{
    private readonly Dictionary<Guid, UploadSession> _sessions = new();
    private readonly Dictionary<Guid, HashSet<int>> _chunks = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _growLocks = new();
    private readonly object _lock = new();

    private static bool IsActive(UploadSession s) => s.Status is UploadStatus.Open or UploadStatus.Completing;
    private static bool IsSettled(UploadSession s) => s.Status is UploadStatus.Completed or UploadStatus.Aborted;

    public Task CreateAsync(UploadSession s, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_sessions.TryAdd(s.Id, s))
                throw new InvalidOperationException("An upload session with that id already exists.");
            _chunks[s.Id] = new HashSet<int>();
        }
        return Task.CompletedTask;
    }

    public Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_sessions.GetValueOrDefault(id));
    }

    public Task<UploadSession?> FindResumableAsync(Guid userId, Guid targetId, string finalPath, long size, DateTimeOffset now,
        CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_sessions.Values
                .Where(s => s.UserId == userId && s.TargetId == targetId && s.FinalPath == finalPath && s.Size == size
                            && s.Status == UploadStatus.Open && s.ExpiresAt > now)
                .OrderByDescending(s => s.CreatedAt)
                .FirstOrDefault());
    }

    public Task MarkChunkAsync(Guid id, int index, DateTimeOffset at, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_chunks.TryGetValue(id, out var set))
                throw new InvalidOperationException("Upload session not found.");
            set.Add(index);
            if (_sessions.TryGetValue(id, out var s))
            {
                var updated = s;
                if (s.LastActivityAt is null || s.LastActivityAt < at)
                    updated = updated with { LastActivityAt = at };
                if (s.Status == UploadStatus.Open && s.ExpiresAt < expiresAt)
                    updated = updated with { ExpiresAt = expiresAt };
                _sessions[id] = updated;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<int>>(_chunks.TryGetValue(id, out var set)
                ? set.OrderBy(i => i).ToList()
                : Array.Empty<int>());
    }

    public Task SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) _sessions[id] = s with { Status = status };
        }
        return Task.CompletedTask;
    }

    public Task<bool> TrySetStatusAsync(Guid id, string expected, string next, DateTimeOffset? claimedAt = null,
        CancellationToken ct = default, bool requireClaim = false, DateTimeOffset? expectedClaim = null)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var s) || s.Status != expected) return Task.FromResult(false);
            if (requireClaim && s.ClaimedAt != expectedClaim) return Task.FromResult(false);
            _sessions[id] = s with { Status = next, ClaimedAt = claimedAt ?? s.ClaimedAt };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<UploadSession>> ListActiveForPathAsync(Guid targetId, string finalPath, DateTimeOffset now,
        CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<UploadSession>>(_sessions.Values
                .Where(s => s.TargetId == targetId && s.FinalPath == finalPath && s.ExpiresAt > now && IsActive(s))
                .ToList());
    }

    public Task<IReadOnlyList<UploadSession>> ListOpenForUserPathAsync(Guid userId, Guid targetId, string finalPath,
        CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<UploadSession>>(_sessions.Values
                .Where(s => s.UserId == userId && s.TargetId == targetId && s.FinalPath == finalPath && s.Status == UploadStatus.Open)
                .ToList());
    }

    public Task<IReadOnlyList<UploadSession>> ListOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<UploadSession>>(_sessions.Values
                .Where(s => s.UserId == userId && s.Status == UploadStatus.Open && s.ExpiresAt > now)
                .OrderByDescending(s => s.LastActivityAt ?? s.CreatedAt)
                .ToList());
    }

    public Task<IReadOnlyList<UploadSession>> ListStuckCompletingAsync(DateTimeOffset claimedBefore, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<UploadSession>>(_sessions.Values
                .Where(s => s.Status == UploadStatus.Completing && (s.ClaimedAt is null || s.ClaimedAt < claimedBefore))
                .ToList());
    }

    public Task SetOverwriteAsync(Guid id, bool overwrite, bool existedAtBegin, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) _sessions[id] = s with { Overwrite = overwrite, ExistedAtBegin = existedAtBegin };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UploadSession>> ListSweepableOpenAsync(DateTimeOffset now, DateTimeOffset idleBefore,
        CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<UploadSession>>(_sessions.Values
                .Where(s => s.Status == UploadStatus.Open
                            && (s.ExpiresAt <= now || (s.LastActivityAt is null && s.CreatedAt < idleBefore)))
                .ToList());
    }

    public Task<IReadOnlyList<UploadSession>> ListAbortedWithStagingAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<UploadSession>>(_sessions.Values
                .Where(s => s.Status == UploadStatus.Aborted && !s.StagingRemoved && !s.CleanupRefused).ToList());
    }

    public Task SetStagingRemovedAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) _sessions[id] = s with { StagingRemoved = true };
        }
        return Task.CompletedTask;
    }

    public Task SetCleanupRefusedAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) _sessions[id] = s with { CleanupRefused = true };
        }
        return Task.CompletedTask;
    }

    public Task<int> CountOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_sessions.Values.Count(s => s.UserId == userId && IsActive(s) && s.ExpiresAt > now));
    }

    public Task<long> SumOpenSizeForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_sessions.Values.Where(s => s.UserId == userId && IsActive(s) && s.ExpiresAt > now).Sum(s => s.Size));
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _sessions.Remove(id);
            _chunks.Remove(id);
            _growLocks.Remove(id);
        }
        return Task.CompletedTask;
    }

    public async Task EnsureStagingSizeAsync(Guid id, long size, Func<CancellationToken, Task> grow, CancellationToken ct = default)
    {
        SemaphoreSlim gate;
        lock (_lock)
        {
            if (!_growLocks.TryGetValue(id, out gate!)) _growLocks[id] = gate = new SemaphoreSlim(1, 1);
        }
        await gate.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(id, out var current) || current.StagingSize >= size) return;
            }
            await grow(ct);
            lock (_lock)
            {
                if (_sessions.TryGetValue(id, out var current) && current.StagingSize < size)
                    _sessions[id] = current with { StagingSize = size };
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<(int Deleted, IReadOnlyList<AbandonedStaging> Abandoned)> PurgeAsync(DateTimeOffset cutoff, DateTimeOffset hardCutoff,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            var old = _sessions.Values
                .Where(s => IsSettled(s)
                            && ((s.ExpiresAt < cutoff && (s.StagingRemoved || s.Status == UploadStatus.Completed)) || s.ExpiresAt < hardCutoff))
                .ToList();
            var abandoned = old.Where(s => !s.StagingRemoved && !s.CleanupRefused && s.Status == UploadStatus.Aborted)
                .Select(s => new AbandonedStaging(s.Id, s.TargetId, s.StagingPath)).ToList();
            foreach (var s in old)
            {
                _sessions.Remove(s.Id);
                _chunks.Remove(s.Id);
                _growLocks.Remove(s.Id);
            }
            return Task.FromResult<(int, IReadOnlyList<AbandonedStaging>)>((old.Count, abandoned));
        }
    }
}

public sealed class PgUploadSessionStore : IUploadSessionStore
{
    private const string Cols = "id, user_id, target_id, final_path, staging_path, size, chunk_size, overwrite, created_at, " +
                                "expires_at, status, claimed_at, last_activity_at, target_kind, existed_at_begin, staging_removed, staging_size, cleanup_refused, " +
                                "first_piece_md5";
    private readonly PgsqlContext _db;
    public PgUploadSessionStore(PgsqlContext db) => _db = db;

    private static UploadSession Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3),
        r.GetString(4), r.GetInt64(5), r.GetInt32(6), r.GetBoolean(7), r.GetFieldValue<DateTimeOffset>(8),
        r.GetFieldValue<DateTimeOffset>(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.Enum<StorageKind>(13), r.GetBoolean(14), r.GetBoolean(15), r.GetInt64(16), r.GetBoolean(17),
        r.IsDBNull(18) ? null : r.GetString(18));

    private static void AddTime(NpgsqlCommand cmd, string name, DateTimeOffset? value) =>
        cmd.Parameters.AddWithValue(name, NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)value?.ToUniversalTime() ?? DBNull.Value);

    public async Task CreateAsync(UploadSession s, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO upload_sessions ({Cols})
            VALUES (@id, @uid, @tid, @final, @staging, @size, @chunk, @overwrite, @created, @expires, @status, @claimed, @activity,
                    @kind, @existed, @removed, @stagingSize, @refused, @firstMd5)
            """, conn);
        cmd.Parameters.AddWithValue("id", s.Id);
        cmd.Parameters.AddWithValue("uid", s.UserId);
        cmd.Parameters.AddWithValue("tid", s.TargetId);
        cmd.Parameters.AddWithValue("final", s.FinalPath);
        cmd.Parameters.AddWithValue("staging", s.StagingPath);
        cmd.Parameters.AddWithValue("size", s.Size);
        cmd.Parameters.AddWithValue("chunk", s.ChunkSize);
        cmd.Parameters.AddWithValue("overwrite", s.Overwrite);
        cmd.Parameters.AddWithValue("created", s.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("expires", s.ExpiresAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("status", s.Status);
        AddTime(cmd, "claimed", s.ClaimedAt);
        AddTime(cmd, "activity", s.LastActivityAt);
        cmd.Parameters.AddWithValue("kind", s.TargetKind.ToString());
        cmd.Parameters.AddWithValue("existed", s.ExistedAtBegin);
        cmd.Parameters.AddWithValue("removed", s.StagingRemoved);
        cmd.Parameters.AddWithValue("stagingSize", s.StagingSize);
        cmd.Parameters.AddWithValue("refused", s.CleanupRefused);
        cmd.Parameters.AddWithValue("firstMd5", NpgsqlTypes.NpgsqlDbType.Text, (object?)s.FirstPieceMd5 ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM upload_sessions WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ReadOneAsync(Map, ct);
    }

    public async Task<UploadSession?> FindResumableAsync(Guid userId, Guid targetId, string finalPath, long size, DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Cols} FROM upload_sessions
            WHERE user_id = @uid AND target_id = @tid AND final_path = @final AND size = @size
              AND status = 'Open' AND expires_at > @now
            ORDER BY created_at DESC
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("uid", userId);
        cmd.Parameters.AddWithValue("tid", targetId);
        cmd.Parameters.AddWithValue("final", finalPath);
        cmd.Parameters.AddWithValue("size", size);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await cmd.ReadOneAsync(Map, ct);
    }

    public async Task MarkChunkAsync(Guid id, int index, DateTimeOffset at, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO upload_chunks (session_id, idx) VALUES (@id, @idx) ON CONFLICT (session_id, idx) DO NOTHING;
            UPDATE upload_sessions SET last_activity_at = GREATEST(COALESCE(last_activity_at, @at), @at),
                expires_at = CASE WHEN status = 'Open' THEN GREATEST(expires_at, @expires) ELSE expires_at END
            WHERE id = @id;
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("idx", index);
        cmd.Parameters.AddWithValue("at", at.ToUniversalTime());
        cmd.Parameters.AddWithValue("expires", expiresAt.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT idx FROM upload_chunks WHERE session_id = @id ORDER BY idx", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ReadAllAsync(r => r.GetInt32(0), ct);
    }

    public async Task SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE upload_sessions SET status = @status WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TrySetStatusAsync(Guid id, string expected, string next, DateTimeOffset? claimedAt = null,
        CancellationToken ct = default, bool requireClaim = false, DateTimeOffset? expectedClaim = null)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE upload_sessions SET status = @next, claimed_at = COALESCE(@claimed, claimed_at) WHERE id = @id AND status = @expected" +
            (requireClaim ? " AND claimed_at IS NOT DISTINCT FROM @expectedClaim" : ""),
            conn);
        AddTime(cmd, "expectedClaim", expectedClaim);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected", expected);
        cmd.Parameters.AddWithValue("next", next);
        AddTime(cmd, "claimed", claimedAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<UploadSession>> ListActiveForPathAsync(Guid targetId, string finalPath, DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cols} FROM upload_sessions WHERE target_id = @tid AND final_path = @final " +
            "AND status IN ('Open', 'Completing') AND expires_at > @now", conn);
        cmd.Parameters.AddWithValue("tid", targetId);
        cmd.Parameters.AddWithValue("final", finalPath);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task<IReadOnlyList<UploadSession>> ListOpenForUserPathAsync(Guid userId, Guid targetId, string finalPath,
        CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cols} FROM upload_sessions WHERE user_id = @uid AND target_id = @tid AND final_path = @final AND status = 'Open'",
            conn);
        cmd.Parameters.AddWithValue("uid", userId);
        cmd.Parameters.AddWithValue("tid", targetId);
        cmd.Parameters.AddWithValue("final", finalPath);
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task<IReadOnlyList<UploadSession>> ListOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Cols} FROM upload_sessions
            WHERE user_id = @uid AND status = 'Open' AND expires_at > @now
            ORDER BY COALESCE(last_activity_at, created_at) DESC
            """, conn);
        cmd.Parameters.AddWithValue("uid", userId);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task<IReadOnlyList<UploadSession>> ListStuckCompletingAsync(DateTimeOffset claimedBefore, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cols} FROM upload_sessions WHERE status = 'Completing' AND (claimed_at IS NULL OR claimed_at < @before)", conn);
        cmd.Parameters.AddWithValue("before", claimedBefore.ToUniversalTime());
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task SetOverwriteAsync(Guid id, bool overwrite, bool existedAtBegin, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE upload_sessions SET overwrite = @overwrite, existed_at_begin = @existed WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("overwrite", overwrite);
        cmd.Parameters.AddWithValue("existed", existedAtBegin);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<UploadSession>> ListSweepableOpenAsync(DateTimeOffset now, DateTimeOffset idleBefore,
        CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Cols} FROM upload_sessions
            WHERE status = 'Open' AND (expires_at <= @now OR (last_activity_at IS NULL AND created_at < @idle))
            ORDER BY expires_at
            """, conn);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("idle", idleBefore.ToUniversalTime());
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task<IReadOnlyList<UploadSession>> ListAbortedWithStagingAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cols} FROM upload_sessions WHERE status = 'Aborted' AND NOT staging_removed AND NOT cleanup_refused", conn);
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task SetStagingRemovedAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE upload_sessions SET staging_removed = TRUE WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetCleanupRefusedAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE upload_sessions SET cleanup_refused = TRUE WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountOpenForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM upload_sessions WHERE user_id = @uid AND status IN ('Open', 'Completing') AND expires_at > @now", conn);
        cmd.Parameters.AddWithValue("uid", userId);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<long> SumOpenSizeForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(size), 0) FROM upload_sessions WHERE user_id = @uid AND status IN ('Open', 'Completing') AND expires_at > @now",
            conn);
        cmd.Parameters.AddWithValue("uid", userId);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM upload_sessions WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnsureStagingSizeAsync(Guid id, long size, Func<CancellationToken, Task> grow, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        long current;
        await using (var read = new NpgsqlCommand("SELECT staging_size FROM upload_sessions WHERE id = @id FOR UPDATE", conn, tx))
        {
            read.Parameters.AddWithValue("id", id);
            if (await read.ExecuteScalarAsync(ct) is not long value) return;
            current = value;
        }
        if (current >= size) return;
        // The row stays locked while storage is resized; a failure rolls back and leaves the recorded size unchanged.
        await grow(ct);
        await using (var write = new NpgsqlCommand("UPDATE upload_sessions SET staging_size = @size WHERE id = @id", conn, tx))
        {
            write.Parameters.AddWithValue("id", id);
            write.Parameters.AddWithValue("size", size);
            await write.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<(int Deleted, IReadOnlyList<AbandonedStaging> Abandoned)> PurgeAsync(DateTimeOffset cutoff, DateTimeOffset hardCutoff,
        CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM upload_sessions
            WHERE status IN ('Completed', 'Aborted')
              AND ((expires_at < @cutoff AND (staging_removed OR status = 'Completed')) OR expires_at < @hard)
            RETURNING id, target_id, staging_path, status, staging_removed, cleanup_refused
            """, conn);
        cmd.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
        cmd.Parameters.AddWithValue("hard", hardCutoff.ToUniversalTime());
        var rows = await cmd.ReadAllAsync(r => (Id: r.GetGuid(0), Target: r.GetGuid(1), Staging: r.GetString(2),
            Status: r.GetString(3), Removed: r.GetBoolean(4), Refused: r.GetBoolean(5)), ct);
        var abandoned = rows.Where(r => !r.Removed && !r.Refused && r.Status == UploadStatus.Aborted)
            .Select(r => new AbandonedStaging(r.Id, r.Target, r.Staging)).ToList();
        return (rows.Count, abandoned);
    }
}

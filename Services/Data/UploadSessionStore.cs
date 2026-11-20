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

public sealed record UploadSession(Guid Id, Guid UserId, Guid TargetId, string FinalPath, string StagingPath,
    long Size, int ChunkSize, bool Overwrite, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status);

public interface IUploadSessionStore
{
    Task CreateAsync(UploadSession s, CancellationToken ct = default);
    Task<UploadSession?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The newest Open, unexpired session for exactly this user, target, final path and size.</summary>
    Task<UploadSession?> FindResumableAsync(Guid userId, Guid targetId, string finalPath, long size, DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Idempotent: marking a received chunk twice is not an error.</summary>
    Task MarkChunkAsync(Guid id, int index, CancellationToken ct = default);
    Task<IReadOnlyList<int>> ReceivedChunksAsync(Guid id, CancellationToken ct = default);
    Task SetStatusAsync(Guid id, string status, CancellationToken ct = default);

    /// <summary>Moves the session to <paramref name="next"/> only if it is still <paramref name="expected"/>.</summary>
    Task<bool> TrySetStatusAsync(Guid id, string expected, string next, CancellationToken ct = default);
    Task SetOverwriteAsync(Guid id, bool overwrite, CancellationToken ct = default);
    Task<IReadOnlyList<UploadSession>> ListExpiredOpenAsync(DateTimeOffset now, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Deletes every session (and its chunk rows) that expired before <paramref name="cutoff"/>.</summary>
    Task<int> PurgeExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public sealed class InMemoryUploadSessionStore : IUploadSessionStore
{
    private readonly Dictionary<Guid, UploadSession> _sessions = new();
    private readonly Dictionary<Guid, HashSet<int>> _chunks = new();
    private readonly object _lock = new();

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

    public Task MarkChunkAsync(Guid id, int index, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_chunks.TryGetValue(id, out var set))
                throw new InvalidOperationException("Upload session not found.");
            set.Add(index);
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

    public Task<bool> TrySetStatusAsync(Guid id, string expected, string next, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var s) || s.Status != expected) return Task.FromResult(false);
            _sessions[id] = s with { Status = next };
            return Task.FromResult(true);
        }
    }

    public Task SetOverwriteAsync(Guid id, bool overwrite, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var s)) _sessions[id] = s with { Overwrite = overwrite };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UploadSession>> ListExpiredOpenAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<UploadSession>>(_sessions.Values
                .Where(s => s.Status == UploadStatus.Open && s.ExpiresAt <= now).ToList());
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _sessions.Remove(id);
            _chunks.Remove(id);
        }
        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var old = _sessions.Values.Where(s => s.ExpiresAt < cutoff).Select(s => s.Id).ToList();
            foreach (var id in old)
            {
                _sessions.Remove(id);
                _chunks.Remove(id);
            }
            return Task.FromResult(old.Count);
        }
    }
}

public sealed class PgUploadSessionStore : IUploadSessionStore
{
    private const string Cols = "id, user_id, target_id, final_path, staging_path, size, chunk_size, overwrite, created_at, expires_at, status";
    private readonly PgsqlContext _db;
    public PgUploadSessionStore(PgsqlContext db) => _db = db;

    private static UploadSession Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3),
        r.GetString(4), r.GetInt64(5), r.GetInt32(6), r.GetBoolean(7), r.GetFieldValue<DateTimeOffset>(8),
        r.GetFieldValue<DateTimeOffset>(9), r.GetString(10));

    public async Task CreateAsync(UploadSession s, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO upload_sessions ({Cols})
            VALUES (@id, @uid, @tid, @final, @staging, @size, @chunk, @overwrite, @created, @expires, @status)
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

    public async Task MarkChunkAsync(Guid id, int index, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO upload_chunks (session_id, idx) VALUES (@id, @idx) ON CONFLICT (session_id, idx) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("idx", index);
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

    public async Task<bool> TrySetStatusAsync(Guid id, string expected, string next, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE upload_sessions SET status = @next WHERE id = @id AND status = @expected", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected", expected);
        cmd.Parameters.AddWithValue("next", next);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task SetOverwriteAsync(Guid id, bool overwrite, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE upload_sessions SET overwrite = @overwrite WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("overwrite", overwrite);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<UploadSession>> ListExpiredOpenAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cols} FROM upload_sessions WHERE status = 'Open' AND expires_at <= @now ORDER BY expires_at", conn);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM upload_sessions WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PurgeExpiredBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM upload_sessions WHERE expires_at < @cutoff", conn);
        cmd.Parameters.AddWithValue("cutoff", cutoff.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

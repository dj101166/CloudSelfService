using Npgsql;

namespace CloudSelfService.Services.Data;

/// <summary>An Admin's upload limit in place of the configured default. A null <see cref="ExpiresAt"/> is permanent.</summary>
public sealed record UploadLimitOverride(long Bytes, DateTimeOffset? ExpiresAt, Guid SetBy, string SetByUpn, DateTimeOffset SetAt, string Note);

/// <summary>The one current upload limit override. History lives in the audit log, not here.</summary>
public interface IUploadLimitStore
{
    Task<UploadLimitOverride?> GetAsync(CancellationToken ct = default);
    Task SetAsync(UploadLimitOverride o, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed class InMemoryUploadLimitStore : IUploadLimitStore
{
    private volatile UploadLimitOverride? _current;

    public Task<UploadLimitOverride?> GetAsync(CancellationToken ct = default) => Task.FromResult(_current);

    public Task SetAsync(UploadLimitOverride o, CancellationToken ct = default)
    {
        _current = o;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _current = null;
        return Task.CompletedTask;
    }
}

public sealed class PgUploadLimitStore : IUploadLimitStore
{
    private readonly PgsqlContext _db;
    public PgUploadLimitStore(PgsqlContext db) => _db = db;

    public async Task<UploadLimitOverride?> GetAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT bytes, expires_at, set_by, set_by_upn, set_at, note FROM upload_limit_override WHERE id = 1", conn);
        return await cmd.ReadOneAsync(r => new UploadLimitOverride(r.GetInt64(0),
            r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1), r.GetGuid(2), r.GetString(3),
            r.GetFieldValue<DateTimeOffset>(4), r.GetString(5)), ct);
    }

    public async Task SetAsync(UploadLimitOverride o, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO upload_limit_override (id, bytes, expires_at, set_by, set_by_upn, set_at, note)
            VALUES (1, @bytes, @expires, @by, @upn, @at, @note)
            ON CONFLICT (id) DO UPDATE SET bytes = EXCLUDED.bytes, expires_at = EXCLUDED.expires_at, set_by = EXCLUDED.set_by,
                set_by_upn = EXCLUDED.set_by_upn, set_at = EXCLUDED.set_at, note = EXCLUDED.note
            """, conn);
        cmd.Parameters.AddWithValue("bytes", o.Bytes);
        cmd.Parameters.AddWithValue("expires", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)o.ExpiresAt?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("by", o.SetBy);
        cmd.Parameters.AddWithValue("upn", o.SetByUpn);
        cmd.Parameters.AddWithValue("at", o.SetAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("note", o.Note);
        await cmd.ExecWriteAsync("The upload limit could not be saved.", "Rejected by a database check.", ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM upload_limit_override WHERE id = 1", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

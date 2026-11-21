using CloudSelfService.Models;
using CloudSelfService.Services.Identity;
using Npgsql;

namespace CloudSelfService.Services.Data;

internal static class PgRead
{
    public static async Task ExecWriteAsync(this NpgsqlCommand cmd, string uniqueMsg, string checkMsg, CancellationToken ct)
    {
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { throw new InvalidOperationException(uniqueMsg, ex); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        { throw new InvalidOperationException(checkMsg, ex); }
    }

    public static string? Str(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static Guid? NGuid(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    public static T Enum<T>(this NpgsqlDataReader r, int i) where T : struct, Enum => System.Enum.Parse<T>(r.GetString(i));

    public static async Task<List<T>> ReadAllAsync<T>(this NpgsqlCommand cmd, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    public static async Task<T?> ReadOneAsync<T>(this NpgsqlCommand cmd, Func<NpgsqlDataReader, T> map, CancellationToken ct) where T : class
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? map(r) : null;
    }
}

public sealed class PgUserStore : IUserStore
{
    private const string Cols = "id, kind, customer_id, upn, display_name, entra_object_id, staff_role, active, created_at, last_sign_in_at";
    private readonly PgsqlContext _db;
    public PgUserStore(PgsqlContext db) => _db = db;

    private static AppUser Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.Enum<UserKind>(1), r.NGuid(2), r.GetString(3),
        r.GetString(4), r.Str(5), r.Enum<StaffRole>(6), r.GetBoolean(7), r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9));

    private async Task<AppUser?> OneAsync(string where, string param, object value, CancellationToken ct)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM users WHERE {where}", conn);
        cmd.Parameters.AddWithValue(param, value);
        return await cmd.ReadOneAsync(Map, ct);
    }

    public Task<AppUser?> GetAsync(Guid id, CancellationToken ct = default) => OneAsync("id = @p", "p", id, ct);
    public Task<AppUser?> FindByObjectIdAsync(string oid, CancellationToken ct = default) => OneAsync("entra_object_id = @p", "p", oid, ct);
    public Task<AppUser?> FindByUpnAsync(string upn, CancellationToken ct = default) => OneAsync("upn_normalized = @p", "p", UpnNormalizer.Normalize(upn), ct);

    public async Task<IReadOnlyList<AppUser>> ListAsync(UserKind? kind = null, Guid? customerId = null, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cols} FROM users WHERE (@kind::text IS NULL OR kind = @kind) AND (@cid::uuid IS NULL OR customer_id = @cid) ORDER BY lower(upn)", conn);
        cmd.Parameters.AddWithValue("kind", NpgsqlTypes.NpgsqlDbType.Text, (object?)kind?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)customerId ?? DBNull.Value);
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task UpsertAsync(AppUser u, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO users (id, kind, customer_id, upn, upn_normalized, display_name, entra_object_id, staff_role, active, created_at, last_sign_in_at)
            VALUES (@id, @kind, @cid, @upn, @upnn, @dn, @oid, @role, @active, @created, @last)
            ON CONFLICT (id) DO UPDATE SET kind = EXCLUDED.kind, customer_id = EXCLUDED.customer_id, upn = EXCLUDED.upn, upn_normalized = EXCLUDED.upn_normalized,
                display_name = EXCLUDED.display_name, entra_object_id = EXCLUDED.entra_object_id, staff_role = EXCLUDED.staff_role,
                active = EXCLUDED.active, last_sign_in_at = EXCLUDED.last_sign_in_at
            """, conn);
        cmd.Parameters.AddWithValue("id", u.Id);
        cmd.Parameters.AddWithValue("kind", u.Kind.ToString());
        cmd.Parameters.AddWithValue("cid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)u.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("upn", u.Upn);
        cmd.Parameters.AddWithValue("upnn", UpnNormalizer.Normalize(u.Upn));
        cmd.Parameters.AddWithValue("dn", u.DisplayName);
        cmd.Parameters.AddWithValue("oid", NpgsqlTypes.NpgsqlDbType.Text, (object?)u.EntraObjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("role", u.StaffRole.ToString());
        cmd.Parameters.AddWithValue("active", u.Active);
        cmd.Parameters.AddWithValue("created", u.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("last", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)u.LastSignInAt?.ToUniversalTime() ?? DBNull.Value);
        await cmd.ExecWriteAsync("A user with that UPN or Entra object id already exists.", "Rejected by a database check.", ct);
    }

    public async Task<bool> SetActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE users SET active = @a WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("a", active);
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SetStaffRoleAsync(Guid userId, StaffRole role, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE users SET staff_role = @r WHERE id = @id AND kind = 'Staff'", conn);
        cmd.Parameters.AddWithValue("r", role.ToString());
        cmd.Parameters.AddWithValue("id", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // One statement: the active check, the oid check and the write cannot be interleaved with an admin's change.
    public async Task<AppUser?> RecordSignInAsync(Guid userId, string oid, string upn, string displayName, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE users SET entra_object_id = @oid, last_sign_in_at = @now, upn = @upn, upn_normalized = @upnn, display_name = @dn
            WHERE id = @id AND active AND (entra_object_id IS NULL OR entra_object_id = @oid)
            RETURNING {Cols}
            """, conn);
        cmd.Parameters.AddWithValue("oid", oid);
        cmd.Parameters.AddWithValue("now", now.ToUniversalTime());
        cmd.Parameters.AddWithValue("upn", upn);
        cmd.Parameters.AddWithValue("upnn", UpnNormalizer.Normalize(upn));
        cmd.Parameters.AddWithValue("dn", displayName);
        cmd.Parameters.AddWithValue("id", userId);
        try
        {
            return await cmd.ReadOneAsync(Map, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("A user with that UPN or Entra object id already exists.", ex);
        }
    }
}

public sealed class PgCustomerStore : ICustomerStore
{
    private const string Cols = "id, name, active, notes, home_tenant_id, cloud, cross_tenant_status";
    private readonly PgsqlContext _db;
    public PgCustomerStore(PgsqlContext db) => _db = db;

    private static Customer Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetString(3),
        r.Str(4), r.Enum<CustomerCloud>(5), r.Enum<CrossTenantStatus>(6));

    public async Task<Customer?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM customers WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ReadOneAsync(Map, ct);
    }

    public async Task<IReadOnlyList<Customer>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM customers ORDER BY lower(name)", conn);
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task UpsertAsync(Customer c, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO customers (id, name, active, notes, home_tenant_id, cloud, cross_tenant_status)
            VALUES (@id, @name, @active, @notes, @tenant, @cloud, @xt)
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, active = EXCLUDED.active, notes = EXCLUDED.notes,
                home_tenant_id = EXCLUDED.home_tenant_id, cloud = EXCLUDED.cloud, cross_tenant_status = EXCLUDED.cross_tenant_status
            """, conn);
        cmd.Parameters.AddWithValue("id", c.Id);
        cmd.Parameters.AddWithValue("name", c.Name);
        cmd.Parameters.AddWithValue("active", c.Active);
        cmd.Parameters.AddWithValue("notes", c.Notes);
        cmd.Parameters.AddWithValue("tenant", NpgsqlTypes.NpgsqlDbType.Text, (object?)c.HomeTenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cloud", c.Cloud.ToString());
        cmd.Parameters.AddWithValue("xt", c.CrossTenantStatus.ToString());
        await cmd.ExecWriteAsync("A customer with that name already exists.", "Rejected by a database check.", ct);
    }
}

public sealed class PgTargetStore : ITargetStore
{
    private const string Cols = "id, customer_id, display_name, subscription_id, resource_group, account_name, kind, container_name, endpoint_suffix, active";
    private readonly PgsqlContext _db;
    public PgTargetStore(PgsqlContext db) => _db = db;

    private static StorageTarget Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.NGuid(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetString(5), r.Enum<StorageKind>(6), r.GetString(7), r.GetString(8), r.GetBoolean(9));

    public async Task<StorageTarget?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM storage_targets WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ReadOneAsync(Map, ct);
    }

    public async Task<IReadOnlyList<StorageTarget>> ListAsync(Guid? customerId = null, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Cols} FROM storage_targets WHERE (@cid::uuid IS NULL OR customer_id = @cid) ORDER BY lower(display_name)", conn);
        cmd.Parameters.AddWithValue("cid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)customerId ?? DBNull.Value);
        return await cmd.ReadAllAsync(Map, ct);
    }

    public async Task UpsertAsync(StorageTarget t, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO storage_targets (id, customer_id, display_name, subscription_id, resource_group, account_name, kind, container_name, endpoint_suffix, active)
            VALUES (@id, @cid, @dn, @sub, @rg, @acct, @kind, @container, @suffix, @active)
            ON CONFLICT (id) DO UPDATE SET customer_id = EXCLUDED.customer_id, display_name = EXCLUDED.display_name,
                subscription_id = EXCLUDED.subscription_id, resource_group = EXCLUDED.resource_group,
                account_name = EXCLUDED.account_name, kind = EXCLUDED.kind, container_name = EXCLUDED.container_name,
                endpoint_suffix = EXCLUDED.endpoint_suffix, active = EXCLUDED.active
            """, conn);
        cmd.Parameters.AddWithValue("id", t.Id);
        cmd.Parameters.AddWithValue("cid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)t.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dn", t.DisplayName);
        cmd.Parameters.AddWithValue("sub", t.SubscriptionId);
        cmd.Parameters.AddWithValue("rg", t.ResourceGroup);
        cmd.Parameters.AddWithValue("acct", t.AccountName);
        cmd.Parameters.AddWithValue("kind", t.Kind.ToString());
        cmd.Parameters.AddWithValue("container", t.ContainerName);
        cmd.Parameters.AddWithValue("suffix", t.EndpointSuffix);
        cmd.Parameters.AddWithValue("active", t.Active);
        await cmd.ExecWriteAsync("A target for that account, kind and container already exists.", "A target's owner cannot change while grants for it exist.", ct);
    }
}

public sealed class PgGrantStore : IGrantStore
{
    private const string Cols = "customer_id, target_id, level, granted_by, granted_at";
    private readonly PgsqlContext _db;
    public PgGrantStore(PgsqlContext db) => _db = db;

    private static Grant Map(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Enum<AccessLevel>(2),
        r.GetString(3), r.GetFieldValue<DateTimeOffset>(4));

    public async Task<Grant?> GetAsync(Guid customerId, Guid targetId, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM grants WHERE customer_id = @c AND target_id = @t", conn);
        cmd.Parameters.AddWithValue("c", customerId);
        cmd.Parameters.AddWithValue("t", targetId);
        return await cmd.ReadOneAsync(Map, ct);
    }

    public async Task<IReadOnlyList<Grant>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Cols} FROM grants WHERE customer_id = @c", conn);
        cmd.Parameters.AddWithValue("c", customerId);
        return await cmd.ReadAllAsync(Map, ct);
    }

    // The grants_owner_check trigger rejects a grant to a customer that does not own the
    // target; surface that as InvalidOperationException to match the in-memory store.
    public async Task SetAsync(Grant g, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO grants (customer_id, target_id, level, granted_by, granted_at)
            VALUES (@c, @t, @level, @by, @at)
            ON CONFLICT (customer_id, target_id) DO UPDATE SET level = EXCLUDED.level,
                granted_by = EXCLUDED.granted_by, granted_at = EXCLUDED.granted_at
            """, conn);
        cmd.Parameters.AddWithValue("c", g.CustomerId);
        cmd.Parameters.AddWithValue("t", g.TargetId);
        cmd.Parameters.AddWithValue("level", g.Level.ToString());
        cmd.Parameters.AddWithValue("by", g.GrantedBy);
        cmd.Parameters.AddWithValue("at", g.GrantedAt.ToUniversalTime());
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            throw new InvalidOperationException("A grant can only go to the customer that owns the target.", ex);
        }
    }

    public async Task RemoveAsync(Guid customerId, Guid targetId, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM grants WHERE customer_id = @c AND target_id = @t", conn);
        cmd.Parameters.AddWithValue("c", customerId);
        cmd.Parameters.AddWithValue("t", targetId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class PgAuditStore : IAuditStore
{
    internal const string InsertSql = """
        INSERT INTO audit_events (id, at, user_id, user_display, customer_id, target_id, operation, path, bytes, result, error, source_ip, correlation_id)
        VALUES (@id, @at, @uid, @ud, @cid, @tid, @op, @path, @bytes, @result, @err, @ip, @corr)
        ON CONFLICT (id) DO NOTHING
        """;

    internal const string QuerySql = """
        SELECT id, at, user_id, user_display, customer_id, target_id, operation, path, bytes, result, error, source_ip, correlation_id
        FROM audit_events
        WHERE (@uid::uuid IS NULL OR user_id = @uid)
          AND (@cid::uuid IS NULL OR customer_id = @cid)
          AND (@tid::uuid IS NULL OR target_id = @tid)
          AND (@op::text IS NULL OR operation = @op)
          AND (@result::text IS NULL OR result = @result)
          AND (@from::timestamptz IS NULL OR at >= @from)
          AND (@to::timestamptz IS NULL OR at <= @to)
        ORDER BY at DESC
        LIMIT @limit
        """;

    private readonly PgsqlContext _db;
    public PgAuditStore(PgsqlContext db) => _db = db;

    public async Task AppendAsync(AuditEvent e, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", e.Id);
        cmd.Parameters.AddWithValue("at", e.At.ToUniversalTime());
        cmd.Parameters.AddWithValue("uid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)e.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ud", e.UserDisplay);
        cmd.Parameters.AddWithValue("cid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)e.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)e.TargetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("op", e.Operation);
        cmd.Parameters.AddWithValue("path", e.Path);
        cmd.Parameters.AddWithValue("bytes", NpgsqlTypes.NpgsqlDbType.Bigint, (object?)e.Bytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("result", e.Result.ToString());
        cmd.Parameters.AddWithValue("err", e.Error);
        cmd.Parameters.AddWithValue("ip", e.SourceIp);
        cmd.Parameters.AddWithValue("corr", e.CorrelationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditQuery q, CancellationToken ct = default)
    {
        await using var conn = await _db.DataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(QuerySql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("uid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)q.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)q.CustomerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tid", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)q.TargetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("op", NpgsqlTypes.NpgsqlDbType.Text, (object?)q.Operation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("result", NpgsqlTypes.NpgsqlDbType.Text, (object?)q.Result?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)q.From?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)q.To?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(q.Limit, 1, 5000));
        return await cmd.ReadAllAsync(r => new AuditEvent(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.NGuid(2),
            r.GetString(3), r.NGuid(4), r.NGuid(5), r.GetString(6), r.GetString(7),
            r.IsDBNull(8) ? null : r.GetInt64(8), r.Enum<AuditResult>(9), r.GetString(10), r.GetString(11), r.GetString(12)), ct);
    }
}

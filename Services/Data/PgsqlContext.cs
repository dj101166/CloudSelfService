using Npgsql;

namespace CloudSelfService.Services.Data;

public sealed class PgsqlContext : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<PgsqlContext> _logger;

    public bool IsAvailable => _dataSource is not null;

    public PgsqlContext(IConfiguration config, ILogger<PgsqlContext> logger)
    {
        _logger = logger;
        var connStr = config.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(connStr))
            _dataSource = NpgsqlDataSource.Create(connStr);
    }

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException("PgsqlContext is not configured (ConnectionStrings:Default missing).");

    // Enums are stored as TEXT holding their names. upload_sessions/upload_chunks are
    // created here but their store arrives with the upload task.
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS customers (
            id                  UUID PRIMARY KEY,
            name                TEXT NOT NULL UNIQUE,
            active              BOOLEAN NOT NULL DEFAULT TRUE,
            notes               TEXT NOT NULL DEFAULT '',
            home_tenant_id      TEXT,
            cloud               TEXT NOT NULL DEFAULT 'Commercial',
            cross_tenant_status TEXT NOT NULL DEFAULT 'NotRequired'
        );

        CREATE TABLE IF NOT EXISTS users (
            id               UUID PRIMARY KEY,
            kind             TEXT NOT NULL,
            customer_id      UUID NULL REFERENCES customers(id),
            upn              TEXT NOT NULL,
            display_name     TEXT NOT NULL DEFAULT '',
            entra_object_id  TEXT NULL UNIQUE,
            staff_role       TEXT NOT NULL DEFAULT 'None',
            active           BOOLEAN NOT NULL DEFAULT TRUE,
            created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            last_sign_in_at  TIMESTAMPTZ NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_users_upn_lower ON users (lower(upn));

        CREATE TABLE IF NOT EXISTS storage_targets (
            id              UUID PRIMARY KEY,
            customer_id     UUID NULL REFERENCES customers(id),
            display_name    TEXT NOT NULL DEFAULT '',
            subscription_id TEXT NOT NULL DEFAULT '',
            resource_group  TEXT NOT NULL DEFAULT '',
            account_name    TEXT NOT NULL,
            kind            TEXT NOT NULL,
            container_name  TEXT NOT NULL,
            endpoint_suffix TEXT NOT NULL DEFAULT 'core.windows.net',
            active          BOOLEAN NOT NULL DEFAULT TRUE,
            UNIQUE (account_name, kind, container_name)
        );

        CREATE TABLE IF NOT EXISTS grants (
            customer_id UUID NOT NULL REFERENCES customers(id),
            target_id   UUID NOT NULL REFERENCES storage_targets(id),
            level       TEXT NOT NULL,
            granted_by  TEXT NOT NULL DEFAULT '',
            granted_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            PRIMARY KEY (customer_id, target_id)
        );

        CREATE OR REPLACE FUNCTION grants_owner_check_fn() RETURNS trigger AS $fn$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM storage_targets t
                           WHERE t.id = NEW.target_id AND t.customer_id = NEW.customer_id) THEN
                RAISE EXCEPTION 'grant customer % does not own storage target %', NEW.customer_id, NEW.target_id
                    USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $fn$ LANGUAGE plpgsql;

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'grants_owner_check') THEN
                CREATE CONSTRAINT TRIGGER grants_owner_check
                    AFTER INSERT OR UPDATE ON grants
                    FOR EACH ROW EXECUTE FUNCTION grants_owner_check_fn();
            END IF;
        END $$;

        CREATE OR REPLACE FUNCTION targets_owner_change_fn() RETURNS trigger AS $fn$
        BEGIN
            IF NEW.customer_id IS DISTINCT FROM OLD.customer_id
               AND EXISTS (SELECT 1 FROM grants g WHERE g.target_id = OLD.id) THEN
                RAISE EXCEPTION 'storage target % owner cannot change while grants exist', OLD.id
                    USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END;
        $fn$ LANGUAGE plpgsql;

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'targets_owner_change') THEN
                CREATE TRIGGER targets_owner_change
                    BEFORE UPDATE ON storage_targets
                    FOR EACH ROW EXECUTE FUNCTION targets_owner_change_fn();
            END IF;
        END $$;

        CREATE INDEX IF NOT EXISTS idx_grants_target ON grants (target_id);

        CREATE TABLE IF NOT EXISTS audit_events (
            id             UUID PRIMARY KEY,
            at             TIMESTAMPTZ NOT NULL,
            user_id        UUID,
            user_display   TEXT NOT NULL DEFAULT '',
            customer_id    UUID,
            target_id      UUID,
            operation      TEXT NOT NULL DEFAULT '',
            path           TEXT NOT NULL DEFAULT '',
            bytes          BIGINT,
            result         TEXT NOT NULL,
            error          TEXT NOT NULL DEFAULT '',
            source_ip      TEXT NOT NULL DEFAULT '',
            correlation_id TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_audit_events_at ON audit_events (at DESC);
        CREATE INDEX IF NOT EXISTS idx_audit_events_customer ON audit_events (customer_id);
        CREATE INDEX IF NOT EXISTS idx_audit_events_target ON audit_events (target_id);

        CREATE TABLE IF NOT EXISTS upload_sessions (
            id           UUID PRIMARY KEY,
            user_id      UUID NOT NULL,
            target_id    UUID NOT NULL,
            final_path   TEXT NOT NULL,
            staging_path TEXT NOT NULL,
            size         BIGINT NOT NULL,
            chunk_size   INT NOT NULL,
            overwrite    BOOLEAN NOT NULL DEFAULT FALSE,
            created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            expires_at   TIMESTAMPTZ NOT NULL,
            status       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS upload_chunks (
            session_id UUID NOT NULL REFERENCES upload_sessions(id) ON DELETE CASCADE,
            idx        INT NOT NULL,
            PRIMARY KEY (session_id, idx)
        );
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_dataSource is null) return;
        _logger.LogInformation("PGSQL: ensuring CloudSelfService schema...");
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(727274001)", conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand(SchemaSql, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("PGSQL: schema ready.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
    }
}

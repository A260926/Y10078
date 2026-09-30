using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CertRotationDrill;

public sealed class IdempotentReplayException(string message) : Exception(message);

public sealed class EventStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public EventStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        CreateSchema();
    }

    public SqliteConnection Connection => _connection;

    public async Task<IDisposable> LockAsync()
    {
        await _writeLock.WaitAsync();
        return new Releaser(_writeLock);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    private void CreateSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                rotation_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                payload TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_events_rotation ON events (rotation_id, seq);

            CREATE TABLE IF NOT EXISTS idempotency (
                idempotency_key TEXT PRIMARY KEY,
                request_hash TEXT NOT NULL,
                rotation_id TEXT NOT NULL,
                status_code INTEGER NOT NULL,
                response TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS issued_serials (
                serial_number TEXT PRIMARY KEY,
                rotation_id TEXT NOT NULL,
                key_id TEXT NOT NULL,
                subject TEXT NOT NULL,
                sans TEXT NOT NULL,
                not_before TEXT NOT NULL,
                not_after TEXT NOT NULL,
                thumbprint TEXT NOT NULL,
                issued_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS kv (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS key_material (
                key_id TEXT PRIMARY KEY,
                nonce BLOB NOT NULL,
                tag BLOB NOT NULL,
                ciphertext BLOB NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public string? GetKv(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM kv WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void PutKv(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO kv (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public bool TryGetIdempotent(string key, string requestHash, out (int StatusCode, string Response, string RotationId) result)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT request_hash, rotation_id, status_code, response
            FROM idempotency WHERE idempotency_key = $key
            """;
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            result = default;
            return false;
        }

        var storedHash = reader.GetString(0);
        var rotationId = reader.GetString(1);
        var statusCode = reader.GetInt32(2);
        var response = reader.GetString(3);
        reader.Close();

        if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
        {
            throw new IdempotentReplayException(
                "idempotency key was reused with a different command payload");
        }

        result = (statusCode, response, rotationId);
        return true;
    }

    public void SaveIdempotent(SqliteTransaction transaction, string key, string requestHash,
        string rotationId, int statusCode, string response, DateTimeOffset now)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO idempotency (idempotency_key, request_hash, rotation_id, status_code, response, created_at)
            VALUES ($key, $hash, $rotation, $status, $response, $created)
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$hash", requestHash);
        command.Parameters.AddWithValue("$rotation", rotationId);
        command.Parameters.AddWithValue("$status", statusCode);
        command.Parameters.AddWithValue("$response", response);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public int NextVersion(SqliteTransaction transaction, string rotationId)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM events WHERE rotation_id = $id";
        command.Parameters.AddWithValue("$id", rotationId);
        return Convert.ToInt32(command.ExecuteScalar()) + 1;
    }

    public (long Seq, int Version) AppendEventWithVersion(SqliteTransaction transaction, string rotationId,
        RotationEvent payload, DateTimeOffset occurredAt, string idempotencyKey)
    {
        var type = payload.GetType().Name;
        var json = JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions);
        var version = NextVersion(transaction, rotationId);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events (rotation_id, version, type, occurred_at, idempotency_key, payload)
            VALUES ($rotation, $version, $type, $at, $key, $payload)
            """;
        command.Parameters.AddWithValue("$rotation", rotationId);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$at", occurredAt.ToString("O"));
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$payload", json);
        command.ExecuteNonQuery();

        using var seqCommand = _connection.CreateCommand();
        seqCommand.Transaction = transaction;
        seqCommand.CommandText = "SELECT last_insert_rowid()";
        var seq = Convert.ToInt64(seqCommand.ExecuteScalar());
        return (seq, version);
    }

    public bool SerialExists(SqliteTransaction transaction, string serial)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM issued_serials WHERE serial_number = $serial";
        command.Parameters.AddWithValue("$serial", serial);
        return command.ExecuteScalar() is not null;
    }

    public void RecordSerial(SqliteTransaction transaction, CertificateDescription cert, string rotationId, DateTimeOffset now)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO issued_serials
                (serial_number, rotation_id, key_id, subject, sans, not_before, not_after, thumbprint, issued_at)
            VALUES
                ($serial, $rotation, $kid, $subject, $sans, $nb, $na, $thumb, $at)
            """;
        command.Parameters.AddWithValue("$serial", cert.SerialNumber);
        command.Parameters.AddWithValue("$rotation", rotationId);
        command.Parameters.AddWithValue("$kid", cert.KeyId);
        command.Parameters.AddWithValue("$subject", cert.Subject);
        command.Parameters.AddWithValue("$sans", JsonSerializer.Serialize(cert.SubjectAlternativeNames, JsonOptions));
        command.Parameters.AddWithValue("$nb", cert.NotBefore.ToString("O"));
        command.Parameters.AddWithValue("$na", cert.NotAfter.ToString("O"));
        command.Parameters.AddWithValue("$thumb", cert.Thumbprint);
        command.Parameters.AddWithValue("$at", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    public long CountIssuedSerials()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM issued_serials";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public List<CertRecord> LoadIssuedCertificates()
    {
        var records = new List<CertRecord>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT serial_number, key_id, subject, sans, not_before, not_after, thumbprint
            FROM issued_serials ORDER BY rowid
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new CertRecord
            {
                SerialNumber = reader.GetString(0),
                KeyId = reader.GetString(1),
                Subject = reader.GetString(2),
                Sans = JsonSerializer.Deserialize<string[]>(reader.GetString(3), JsonOptions) ?? Array.Empty<string>(),
                NotBefore = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                NotAfter = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                Thumbprint = reader.GetString(6)
            });
        }
        return records;
    }

    public List<StoredEvent> LoadEvents()
    {
        var events = new List<StoredEvent>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT seq, rotation_id, version, type, occurred_at, idempotency_key, payload
            FROM events ORDER BY seq
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var seq = reader.GetInt64(0);
            var rotationId = reader.GetString(1);
            var version = reader.GetInt32(2);
            var type = reader.GetString(3);
            var occurredAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);
            var idemKey = reader.GetString(5);
            var payloadJson = reader.GetString(6);
            var payloadType = Type.GetType("CertRotationDrill." + type)
                ?? throw new InvalidOperationException("unknown event type " + type);
            var payload = (RotationEvent)(JsonSerializer.Deserialize(payloadJson, payloadType, JsonOptions)
                ?? throw new InvalidOperationException("bad event payload"));
            events.Add(new StoredEvent(seq, rotationId, version, type, occurredAt, idemKey, payload));
        }
        return events;
    }

    public void Dispose()
    {
        _connection.Dispose();
        _writeLock.Dispose();
    }
}

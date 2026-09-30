using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CertRotationDrill;

public sealed class KeySlot
{
    private readonly byte[] _dataEncryptionKey;

    public KeySlot(string keyFilePath)
    {
        _dataEncryptionKey = LoadOrCreateKeyFile(keyFilePath);
    }

    private static byte[] LoadOrCreateKeyFile(string path)
    {
        if (File.Exists(path))
        {
            var raw = File.ReadAllBytes(path);
            if (raw.Length != 32)
            {
                throw new InvalidOperationException("key slot master key file is corrupt");
            }
            return raw;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, generated);
        TryRestrictPermissions(path);
        return generated;
    }

    private static void TryRestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // permission hardening is best-effort; the file still contains only a local drill key
            }
        }
    }

    public bool Has(SqliteConnection connection, SqliteTransaction? transaction, string keyId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM key_material WHERE key_id = $id";
        command.Parameters.AddWithValue("$id", keyId);
        return command.ExecuteScalar() is not null;
    }

    public void Put(SqliteConnection connection, SqliteTransaction? transaction, string keyId, string privateKey)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(privateKey);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(_dataEncryptionKey, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO key_material (key_id, nonce, tag, ciphertext, created_at)
            VALUES ($id, $nonce, $tag, $ct, $created)
            """;
        command.Parameters.AddWithValue("$id", keyId);
        command.Parameters.AddWithValue("$nonce", nonce);
        command.Parameters.AddWithValue("$tag", tag);
        command.Parameters.AddWithValue("$ct", ciphertext);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}

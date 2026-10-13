using Microsoft.Data.Sqlite;

namespace PonteIPhone.Core.Storage;

/// <summary>Un file del telefono già copiato sul PC.</summary>
public sealed record CopyRecord(string DeviceKey, string FileKey, string Destination, long Size, string Sha256,
    DateTime CopiedAt, DateTime? DeletedFromPhoneAt);

/// <summary>
/// Registro di tutte le copie (SQLite nella cartella dati): serve a riconoscere ciò che è già sul PC
/// anche dopo mesi, a mostrare il bollino "✓ PC" e il filtro "Da copiare", e a ripetere la verifica.
/// </summary>
public sealed class CopyRegistry : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public CopyRegistry(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS copies (
                device TEXT NOT NULL,
                file_key TEXT NOT NULL,
                destination TEXT NOT NULL,
                size INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                copied_at TEXT NOT NULL,
                deleted_at TEXT NULL,
                PRIMARY KEY (device, file_key)
            );
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Tutte le copie di un telefono, per chiave del file.</summary>
    public Dictionary<string, CopyRecord> Load(string deviceKey)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT file_key, destination, size, sha256, copied_at, deleted_at FROM copies WHERE device = $d";
            cmd.Parameters.AddWithValue("$d", deviceKey);
            using var r = cmd.ExecuteReader();
            var result = new Dictionary<string, CopyRecord>(StringComparer.Ordinal);
            while (r.Read())
            {
                result[r.GetString(0)] = new CopyRecord(deviceKey, r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3),
                    DateTime.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                    r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
            }
            return result;
        }
    }

    public CopyRecord? Get(string deviceKey, string fileKey)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT destination, size, sha256, copied_at, deleted_at FROM copies WHERE device = $d AND file_key = $k";
            cmd.Parameters.AddWithValue("$d", deviceKey);
            cmd.Parameters.AddWithValue("$k", fileKey);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new CopyRecord(deviceKey, fileKey, r.GetString(0), r.GetInt64(1), r.GetString(2),
                DateTime.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
        }
    }

    public void Record(string deviceKey, string fileKey, string destination, long size, string sha256)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO copies (device, file_key, destination, size, sha256, copied_at, deleted_at)
                VALUES ($d, $k, $dest, $s, $h, $t, NULL)
                ON CONFLICT(device, file_key) DO UPDATE SET destination = $dest, size = $s, sha256 = $h, copied_at = $t
                """;
            cmd.Parameters.AddWithValue("$d", deviceKey);
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$dest", destination);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$h", sha256);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkDeleted(string deviceKey, string fileKey)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE copies SET deleted_at = $t WHERE device = $d AND file_key = $k";
            cmd.Parameters.AddWithValue("$d", deviceKey);
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Quanti file e in quale cartella principale (per il riepilogo nella barra laterale).</summary>
    public (int Count, DateTime? Last) Summary(string deviceKey)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), MAX(copied_at) FROM copies WHERE device = $d";
            cmd.Parameters.AddWithValue("$d", deviceKey);
            using var r = cmd.ExecuteReader();
            r.Read();
            var count = r.GetInt32(0);
            DateTime? last = r.IsDBNull(1) ? null
                : DateTime.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            return (count, last);
        }
    }

    /// <summary>
    /// Il file registrato è ancora sul PC, intero? Controllo veloce sulla dimensione: l'impronta si
    /// ricontrolla solo con "Ripeti la verifica" (rileggere migliaia di file a ogni apertura sarebbe lento).
    /// </summary>
    public static bool StillOnDisk(CopyRecord r)
    {
        try
        {
            var fi = new FileInfo(r.Destination);
            return fi.Exists && fi.Length == r.Size;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

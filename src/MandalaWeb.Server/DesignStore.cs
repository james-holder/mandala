using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MandalaWeb.Server;

/// <summary>A saved design: its .mandala JSON plus a coloring-page SVG for thumbnails and PDF export.</summary>
public sealed record DesignSummary(long Id, string Name, DateTime UpdatedUtc);

public sealed record DesignUpload(string? Name, string Json, string Svg);

/// <summary>
/// Saved designs in a SQLite file. On Fly.io the file lives on a volume (DESIGNS_DB points at it),
/// so it survives deploys and restarts.
/// </summary>
public sealed class DesignStore
{
    private readonly string _connection;

    public DesignStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        Exec(db, """
            CREATE TABLE IF NOT EXISTS Designs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Json TEXT NOT NULL,
                Svg TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            PRAGMA journal_mode = WAL;
            """);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public List<DesignSummary> List()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, UpdatedUtc FROM Designs ORDER BY UpdatedUtc DESC";
        using var r = cmd.ExecuteReader();
        var result = new List<DesignSummary>();
        while (r.Read()) result.Add(new DesignSummary(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2)).ToUniversalTime()));
        return result;
    }

    public (string Name, string Json)? GetJson(long id) => Get(id, "Json");

    public (string Name, string Svg)? GetSvg(long id) => Get(id, "Svg");

    private (string, string)? Get(long id, string column)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT Name, {column} FROM Designs WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
    }

    /// <summary>
    /// The next automatic name (Mandala_001, Mandala_002…), one higher than any number already used,
    /// so a deleted design's name is never reused.
    /// </summary>
    public string NextName(string prefix = "Mandala")
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Designs";
        using var r = cmd.ExecuteReader();
        var pattern = new Regex("^" + Regex.Escape(prefix) + @"_(\d+)$", RegexOptions.IgnoreCase);
        int highest = 0;
        while (r.Read())
        {
            var m = pattern.Match(r.GetString(0));
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) highest = Math.Max(highest, n);
        }
        return $"{prefix}_{highest + 1:000}";
    }

    public DesignSummary Create(DesignUpload upload)
    {
        string name = Clean(upload.Name) ?? NextName();
        string now = DateTime.UtcNow.ToString("O");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Designs (Name, Json, Svg, CreatedUtc, UpdatedUtc) VALUES ($name, $json, $svg, $now, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$json", upload.Json);
        cmd.Parameters.AddWithValue("$svg", upload.Svg);
        cmd.Parameters.AddWithValue("$now", now);
        long id = (long)cmd.ExecuteScalar()!;
        return new DesignSummary(id, name, DateTime.Parse(now).ToUniversalTime());
    }

    public bool Update(long id, DesignUpload upload)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        string? name = Clean(upload.Name);
        cmd.CommandText = name == null
            ? "UPDATE Designs SET Json = $json, Svg = $svg, UpdatedUtc = $now WHERE Id = $id"
            : "UPDATE Designs SET Name = $name, Json = $json, Svg = $svg, UpdatedUtc = $now WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        if (name != null) cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$json", upload.Json);
        cmd.Parameters.AddWithValue("$svg", upload.Svg);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Rename(long id, string name)
    {
        string? clean = Clean(name);
        if (clean == null) return false;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Designs SET Name = $name, UpdatedUtc = $now WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", clean);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Designs WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Trims a name and removes characters that can't go in a file name. Null if nothing is left.</summary>
    public static string? Clean(string? name)
    {
        if (name == null) return null;
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        string s = new string(name.Trim().Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray());
        if (s.Length > 80) s = s[..80];
        return s.Length == 0 ? null : s;
    }
}

using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MandalaWeb.Server;

/// <summary>A saved design: its .mandala JSON plus a coloring-page SVG for thumbnails and PDF export.</summary>
/// <summary>
/// Kind is "mandala" (Json is a .mandala document), "traced" (Json holds the source image and trace settings)
/// or "page" (Json is a page layout of designs and text).
/// </summary>
public sealed record DesignSummary(long Id, string Name, DateTime UpdatedUtc, long? FolderId = null, string Kind = "mandala");

/// <summary>FolderId and Kind are only used when a design is first saved; moving it later goes through Move.</summary>
public sealed record DesignUpload(string? Name, string Json, string Svg, long? FolderId = null, string? Kind = null);

/// <summary>A folder for organizing designs, such as one per book. Each design is in at most one folder.</summary>
public sealed record FolderSummary(long Id, string Name);

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
            CREATE TABLE IF NOT EXISTS Folders (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            PRAGMA journal_mode = WAL;
            """);
        // Databases from before folders existed get the new column
        if (!HasColumn(db, "Designs", "FolderId"))
            Exec(db, "ALTER TABLE Designs ADD COLUMN FolderId INTEGER NULL");
        if (!HasColumn(db, "Designs", "Kind"))
            Exec(db, "ALTER TABLE Designs ADD COLUMN Kind TEXT NOT NULL DEFAULT 'mandala'");
    }

    private static bool HasColumn(SqliteConnection db, string table, string column)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
        cmd.CommandText = "SELECT Id, Name, UpdatedUtc, FolderId, Kind FROM Designs ORDER BY UpdatedUtc DESC";
        using var r = cmd.ExecuteReader();
        var result = new List<DesignSummary>();
        while (r.Read())
            result.Add(new DesignSummary(r.GetInt64(0), r.GetString(1), DateTime.Parse(r.GetString(2)).ToUniversalTime(),
                r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4)));
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
        string name = Clean(upload.Name) ?? NextName(upload.Kind switch { "traced" => "Image", "page" => "Page", _ => "Mandala" });
        string now = DateTime.UtcNow.ToString("O");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Designs (Name, Json, Svg, CreatedUtc, UpdatedUtc, FolderId, Kind) VALUES ($name, $json, $svg, $now, $now, $folder, $kind);
            SELECT last_insert_rowid();
            """;
        long? folder = upload.FolderId is long f && FolderExists(db, f) ? f : null;
        cmd.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);
        string kind = upload.Kind is "traced" or "page" ? upload.Kind : "mandala";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$json", upload.Json);
        cmd.Parameters.AddWithValue("$svg", upload.Svg);
        cmd.Parameters.AddWithValue("$now", now);
        long id = (long)cmd.ExecuteScalar()!;
        return new DesignSummary(id, name, DateTime.Parse(now).ToUniversalTime(), folder, kind);
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

    // ---------- Folders ----------

    public List<FolderSummary> Folders()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, Name FROM Folders ORDER BY Name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var result = new List<FolderSummary>();
        while (r.Read()) result.Add(new FolderSummary(r.GetInt64(0), r.GetString(1)));
        return result;
    }

    private static bool FolderExists(SqliteConnection db, long id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Folders WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() != null;
    }

    public FolderSummary? CreateFolder(string name)
    {
        string? clean = Clean(name);
        if (clean == null) return null;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO Folders (Name, CreatedUtc) VALUES ($name, $now); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", clean);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return new FolderSummary((long)cmd.ExecuteScalar()!, clean);
    }

    public bool RenameFolder(long id, string name)
    {
        string? clean = Clean(name);
        if (clean == null) return false;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE Folders SET Name = $name WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", clean);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Deletes a folder. Its designs aren't deleted; they go back to Unfiled.</summary>
    public bool DeleteFolder(long id)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var move = db.CreateCommand();
        move.CommandText = "UPDATE Designs SET FolderId = NULL WHERE FolderId = $id";
        move.Parameters.AddWithValue("$id", id);
        move.ExecuteNonQuery();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM Folders WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        bool deleted = cmd.ExecuteNonQuery() > 0;
        tx.Commit();
        return deleted;
    }

    /// <summary>Moves designs into a folder, or to Unfiled when folderId is null. False if the folder doesn't exist.</summary>
    public bool Move(IEnumerable<long> designIds, long? folderId)
    {
        using var db = Open();
        if (folderId is long f && !FolderExists(db, f)) return false;
        using var tx = db.BeginTransaction();
        foreach (long id in designIds)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE Designs SET FolderId = $folder WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
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

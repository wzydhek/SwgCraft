using Microsoft.Data.Sqlite;

namespace SwgCraft.Data;

/// <summary>Owns the connection string and schema migrations (PRAGMA user_version).</summary>
public sealed class SpawnDatabase
{
    public string FilePath { get; }
    readonly string _connectionString;

    public SpawnDatabase(string filePath)
    {
        FilePath = filePath;
        // Pooling off: connections release the file immediately, which keeps backups and test cleanup simple.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath, Pooling = false }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Sql.Exec(c, null, "PRAGMA foreign_keys = ON;");
        return c;
    }

    public void Migrate()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var c = Open();
        Sql.Exec(c, null, "PRAGMA journal_mode = WAL;");
        int version = Convert.ToInt32(Sql.Scalar(c, null, "PRAGMA user_version;"));
        if (version >= Migrations.Length) return;

        if (version > 0)
        {
            var backup = $"{FilePath}.bak-v{version}";
            if (File.Exists(backup)) File.Delete(backup);
            Sql.Exec(c, null, $"VACUUM INTO '{backup.Replace("'", "''")}';");
        }

        for (int v = version; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            Sql.Exec(c, tx, Migrations[v]);
            Sql.Exec(c, tx, $"PRAGMA user_version = {v + 1};");
            tx.Commit();
        }
    }

    // Add new schema versions by appending to this array; never edit an existing entry once released.
    static readonly string[] Migrations = { V1, V2, V3, V4 };

    const string V1 = """
        CREATE TABLE galaxy (
          id     INTEGER PRIMARY KEY,
          name   TEXT NOT NULL,
          source TEXT NOT NULL DEFAULT 'galaxyHarvester'
        );

        CREATE TABLE spawn (
          id               INTEGER PRIMARY KEY,
          galaxy_id        INTEGER NOT NULL REFERENCES galaxy(id),
          name             TEXT NOT NULL CHECK (length(name) > 0 AND name = lower(name) AND name NOT LIKE '% %'),
          resource_type_id TEXT NOT NULL,
          er INTEGER, cr INTEGER, cd INTEGER, dr INTEGER, fl INTEGER, hr INTEGER,
          mb INTEGER, pe INTEGER, oq INTEGER, sr INTEGER, ut INTEGER,
          spawned_utc      TEXT NOT NULL,
          first_seen_utc   TEXT NOT NULL,
          last_seen_utc    TEXT NOT NULL,
          despawned_utc    TEXT,
          UNIQUE (galaxy_id, id)
        );
        CREATE UNIQUE INDEX ux_spawn_current_name ON spawn (galaxy_id, name) WHERE despawned_utc IS NULL;
        CREATE INDEX ix_spawn_lookup ON spawn (galaxy_id, name);
        CREATE INDEX ix_spawn_type_current ON spawn (galaxy_id, resource_type_id) WHERE despawned_utc IS NULL;

        CREATE TABLE spawn_planet (
          spawn_id       INTEGER NOT NULL REFERENCES spawn(id),
          planet         TEXT NOT NULL,
          first_seen_utc TEXT NOT NULL,
          removed_utc    TEXT,
          PRIMARY KEY (spawn_id, planet)
        );

        CREATE TABLE inventory (
          id          INTEGER PRIMARY KEY,
          galaxy_id   INTEGER NOT NULL,
          spawn_id    INTEGER NOT NULL,
          quantity    INTEGER NOT NULL CHECK (quantity >= 0),
          note        TEXT,
          updated_utc TEXT NOT NULL,
          UNIQUE (galaxy_id, spawn_id),
          FOREIGN KEY (galaxy_id, spawn_id) REFERENCES spawn (galaxy_id, id)
        );

        CREATE TABLE sync_log (
          id               INTEGER PRIMARY KEY,
          source           TEXT NOT NULL,
          galaxy_id        INTEGER NOT NULL,
          started_utc      TEXT NOT NULL,
          finished_utc     TEXT NOT NULL,
          status           TEXT NOT NULL,
          spawns_added     INTEGER NOT NULL DEFAULT 0,
          spawns_updated   INTEGER NOT NULL DEFAULT 0,
          spawns_reopened  INTEGER NOT NULL DEFAULT 0,
          spawns_replaced  INTEGER NOT NULL DEFAULT 0,
          spawns_despawned INTEGER NOT NULL DEFAULT 0,
          unmapped_count   INTEGER NOT NULL DEFAULT 0,
          rejected_count   INTEGER NOT NULL DEFAULT 0,
          message          TEXT
        );
        """;

    const string V2 = "ALTER TABLE spawn ADD COLUMN source TEXT NOT NULL DEFAULT 'feed';";

    const string V3 = """
       ALTER TABLE spawn ADD COLUMN filtered INTEGER NOT NULL DEFAULT 0;
       ALTER TABLE spawn ADD COLUMN generic  INTEGER NOT NULL DEFAULT 0;
       ALTER TABLE inventory ADD COLUMN price_per_unit REAL;
       """;

    // Inventory of looted and crafted items. An item is a looted item or a schematic's product (by ID from the static data).
    // serial '' means "no serial number"; it is not NULL so that UNIQUE keeps one stack per item + serial.
    const string V4 = """
       CREATE TABLE item_inventory (
         id          INTEGER PRIMARY KEY,
         galaxy_id   INTEGER NOT NULL REFERENCES galaxy(id),
         item_kind   TEXT NOT NULL CHECK (item_kind IN ('LootItem', 'Schematic')),
         item_id     TEXT NOT NULL CHECK (length(item_id) > 0),
         serial      TEXT NOT NULL DEFAULT '',
         quantity    INTEGER NOT NULL CHECK (quantity > 0),
         note        TEXT,
         updated_utc TEXT NOT NULL,
         UNIQUE (galaxy_id, item_kind, item_id, serial)
       );
       """;
}

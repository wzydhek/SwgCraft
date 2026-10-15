using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SwgCraft.Data;

/// <summary>Timestamps are stored as UTC ISO-8601 text so they sort correctly and read well in a DB browser.</summary>
public static class DbTime
{
    public static string ToDb(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset FromDb(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}

internal static class Sql
{
    public static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, tx, sql, ps);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = Cmd(c, tx, sql, ps);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }
}

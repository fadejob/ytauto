using System.Text.RegularExpressions;
using Npgsql;

namespace YtAuto.Infrastructure.Data;

/// <summary>
/// Online baza sxemasi: yagona manba <c>bot/db/schema.sql</c> (exe ichiga joylangan). Faqat jadval egasi (bot yoki eski
/// "postgres" ulanish satri) ishga tushiradi; qurilma rollari (ytd_*) sxemani o'zgartira olmaydi va migratsiya qilmaydi.
/// Versiya o'zgarmagan bo'lsa fayl qayta bajarilmaydi.
/// </summary>
public static class SchemaMigrator
{
    private static string? _sql;

    public static string Sql
    {
        get
        {
            if (_sql is not null) return _sql;
            using var s = typeof(SchemaMigrator).Assembly.GetManifestResourceStream("YtAuto.schema.sql")
                ?? throw new InvalidOperationException("schema.sql exe ichida topilmadi.");
            using var r = new StreamReader(s);
            return _sql = r.ReadToEnd();
        }
    }

    public static string Version => Regex.Match(Sql, @"ytauto-schema-version:\s*(\S+)").Groups[1].Value;

    /// <summary>Sxema nomi "ytauto" dan boshqa bo'lsa (testlar vaqtinchalik sxemada ishlaydi) jadval nomlari almashtiriladi.</summary>
    public static string ForSchema(string schema) =>
        schema == "ytauto" ? Sql : Regex.Replace(Sql, @"\bytauto\b", schema); // ytauto_device/ytauto_bot/ytauto_ctx o'zgarmaydi ("_" so'z belgisi)

    public static bool IsDeviceRole(NpgsqlConnection c)
    {
        using var cmd = new NpgsqlCommand("SELECT session_user::text", c);
        return (cmd.ExecuteScalar() as string ?? "").StartsWith("ytd_", StringComparison.Ordinal);
    }

    /// <summary>Kerak bo'lsa migratsiya qiladi. true - bajarildi; false - kerak emas edi yoki qurilma roli.</summary>
    public static bool EnsureMigrated(NpgsqlConnection c, string schema = "ytauto")
    {
        if (IsDeviceRole(c)) return false;
        using (var cmd = new NpgsqlCommand($"SELECT to_regclass('{schema}.schema_meta') IS NOT NULL", c))
            if (cmd.ExecuteScalar() is true)
            {
                using var v = new NpgsqlCommand($"SELECT value FROM {schema}.schema_meta WHERE key = 'version'", c);
                if (v.ExecuteScalar() as string == Version) return false;
            }
        using var tx = c.BeginTransaction();
        using (var run = new NpgsqlCommand(ForSchema(schema), c, tx)) { run.CommandTimeout = 120; run.ExecuteNonQuery(); }
        tx.Commit();
        return true;
    }
}

using Npgsql;
using NpgsqlTypes;
using YtAuto.Core.Visuals;

namespace YtAuto.Infrastructure.Data;

/// <summary>
/// Umumiy rasm kutubxonasi (ytauto.assets, schema v9+): to'liq matnli qidiruv va kanal bo'yicha ishlatilganlarni yozish.
/// Jadval hali yo'q (baza migratsiya qilinmagan) bo'lsa - <see cref="AssetLibraryUnavailableException"/>: worker stok qidiruvga o'tadi.
/// </summary>
public sealed partial class PostgresJobStore
{
    /// <summary>
    /// Kalit so'zlar YOKI bilan birlashtiriladi (websearch_to_tsquery: sintaksis xatosi bo'lmaydi). Reyting: matn mosligi (sarlavha >
    /// teglar > tavsif) × sifat × kerakli turdagi afzallik × sahna yo'nalishiga moslik. Shu kanalda yaqinda ishlatilganlar chiqmaydi.
    /// </summary>
    public Task<IReadOnlyList<AssetHit>> SearchAssetsAsync(AssetQuery q, CancellationToken ct) => Task.Run(() => SearchAssets(q), ct);

    Task<IReadOnlyList<AssetHit>> IAssetCatalog.SearchAsync(AssetQuery query, CancellationToken ct) => SearchAssetsAsync(query, ct);

    public static string TsQuery(IEnumerable<string> keywords) =>
        string.Join(" or ", keywords.Select(k => System.Text.RegularExpressions.Regex.Replace(k ?? "", "[\"'\\\\:&|!()<>-]", " ").Trim())
            .Where(k => k.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Select(k => k.Contains(' ') ? $"\"{k}\"" : k));

    private IReadOnlyList<AssetHit> SearchAssets(AssetQuery q)
    {
        var text = TsQuery(q.Keywords);
        if (text.Length == 0 || q.Kinds.Count == 0 || q.Limit <= 0) return Array.Empty<AssetHit>();
        try
        {
            using var c = Open();
            using var cmd = Cmd(c, $"""
                SELECT a.id, a.source, a.kind, a.title, a.file_url, a.preview_url, a.width, a.height, a.has_alpha, a.license, a.license_url,
                       a.author, a.page_url, a.needs_credit,
                       ts_rank_cd(a.search, q, 32) * (0.5 + a.quality)
                         * (1.0 - 0.12 * (array_position(@kinds, a.kind) - 1))
                         * (CASE WHEN @orient = '' THEN 1.0
                                 WHEN @orient = 'portrait' AND a.height > a.width * 1.1 THEN 1.25
                                 WHEN @orient = 'landscape' AND a.width > a.height * 1.1 THEN 1.25
                                 ELSE 1.0 END) AS rank
                FROM {T("assets")} a, websearch_to_tsquery('english', @q) q
                WHERE NOT a.disabled AND a.search @@ q AND a.kind = ANY(@kinds) AND GREATEST(a.width, a.height) >= @minlong
                  AND NOT (a.id = ANY(@exclude))
                  AND NOT EXISTS (SELECT 1 FROM {T("asset_usage")} u WHERE u.asset_id = a.id AND u.channel_id = @ch
                                  AND u.used_at_utc > now() - make_interval(days => @days))
                ORDER BY rank DESC, a.quality DESC, a.id
                LIMIT @lim
                """,
                ("q", text), ("orient", q.Orientation ?? ""), ("minlong", q.MinLongSide), ("ch", q.ChannelId ?? ""),
                ("days", Math.Max(0, q.RecentDays)), ("lim", q.Limit));
            cmd.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = q.Kinds.ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("exclude", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = (q.ExcludeIds ?? Array.Empty<long>()).ToArray() });
            using var r = cmd.ExecuteReader();
            var list = new List<AssetHit>();
            while (r.Read())
                list.Add(new AssetHit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetInt32(6), r.GetInt32(7), r.GetBoolean(8), r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12),
                    r.GetBoolean(13), r.GetDouble(14)));
            return list;
        }
        catch (PostgresException e) when (e.SqlState is "42P01" or "42501" or "42703")
        {
            throw new AssetLibraryUnavailableException("Rasm kutubxonasi bazada yo'q (sxema v9 qo'llanmagan): " + e.MessageText, e);
        }
    }

    public Task RecordUsageAsync(IReadOnlyCollection<long> assetIds, string channelId, long jobId, CancellationToken ct) => Task.Run(() =>
    {
        if (assetIds.Count == 0) return;
        try
        {
            using var c = Open();
            using var cmd = Cmd(c, $"INSERT INTO {T("asset_usage")}(asset_id, channel_id, job_id) SELECT unnest(@ids), @ch, @job",
                ("ch", channelId ?? ""), ("job", jobId));
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = assetIds.Distinct().ToArray() });
            cmd.ExecuteNonQuery();
        }
        catch (PostgresException e) when (e.SqlState is "42P01" or "42501" or "23503")
        {
            throw new AssetLibraryUnavailableException("Kutubxonadan foydalanish yozilmadi: " + e.MessageText, e);
        }
    }, ct);
}

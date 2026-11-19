using System.Text;
using System.Text.Json;
using YtAuto.Core.Domain;

namespace YtAuto.Core.Pipeline;

/// <summary>Montajga beriladigan fon musiqasi: fayl (film/ ichida nisbiy), trek ma'lumoti va ovoz balandligi.</summary>
public sealed record MusicPick(string File, MusicTrack Track, double Volume, double DurationSec);

/// <summary>
/// Fon musiqasi: botdagi kutubxonadan (CC0 / CC BY) shu vazifaga navbat bilan bitta trek tanlanadi, kompyuter keshidan
/// film/assets/ ga nusxalanadi va Claude'ga alohida audio trekda past ovozda qo'yish aytiladi. Tanlangan trek music.json'ga yoziladi -
/// upload bosqichi muallifni YouTube tavsifiga qo'shadi. Har qanday xato videoni to'xtatmaydi (musiqasiz davom etadi).
/// </summary>
public sealed partial class JobRunner
{
    private const string MusicRecordFile = "music.json";

    private async Task<MusicPick?> PrepareMusicAsync(JobContext c, double videoSec)
    {
        var record = Path.Combine(c.RecordDir, MusicRecordFile);
        var assets = Path.Combine(c.FilmDir, "assets");
        var track = c.S.Music.PickFor(c.Job.Id);
        if (track is null || _x.Music is null) { TryDelete(record); return null; } // shablonning o'z musiqasiga tegilmaydi
        try
        {
            // Oldingi urinishdagi (boshqa kengaytmali) nusxa qolmasin.
            foreach (var old in Directory.Exists(assets) ? Directory.GetFiles(assets, "music.*") : Array.Empty<string>()) TryDelete(old);
            var local = await _x.Music.GetLocalAsync(track, c.Log, c.Ct).ConfigureAwait(false);
            var ext = Path.GetExtension(local).ToLowerInvariant();
            if (ext is not (".mp3" or ".ogg" or ".wav" or ".flac" or ".m4a")) ext = ".mp3";
            Directory.CreateDirectory(assets);
            var rel = "assets/music" + ext;
            File.Copy(local, Path.Combine(c.FilmDir, "assets", "music" + ext), true);
            double dur = track.DurationSec > 0 ? track.DurationSec : 0;
            try { var info = await _x.Media.ProbeAsync(local, c.Ct).ConfigureAwait(false); if (info.Duration > 0) dur = info.Duration; } catch { /* Openverse davomiyligi yetadi */ }
            var pick = new MusicPick(rel, track, Math.Clamp(c.S.Music.Volume, 0.03, 0.2), dur);
            File.WriteAllText(record, JsonSerializer.Serialize(pick, AppSettings.Json), new UTF8Encoding(false));
            c.StageMetrics["music_sec"] = dur;
            c.Log.Info($"Fon musiqasi: «{track.Title}» — {track.Creator} ({track.LicenseLabel}, {dur:0}s, ovoz {pick.Volume:0.00}); video {videoSec:0}s.", "Edit");
            return pick;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.Log.Warn($"Fon musiqasini tayyorlab bo'lmadi, video musiqasiz davom etadi: {e.Message}", "Edit");
            TryDelete(record);
            return null;
        }
    }

    /// <summary>YouTube tavsifi uchun muallif qatori (musiqa qo'yilmagan bo'lsa "").</summary>
    internal string MusicCreditsBlock(JobContext c)
    {
        try
        {
            var p = Path.Combine(c.RecordDir, MusicRecordFile);
            return File.Exists(p) ? JsonSerializer.Deserialize<MusicPick>(File.ReadAllText(p), AppSettings.Json)?.Track.Credit() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>Rasm va musiqa mualliflari bitta blokda.</summary>
    internal static string JoinCredits(params string[] blocks) => string.Join("\n\n", blocks.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()));

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}

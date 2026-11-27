using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YtAuto.Core.Domain;

namespace YtAuto.Core.Text;

/// <summary>Bitta aytilgan so'z va uning vaqti (voice.vtt dagi so'z teglaridan).</summary>
public sealed record CaptionWord(string Text, double Start, double End);

/// <summary>Ekranda birga ko'rinadigan qisqa ibora (2-5 so'z).</summary>
public sealed record CaptionChunk(double Start, double End, IReadOnlyList<CaptionWord> Words);

/// <summary>
/// Dizaynli subtitrlar: OpenFilm'ning oddiy "kuydirilgan" subtitri o'rniga worker film.html'ning eng yuqori yo'lagiga o'z sahifasini
/// (captions.html) qo'yadi. So'zlar ovoz bilan birga yonadi (karaoke), iboralar qisqa (Reels/Shorts uslubi), uslub sozlamadan.
/// Sahifa faqat t ga bog'liq (OpenFilm qoidasi), so'z vaqtlari ichiga JSON bo'lib yoziladi.
/// </summary>
public static class Captions
{
    public const string PageFile = "captions.html";
    public const string TrackMarker = "ytauto-captions";
    private static readonly Regex TimeLine = new(@"^\s*([\d:.,]+)\s*-->\s*([\d:.,]+)", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"<(\d{1,2}:\d{2}:\d{2}[.,]\d{1,3}|\d{1,2}:\d{2}[.,]\d{1,3})>", RegexOptions.Compiled);
    private static readonly Regex OtherTag = new(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);

    /// <summary>
    /// VTT'dan so'zlar: cue'ning birinchi so'zi cue boshida, har &lt;vaqt&gt; tegi keyingi so'zning boshlanishi; so'z keyingisi boshlanguncha
    /// (cue'ning oxirgisi - cue tugaguncha) davom etadi.
    /// </summary>
    public static List<CaptionWord> ParseWords(string vtt)
    {
        var words = new List<CaptionWord>();
        var lines = vtt.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimeLine.Match(lines[i]);
            if (!m.Success) continue;
            var cs = VttParser.ParseTs(m.Groups[1].Value); var ce = VttParser.ParseTs(m.Groups[2].Value);
            var text = new StringBuilder();
            int j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length > 0) { text.Append(' ').Append(lines[j]); j++; }
            i = j;
            var body = OtherTag.Replace(text.ToString(), "");
            var parts = WordTag.Split(body); // [text0, ts1, text1, ts2, text2 ...]
            var segs = new List<(double Start, string Text)> { (cs, parts[0]) };
            for (int k = 1; k + 1 < parts.Length; k += 2) segs.Add((VttParser.ParseTs(parts[k]), parts[k + 1]));
            var cue = new List<(double, string)>();
            foreach (var (st, tx) in segs)
            {
                var ws = tx.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < ws.Length; k++) cue.Add((st + k * 0.05, ws[k]));
            }
            for (int k = 0; k < cue.Count; k++)
            {
                var start = Math.Clamp(cue[k].Item1, cs, ce);
                var end = k + 1 < cue.Count ? Math.Clamp(cue[k + 1].Item1, start, ce) : ce;
                words.Add(new CaptionWord(cue[k].Item2, Math.Round(start, 3), Math.Round(Math.Max(end, start + 0.05), 3)));
            }
        }
        return words;
    }

    /// <summary>
    /// Iboralarga bo'lish: ko'pi bilan <paramref name="maxWords"/> so'z va <paramref name="maxChars"/> belgi; gap oxiri (. ! ? : ;) va
    /// vergulda, hamda 0.5 s dan uzun pauzada uziladi. Ibora keyingisi boshlanguncha (ko'pi bilan oxirgi so'zdan 0.5 s keyin) turadi.
    /// </summary>
    public static List<CaptionChunk> Chunk(IReadOnlyList<CaptionWord> words, int maxWords, int maxChars)
    {
        var chunks = new List<List<CaptionWord>>();
        var cur = new List<CaptionWord>();
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (cur.Count > 0)
            {
                var chars = cur.Sum(x => x.Text.Length + 1) + w.Text.Length;
                var gap = w.Start - cur[^1].End;
                var stop = Regex.IsMatch(cur[^1].Text, @"[.!?:;…]$") || (cur.Count >= 2 && cur[^1].Text.EndsWith(','));
                if (cur.Count >= maxWords || chars > maxChars || gap > 0.5 || stop) { chunks.Add(cur); cur = new(); }
            }
            cur.Add(w);
        }
        if (cur.Count > 0) chunks.Add(cur);
        var list = new List<CaptionChunk>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var end = c[^1].End + 0.5;
            if (i + 1 < chunks.Count) end = Math.Min(end, chunks[i + 1][0].Start);
            list.Add(new CaptionChunk(c[0].Start, Math.Max(end, c[^1].End), c));
        }
        return list;
    }

    /// <summary>Format va sozlamaga qarab iborada nechta so'z.</summary>
    public static int WordsPerChunk(SubtitleSettings s, VideoFormat f) =>
        s.MaxWords > 0 ? Math.Clamp(s.MaxWords, 1, 8) : f == VideoFormat.Landscape16x9 ? 5 : 3;

    /// <summary>Subtitr markazining balandligi (kadr balandligiga nisbatan): pastki 22% (sahifalar u yerga matn qo'ymaydi).</summary>
    public static double PositionOf(SubtitleSettings s, VideoFormat f) =>
        s.Position > 0 ? Math.Clamp(s.Position, 0.5, 0.92) : f == VideoFormat.Landscape16x9 ? 0.86 : 0.80;

    public static string BuildPage(IReadOnlyList<CaptionChunk> chunks, SubtitleSettings s, int w, int h, double duration, VideoFormat format)
    {
        var inv = CultureInfo.InvariantCulture;
        var style = SubtitleSettings.Styles.Contains(s.Style) ? s.Style : "bold";
        var size = Math.Round(Math.Min(w, h) * (format == VideoFormat.Landscape16x9 ? 0.068 : 0.082) * Math.Clamp(s.FontScale, 0.5, 2.0));
        if (style == "clean") size = Math.Round(size * 0.78);
        var data = JsonSerializer.Serialize(chunks.Select(c => new
        {
            s = Math.Round(c.Start, 3), e = Math.Round(c.End, 3),
            w = c.Words.Select(x => new { t = s.Uppercase && style != "clean" ? x.Text.ToUpperInvariant() : x.Text, s = Math.Round(x.Start, 3), e = Math.Round(x.End, 3) }),
        }));
        string Color(string v, string d) => Regex.IsMatch(v ?? "", "^#[0-9a-fA-F]{6}$") ? v : d;
        var text = Color(s.TextColor, "#FFFFFF"); var hi = Color(s.HighlightColor, "#FFD60A"); var box = Color(s.BoxColor, "#111111");
        var font = string.IsNullOrWhiteSpace(s.Font) ? "\"Segoe UI\", \"Arial Black\", Arial, sans-serif" : $"\"{s.Font.Replace("\"", "")}\", \"Segoe UI\", Arial, sans-serif";
        var css = style switch
        {
            "box" => $".line{{background:{box}d9;border-radius:{size * 0.32:0}px;padding:{size * 0.16:0}px {size * 0.34:0}px;box-shadow:0 10px 30px #0008}}" +
                     $".w{{color:{text}}} .w.on{{background:{hi};color:{box};border-radius:{size * 0.18:0}px;padding:0 {size * 0.14:0}px;margin:0 -{size * 0.14:0}px}}",
            "clean" => $".w{{color:{text};opacity:.72;text-shadow:0 2px 12px #000c,0 0 3px #000}} .w.on,.w.past{{opacity:1}} .w.on{{color:{hi}}}",
            _ => $".w{{color:{text};-webkit-text-stroke:{Math.Max(2, size * 0.11):0}px #000;paint-order:stroke fill;text-shadow:0 {size * 0.06:0}px {size * 0.18:0}px #000b}}" +
                 $" .w.on{{color:{hi}}}",
        };
        var weight = style == "clean" ? 700 : 900;
        var pos = PositionOf(s, format);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><style>\n");
        sb.Append("html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}\n");
        sb.Append($"#cap{{position:absolute;left:6%;right:6%;top:{pos * 100:0.##}%;transform:translateY(-50%);display:flex;justify-content:center;text-align:center;" +
                  $"font-family:{font};font-weight:{weight};font-size:{size:0}px;line-height:1.12;letter-spacing:{(style == "clean" ? "0" : "-0.01em")}}}\n");
        sb.Append(".line{display:inline-block;max-width:100%;transform-origin:50% 60%}\n");
        sb.Append($".w{{display:inline-block;margin:0 {size * 0.12:0}px;transform-origin:50% 70%}}\n{css}\n</style></head><body><div id=\"cap\"></div>\n");
        sb.Append("<script>\n");
        sb.Append($"const C={data};const D={duration.ToString("0.###", inv)};const STYLE=\"{style}\";\n");
        sb.Append("""
const cap=document.getElementById("cap");let shown=-1;
const ease=(p)=>1-Math.pow(1-Math.min(1,Math.max(0,p)),3);
function chunkAt(t){let lo=0,hi=C.length-1,r=-1;while(lo<=hi){const m=(lo+hi)>>1;if(C[m].s<=t){r=m;lo=m+1;}else hi=m-1;}return r>=0&&t<C[r].e?r:-1;}
function build(i){cap.innerHTML="";if(i<0)return;const line=document.createElement("div");line.className="line";
  for(const w of C[i].w){const s=document.createElement("span");s.className="w";s.textContent=w.t;line.appendChild(s);line.appendChild(document.createTextNode(" "));}
  cap.appendChild(line);fit(line);}
function fit(line){line.style.fontSize="";const max=cap.clientWidth;let k=1;for(let n=0;n<6&&line.scrollWidth>max*1.0;n++){k*=0.9;line.style.fontSize=(k*100)+"%";}
  if(line.offsetHeight>parseFloat(getComputedStyle(cap).fontSize)*2.5){line.style.fontSize=(k*0.85*100)+"%";}}
window.film={duration:D,ready:document.fonts?document.fonts.ready:null,frame(t){
  const i=chunkAt(t);if(i!==shown){build(i);shown=i;}if(i<0)return;
  const c=C[i],line=cap.firstChild,p=ease((t-c.s)/0.14);
  line.style.opacity=String(Math.min(1,(t-c.s)/0.08+0.2));line.style.transform=`scale(${0.9+0.1*p}) translateY(${(1-p)*14}px)`;
  [...line.querySelectorAll(".w")].forEach((el,k)=>{const w=c.w[k];const on=t>=w.s&&t<w.e,past=t>=w.e;
    el.className="w"+(on?" on":"")+(past?" past":"");
    if(STYLE==="karaoke"){const q=ease((t-w.s)/0.12);el.style.opacity=t<w.s?"0":String(q);el.style.transform=`translateY(${(1-q)*18}px) scale(${on?1.08:1})`;}
    else if(STYLE==="clean"){el.style.transform="";}
    else{const q=on?ease((t-w.s)/0.12):1;el.style.transform=on?`scale(${1+0.14*(1-Math.abs(1-2*Math.min(1,q)))+0.06})`:"scale(1)";}
  });}};
</script></body></html>
""");
        return sb.ToString();
    }

    /// <summary>
    /// film.html'ga subtitr yo'lagini (eng yuqorida, butun video bo'ylab) qo'yadi; oldingisi bo'lsa almashtiriladi. Qaytaradi: yangi film.html.
    /// </summary>
    public static string InsertTrack(string filmHtml, double duration)
    {
        var inv = CultureInfo.InvariantCulture;
        var cleaned = Regex.Replace(filmHtml, @"\s*<section[^>]*data-ytauto=""" + TrackMarker + @"""[^>]*>[\s\S]*?</section>", "");
        var track = $"\n<section data-ytauto=\"{TrackMarker}\">\n  <iframe src=\"{PageFile}#t=0,{duration.ToString("0.###", inv)}\" at=\"0\" id=\"{TrackMarker}\"></iframe>\n</section>";
        var m = Regex.Match(cleaned, @"<body[^>]*>", RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidOperationException("film.html da <body> yo'q");
        return cleaned.Insert(m.Index + m.Length, track);
    }
}

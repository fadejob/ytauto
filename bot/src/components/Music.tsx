"use client";

import { useCallback, useEffect, useState } from "react";
import { api, cache, confirmBox, haptic, openExternal } from "@/lib/client";
import type { MusicCandidate, MusicLibrary } from "@/lib/types";
import type { Toast } from "./App";

/** Kayfiyatlar: Openverse'da inglizcha qidiruv so'zi. */
const MOODS: [string, string][] = [
  ["calm", "Tinch"], ["cinematic", "Kinematik"], ["inspiring", "Ilhomlantiruvchi"], ["documentary", "Hujjatli"],
  ["ambient", "Ambient"], ["piano", "Pianino"], ["acoustic", "Akustik"], ["electronic", "Elektron"],
];
const PAGE = 10;
const VOLUMES = [0.05, 0.08, 0.1, 0.12, 0.15, 0.2];

/** Bitta umumiy pleyer (bir vaqtda bitta trek chalinadi). */
let player: HTMLAudioElement | null = null;
function getPlayer(): HTMLAudioElement {
  if (!player) player = new Audio();
  return player;
}

type Pages = { q: string; pages: MusicCandidate[][]; at: number; total: number; pageCount: number };

const dur = (s: number) => (s > 0 ? `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}` : "—");
const lic = (l: string, v: string) => (l === "cc0" ? "CC0" : `CC ${l.toUpperCase()}${v ? " " + v : ""}`);

/**
 * Openverse'dan (CC0 / CC BY musiqa) bitta sahifa - brauzer to'g'ridan-to'g'ri so'raydi (CORS ochiq; cheklov har foydalanuvchiga alohida).
 * length=short,medium - 30 soniyadan 10 daqiqagacha.
 */
async function fetchPage(q: string, page: number): Promise<{ items: MusicCandidate[]; total: number; pageCount: number }> {
  const u = `https://api.openverse.org/v1/audio/?q=${encodeURIComponent(q)}&category=music&license=cc0,by&length=short,medium&page_size=${PAGE}&page=${page}`;
  const r = await fetch(u, { cache: "no-store" });
  if (r.status === 429) throw new Error("Openverse so'rovlar chegarasi tugadi — bir daqiqadan keyin qayta urinib ko'ring.");
  if (r.status === 400 || r.status === 401) throw new Error("Bu qidiruvda boshqa natija yo'q.");
  if (!r.ok) throw new Error(`Openverse javob bermadi (HTTP ${r.status}).`);
  const j = (await r.json()) as {
    result_count?: number; page_count?: number;
    results?: { id: string; title?: string; creator?: string; license?: string; license_version?: string; duration?: number | null; genres?: string[] | null; url?: string; foreign_landing_url?: string }[];
  };
  const items = (j.results ?? []).filter((x) => x.url).map((x) => ({
    id: x.id, title: x.title?.trim() || "Nomsiz trek", creator: x.creator?.trim() ?? "", license: (x.license ?? "").toLowerCase(), licenseVersion: x.license_version ?? "",
    durationSec: Math.round((x.duration ?? 0) / 1000), genres: (x.genres ?? []).slice(0, 4), previewUrl: x.url!, landingUrl: x.foreign_landing_url ?? "",
  }));
  return { items, total: j.result_count ?? 0, pageCount: j.page_count ?? 0 };
}

/**
 * Fon musiqasi: Openverse ro'yxati 10 tadan («Boshqa 10 ta» - keyingi sahifa, «Ortga» - oldin yuklanganlar, ular saqlanib qoladi),
 * tinglab ko'rish va «Ishlatish» - trek serverga yuklanadi va kompyuter videolarga fon musiqasi sifatida qo'yadi.
 */
export default function Music({ toast }: { toast: Toast }) {
  const [lib, setLib] = useState<MusicLibrary | null>(null);
  // Oldin yuklangan sahifalar keshda: bo'lim qayta ochilganda ham «Ortga» bilan ko'rinadi.
  const [res, setRes] = useState<Pages | null>(() => cache.get<Pages>("music:pages"));
  const [draft, setDraft] = useState(() => cache.get<Pages>("music:pages")?.q ?? MOODS[0][0]);
  const [loading, setLoading] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [playing, setPlaying] = useState<string | null>(null);

  useEffect(() => { api<MusicLibrary>("/api/music").then(setLib).catch((e) => setErr(e instanceof Error ? e.message : String(e))); }, []);
  useEffect(() => { if (res) cache.set("music:pages", res); }, [res]);
  useEffect(() => () => { player?.pause(); }, []);

  const load = useCallback(async (query: string, pageNo: number, base: Pages | null) => {
    setLoading(true); setErr(null);
    try {
      const r = await fetchPage(query, pageNo);
      if (r.items.length === 0 && pageNo > 1) { setErr("Bu qidiruvda boshqa trek qolmadi."); return; }
      const prev = base && base.q === query ? base.pages : [];
      const pages = [...prev.slice(0, pageNo - 1), r.items];
      setRes({ q: query, pages, at: pageNo - 1, total: r.total, pageCount: r.pageCount });
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
    finally { setLoading(false); }
  }, []);

  // Birinchi ochilishda (keshda hech narsa bo'lmasa) birinchi 10 ta.
  useEffect(() => {
    if (cache.get<Pages>("music:pages")) return;
    let live = true;
    const q0 = MOODS[0][0];
    fetchPage(q0, 1)
      .then((r) => { if (live) setRes({ q: q0, pages: [r.items], at: 0, total: r.total, pageCount: r.pageCount }); })
      .catch((e) => { if (live) setErr(e instanceof Error ? e.message : String(e)); });
    return () => { live = false; };
  }, []);

  function search(query: string) {
    const v = query.trim().slice(0, 60);
    if (!v) return;
    haptic.tap(); setDraft(v);
    if (v === res?.q) { setRes({ ...res, at: 0 }); return; }
    void load(v, 1, null);
  }

  function next() {
    if (!res) return;
    haptic.tap();
    const at = res.at + 1;
    if (res.pages[at]) setRes({ ...res, at }); // oldin yuklangan - qayta so'ralmaydi
    else void load(res.q, at + 1, res);
  }
  function back() { if (res && res.at > 0) { haptic.tap(); setRes({ ...res, at: res.at - 1 }); } }

  function toggle(id: string, src: string) {
    const a = getPlayer();
    if (playing === id) { a.pause(); setPlaying(null); return; }
    a.pause(); a.src = src; a.currentTime = 0;
    a.onended = () => setPlaying(null);
    a.onerror = () => { setPlaying(null); toast("Trekni ochib bo'lmadi.", true); };
    void a.play().then(() => setPlaying(id)).catch(() => { setPlaying(null); toast("Tinglashni boshlab bo'lmadi.", true); });
  }

  async function use(t: MusicCandidate) {
    haptic.tap(); setBusy(t.id);
    try {
      const r = await api<{ library: MusicLibrary; message: string }>("/api/music", { action: "add", id: t.id });
      setLib(r.library); haptic.ok(); toast(r.message);
    } catch (e) { haptic.fail(); toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  async function remove(id: string, title: string) {
    if (!(await confirmBox(`«${title}» kutubxonadan olib tashlansinmi? Keyingi videolarda ishlatilmaydi.`))) return;
    setBusy(id);
    try { const r = await api<{ library: MusicLibrary; message: string }>("/api/music", { action: "remove", id }); setLib(r.library); toast(r.message); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setBusy(null);
  }

  async function settings(patch: { enabled?: boolean; volume?: number }) {
    try { const r = await api<{ library: MusicLibrary }>("/api/music", { action: "settings", ...patch }); setLib(r.library); haptic.ok(); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
  }

  const page = res?.pages[res.at] ?? [];
  const inLib = new Set(lib?.tracks.map((t) => t.id) ?? []);
  const lastPage = !!res && res.pageCount > 0 && res.at + 1 >= res.pageCount;

  return (
    <>
      <div className="sect">Fon musiqasi {lib ? `(${lib.tracks.length})` : ""}</div>
      <div className="card formcard">
        <div className="hint">
          Ochiq litsenziyali musiqa (CC0 / CC BY, Openverse orqali, asosan Jamendo). Tinglab ko&apos;ring, yoqqanini «Ishlatish» bilan qo&apos;shing —
          trek serverga yuklanadi, kompyuter har videoga kutubxonadan navbat bilan bittasini qo&apos;yadi. CC BY talabi bo&apos;yicha muallif YouTube tavsifiga avtomatik yoziladi.
        </div>
        {lib ? (
          <div className="grid2">
            <label className="chk"><input type="checkbox" checked={lib.enabled} onChange={(e) => void settings({ enabled: e.target.checked })} /><span>Videolarga fon musiqasi qo&apos;yilsin</span></label>
            <div className="field"><label>Ovoz balandligi</label>
              <select className="input" value={lib.volume} disabled={!lib.enabled} onChange={(e) => void settings({ volume: Number(e.target.value) })}>
                {[...new Set([...VOLUMES, lib.volume])].sort((a, b) => a - b).map((v) => <option key={v} value={v}>{Math.round(v * 100)}%{v === 0.12 ? " (tavsiya)" : ""}</option>)}
              </select></div>
          </div>
        ) : <div className="skeleton" style={{ height: 44 }} />}

        {lib && lib.tracks.length > 0 ? (
          <>
            <div className="hint"><b>Kutubxona</b> — keyingi videolarda shular ishlatiladi:</div>
            {lib.tracks.map((t) => (
              <div key={t.id} className="track">
                <button className="play" aria-label={playing === "lib:" + t.id ? "To'xtatish" : "Tinglash"} onClick={() => toggle("lib:" + t.id, t.url || t.previewUrl)}>{playing === "lib:" + t.id ? "❚❚" : "▶"}</button>
                <div className="info"><b>{t.title}</b><div className="hint">{t.creator || "—"} · {dur(t.durationSec)} · {lic(t.license, t.licenseVersion)}</div></div>
                <button className="btn danger" disabled={busy === t.id} onClick={() => remove(t.id, t.title)}>O&apos;chirish</button>
              </div>
            ))}
          </>
        ) : lib ? <div className="hint">Kutubxona bo&apos;sh: pastdagi ro&apos;yxatdan tanlang.</div> : null}
      </div>

      <div className="card formcard">
        <div className="chips">
          {MOODS.map(([k, label]) => <button key={k} className={res?.q === k ? "on" : ""} onClick={() => search(k)}>{label}</button>)}
        </div>
        <div className="row2">
          <input className="input" value={draft} maxLength={60} placeholder="Qidirish (inglizcha): epic, lofi, guitar…" onChange={(e) => setDraft(e.target.value)} onKeyDown={(e) => e.key === "Enter" && search(draft)} />
          <button className="btn" onClick={() => search(draft)} disabled={!draft.trim() || loading}>Qidirish</button>
        </div>
        {err ? <div className="err">{err}</div> : null}

        {(loading || (!res && !err)) && page.length === 0 ? [0, 1, 2].map((i) => <div key={i} className="skeleton" style={{ height: 52 }} />) : null}
        {res && page.length === 0 && !loading ? <div className="hint">Hech narsa topilmadi.</div> : null}
        {page.map((t) => (
          <div key={t.id} className="track">
            <button className="play" aria-label={playing === t.id ? "To'xtatish" : "Tinglash"} onClick={() => toggle(t.id, t.previewUrl)}>{playing === t.id ? "❚❚" : "▶"}</button>
            <div className="info">
              <b>{t.title}</b>
              <div className="hint">
                {t.creator || "—"} · {dur(t.durationSec)} · {lic(t.license, t.licenseVersion)}{t.genres.length ? ` · ${t.genres.join(", ")}` : ""}
                {t.landingUrl ? <> · <button className="linkbtn" onClick={() => openExternal(t.landingUrl)}>manba</button></> : null}
              </div>
            </div>
            {inLib.has(t.id)
              ? <span className="chip ok">✓ kutubxonada</span>
              : <button className="btn primary" disabled={busy !== null || !lib || lib.tracks.length >= 30} onClick={() => use(t)}>{busy === t.id ? "Yuklanmoqda…" : "Ishlatish"}</button>}
          </div>
        ))}

        {res ? (
          <div className="pager">
            <button className="btn" onClick={back} disabled={res.at === 0 || loading}>← Ortga</button>
            <span className="hint">{res.at + 1}-sahifa{res.total ? ` · ${res.at * PAGE + 1}–${res.at * PAGE + page.length} / ${res.total}` : ""}</span>
            <button className="btn" onClick={next} disabled={loading || lastPage || page.length < PAGE}>{loading && res.pages.length === res.at + 1 ? "Yuklanmoqda…" : "Boshqa 10 ta →"}</button>
          </div>
        ) : null}
      </div>
    </>
  );
}

"use client";

import { useCallback, useEffect, useState } from "react";
import { ArrowLeft, LoaderCircle, Music as MusicIcon, Pause, Play, Search, Trash2 } from "lucide-react";
import { api, cache, confirmBox, haptic, openExternal } from "@/lib/client";
import type { MusicCandidate, MusicLibrary } from "@/lib/types";
import type { Toast } from "./App";
import { Alert, Btn, Card, Empty, Sheet, Skel, Status, Toggle, useBusy } from "./ui";
import st from "./Music.module.css";

/** Kayfiyatlar: Openverse'da inglizcha qidiruv so'zi. */
const MOODS: [string, string][] = [
  ["calm", "Tinch"], ["cinematic", "Kinematik"], ["inspiring", "Ilhomlantiruvchi"], ["documentary", "Hujjatli"],
  ["ambient", "Ambient"], ["piano", "Pianino"], ["acoustic", "Akustik"], ["electronic", "Elektron"],
];
const PAGE = 10;
const VOLUMES = [0.05, 0.08, 0.1, 0.12, 0.15, 0.2];
const RECOMMENDED = 0.12;
/** Kutubxonadagi treklar chegarasi (server ham tekshiradi). */
const MAX_TRACKS = 30;

/** Bitta umumiy pleyer (bir vaqtda bitta trek chalinadi). */
let player: HTMLAudioElement | null = null;
function getPlayer(): HTMLAudioElement {
  if (!player) player = new Audio();
  return player;
}

type Pages = { q: string; pages: MusicCandidate[][]; at: number; total: number; pageCount: number };
type Busy = ReturnType<typeof useBusy>;

const dur = (s: number) => (s > 0 ? `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}` : "—");
const lic = (l: string, v: string) => (l === "cc0" ? "CC0" : `CC ${l.toUpperCase()}${v ? " " + v : ""}`);
const pct = (v: number) => `${Math.round(v * 100)}%`;
const thousands = (n: number) => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, " ");

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
 * Fon musiqasi (dizayn: Shablonlar va musiqa · Musiqa Trfuhk): umumiy yoqish/o'chirish va ovoz balandligi, kutubxona (tinglash, olib tashlash)
 * va «Openverse'dan qo'shish» sheet'i (Musiqa qo'shish baf7Co). «Qo'shish» - trek serverga yuklanadi, kompyuter videolarga fon musiqasi qo'yadi.
 * onChanged - kutubxona o'zgardi (mavzu oynasidagi tanlov ro'yxati katalogdan olinadi).
 */
export default function Music({ toast, onCount, onChanged, active = true }: { toast: Toast; onCount?: (n: number) => void; onChanged?: () => void; active?: boolean }) {
  const [lib, setLib] = useState<MusicLibrary | null>(null);
  const [libErr, setLibErr] = useState<string | null>(null);
  const [playing, setPlaying] = useState<string | null>(null);
  /** Tanlangan, lekin hali saqlanmagan ovoz balandligi (null - saqlangani). */
  const [vol, setVol] = useState<number | null>(null);
  const [sheet, setSheet] = useState(false);
  const busy = useBusy((m) => { haptic.fail(); toast(m, true); });

  const loadLib = useCallback(() => {
    api<MusicLibrary>("/api/music").then((l) => { setLib(l); setLibErr(null); }).catch((e) => setLibErr(e instanceof Error ? e.message : String(e)));
  }, []);
  useEffect(() => { loadLib(); }, [loadLib]);
  useEffect(() => { if (lib) onCount?.(lib.tracks.length); }, [lib, onCount]);
  useEffect(() => () => { player?.pause(); }, []);
  // Bo'lim yashirilganda (Shablonlar segmenti) chalinayotgan trek to'xtatiladi.
  const [wasActive, setWasActive] = useState(active);
  if (wasActive !== active) { setWasActive(active); if (!active) setPlaying(null); }
  useEffect(() => { if (!active) player?.pause(); }, [active]);

  const toggle = useCallback((id: string, src: string) => {
    const a = getPlayer();
    if (playing === id) { a.pause(); setPlaying(null); return; }
    a.pause(); a.src = src; a.currentTime = 0;
    a.onended = () => setPlaying(null);
    a.onerror = () => { setPlaying(null); toast("Trekni ochib bo'lmadi.", true); };
    void a.play().then(() => setPlaying(id)).catch(() => { setPlaying(null); toast("Tinglashni boshlab bo'lmadi.", true); });
  }, [playing, toast]);

  function closeSheet() {
    // Qidiruv natijasi chalinayotgan bo'lsa to'xtatiladi (kutubxonadagisi chalinaversin).
    if (playing && !playing.startsWith("lib:")) { player?.pause(); setPlaying(null); }
    setSheet(false);
  }

  async function add(t: MusicCandidate) {
    haptic.tap();
    await busy.run("add:" + t.id, async () => {
      const r = await api<{ library: MusicLibrary; message: string }>("/api/music", { action: "add", id: t.id });
      setLib(r.library); haptic.ok(); toast(r.message); onChanged?.();
    });
  }

  async function remove(id: string, title: string, uses: number) {
    const extra = uses > 0 ? `\n\n${uses} ta mavzuda shu trek tanlangan: ular uchun boshqasini tanlash kerak bo'ladi.` : "";
    if (!(await confirmBox(`«${title}» kutubxonadan olib tashlansinmi? Keyingi videolarda ishlatilmaydi.${extra}`))) return;
    await busy.run("rm:" + id, async () => {
      if (playing === "lib:" + id) { player?.pause(); setPlaying(null); }
      const r = await api<{ library: MusicLibrary; message: string }>("/api/music", { action: "remove", id });
      setLib(r.library); toast(r.message); onChanged?.();
    });
  }

  async function setEnabled(enabled: boolean) {
    await busy.run("enabled", async () => {
      const r = await api<{ library: MusicLibrary }>("/api/music", { action: "settings", enabled });
      setLib(r.library); haptic.ok(); onChanged?.();
    });
  }

  async function saveVolume() {
    if (vol === null) return;
    const ok = await busy.run("volume", async () => {
      const r = await api<{ library: MusicLibrary }>("/api/music", { action: "settings", volume: vol });
      setLib(r.library); haptic.ok();
    });
    if (ok !== false) setVol(null);
  }

  if (!lib) {
    return libErr ? (
      <Alert>{libErr} <button type="button" className="linkbtn" onClick={() => { setLibErr(null); loadLib(); }}>Qayta urinish</button></Alert>
    ) : (
      <div className={st.wrap} aria-busy="true" aria-label="Yuklanmoqda">
        <div className="card"><Skel h={18} w="60%" /><Skel h={12} w="80%" /><Skel h={48} r={999} /></div>
        <div className="card"><Skel h={18} w="40%" />{[0, 1, 2].map((i) => <Skel key={i} h={44} />)}</div>
      </div>
    );
  }

  const volume = vol ?? lib.volume;
  const dirty = vol !== null && vol !== lib.volume;
  const saving = busy.is("volume");
  const volumes = [...new Set([...VOLUMES, lib.volume])].sort((a, b) => a - b);
  // Mavzular tanlovi: trek id → nechta mavzuda tanlangan; "none" - musiqasiz.
  const topicPicks = Object.values(lib.topics ?? {});
  const uses = (id: string) => topicPicks.filter((v) => v === id).length;
  const own = topicPicks.length;

  return (
    <div className={st.wrap}>
      <Card>
        <div className={st.head}>
          <div className={st.headT}>
            <h2 className="h2">Videolarga fon musiqasi</h2>
            <span className="sub">Har videoga kutubxonadan navbat bilan bittasi</span>
          </div>
          {busy.is("enabled") ? <LoaderCircle size={18} className={`spin ${st.spin}`} aria-hidden /> : null}
          <Toggle on={lib.enabled} disabled={busy.is("enabled")} label="Videolarga fon musiqasi" onChange={(v) => void setEnabled(v)} />
        </div>

        <div className={st.volume}>
          <div className={st.volHead}><span className="label" id="vol-label">Ovoz balandligi</span><span className="sub tnum">tavsiya — {pct(RECOMMENDED)}</span></div>
          <div className={st.vols} role="radiogroup" aria-labelledby="vol-label">
            {volumes.map((v) => (
              <button key={v} type="button" role="radio" aria-checked={volume === v} disabled={!lib.enabled || saving} onClick={() => setVol(v === lib.volume ? null : v)}>{pct(v)}</button>
            ))}
          </div>
          {!lib.enabled ? <div className="hint">Fon musiqasi o'chiq: mavzu oynasida alohida tanlangan trek baribir qo'yiladi.</div> : null}
        </div>

        {dirty || saving || busy.done("volume") ? (
          <div className={st.foot}>
            <span className="sub tnum">{dirty || saving ? `${pct(lib.volume)} → ${pct(volume)}` : `Ovoz balandligi: ${pct(lib.volume)}`}</span>
            <Btn loading={saving} done={busy.done("volume") && !dirty} onClick={() => void saveVolume()}>
              {saving ? "Saqlanmoqda" : busy.done("volume") && !dirty ? "Saqlandi" : "Saqlash"}
            </Btn>
          </div>
        ) : null}
      </Card>

      <Card title="Kutubxona" action={<span className="sub tnum">{lib.tracks.length} / {MAX_TRACKS} trek</span>}>
        {lib.tracks.length === 0 ? (
          <Empty icon={MusicIcon} title="Kutubxona bo'sh">Openverse'dan ochiq litsenziyali musiqa qo'shing: tinglab ko'ring, yoqqanini qo'shing.</Empty>
        ) : (
          <div className={st.lib}>
            {lib.tracks.map((t) => {
              const n = uses(t.id);
              return (
                <div key={t.id} className={st.row}>
                  <PlayBtn on={playing === "lib:" + t.id} title={t.title} onClick={() => toggle("lib:" + t.id, t.url || t.previewUrl)} />
                  <div className={st.info}>
                    <b>{t.title}</b>
                    <span className="tnum">{t.creator || "—"} · {dur(t.durationSec)} · {lic(t.license, t.licenseVersion)}{n > 0 ? ` · ${n} ta mavzuda` : ""}</span>
                  </div>
                  <button type="button" className={st.rm} aria-label={`«${t.title}» ni kutubxonadan olib tashlash`} disabled={busy.is("rm:" + t.id)} onClick={() => void remove(t.id, t.title, n)}>
                    {busy.is("rm:" + t.id) ? <LoaderCircle size={18} className="spin" /> : <Trash2 size={18} />}
                  </button>
                </div>
              );
            })}
          </div>
        )}
        <Btn tone="secondary" block icon={Search} onClick={() => { haptic.tap(); setSheet(true); }}>Openverse&apos;dan qo&apos;shish</Btn>
        <div className="hint">
          CC BY muallifi YouTube tavsifiga avtomatik yoziladi. Mavzu oynasida har videoga alohida trek yoki «musiqasiz» tanlash mumkin
          {own > 0 ? ` — hozir ${own} ta mavzuda o'z tanlovi bor.` : "."}
        </div>
      </Card>

      {sheet ? <MusicSearch lib={lib} playing={playing} toggle={toggle} add={add} busy={busy} onClose={closeSheet} /> : null}
    </div>
  );
}

function PlayBtn({ on, title, onClick }: { on: boolean; title: string; onClick: () => void }) {
  return (
    <button type="button" className={`${st.play}${on ? " " + st.on : ""}`} aria-label={on ? `«${title}» ni to'xtatish` : `«${title}» ni tinglash`} aria-pressed={on} onClick={onClick}>
      {on ? <Pause size={18} /> : <Play size={18} />}
    </button>
  );
}

/**
 * «Musiqa qo'shish» sheet'i: Openverse ro'yxati 10 tadan («Boshqa 10 ta» - keyingi sahifa, «Ortga» - oldin yuklanganlar, ular saqlanib qoladi).
 * Oldin yuklangan sahifalar keshda: sheet qayta ochilganda ham ko'rinadi.
 */
function MusicSearch({ lib, playing, toggle, add, busy, onClose }: {
  lib: MusicLibrary; playing: string | null; toggle: (id: string, src: string) => void; add: (t: MusicCandidate) => Promise<void>; busy: Busy; onClose: () => void;
}) {
  const [res, setRes] = useState<Pages | null>(() => cache.get<Pages>("music:pages"));
  const [draft, setDraft] = useState(() => cache.get<Pages>("music:pages")?.q ?? MOODS[0][0]);
  /** Nima yuklanmoqda: yangi qidiruv yoki keyingi sahifa (shu tugma ichida spinner). */
  const [loading, setLoading] = useState<"search" | "next" | null>(() => (cache.get<Pages>("music:pages") ? null : "search"));
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => { if (res) cache.set("music:pages", res); }, [res]);

  const load = useCallback(async (query: string, pageNo: number, base: Pages | null, kind: "search" | "next") => {
    setLoading(kind); setErr(null);
    try {
      const r = await fetchPage(query, pageNo);
      if (r.items.length === 0 && pageNo > 1) { setErr("Bu qidiruvda boshqa trek qolmadi."); return; }
      const prev = base && base.q === query ? base.pages : [];
      const pages = [...prev.slice(0, pageNo - 1), r.items];
      setRes({ q: query, pages, at: pageNo - 1, total: r.total, pageCount: r.pageCount });
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
    finally { setLoading(null); }
  }, []);

  // Birinchi ochilishda (keshda hech narsa bo'lmasa) birinchi 10 ta.
  useEffect(() => {
    if (cache.get<Pages>("music:pages")) return;
    let live = true;
    const q0 = MOODS[0][0];
    fetchPage(q0, 1)
      .then((r) => { if (live) setRes({ q: q0, pages: [r.items], at: 0, total: r.total, pageCount: r.pageCount }); })
      .catch((e) => { if (live) setErr(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (live) setLoading(null); });
    return () => { live = false; };
  }, []);

  function search(query: string) {
    const v = query.trim().slice(0, 60);
    if (!v) return;
    haptic.tap(); setDraft(v);
    if (v === res?.q) { setRes({ ...res, at: 0 }); return; }
    void load(v, 1, null, "search");
  }

  function next() {
    if (!res) return;
    haptic.tap();
    const at = res.at + 1;
    if (res.pages[at]) setRes({ ...res, at }); // oldin yuklangan - qayta so'ralmaydi
    else void load(res.q, at + 1, res, "next");
  }
  function back() { if (res && res.at > 0) { haptic.tap(); setRes({ ...res, at: res.at - 1 }); } }

  const page = res?.pages[res.at] ?? [];
  const inLib = new Set(lib.tracks.map((t) => t.id));
  const full = lib.tracks.length >= MAX_TRACKS;
  const adding = page.some((t) => busy.is("add:" + t.id));
  const lastPage = !!res && res.pageCount > 0 && res.at + 1 >= res.pageCount;

  return (
    <Sheet title="Musiqa qo'shish" sub="Openverse · faqat CC0 va CC BY, asosan Jamendo" onClose={onClose}>
      <div className={`chips ${st.moods}`} role="group" aria-label="Kayfiyat">
        {MOODS.map(([k, label]) => <button key={k} type="button" className={res?.q === k ? "on" : ""} aria-pressed={res?.q === k} onClick={() => search(k)}>{label}</button>)}
      </div>

      <form className={st.search} role="search" onSubmit={(e) => { e.preventDefault(); search(draft); }}>
        <label className={st.box}>
          <Search aria-hidden />
          <input className="inp" value={draft} maxLength={60} enterKeyHint="search" aria-label="Qidirish (inglizcha)" placeholder="Qidirish (inglizcha): epic, lofi, guitar…" onChange={(e) => setDraft(e.target.value)} />
        </label>
        <Btn type="submit" tone="secondary" loading={loading === "search"} disabled={!draft.trim() || loading !== null}>Qidirish</Btn>
      </form>

      {full ? <Alert tone="note">Kutubxona to'la ({MAX_TRACKS} trek): yangisini qo'shish uchun avval birortasini olib tashlang.</Alert> : null}

      <div className={st.results} aria-busy={loading === "search" || undefined}>
        {loading === "search" && page.length === 0 ? [0, 1, 2].map((i) => (
          <div key={i} className={st.row}><Skel h={44} w={44} r={999} /><div className={st.info}><Skel h={14} w="60%" /><Skel h={12} w="40%" /></div></div>
        )) : null}
        {res && page.length === 0 && loading === null && !err ? <div className="hint">Hech narsa topilmadi.</div> : null}
        {page.map((t) => (
          <div key={t.id} className={st.row}>
            <PlayBtn on={playing === t.id} title={t.title} onClick={() => toggle(t.id, t.previewUrl)} />
            <div className={st.info}>
              <b>{t.title}</b>
              <span className="tnum">{t.creator || "—"} · {dur(t.durationSec)}</span>
              <span className={st.extra}>
                {lic(t.license, t.licenseVersion)}{t.genres.length ? ` · ${t.genres.join(", ")}` : ""}
                {t.landingUrl ? <> · <button type="button" className={st.src} onClick={() => openExternal(t.landingUrl)}>manba</button></> : null}
              </span>
            </div>
            {inLib.has(t.id)
              ? <Status tone="ok">kutubxonada</Status>
              : <Btn tone="secondary" size="sm" loading={busy.is("add:" + t.id)} disabled={adding || full} onClick={() => void add(t)}>{busy.is("add:" + t.id) ? "Qo'shilmoqda" : "Qo'shish"}</Btn>}
          </div>
        ))}
      </div>

      {err ? <Alert>{err}</Alert> : null}

      {res ? (
        <div className={st.pager}>
          <Btn tone="ghost" size="sm" icon={ArrowLeft} onClick={back} disabled={res.at === 0 || loading !== null}>Ortga</Btn>
          <span className="tnum">{res.at + 1}-sahifa{res.total && page.length ? ` · ${res.at * PAGE + 1}–${res.at * PAGE + page.length} / ${thousands(res.total)}` : ""}</span>
          <Btn tone="secondary" size="sm" loading={loading === "next"} onClick={next} disabled={loading !== null || lastPage || page.length < PAGE}>Boshqa 10 ta</Btn>
        </div>
      ) : null}
    </Sheet>
  );
}

"use client";

/**
 * Gemini model zanjiri va limitlar (dizayn: Sozlamalar → «AI va promptlar», «Model zanjiri» kartasi).
 * - useGeminiUsage: /api/gemini/usage (faqat asosiy hisob egasi; mehmonga so'ralmaydi).
 * - ModelChainCard: zanjir tartibi (birinchisi - asosiy, qolganlari zaxira; sudrab yoki klaviatura bilan), har model bo'yicha bugungi
 *   sarf va bepul tarif limiti (kichik chiziq), temperature, Google qidiruvi va timeout. O'z «Saqlash» tugmasi bilan.
 * - default GeminiLimits: faqat sarf jadvali (eski sozlamalar formasi uchun, o'sha sahifalar ko'chirilguncha).
 */
import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { ChevronsDown, ChevronsUp, GripVertical, RefreshCw } from "lucide-react";
import { api } from "@/lib/client";
import { DEFAULT_GEMINI_CHAIN, limitsFor, modelChain, nextPacificMidnight } from "@/lib/gemini-models";
import type { GeminiUsageView, ModelLimitRow } from "@/lib/gemini-usage";
import { differs, type ConfigDraft } from "@/lib/settings-data";
import type { BotConfig } from "@/lib/types";
import type { Toast } from "./App";
import { Btn, Card, Field, Skel, Status, ToggleRow, useBusy } from "./ui";
import s from "./GeminiLimits.module.css";

export const fmtNum = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(n >= 10_000 ? 0 : 1)}K` : String(n));
const time = (iso: string | number) => new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
const lc = (x: string) => x.trim().toLowerCase();

// ------------------------------------------------------------------ sarf ma'lumoti
/** Bugungi sarf: avval birinchi yuklash, keyin «Yangilash». enabled=false (mehmon) - so'ralmaydi. */
export function useGeminiUsage(enabled = true) {
  const [view, setView] = useState<GeminiUsageView | null>(null);
  const [error, setError] = useState<string | null>(null);
  /** Qo'lda yangilash: xato bo'lsa throw (useBusy toast qiladi). */
  const load = useCallback(async () => {
    const v = await api<GeminiUsageView>("/api/gemini/usage");
    setView(v); setError(null);
    return v;
  }, []);
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    api<GeminiUsageView>("/api/gemini/usage")
      .then((v) => { if (live) { setView(v); setError(null); } })
      .catch((e) => { if (live) setError(e instanceof Error ? e.message : String(e)); });
    return () => { live = false; };
  }, [enabled]);
  return { view, error, load };
}

type Tone = "bad" | "accent" | "muted";
/** Model qatoridagi holat matni. active - zanjirda hozir shu model yozyapti. */
function rowLine(r: ModelLimitRow | undefined, active: boolean, keys: number): { tone: Tone; text: string } {
  if (!r) return { tone: "muted", text: "Zaxira" };
  const tail = [r.tokensToday ? `${fmtNum(r.tokensToday)} token` : "", r.rateLimitedToday ? `429: ${r.rateLimitedToday}` : ""].filter(Boolean).join(" · ");
  switch (r.status) {
    case "day": return { tone: "bad", text: `Kunlik limit tugadi · ${time(r.until!)} da tiklanadi` };
    case "minute": return { tone: "bad", text: `Daqiqalik limit · ${time(r.until!)} gacha` };
    case "unavailable": return { tone: "muted", text: r.note || "Mavjud emas" };
  }
  if (active) {
    const rpm = r.limits ? r.limits.rpm * keys : 0;
    return { tone: "accent", text: [`Hozir shu yozyapti · daqiqada ${r.requestsLastMinute}${rpm ? ` / ${rpm}` : ""}`, tail].filter(Boolean).join(" · ") };
  }
  return { tone: "muted", text: ["Zaxira", r.requestsToday ? `bugun ${r.requestsToday} so'rov` : "bugun ishlatilmagan", tail].filter(Boolean).join(" · ") };
}

const usable = (r: ModelLimitRow | undefined) => !r || r.status === "ok" || r.status === "idle";

/** Bitta model qatori (zanjirda ham, sarf jadvalida ham). */
function ModelRow({ name, primary, row, keys, active, handle }: {
  name: string; primary: boolean; row: ModelLimitRow | undefined; keys: number; active: boolean; handle?: React.ReactNode;
}) {
  const lim = row?.limits ?? limitsFor(name);
  const rpd = lim ? lim.rpd * keys : 0;
  const used = row?.requestsToday ?? 0;
  const pct = rpd ? Math.min(100, Math.round((used / rpd) * 100)) : 0;
  const full = !!row && (row.status === "day" || (rpd > 0 && used >= rpd));
  const line = row ? rowLine(row, active, keys) : null;
  return (
    <>
      {handle}
      <div className={s.body}>
        <div className={s.nameRow}>
          <b className={s.name}>{name}</b>
          {primary ? <span className={s.tag}>Asosiy</span> : null}
          <span className={`${s.count} tnum`}>
            {row ? <><b className={full ? s.bad : undefined}>{used}</b>{rpd ? ` / ${fmtNum(rpd)}` : ""}</> : rpd ? `kuniga ${fmtNum(rpd)}` : null}
          </span>
        </div>
        {row && rpd ? (
          <div className={s.bar} role="progressbar" aria-label={`${name}: bugun ${used} / ${rpd} so'rov`} aria-valuemin={0} aria-valuemax={rpd} aria-valuenow={Math.min(used, rpd)}>
            <i className={full ? s.fillBad : active ? s.fillAccent : s.fillMuted} style={{ width: `${full ? 100 : pct}%` }} />
          </div>
        ) : null}
        {line ? <span className={`${s.line} ${line.tone === "bad" ? s.bad : line.tone === "accent" ? s.accentInk : ""}`}>{line.text}</span> : null}
      </div>
    </>
  );
}

/** Zanjirdagi har nom uchun sarf qatori: avval saqlangan zanjir tartibi (server shu tartibda qaytaradi), bo'lmasa nomi bo'yicha. */
function usageByName(view: GeminiUsageView | null, config: BotConfig | null) {
  const map = new Map<string, ModelLimitRow>();
  if (!view) return { map, extra: [] as ModelLimitRow[] };
  const used = new Set<ModelLimitRow>();
  const saved = config ? modelChain(config.gemini.models, config.gemini.model) : [];
  saved.forEach((n, i) => { const r = view.rows[i]; if (r) { map.set(lc(n), r); used.add(r); } });
  for (const r of view.rows) {
    if (used.has(r)) continue;
    for (const k of [r.model, r.display]) if (k && !map.has(lc(k))) map.set(lc(k), r);
  }
  // Zanjirda yo'q, lekin bugun ishlatilgan modellar (eski sozlama yoki boshqa yo'l bilan).
  const extra = view.rows.filter((r, i) => i >= saved.length && !saved.some((n) => lc(n) === lc(r.model) || lc(n) === lc(r.display)));
  return { map, extra };
}

// ------------------------------------------------------------------ «Model zanjiri» kartasi
type ChainEdit = { models: string[]; temperature: string; timeoutSec: string; useSearchGrounding: boolean };

/** Saqlangan zanjir (bo'sh - standart) + standart zanjirdagi qolgan modellar zaxira sifatida oxirida (eski «Joriy model» tanlovi kabi). */
function chainFrom(c: BotConfig): ChainEdit {
  const chain = c.gemini.models.length ? c.gemini.models : DEFAULT_GEMINI_CHAIN;
  return {
    models: [...chain, ...DEFAULT_GEMINI_CHAIN.filter((m) => !chain.some((x) => lc(x) === lc(m)))],
    temperature: String(c.gemini.temperature), timeoutSec: String(c.gemini.timeoutSec), useSearchGrounding: c.gemini.useSearchGrounding,
  };
}

const VISIBLE = 4;

export function ModelChainCard({ config, saveConfig, guest, toast, onSaved }: {
  config: BotConfig | null;
  saveConfig: (patch: (d: ConfigDraft) => ConfigDraft) => Promise<BotConfig>;
  guest: boolean; toast: Toast; onSaved: () => void;
}) {
  const busy = useBusy((m) => toast(m, true));
  const usage = useGeminiUsage(!guest);
  const [edit, setEdit] = useState<ChainEdit | null>(null);
  const [open, setOpen] = useState(false);
  const [drag, setDrag] = useState<number | null>(null);
  const listRef = useRef<HTMLOListElement>(null);
  const handles = useRef(new Map<number, HTMLButtonElement>());

  if (!config) return <ChainSkeleton />;
  const base = chainFrom(config);
  const cur = edit ?? base;
  const dirty = differs(cur, base);
  const orderOnly = dirty && differs(cur.models, base.models) && !differs({ ...cur, models: [] }, { ...base, models: [] });

  const temp = Number(cur.temperature.replace(",", "."));
  const tempErr = cur.temperature.trim() === "" || !Number.isFinite(temp) || temp < 0 || temp > 2 ? "0 dan 2 gacha" : null;
  const tout = Number(cur.timeoutSec);
  const toutErr = !Number.isInteger(tout) || tout < 30 || tout > 1800 ? "30 dan 1800 gacha (butun son)" : null;

  const keys = config.gemini.apiKeys.length;
  const k = Math.max(1, usage.view?.keys ?? keys);
  const { map, extra } = usageByName(usage.view, config);
  const rowOf = (n: string) => map.get(lc(n));
  const activeIdx = keys && usage.view ? cur.models.findIndex((n) => usable(rowOf(n))) : -1;

  // Bugun qolgan so'rovlar (limiti ma'lum, ishlayotgan modellar bo'yicha) va qachon tiklanishi.
  const known = usage.view ? cur.models.map(rowOf).filter((r): r is ModelLimitRow => !!r?.limits) : [];
  const left = known.filter((r) => r.status !== "day" && r.status !== "unavailable").reduce((a, r) => a + Math.max(0, r.limits!.rpd * k - r.requestsToday), 0);
  // Server bergan Tinch okeani sanasidan (kun o'rtasi) keyingi yarim tun - limitlar shunda tiklanadi.
  const resetAt = usage.view ? time(nextPacificMidnight(Date.parse(`${usage.view.day}T20:00:00Z`))) : "";
  const anyRows = usage.view ? cur.models.map(rowOf).filter(Boolean) as ModelLimitRow[] : [];
  const status = !keys ? <Status tone="bad">Kalit yo&apos;q</Status>
    : !usage.view ? null
    : anyRows.some((r) => r.status === "ok") ? <Status tone="ok">Ishlayapti</Status>
    : anyRows.length && anyRows.every((r) => !usable(r)) ? <Status tone="bad">Limit tugadi</Status>
    : <Status>Tayyor</Status>;

  const set = (p: Partial<ChainEdit>) => setEdit({ ...cur, ...p });
  const move = (from: number, to: number) => {
    if (to < 0 || to >= cur.models.length || from === to) return;
    const m = [...cur.models];
    const [x] = m.splice(from, 1);
    m.splice(to, 0, x);
    set({ models: m });
    if (to >= VISIBLE) setOpen(true);
    return to;
  };

  // Sudrash: tutqichni bosib yuqoriga/pastga olib boriladi (sensorli ekranda ham - pointer events).
  const onDown = (i: number) => (e: PointerEvent<HTMLButtonElement>) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    setDrag(i);
  };
  const onMove = (e: PointerEvent<HTMLButtonElement>) => {
    if (drag === null || !listRef.current) return;
    const items = Array.from(listRef.current.querySelectorAll<HTMLElement>("[data-idx]"));
    let to = drag;
    for (const el of items) {
      const r = el.getBoundingClientRect(), idx = Number(el.dataset.idx);
      if (e.clientY > r.top && e.clientY < r.bottom) { to = idx; break; }
    }
    if (to !== drag) { move(drag, to); setDrag(to); }
  };
  const onUp = () => setDrag(null);
  const onKey = (i: number) => (e: KeyboardEvent<HTMLButtonElement>) => {
    const to = e.key === "ArrowUp" ? i - 1 : e.key === "ArrowDown" ? i + 1 : e.key === "Home" ? 0 : e.key === "End" ? cur.models.length - 1 : null;
    if (to === null) return;
    e.preventDefault();
    if (move(i, to) !== undefined) requestAnimationFrame(() => handles.current.get(to)?.focus());
  };

  async function save() {
    if (tempErr || toutErr) return;
    const ok = await busy.run("save", () => saveConfig((d) => ({
      ...d, gemini: { ...d.gemini, models: cur.models, temperature: temp, timeoutSec: tout, useSearchGrounding: cur.useSearchGrounding },
    })));
    if (ok === false) return;
    setEdit(null);
    toast("Saqlandi. Kompyuterlar ~2 daqiqada yangilanadi, yangi videolardan boshlab qo'llanadi.");
    onSaved();
    if (!guest) void usage.load().catch(() => {});
  }

  const shown = open ? cur.models : cur.models.slice(0, VISIBLE);
  const hidden = cur.models.length - shown.length;

  return (
    <Card title="Model zanjiri" action={status}>
      {!guest && keys ? (
        usage.view && known.length ? (
          <div className={s.focal}>
            <p className="focal">{left.toLocaleString("en-US").replace(/,/g, " ")} <span className="dim">so&apos;rov</span></p>
            <span className="sub">bugun qoldi · {k} ta kalit · {resetAt} da tiklanadi</span>
          </div>
        ) : !usage.view && !usage.error ? (
          <div className={s.focal}><Skel h={40} w="55%" /><Skel h={12} w="70%" /></div>
        ) : null
      ) : null}
      {usage.error && !guest ? <div className="hint">Sarfni yuklab bo&apos;lmadi: {usage.error}</div> : null}

      <ol className={s.list} ref={listRef} aria-label="Gemini modellari tartibi: birinchisi asosiy">
        {shown.map((n, i) => (
          <li key={n} data-idx={i} className={`${s.row}${i === activeIdx ? " " + s.active : ""}${drag === i ? " " + s.dragging : ""}`}>
            <ModelRow
              name={n} primary={i === 0} row={rowOf(n)} keys={k} active={i === activeIdx}
              handle={
                <button
                  type="button" className={s.handle} aria-label={`${n}: ${i + 1}-o'rin. Tartibini o'zgartirish uchun sudrang yoki yuqoriga/pastga tugmalarini bosing`}
                  ref={(el) => { if (el) handles.current.set(i, el); else handles.current.delete(i); }}
                  onPointerDown={onDown(i)} onPointerMove={onMove} onPointerUp={onUp} onPointerCancel={onUp} onKeyDown={onKey(i)}
                >
                  <GripVertical size={18} />
                </button>
              }
            />
          </li>
        ))}
      </ol>

      {cur.models.length > VISIBLE ? (
        <button type="button" className={s.more} aria-expanded={open} onClick={() => setOpen(!open)}>
          {open ? <ChevronsUp size={18} /> : <ChevronsDown size={18} />}
          <b>{open ? "Kamroq ko'rsatish" : `Yana ${hidden} ta zaxira model`}</b>
          <span>sudrab tartiblang</span>
        </button>
      ) : null}

      {open && extra.length ? (
        <div className={s.extra}>
          <span className="label">Zanjirda yo&apos;q, bugun ishlatilgan</span>
          {extra.map((r) => <div key={r.model} className={s.row}><span className={s.handleGap} /><ModelRow name={r.display || r.model} primary={false} row={r} keys={k} active={false} /></div>)}
        </div>
      ) : null}

      <div className="hint">
        Ssenariy va boshqa Gemini ishlari birinchi model bilan bajariladi. Uning limiti tugasa yoki ishlamasa, keyingilariga navbat bilan avtomatik o&apos;tiladi.
        {!guest ? <> Sarfni YtAuto o&apos;zi sanaydi (worker va bot, faqat sizning kalitlaringiz); limitlar — Google AI Studio bepul tarifi{k > 1 ? `, ${k} ta kalit uchun (har kalit alohida loyihadan bo'lsa)` : ""}. Kun Tinch okeani vaqti bilan yangilanadi{usage.view?.updatedAtUtc ? ` · oxirgi yozuv ${time(usage.view.updatedAtUtc)}` : ""}.</> : null}
      </div>

      <div className={s.params}>
        <div className="grid2">
          <Field label="Temperature" error={tempErr} hint="0 — aniq, 2 — erkin">
            <input className="inp" inputMode="decimal" value={cur.temperature} aria-invalid={!!tempErr} onChange={(e) => set({ temperature: e.target.value })} />
          </Field>
          <Field label="Timeout, soniya" error={toutErr} hint="standart 180">
            <input className="inp" inputMode="numeric" value={cur.timeoutSec} aria-invalid={!!toutErr} onChange={(e) => set({ timeoutSec: e.target.value })} />
          </Field>
        </div>
        <ToggleRow title="Google qidiruvi" sub="Ssenariydan oldin faktlarni Google qidiruvi bilan tekshiradi" on={cur.useSearchGrounding} onChange={(v) => set({ useSearchGrounding: v })} />
      </div>

      <div className={s.foot}>
        {!guest ? (
          <Btn tone="ghost" icon={RefreshCw} loading={busy.is("usage")} onClick={() => void busy.run("usage", usage.load)}>Yangilash</Btn>
        ) : <span />}
        <Btn disabled={!dirty || !!tempErr || !!toutErr} loading={busy.is("save")} done={busy.done("save") && !dirty}
          tone={dirty || busy.is("save") ? "primary" : "secondary"} onClick={() => void save()}>
          {busy.done("save") && !dirty ? "Saqlandi" : orderOnly || !dirty ? "Tartibni saqlash" : "Saqlash"}
        </Btn>
      </div>
    </Card>
  );
}

function ChainSkeleton() {
  return (
    <div className="card" aria-busy="true" aria-label="Yuklanmoqda">
      <Skel h={18} w="45%" />
      <Skel h={40} w="55%" />
      {[0, 1, 2].map((i) => <div key={i} className={s.skelRow}><Skel h={14} w="60%" /><Skel h={6} /></div>)}
    </div>
  );
}

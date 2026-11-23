"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Check, ChevronUp, Copy, KeyRound } from "lucide-react";
import { api, confirmBox } from "@/lib/client";
import { differs, useBotConfig } from "@/lib/settings-data";
import type { BotConfig } from "@/lib/types";
import YouTubeChannels, { type YtStatus } from "../YouTubeChannels";
import { Alert, Btn, Card, Field, PageHead, SaveBtn, SettingsRow, SkelCard, ToggleRow, useBusy } from "../ui";
import type { SettingsPageProps } from "./types";
import styles from "./Channels.module.css";

type Yt = BotConfig["youtube"];
type YtPatch = Partial<Yt> & { clientSecret?: string };
type Busy = ReturnType<typeof useBusy>;
/** Karta saqlashi: tasdiqlash, faqat shu kartaning maydonlari bilan saqlash, toast. Muvaffaqiyatli bo'lsa true. */
type SaveFn = (key: string, label: string, patch: YtPatch) => Promise<boolean>;

const CATEGORIES: [string, string][] = [
  ["1", "Film va animatsiya"], ["2", "Avto va transport"], ["10", "Musiqa"], ["15", "Uy hayvonlari"], ["17", "Sport"], ["19", "Sayohat"],
  ["20", "O'yinlar"], ["22", "Odamlar va blog"], ["23", "Komediya"], ["24", "Ko'ngilochar"], ["25", "Yangiliklar va siyosat"],
  ["26", "Qo'llanma va uslub"], ["27", "Ta'lim"], ["28", "Fan va texnologiya"], ["29", "Notijorat va faollik"],
];

/** Server tekshiruvi bilan bir xil (lib/config.ts hhmm). */
const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;

/**
 * Kartaning qoralamasi: null - saqlangan qiymat ko'rinadi; o'zgartirilgan maydonlargina qoralamada turadi.
 * Saqlangandan keyin reset() - karta yana saqlangan (server qaytargan) qiymatni ko'rsatadi.
 */
function useDraft<T extends object>(saved: T | null) {
  const [draft, setDraft] = useState<Partial<T> | null>(null);
  const value = saved ? ({ ...saved, ...draft } as T) : null;
  const dirty = !!saved && !!draft && differs(value, saved);
  const set = useCallback((p: Partial<T>) => setDraft((d) => ({ ...d, ...p })), []);
  const reset = useCallback(() => setDraft(null), []);
  return { value, dirty, set, reset };
}

const pick = <K extends keyof Yt>(y: Yt | undefined, keys: readonly K[]): Pick<Yt, K> | null =>
  y ? (Object.fromEntries(keys.map((k) => [k, y[k]])) as Pick<Yt, K>) : null;

/** Kanallar va YouTube: ulangan kanallar, keyin har biri o'z «Saqlash» tugmasi bilan kartalar. */
export default function Channels({ toast, back, onChanged }: SettingsPageProps) {
  const { config, error, reload, saveConfig } = useBotConfig();
  const busy = useBusy((m) => toast(m, true));
  const [yt, setYt] = useState<YtStatus | null>(null);
  const [ytError, setYtError] = useState<string | null>(null);
  const [googleOpen, setGoogleOpen] = useState(false);
  const ytRef = useRef<YtStatus | null>(null);
  const onChangedRef = useRef(onChanged);
  useEffect(() => { ytRef.current = yt; onChangedRef.current = onChanged; });

  const loadYt = useCallback(async () => {
    try { setYt(await api<YtStatus>("/api/youtube")); setYtError(null); }
    catch (e) { setYtError(e instanceof Error ? e.message : String(e)); }
  }, []);

  useEffect(() => {
    // Birinchi yuklash: tashqi tizim (API) bilan sinxronlash.
    api<YtStatus>("/api/youtube").then((s) => { setYt(s); setYtError(null); }).catch((e) => setYtError(e instanceof Error ? e.message : String(e)));
    // Ulanish tugagach (tashqi brauzerdan qaytilganda) holat yangilanadi. Yangi kanal ulangan bo'lsa (u faol bo'ladi) mavzular ham.
    const onVis = () => {
      if (document.visibilityState !== "visible") return;
      api<YtStatus>("/api/youtube").then((s) => {
        if (ytRef.current && ytRef.current.channelId !== s.channelId) onChangedRef.current();
        setYt(s); setYtError(null);
      }).catch(() => {});
    };
    document.addEventListener("visibilitychange", onVis);
    return () => document.removeEventListener("visibilitychange", onVis);
  }, []);

  const save: SaveFn = useCallback(async (key, label, patch) => {
    if (!(await confirmBox(`«${label}» saqlansinmi? Yangi videolardan boshlab qo'llanadi.`))) return false;
    const r = await busy.run(key, () => saveConfig((d) => ({ ...d, youtube: { ...d.youtube, ...patch } })));
    if (r === false) return false;
    toast(`${label} saqlandi.`);
    onChanged();
    return true;
  }, [busy, saveConfig, toast, onChanged]);

  const y = config?.youtube;
  const google = useDraft(useMemo(() => (y ? { clientId: y.clientId, clientSecret: "" } : null), [y]));
  const configured = !!yt?.clientConfigured;
  const blocked = !yt ? null : !configured ? "Avval Client ID va Secret'ni saqlang" : google.dirty ? "Avval Google ulanishi o'zgarishlarini saqlang" : null;

  async function saveGoogle() {
    if (!google.value) return;
    const { clientId, clientSecret } = google.value;
    const ok = await save("google", "Google ulanishi", clientSecret ? { clientId, clientSecret } : { clientId });
    if (!ok) return;
    google.reset();
    setGoogleOpen(false);
    await loadYt(); // clientConfigured yangilanadi - kanal ulash tugmasi ochiladi
  }

  const configLoading = !config && !error;
  const configError = !config && error ? (
    <div className="card">
      <Alert>Sozlamalarni yuklab bo&apos;lmadi: {error}</Alert>
      <Btn tone="secondary" size="sm" loading={busy.is("reload")} onClick={() => void busy.run("reload", reload)}>Qayta urinish</Btn>
    </div>
  ) : null;

  const googleCard = configLoading ? <SkelCard lines={4} /> : google.value ? (
    <GoogleCard value={google.value} set={google.set} dirty={google.dirty} busy={busy} secretSet={!!y?.clientSecretSet}
      redirectUri={yt?.redirectUri ?? null} toast={toast} onSave={() => void saveGoogle()}
      onCollapse={configured ? () => { google.reset(); setGoogleOpen(false); } : undefined} />
  ) : null;

  return (
    <div className="page">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Kanallar va YouTube" sub="Qaysi kanalda ishlayapsiz va videolar qanday chiqadi" />

      <div className={styles.tiles}>
        <YouTubeChannels yt={yt} setYt={setYt} toast={toast} blocked={blocked} onSwitched={onChanged} error={ytError} onRetry={loadYt} />
      </div>

      {/* Birinchi ulash: Client ID/Secret saqlanmaguncha Google kartasi kanallardan keyin turadi. */}
      {(yt && !configured) || (!yt && ytError) ? (configError ?? googleCard) : null}

      <div className="section-label"><span>Barcha kanallar uchun</span><span style={{ textTransform: "none" }}>har bir kanalga bir xil</span></div>
      {configError ?? (
        <div className="cards">
          {configLoading || !y ? <><SkelCard lines={5} /><SkelCard lines={6} /><SkelCard lines={4} /></> : (
            <>
              <PublishCard y={y} busy={busy} save={save} />
              <FlagsCard y={y} busy={busy} save={save} />
              <DescriptionCard y={y} busy={busy} save={save} />
            </>
          )}
        </div>
      )}

      {yt && configured && !configError ? (
        <>
          <div className="section-label"><span>Ulanish</span></div>
          {googleOpen || google.dirty ? googleCard : (
            <div className={styles.ulanish}>
              <SettingsRow icon={KeyRound} title="Google ulanishi" summary={y?.clientSecretSet ? "Client ID va Secret saqlangan" : "Client ID va Secret"} onClick={() => setGoogleOpen(true)} />
            </div>
          )}
        </>
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ Google ulanishi
function GoogleCard({ value, set, dirty, busy, secretSet, redirectUri, toast, onSave, onCollapse }: {
  value: { clientId: string; clientSecret: string }; set: (p: Partial<{ clientId: string; clientSecret: string }>) => void;
  dirty: boolean; busy: Busy; secretSet: boolean; redirectUri: string | null; toast: SettingsPageProps["toast"];
  onSave: () => void; onCollapse?: () => void;
}) {
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const t = window.setTimeout(() => setCopied(false), 1500);
    return () => window.clearTimeout(t);
  }, [copied]);
  async function copy() {
    if (!redirectUri) return;
    try { await navigator.clipboard.writeText(redirectUri); setCopied(true); toast("Manzil nusxalandi."); }
    catch { toast("Nusxalab bo'lmadi — manzilni bosib belgilang va qo'lda nusxalang.", true); }
  }
  const saving = busy.is("google");
  return (
    <Card title="Google ulanishi" sub="Bir marta, barcha kanallar uchun" action={
      <div className={styles.headActions}>
        <SaveBtn dirty={dirty} saving={saving} done={busy.done("google")} onClick={onSave} />
        {onCollapse && !saving ? <button type="button" className={styles.collapse} aria-label="Yig'ish" onClick={onCollapse}><ChevronUp /></button> : null}
      </div>
    }>
      <Field label={<label htmlFor="yt-client-id">OAuth Client ID</label>}>
        <input id="yt-client-id" className="inp" value={value.clientId} autoComplete="off" spellCheck={false} disabled={saving}
          placeholder="…apps.googleusercontent.com" onChange={(e) => set({ clientId: e.target.value })} />
      </Field>
      <Field label={<label htmlFor="yt-client-secret">Client Secret</label>} right={secretSet ? "saqlangan ✓" : "kiritilmagan"}
        hint={secretSet ? "Bo'sh qoldirilsa saqlangan sir o'zgarmaydi." : undefined}>
        <input id="yt-client-secret" className="inp" type="password" autoComplete="off" spellCheck={false} disabled={saving} value={value.clientSecret}
          placeholder={secretSet ? "O'zgartirish uchun yangisini kiriting" : "GOCSPX-…"} onChange={(e) => set({ clientSecret: e.target.value })} />
      </Field>
      <Field label="Redirect URI" hint={<>Google Cloud → Credentials → <b>Web application</b> client → «Authorized redirect URIs» ga qo&apos;shing.</>}>
        <div className={styles.uri}>
          <code>{redirectUri ?? "…"}</code>
          <button type="button" className={styles.copy} aria-label="Manzilni nusxalash" disabled={!redirectUri} onClick={() => void copy()}>
            {copied ? <Check /> : <Copy />}
          </button>
        </div>
      </Field>
    </Card>
  );
}

// ------------------------------------------------------------------ Chiqarish
const PUBLISH_KEYS = ["publishTime", "timeZoneId", "categoryId", "license", "regionCode", "minLeadMinutes"] as const;

function PublishCard({ y, busy, save }: { y: Yt; busy: Busy; save: SaveFn }) {
  const saved = useMemo(() => pick(y, PUBLISH_KEYS), [y]);
  const d = useDraft(saved);
  const zones = useMemo(() => {
    let list: string[] = [];
    try { list = (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.("timeZone") ?? []; } catch { list = []; }
    return list;
  }, []);
  if (!d.value || !saved) return null;
  const v = d.value;
  const saving = busy.is("publish");
  const timeBad = !HHMM.test(v.publishTime);
  const zoneOptions = zones.includes(v.timeZoneId) || !v.timeZoneId ? zones : [v.timeZoneId, ...zones];

  async function onSave() {
    if (await save("publish", "Chiqarish", { ...d.value })) d.reset();
  }

  return (
    <Card title="Chiqarish" sub="Qachon va qanday chiqadi" action={<SaveBtn dirty={d.dirty} saving={saving} done={busy.done("publish")} onClick={() => void onSave()} />}>
      <div className={styles.slab}>
        <div className={styles.slabCol}>
          <label className="label" htmlFor="yt-publish-time">Public soati</label>
          <input id="yt-publish-time" className={styles.time} value={v.publishTime} inputMode="numeric" maxLength={5} placeholder="18:00" disabled={saving}
            aria-invalid={timeBad || undefined} aria-describedby="yt-publish-hint" onChange={(e) => d.set({ publishTime: e.target.value })} />
        </div>
        <div className={`${styles.slabCol} ${styles.slabRight}`}>
          <label className="label" htmlFor="yt-tz">Vaqt zonasi</label>
          {zoneOptions.length ? (
            <select id="yt-tz" className={styles.zone} value={v.timeZoneId} disabled={saving} onChange={(e) => d.set({ timeZoneId: e.target.value })}>
              {zoneOptions.map((z) => <option key={z} value={z}>{z}</option>)}
            </select>
          ) : (
            <input id="yt-tz" className={styles.zone} value={v.timeZoneId} disabled={saving} onChange={(e) => d.set({ timeZoneId: e.target.value })} />
          )}
        </div>
      </div>
      <div id="yt-publish-hint" className={`${styles.slabHint}${timeBad ? " " + styles.bad : ""}`}>
        {timeBad ? "HH:mm formatida yozing, masalan 18:00"
          : <>Har kuni shu vaqtda chiqadi{v.publishTime !== saved.publishTime ? ` · avval ${saved.publishTime} edi` : ""}</>}
      </div>

      <div className="grid2">
        <Field label={<label htmlFor="yt-cat">Kategoriya</label>}>
          <select id="yt-cat" className="inp" value={v.categoryId} disabled={saving} onChange={(e) => d.set({ categoryId: e.target.value })}>
            {CATEGORIES.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
            {!CATEGORIES.some(([id]) => id === v.categoryId) ? <option value={v.categoryId}>Kategoriya {v.categoryId}</option> : null}
          </select>
        </Field>
        <Field label={<label htmlFor="yt-license">Litsenziya</label>}>
          <select id="yt-license" className="inp" value={v.license} disabled={saving} onChange={(e) => d.set({ license: e.target.value })}>
            <option value="youtube">Standart YouTube</option>
            <option value="creativeCommon">Creative Commons (CC BY)</option>
            {v.license !== "youtube" && v.license !== "creativeCommon" ? <option value={v.license}>{v.license}</option> : null}
          </select>
        </Field>
        <Field label={<label htmlFor="yt-region">Mamlakat kodi</label>}>
          <input id="yt-region" className="inp" value={v.regionCode} autoComplete="off" placeholder="UZ" disabled={saving} onChange={(e) => d.set({ regionCode: e.target.value })} />
        </Field>
        <Field label={<label htmlFor="yt-lead">Min. oldindan, daq</label>}>
          <input id="yt-lead" className="inp tnum" inputMode="numeric" value={v.minLeadMinutes} disabled={saving}
            onChange={(e) => d.set({ minLeadMinutes: e.target.value === "" ? 0 : Number(e.target.value.replace(/\D/g, "")) })} />
        </Field>
      </div>
    </Card>
  );
}

// ------------------------------------------------------------------ Belgilar
const FLAGS: [keyof Yt & ("madeForKids" | "containsSyntheticMedia" | "embeddable" | "publicStatsViewable" | "notifySubscribers" | "setRecordingDate"), string][] = [
  ["madeForKids", "Bolalar uchun"],
  ["containsSyntheticMedia", "AI (sintetik) kontent belgisi"],
  ["embeddable", "Boshqa saytlarga joylashga ruxsat"],
  ["publicStatsViewable", "Statistika hammaga ko'rinadi"],
  ["notifySubscribers", "Obunachilarga bildirishnoma"],
  ["setRecordingDate", "Yozib olingan sanani belgilash"],
];
const FLAG_KEYS = FLAGS.map(([k]) => k);

function FlagsCard({ y, busy, save }: { y: Yt; busy: Busy; save: SaveFn }) {
  const saved = useMemo(() => pick(y, FLAG_KEYS), [y]);
  const d = useDraft(saved);
  if (!d.value) return null;
  const v = d.value;
  const saving = busy.is("flags");
  async function onSave() {
    if (await save("flags", "Belgilar", { ...d.value })) d.reset();
  }
  return (
    <Card title="Belgilar" sub="Har bir yuklanadigan videoga qo'yiladi" action={<SaveBtn dirty={d.dirty} saving={saving} done={busy.done("flags")} onClick={() => void onSave()} />}>
      <div className={styles.rows}>
        {FLAGS.map(([k, label]) => (
          <ToggleRow key={k} title={label} on={!!v[k]} disabled={saving} onChange={(on) => d.set({ [k]: on })} />
        ))}
      </div>
    </Card>
  );
}

// ------------------------------------------------------------------ Tavsif va teglar
const DESC_KEYS = ["defaultTags", "defaultDescription", "locationDescription"] as const;

function DescriptionCard({ y, busy, save }: { y: Yt; busy: Busy; save: SaveFn }) {
  const saved = useMemo(() => pick(y, DESC_KEYS), [y]);
  const d = useDraft(saved);
  if (!d.value) return null;
  const v = d.value;
  const saving = busy.is("desc");
  async function onSave() {
    if (await save("desc", "Tavsif va teglar", { ...d.value })) d.reset();
  }
  return (
    <Card title="Tavsif va teglar" sub="Bo'sh qolsa, Gemini o'zi yozadi" action={<SaveBtn dirty={d.dirty} saving={saving} done={busy.done("desc")} onClick={() => void onSave()} />}>
      <Field label={<label htmlFor="yt-tags">Standart teglar, vergul bilan</label>}
        hint="To'ldirilsa faqat shular qo'yiladi, bo'sh bo'lsa Gemini har videoga 5 ta teg yozadi.">
        <input id="yt-tags" className="inp" value={v.defaultTags} disabled={saving} placeholder="fan, texnologiya, ilm-fan" onChange={(e) => d.set({ defaultTags: e.target.value })} />
      </Field>
      <Field label={<label htmlFor="yt-desc">Standart tavsif</label>} right={`${v.defaultDescription.length}/2000`}
        hint="To'ldirilsa faqat shu qo'yiladi, bo'sh bo'lsa Gemini har videoga tavsif yozadi.">
        <textarea id="yt-desc" className="inp" rows={4} maxLength={2000} disabled={saving} value={v.defaultDescription}
          placeholder="Masalan: kanal havolalari, ijtimoiy tarmoqlar, ogohlantirish…" onChange={(e) => d.set({ defaultDescription: e.target.value })} />
      </Field>
      <Field label={<label htmlFor="yt-loc">Joy tavsifi, ixtiyoriy</label>}>
        <input id="yt-loc" className="inp" value={v.locationDescription} disabled={saving} placeholder="Toshkent, O'zbekiston" onChange={(e) => d.set({ locationDescription: e.target.value })} />
      </Field>
    </Card>
  );
}

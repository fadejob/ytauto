"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Check, ChevronUp, Copy, History, KeyRound, Trash2 } from "lucide-react";
import { api, confirmBox } from "@/lib/client";
import { differs, useBotConfig } from "@/lib/settings-data";
import type { BotConfig, State } from "@/lib/types";
import TagInput, { joinTags, parseTags, TAGS_MAX_CHARS } from "../TagInput";
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
  const activeCh = yt?.channels.find((c) => c.active) ?? null;
  const blocked = !yt ? null : !configured ? "Avval Client ID va Secret'ni saqlang" : google.dirty ? "Avval YouTube ulanishi o'zgarishlarini saqlang" : null;

  async function saveGoogle() {
    if (!google.value) return;
    const { clientId, clientSecret } = google.value;
    const ok = await save("google", "YouTube ulanishi", clientSecret ? { clientId, clientSecret } : { clientId });
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

      {activeCh ? (
        <>
          <div className="section-label"><span>{activeCh.title || "Faol kanal"}</span><span style={{ textTransform: "none" }}>faqat shu kanal uchun</span></div>
          <ChannelDefaultsCard key={activeCh.id} ch={activeCh} busy={busy} toast={toast} onSaved={setYt} />
          <ChannelDataCard ch={activeCh} busy={busy} toast={toast} onDone={(r) => { setYt(r); onChanged(r.state); }} />
        </>
      ) : null}

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
              <SettingsRow icon={KeyRound} title="YouTube ulanishi" summary={y?.clientSecretSet ? "O'z Client ID va Secret'ingiz saqlangan" : yt.globalClient ? "Umumiy YtAuto ilovasi · o'z client'ingiz ixtiyoriy" : "Client ID va Secret"} onClick={() => setGoogleOpen(true)} />
            </div>
          )}
        </>
      ) : null}
    </div>
  );
}

// ------------------------------------------------------------------ YouTube ulanishi (Google OAuth client)
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
    <Card title="YouTube ulanishi" sub="Bir marta, barcha kanallar uchun" action={
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
const DESC_KEYS = ["locationDescription"] as const;

/** Barcha kanallar uchun umumiy: joy tavsifi. Teglar va tavsif - har kanalda alohida (ChannelDefaultsCard). */
function DescriptionCard({ y, busy, save }: { y: Yt; busy: Busy; save: SaveFn }) {
  const saved = useMemo(() => pick(y, DESC_KEYS), [y]);
  const d = useDraft(saved);
  if (!d.value) return null;
  const v = d.value;
  const saving = busy.is("desc");
  async function onSave() {
    if (await save("desc", "Joy tavsifi", { ...d.value })) d.reset();
  }
  return (
    <Card title="Joy tavsifi" sub="Ixtiyoriy · videoning joylashuvi" action={<SaveBtn dirty={d.dirty} saving={saving} done={busy.done("desc")} onClick={() => void onSave()} />}>
      <Field label={<label htmlFor="yt-loc">Joy tavsifi</label>}>
        <input id="yt-loc" className="inp" value={v.locationDescription} disabled={saving} placeholder="Toshkent, O'zbekiston" onChange={(e) => d.set({ locationDescription: e.target.value })} />
      </Field>
    </Card>
  );
}

// ------------------------------------------------------------------ Kanalning teglari va tavsifi (har kanalda alohida)
type ChannelInfo = YtStatus["channels"][number];

/**
 * Faol kanalning standart teglari va tavsifi: shu kanal videolariga qo'yiladi (bo'sh bo'lsa Gemini yozadi). Kanal almashtirilsa
 * karta o'sha kanal qiymatlari bilan qayta ochiladi (key = kanal id). Saqlash - /api/youtube {action: "defaults"}.
 */
function ChannelDefaultsCard({ ch, busy, toast, onSaved }: { ch: ChannelInfo; busy: Busy; toast: SettingsPageProps["toast"]; onSaved: (s: YtStatus) => void }) {
  const saved = useMemo(() => ({ defaultTags: ch.defaults?.tags ?? "", defaultDescription: ch.defaults?.description ?? "" }), [ch.defaults]);
  const d = useDraft(saved);
  /** Yozilayotgan (hali chipga aylanmagan) teg: «Saqlash» bosilsa u ham qo'shiladi. */
  const [pending, setPending] = useState("");
  if (!d.value) return null;
  const v = d.value;
  const key = "chdesc";
  const saving = busy.is(key);
  const tags = parseTags(v.defaultTags);
  const allTags = pending.trim() ? parseTags(`${v.defaultTags},${pending}`) : tags;
  const tooLong = joinTags(allTags).length > TAGS_MAX_CHARS;
  const dirty = d.dirty || !!pending.trim();
  const name = ch.title || "shu kanal";
  async function onSave() {
    if (tooLong) return;
    if (!(await confirmBox(`«${name}» kanalining teglari va tavsifi saqlansinmi? Shu kanalning yangi videolaridan boshlab qo'llanadi.`))) return;
    const r = await busy.run(key, () => api<YtStatus>("/api/youtube", { action: "defaults", id: ch.id, tags: joinTags(allTags), description: v.defaultDescription }));
    if (r === false) return;
    onSaved(r); d.reset(); setPending("");
    toast(`«${name}»: teglar va tavsif saqlandi.`);
  }
  return (
    <Card title="Tavsif va teglar" sub={`Faqat «${name}» videolari uchun · bo'sh qolsa, Gemini o'zi yozadi`}
      action={<SaveBtn dirty={dirty && !tooLong} saving={saving} done={busy.done(key)} onClick={() => void onSave()} />}>
      <Field label={<label htmlFor="yt-tags">Standart teglar</label>}
        error={tooLong ? `Teglar birga ${TAGS_MAX_CHARS} belgidan oshmasin (YouTube cheklovi).` : undefined}
        hint="Tegni yozib vergul yoki Enter bosing — u tepada chiqadi. To'ldirilsa faqat shular qo'yiladi, bo'sh bo'lsa Gemini har videoga 5 ta teg yozadi. Boshqa kanalning teglari alohida.">
        <TagInput id="yt-tags" tags={tags} pending={pending} disabled={saving} placeholder="fan, texnologiya, ilm-fan"
          onTags={(t) => d.set({ defaultTags: joinTags(t) })} onPending={setPending} />
      </Field>
      <Field label={<label htmlFor="yt-desc">Standart tavsif</label>} right={`${v.defaultDescription.length}/2000`}
        hint="To'ldirilsa faqat shu qo'yiladi, bo'sh bo'lsa Gemini har videoga tavsif yozadi.">
        <textarea id="yt-desc" className="inp" rows={4} maxLength={2000} disabled={saving} value={v.defaultDescription}
          placeholder="Masalan: kanal havolalari, ijtimoiy tarmoqlar, ogohlantirish…" onChange={(e) => d.set({ defaultDescription: e.target.value })} />
      </Field>
    </Card>
  );
}

// ------------------------------------------------------------------ Kanal ma'lumotlarini tozalash
type PurgeResponse = YtStatus & { state?: State; result?: { ok: boolean; deleted: number; skipped: number; message: string } };

/**
 * Faol kanal ma'lumotlarini tozalash: chiqqan videolar tarixi yoki kanalga tegishli hamma narsa (YtAuto'da). YouTube'dagi videolarga
 * tegilmaydi. Har biri aniq ogohlantirish bilan; javobda ilovaning yangi holati keladi (alohida yangilash yo'q).
 */
function ChannelDataCard({ ch, busy, toast, onDone }: { ch: ChannelInfo; busy: Busy; toast: SettingsPageProps["toast"]; onDone: (r: PurgeResponse) => void }) {
  const name = ch.title || ch.id;
  const videos = ch.videos ?? 0, published = ch.published ?? 0;
  const working = busy.is("purge") || busy.is("purge_published");

  async function purge(action: "purge" | "purge_published") {
    const text = action === "purge_published"
      ? `«${name}» kanalining ${published} ta chiqqan video tarixi o'chirilsinmi?\n\nYtAuto'dagi yozuvlari, loglari va bosqichlari o'chadi. YouTube'dagi videolar O'ZGARMAYDI. Qaytarib bo'lmaydi.`
      : `«${name}» kanalining BARCHA ma'lumotlari o'chirilsinmi?\n\n• ${videos} ta video/mavzu (navbatdagilar ham) — yozuvlari, loglari, fayllari\n• kanal tahlili, mavzu tavsiyalari, kontekst\n• standart teglar va tavsif\n\nYouTube'dagi videolar O'ZGARMAYDI, kanal ulanishi qoladi. Qaytarib bo'lmaydi.`;
    if (!(await confirmBox(text))) return;
    if (action === "purge" && !(await confirmBox(`Aniqmi? «${name}» bo'yicha YtAuto'dagi hamma narsa butunlay o'chadi.`))) return;
    const r = await busy.run(action, () => api<PurgeResponse>("/api/youtube", { action, id: ch.id }));
    if (r === false) return;
    onDone(r);
    if (r.result) toast(r.result.message, !r.result.ok);
  }

  return (
    <Card title="Ma'lumotlarni tozalash" sub={`Faqat YtAuto'da · YouTube'dagi videolarga tegilmaydi`}>
      <div className="sub">{videos} ta video yozuvi, ulardan {published} tasi chiqqan.</div>
      {/* Ustma-ust, to'liq kenglikda: telefonda uzun yozuvlar sig'adi. */}
      <div style={{ display: "grid", gap: 8 }}>
        <Btn tone="secondary" block icon={History} loading={busy.is("purge_published")} disabled={working || !published} onClick={() => void purge("purge_published")}>
          Chiqqan videolar tarixini tozalash
        </Btn>
        <Btn tone="danger" block icon={Trash2} loading={busy.is("purge")} disabled={working} onClick={() => void purge("purge")}>
          Kanal ma&apos;lumotlarini o&apos;chirish
        </Btn>
      </div>
    </Card>
  );
}

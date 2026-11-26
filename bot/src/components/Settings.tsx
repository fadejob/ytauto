"use client";

import { useEffect, useState } from "react";
import { api, cache, confirmBox, openExternal } from "@/lib/client";
import { FormatText, type BotConfig, type BotConfigInput, type Catalog, type VoiceInfo } from "@/lib/types";
import type { Toast } from "./App";
import VoiceProfiles from "./VoiceProfiles";
import VoiceClone from "./VoiceClone";
import Library from "./Library";
import Guests from "./Guests";
import McpConnect from "./McpConnect";
import GeminiLimits from "./GeminiLimits";
import { DEFAULT_GEMINI_CHAIN, limitsFor } from "@/lib/gemini-models";

/**
 * Claude Code (montaj) modeli: `claude --model <id>` ga uzatiladi. Bo'sh - Claude Code'ning o'z standarti.
 * Fable - eng kuchli va eng qimmat; obunangizda bo'lmasa Claude Code xato beradi.
 */
const CLAUDE_MODELS: [string, string][] = [
  ["", "Claude Code standarti"],
  ["claude-opus-5-5", "Claude Opus 5.5 — tavsiya"],
  ["claude-sonnet-5-5", "Claude Sonnet 5.5 — tezroq, arzonroq"],
  ["claude-haiku-5-5", "Claude Haiku 5.5 — eng tez va arzon"],
  ["claude-fable-5-2", "Claude Fable 5.2 — eng kuchli, eng qimmat"],
];

type Sch = { enabled: boolean; startTime: string; endTime: string; timeZoneId: string; wakeToRun: boolean; executionLimitHours: number };
type Draft = BotConfigInput;

const toDraft = (c: BotConfig): Draft => ({
  gemini: { ...c.gemini, apiKeys: c.gemini.apiKeys.map((k) => ({ ...k })) },
  limits: { ...c.limits },
  defaults: { ...c.defaults },
  youtube: { ...c.youtube, clientSecret: "" },
  openFilm: { ...c.openFilm },
  visuals: { ...c.visuals, pexelsApiKey: "", pixabayApiKey: "" },
  claude: { ...c.claude },
  voice: { profiles: c.voice.profiles.map((p) => ({ ...p })), settings: { ...c.voice.settings } },
});

const CATEGORIES: [string, string][] = [
  ["1", "Film va animatsiya"], ["2", "Avto va transport"], ["10", "Musiqa"], ["15", "Uy hayvonlari"], ["17", "Sport"], ["19", "Sayohat"],
  ["20", "O'yinlar"], ["22", "Odamlar va blog"], ["23", "Komediya"], ["24", "Ko'ngilochar"], ["25", "Yangiliklar va siyosat"],
  ["26", "Qo'llanma va uslub"], ["27", "Ta'lim"], ["28", "Fan va texnologiya"], ["29", "Notijorat va faollik"],
];

export default function Settings({ catalog, toast, onSaved, onLogout, account }: {
  catalog: Catalog | null; toast: Toast; onSaved?: () => void; onLogout?: () => void; account?: { guest: boolean; hostLabel: string | null };
}) {
  /** Qo'shimcha token: kompyuter darajasidagi sozlamalar (jadval, Claude, limitlar, render, VoiceStudio) kompyuter egasida qoladi. */
  const guest = account?.guest === true;
  const [sch, setSch] = useState<Sch | null>(null);
  const [schSaved, setSchSaved] = useState<Sch | null>(null);
  const [cfg, setCfg] = useState<Draft | null>(null);
  const [cfgSaved, setCfgSaved] = useState<BotConfig | null>(null);
  /** Yangi Gemini kalitlari: har qatorga bittasi (saqlanganda qo'shiladi). */
  const [newKeys, setNewKeys] = useState("");
  /** Gemini model zanjiri matni: har qatorga bitta model (birinchisi asosiy). */
  const [voiceInfo, setVoiceInfo] = useState<VoiceInfo[]>([]);
  const [saving, setSaving] = useState(false);
  const [yt, setYt] = useState<{ connected: boolean; channelTitle: string; connectedAt: string | null; clientConfigured: boolean; redirectUri: string; globalClient: boolean } | null>(null);
  const [ytBusy, setYtBusy] = useState(false);
  const [ytCheck, setYtCheck] = useState<{ ok: boolean; message: string } | null>(null);

  // Ulanish tugagach (tashqi brauzerdan qaytilganda) holat yangilanadi.
  useEffect(() => {
    const onVis = () => { if (document.visibilityState === "visible") api<NonNullable<typeof yt>>("/api/youtube").then(setYt).catch(() => {}); };
    document.addEventListener("visibilitychange", onVis);
    return () => document.removeEventListener("visibilitychange", onVis);
  }, []);

  async function connectYt() {
    setYtBusy(true);
    try { const { url } = await api<{ url: string }>("/api/youtube", { action: "start" }); openExternal(url); toast("Google oynasi ochildi. Ruxsat bergach shu yerga qayting."); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setYtBusy(false);
  }
  async function verifyYt() {
    setYtBusy(true); setYtCheck(null);
    try { setYtCheck(await api<{ ok: boolean; message: string }>("/api/youtube", { action: "verify" })); }
    catch (e) { setYtCheck({ ok: false, message: e instanceof Error ? e.message : String(e) }); }
    setYtBusy(false);
  }
  async function disconnectYt() {
    if (!(await confirmBox("YouTube kanali uziladimi? Barcha kompyuterlarda yuklash to'xtaydi."))) return;
    try { setYt(await api("/api/youtube", { action: "disconnect" })); toast("YouTube uzildi."); }
    catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
  }

  useEffect(() => {
    const c = cache.get<Sch>("schedule");
    // eslint-disable-next-line react-hooks/set-state-in-effect -- localStorage faqat mount'dan keyin o'qiladi
    if (c) { setSch(c); setSchSaved(c); }
    api<Sch>("/api/schedule").then((s) => { setSch(s); setSchSaved(s); cache.set("schedule", s); }).catch((e) => toast(e instanceof Error ? e.message : String(e), true));
    api<NonNullable<typeof yt>>("/api/youtube").then(setYt).catch(() => {});
    api<{ voice: VoiceInfo[] }>("/api/library").then((d) => setVoiceInfo(d.voice)).catch(() => {});
    api<BotConfig>("/api/config").then((c) => { setCfgSaved(c); setCfg(toDraft(c)); }).catch((e) => toast(e instanceof Error ? e.message : String(e), true));
  }, [toast]);

  const schDirty = !!sch && !!schSaved && JSON.stringify(sch) !== JSON.stringify(schSaved);
  const cfgDirty = !!cfg && !!cfgSaved && (JSON.stringify({ ...cfg, youtube: { ...cfg.youtube, clientSecret: "" } }) !== JSON.stringify(toDraft(cfgSaved))
    || !!newKeys.trim() || !!cfg.youtube.clientSecret);
  const dirty = schDirty || cfgDirty;

  async function save() {
    if (!dirty) return;
    if (!(await confirmBox("Sozlamalar saqlansinmi? Kompyuterlar ~2 daqiqada yangilanadi, yangi videolardan boshlab qo'llanadi."))) return;
    setSaving(true);
    try {
      if (schDirty && sch) { const s = await api<Sch>("/api/schedule", sch); setSch(s); setSchSaved(s); cache.set("schedule", s); }
      if (cfgDirty && cfg) {
        const newApiKeys = newKeys.split(/[\s,;]+/).map((k) => k.trim()).filter(Boolean);
        const c = await api<BotConfig>("/api/config", { ...cfg, gemini: { ...cfg.gemini, newApiKeys } });
        setCfgSaved(c); setCfg(toDraft(c)); setNewKeys("");
        onSaved?.(); // Gemini kaliti qo'shilgan bo'lsa mavzular va AI yordamchi ochiladi
      }
      toast("Sozlamalar saqlandi.");
    } catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setSaving(false);
  }

  const G = (p: Partial<Draft["gemini"]>) => setCfg((c) => c && { ...c, gemini: { ...c.gemini, ...p } });
  const D = (p: Partial<Draft["defaults"]>) => setCfg((c) => c && { ...c, defaults: { ...c.defaults, ...p } });
  const Y = (p: Partial<Draft["youtube"]>) => setCfg((c) => c && { ...c, youtube: { ...c.youtube, ...p } });
  const V = (p: Partial<Draft["visuals"]>) => setCfg((c) => c && { ...c, visuals: { ...c.visuals, ...p } });
  const C = (p: Partial<Draft["claude"]>) => setCfg((c) => c && { ...c, claude: { ...c.claude, ...p } });
  const num = (v: string) => (v === "" ? 0 : Number(v));

  return (
    <>
      <header className="head">
        <div>
          <h1>Sozlamalar</h1>
          <div className="sub">Faqat sizning hisobingiz uchun (online baza)</div>
        </div>
      </header>
      <div className="list">
        {!sch || !cfg ? <div className="skeleton" style={{ height: 220 }} /> : (
          <>
            {guest ? (
              <>
                <div className="sect">Qo&apos;shimcha token</div>
                <div className="card formcard">
                  <div className="hint">
                    Videolaringiz {account?.hostLabel ? <b>«{account.hostLabel}»</b> : "token bergan hisob"} kompyuterida, uning ish vaqtida tayyorlanadi —
                    chiqish vaqti yaqinlari birinchi. Kech tayyor bo&apos;lsa, YouTube&apos;ga tayyor bo&apos;lgandan 1 soat keyin chiqadi.
                    «Hozir chiqarish» va kompyuter sozlamalari (jadval, Claude, render, limitlar) kompyuter egasida.
                    Gemini kaliti, promptlar, ovoz profillari va YouTube kanalingiz — o&apos;zingizniki.
                  </div>
                </div>
              </>
            ) : null}
            {!guest ? <>
            <div className="sect">Jadval</div>
            <div className="card formcard">
              <label className="chk"><input type="checkbox" checked={sch.enabled} onChange={(e) => setSch({ ...sch, enabled: e.target.checked })} /><span>Kunlik jadval yoqilgan</span></label>
              <div className="grid2">
                <div className="field"><label>Ish oynasi boshi (HH:mm)</label><input className="input" value={sch.startTime} inputMode="numeric" placeholder="01:00" onChange={(e) => setSch({ ...sch, startTime: e.target.value })} /></div>
                <div className="field"><label>Ish oynasi oxiri (HH:mm)</label><input className="input" value={sch.endTime} inputMode="numeric" placeholder="04:00" onChange={(e) => setSch({ ...sch, endTime: e.target.value })} /></div>
                <div className="field"><label>Vaqt zonasi</label><input className="input" value={sch.timeZoneId} onChange={(e) => setSch({ ...sch, timeZoneId: e.target.value })} /></div>
              </div>
              <label className="chk"><input type="checkbox" checked={sch.wakeToRun} onChange={(e) => setSch({ ...sch, wakeToRun: e.target.checked })} /><span>Uyqudan uyg&apos;otish (WakeToRun)</span></label>
              <div className="hint">Jadval o&apos;chiq bo&apos;lsa ham «Hozir chiqarish» ishlayveradi.</div>
            </div>
            </> : null}

            <div className="sect">Gemini (ssenariy)</div>
            <div className="card formcard">
              <div className="field"><label>API kalitlari {cfg.gemini.apiKeys.length ? `· ${cfg.gemini.apiKeys.length} ta saqlangan` : "· kiritilmagan"}</label>
                {cfg.gemini.apiKeys.map((k, i) => (
                  <div key={k.id} style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 6 }}>
                    <span className="chip mute">{i + 1}</span>
                    <code style={{ flex: 1 }}>{k.hint}</code>
                    <button className="btn danger" type="button" onClick={() => G({ apiKeys: cfg.gemini.apiKeys.filter((x) => x.id !== k.id) })}>O&apos;chirish</button>
                  </div>
                ))}
                <textarea className="textarea" rows={2} autoComplete="off" spellCheck={false} value={newKeys} placeholder={"Yangi kalit(lar): AIza…\nhar qatorga bittadan"} onChange={(e) => setNewKeys(e.target.value)} />
              </div>
              <div className="hint">Kalitlar tartib bilan ishlatiladi: biri limitga (429) tushsa keyingisi bilan davom etiladi, hammasi tugasa ish to&apos;xtaydi. AI yordamchi ham shu kalitlardan foydalanadi.</div>
              {(() => {
                // Zanjirning birinchisi - joriy model; tanlangani boshga o'tadi, qolganlari shu tartibda zaxira bo'lib qoladi.
                const chain = cfg.gemini.models.length ? cfg.gemini.models : DEFAULT_GEMINI_CHAIN;
                const options = [...chain, ...DEFAULT_GEMINI_CHAIN.filter((m) => !chain.some((c) => c.toLowerCase() === m.toLowerCase()))];
                const perDay = (m: string) => { const l = limitsFor(m); return l ? ` — bepul kalitda kuniga ${l.rpd.toLocaleString("en-US").replace(/,/g, " ")} so'rov` : ""; };
                return (
                  <div className="field"><label>Joriy model</label>
                    <select className="input" value={chain[0]} onChange={(e) => G({ models: [e.target.value, ...options.filter((m) => m !== e.target.value)] })}>
                      {options.map((m) => <option key={m} value={m}>{m}{perDay(m)}</option>)}
                    </select>
                    <div className="hint">Ssenariy va boshqa Gemini ishlari shu model bilan bajariladi. Uning limiti tugasa yoki ishlamasa, qolgan modellarga navbat bilan avtomatik o&apos;tiladi.</div>
                  </div>
                );
              })()}
            </div>
            {!guest ? <GeminiLimits toast={toast} /> : null}

            {!guest ? <>
            <div className="sect">Claude (montaj)</div>
            <div className="card formcard">
              <div className="grid2">
                <div className="field"><label>Model</label>
                  <select className="input" value={cfg.claude.model} onChange={(e) => C({ model: e.target.value })}>
                    {CLAUDE_MODELS.map(([id, label]) => <option key={id || "default"} value={id}>{label}</option>)}
                    {cfg.claude.model && !CLAUDE_MODELS.some(([id]) => id === cfg.claude.model) ? <option value={cfg.claude.model}>{cfg.claude.model}</option> : null}
                  </select></div>
                <div className="field"><label>Maks. byudjet USD (0 — cheklovsiz)</label><input className="input" inputMode="decimal" value={cfg.claude.maxBudgetUsd} onChange={(e) => C({ maxBudgetUsd: num(e.target.value) })} /></div>
              </div>
            </div>
            </> : null}

            <VoiceClone toast={toast} onChanged={() => {
              // Klon profili server tomonda qo'shildi/olib tashlandi: qoralama yangilanadi (saqlanmagan o'zgarishlar bo'lmasa).
              if (!cfgDirty) api<BotConfig>("/api/config").then((c) => { setCfgSaved(c); setCfg(toDraft(c)); }).catch(() => {});
              onSaved?.();
            }} />

            <VoiceProfiles
              profiles={cfg.voice.profiles} defaultProfile={cfg.defaults.voiceProfile} info={voiceInfo}
              onProfiles={(p) => setCfg((c) => c && { ...c, voice: { ...c.voice, profiles: p } })}
              onDefault={(n) => D({ voiceProfile: n })}
            />

            <div className="sect">Yangi mavzu standartlari</div>
            <div className="card formcard">
              <div className="grid2">
                <div className="field"><label>Shablon</label>
                  <select className="input" value={cfg.defaults.template} onChange={(e) => D({ template: e.target.value })}>
                    <option value="">Shablonsiz</option>
                    {catalog?.templates.map((t) => <option key={t.name} value={t.name}>{t.name}</option>)}
                    {cfg.defaults.template && !catalog?.templates.some((t) => t.name === cfg.defaults.template) ? <option value={cfg.defaults.template}>{cfg.defaults.template}</option> : null}
                  </select></div>
                <div className="field"><label>Format</label>
                  <select className="input" value={cfg.defaults.format} onChange={(e) => D({ format: Number(e.target.value) })}>
                    {[0, 1, 2].map((f) => <option key={f} value={f}>{FormatText[f]}</option>)}
                  </select></div>
              </div>
              <div className="grid2">
                <div className="field"><label>Ovoz profili</label>
                  <select className="input" value={cfg.defaults.voiceProfile} onChange={(e) => D({ voiceProfile: e.target.value })}>
                    <option value="">Kompyuterdagi standart</option>
                    {catalog?.voiceProfiles.map((v) => <option key={v} value={v}>{v}</option>)}
                  </select></div>
                <div className="field"><label>Til</label><input className="input" value="en (doim ingliz tili)" disabled /></div>
              </div>
            </div>

            <Library toast={toast} canUpload={!guest} />

            {!guest ? <Guests toast={toast} /> : null}

            <McpConnect toast={toast} />

            <div className="sect">Video sifati</div>
            <div className="card formcard">
              <label className="chk"><input type="checkbox" checked={cfg.visuals.realMedia} onChange={(e) => V({ realMedia: e.target.checked })} /><span>Real rasm, logo va ikonlar (barchasi bepul va tijoratda ishlatish mumkin)</span></label>
              <label className="chk"><input type="checkbox" checked={cfg.visuals.styleReview} onChange={(e) => V({ styleReview: e.target.checked })} /><span>Tayyor videoni shablon bilan solishtirish va mos kelmasa tuzattirish</span></label>
              <div className="grid2">
                <div className="field"><label>Pexels API kaliti {cfg.visuals.pexelsKeySet ? "· saqlangan ✓" : "· kiritilmagan"}</label>
                  <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                    <input className="input" type="password" autoComplete="off" value={cfg.visuals.pexelsApiKey ?? ""} placeholder={cfg.visuals.pexelsKeySet ? "O'zgartirish uchun yangisini kiriting" : "Bepul: pexels.com/api"} onChange={(e) => V({ pexelsApiKey: e.target.value })} />
                    {cfg.visuals.pexelsKeySet ? <button className="btn danger" type="button" onClick={() => V({ pexelsKeySet: false, pexelsApiKey: "" })}>O&apos;chirish</button> : null}
                  </div></div>
                <div className="field"><label>Pixabay API kaliti {cfg.visuals.pixabayKeySet ? "· saqlangan ✓" : "· kiritilmagan"}</label>
                  <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                    <input className="input" type="password" autoComplete="off" value={cfg.visuals.pixabayApiKey ?? ""} placeholder={cfg.visuals.pixabayKeySet ? "O'zgartirish uchun yangisini kiriting" : "Bepul: pixabay.com/api/docs"} onChange={(e) => V({ pixabayApiKey: e.target.value })} />
                    {cfg.visuals.pixabayKeySet ? <button className="btn danger" type="button" onClick={() => V({ pixabayKeySet: false, pixabayApiKey: "" })}>O&apos;chirish</button> : null}
                  </div></div>
              </div>
            </div>

            <div className="sect">YouTube</div>
            <div className="card formcard">
              <div className="field">
                <label>Kanal {yt?.connected ? `· ulangan ✓ ${yt.channelTitle}` : "· ulanmagan"}</label>
                <div className="grid2">
                  <button className="btn primary block" disabled={ytBusy || !yt?.clientConfigured || dirty} onClick={connectYt}>{yt?.connected ? "Qayta ulash" : "YouTube'ni ulash"}</button>
                  <button className="btn block" disabled={!yt?.connected} onClick={disconnectYt}>Uzish</button>
                </div>
                <button className="btn block" disabled={ytBusy || !yt?.connected} onClick={verifyYt}>Ulanishni tekshirish</button>
                {ytCheck ? <div className={ytCheck.ok ? "hint" : "err"}>{ytCheck.ok ? "✓ " : "✗ "}{ytCheck.message}</div> : null}
                {!yt?.clientConfigured ? <div className="hint">Avval Client ID va Secret&apos;ni kiritib, «Saqlash» ni bosing.</div> : dirty ? <div className="hint">Avval o&apos;zgarishlarni saqlang.</div> : null}
                {yt?.globalClient ? <div className="hint">«YouTube&apos;ni ulash» ni bosing va o&apos;z kanalingiz Google hisobini tanlang — boshqa hech narsa kerak emas. Kanalingiz faqat sizning hisobingizda saqlanadi.</div> : null}
              </div>
              <details open={yt ? !yt.globalClient : undefined}>
                <summary className="hint">{yt?.globalClient ? "Kengaytirilgan: o'z Google OAuth client'im (ixtiyoriy)" : "Google OAuth client"}</summary>
                <div className="field"><label>OAuth Client ID</label><input className="input" value={cfg.youtube.clientId} autoComplete="off" onChange={(e) => Y({ clientId: e.target.value })} /></div>
                <div className="field"><label>Client Secret {cfg.youtube.clientSecretSet ? "· saqlangan ✓" : "· kiritilmagan"}</label>
                <input className="input" type="password" autoComplete="off" value={cfg.youtube.clientSecret ?? ""} placeholder={cfg.youtube.clientSecretSet ? "O'zgartirish uchun yangisini kiriting" : ""} onChange={(e) => Y({ clientSecret: e.target.value })} /></div>
                <div className="hint">Google Cloud → Credentials → <b>Web application</b> turidagi OAuth client yarating va «Authorized redirect URIs» ga shu manzilni qo&apos;shing:<br /><code style={{ wordBreak: "break-all" }}>{yt?.redirectUri ?? "…"}</code>{yt?.globalClient ? <><br />Bo&apos;sh qoldirilsa umumiy ilova ishlatiladi.</> : null}</div>
              </details>
              <div className="grid2">
                <div className="field"><label>Public soati (HH:mm)</label><input className="input" value={cfg.youtube.publishTime} onChange={(e) => Y({ publishTime: e.target.value })} /></div>
                <div className="field"><label>Vaqt zonasi</label><input className="input" value={cfg.youtube.timeZoneId} onChange={(e) => Y({ timeZoneId: e.target.value })} /></div>
                <div className="field"><label>Kategoriya</label>
                  <select className="input" value={cfg.youtube.categoryId} onChange={(e) => Y({ categoryId: e.target.value })}>
                    {CATEGORIES.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
                    {!CATEGORIES.some(([id]) => id === cfg.youtube.categoryId) ? <option value={cfg.youtube.categoryId}>Kategoriya {cfg.youtube.categoryId}</option> : null}
                  </select></div>
                <div className="field"><label>Litsenziya</label>
                  <select className="input" value={cfg.youtube.license} onChange={(e) => Y({ license: e.target.value })}>
                    <option value="youtube">Standart YouTube</option><option value="creativeCommon">Creative Commons (CC BY)</option>
                  </select></div>
                <div className="field"><label>Mamlakat kodi</label><input className="input" value={cfg.youtube.regionCode} onChange={(e) => Y({ regionCode: e.target.value })} /></div>
                <div className="field"><label>Min. oldindan (daqiqa)</label><input className="input" inputMode="numeric" value={cfg.youtube.minLeadMinutes} onChange={(e) => Y({ minLeadMinutes: num(e.target.value) })} /></div>
              </div>
              <div className="field"><label>Standart teglar (vergul bilan) — to&apos;ldirilsa faqat shular qo&apos;yiladi, bo&apos;sh bo&apos;lsa Gemini har videoga 5 ta teg yozadi</label><input className="input" value={cfg.youtube.defaultTags} onChange={(e) => Y({ defaultTags: e.target.value })} /></div>
              <div className="field"><label>Standart tavsif — to&apos;ldirilsa faqat shu qo&apos;yiladi, bo&apos;sh bo&apos;lsa Gemini har videoga tavsif yozadi</label><textarea className="textarea" rows={4} maxLength={2000} placeholder="Masalan: kanal havolalari, ijtimoiy tarmoqlar, ogohlantirish…" value={cfg.youtube.defaultDescription} onChange={(e) => Y({ defaultDescription: e.target.value })} /></div>
              <div className="field"><label>Joy tavsifi (ixtiyoriy)</label><input className="input" value={cfg.youtube.locationDescription} onChange={(e) => Y({ locationDescription: e.target.value })} /></div>
              <label className="chk"><input type="checkbox" checked={cfg.youtube.madeForKids} onChange={(e) => Y({ madeForKids: e.target.checked })} /><span>Bolalar uchun</span></label>
              <label className="chk"><input type="checkbox" checked={cfg.youtube.containsSyntheticMedia} onChange={(e) => Y({ containsSyntheticMedia: e.target.checked })} /><span>AI (sintetik) kontent belgisi</span></label>
              <label className="chk"><input type="checkbox" checked={cfg.youtube.embeddable} onChange={(e) => Y({ embeddable: e.target.checked })} /><span>Boshqa saytlarga joylashga ruxsat</span></label>
              <label className="chk"><input type="checkbox" checked={cfg.youtube.publicStatsViewable} onChange={(e) => Y({ publicStatsViewable: e.target.checked })} /><span>Statistika hammaga ko&apos;rinadi</span></label>
              <label className="chk"><input type="checkbox" checked={cfg.youtube.notifySubscribers} onChange={(e) => Y({ notifySubscribers: e.target.checked })} /><span>Obunachilarga bildirishnoma</span></label>
              <label className="chk"><input type="checkbox" checked={cfg.youtube.setRecordingDate} onChange={(e) => Y({ setRecordingDate: e.target.checked })} /><span>Yozib olingan sanani belgilash</span></label>
            </div>
          </>
        )}
        {onLogout ? (
          <button className="btn ghost block" onClick={async () => { if (await confirmBox("Bu Telegram hisobi YtAuto hisobidan uzilsinmi? Qayta kirish uchun token kerak bo'ladi.")) onLogout(); }}>
            Hisobdan chiqish (boshqa token bilan kirish)
          </button>
        ) : null}
        <div className="grid2 savebar">
          <button className="btn block" disabled={!dirty || saving} onClick={() => { if (schSaved) setSch(schSaved); if (cfgSaved) setCfg(toDraft(cfgSaved)); }}>Qaytarish</button>
          <button className="btn primary block" disabled={!dirty || saving} onClick={save}>{saving ? "Saqlanmoqda…" : "Saqlash"}</button>
        </div>
      </div>
    </>
  );
}

"use client";

import { useEffect, useState } from "react";
import { api, cache, confirmBox, openExternal } from "@/lib/client";
import { FormatText, type BotConfig, type BotConfigInput, type Catalog, type VoiceInfo } from "@/lib/types";
import type { Toast } from "./App";
import VoiceProfiles from "./VoiceProfiles";
import Library from "./Library";

type Sch = { enabled: boolean; startTime: string; endTime: string; timeZoneId: string; wakeToRun: boolean; executionLimitHours: number };
type Draft = BotConfigInput;

const toDraft = (c: BotConfig): Draft => ({
  gemini: { ...c.gemini, apiKey: "" },
  limits: { ...c.limits },
  defaults: { ...c.defaults },
  youtube: { ...c.youtube, clientSecret: "" },
  openFilm: { ...c.openFilm },
  claude: { ...c.claude },
  voice: { profiles: c.voice.profiles.map((p) => ({ ...p })), settings: { ...c.voice.settings } },
});

const CATEGORIES: [string, string][] = [
  ["1", "Film va animatsiya"], ["2", "Avto va transport"], ["10", "Musiqa"], ["15", "Uy hayvonlari"], ["17", "Sport"], ["19", "Sayohat"],
  ["20", "O'yinlar"], ["22", "Odamlar va blog"], ["23", "Komediya"], ["24", "Ko'ngilochar"], ["25", "Yangiliklar va siyosat"],
  ["26", "Qo'llanma va uslub"], ["27", "Ta'lim"], ["28", "Fan va texnologiya"], ["29", "Notijorat va faollik"],
];

export default function Settings({ catalog, toast }: { catalog: Catalog | null; toast: Toast }) {
  const [sch, setSch] = useState<Sch | null>(null);
  const [schSaved, setSchSaved] = useState<Sch | null>(null);
  const [cfg, setCfg] = useState<Draft | null>(null);
  const [cfgSaved, setCfgSaved] = useState<BotConfig | null>(null);
  const [voiceInfo, setVoiceInfo] = useState<VoiceInfo[]>([]);
  const [saving, setSaving] = useState(false);
  const [yt, setYt] = useState<{ connected: boolean; channelTitle: string; connectedAt: string | null; clientConfigured: boolean; redirectUri: string } | null>(null);
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
  const cfgDirty = !!cfg && !!cfgSaved && (JSON.stringify({ ...cfg, gemini: { ...cfg.gemini, apiKey: "" }, youtube: { ...cfg.youtube, clientSecret: "" } }) !== JSON.stringify(toDraft(cfgSaved))
    || !!cfg.gemini.apiKey || !!cfg.youtube.clientSecret);
  const dirty = schDirty || cfgDirty;

  async function save() {
    if (!dirty) return;
    if (!(await confirmBox("Sozlamalar saqlansinmi? Kompyuterlar ~2 daqiqada yangilanadi, yangi videolardan boshlab qo'llanadi."))) return;
    setSaving(true);
    try {
      if (schDirty && sch) { const s = await api<Sch>("/api/schedule", sch); setSch(s); setSchSaved(s); cache.set("schedule", s); }
      if (cfgDirty && cfg) { const c = await api<BotConfig>("/api/config", cfg); setCfgSaved(c); setCfg(toDraft(c)); }
      toast("Sozlamalar saqlandi.");
    } catch (e) { toast(e instanceof Error ? e.message : String(e), true); }
    setSaving(false);
  }

  const G = (p: Partial<Draft["gemini"]>) => setCfg((c) => c && { ...c, gemini: { ...c.gemini, ...p } });
  const L = (p: Partial<Draft["limits"]>) => setCfg((c) => c && { ...c, limits: { ...c.limits, ...p } });
  const D = (p: Partial<Draft["defaults"]>) => setCfg((c) => c && { ...c, defaults: { ...c.defaults, ...p } });
  const Y = (p: Partial<Draft["youtube"]>) => setCfg((c) => c && { ...c, youtube: { ...c.youtube, ...p } });
  const O = (p: Partial<Draft["openFilm"]>) => setCfg((c) => c && { ...c, openFilm: { ...c.openFilm, ...p } });
  const C = (p: Partial<Draft["claude"]>) => setCfg((c) => c && { ...c, claude: { ...c.claude, ...p } });
  const num = (v: string) => (v === "" ? 0 : Number(v));

  return (
    <>
      <header className="head">
        <div>
          <h1>Sozlamalar</h1>
          <div className="sub">Barcha kompyuterlar uchun umumiy (online baza)</div>
        </div>
      </header>
      <div className="list">
        {!sch || !cfg ? <div className="skeleton" style={{ height: 220 }} /> : (
          <>
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

            <div className="sect">Gemini (ssenariy)</div>
            <div className="card formcard">
              <div className="field"><label>API kaliti {cfg.gemini.apiKeySet ? "· saqlangan ✓" : "· kiritilmagan"}</label>
                <input className="input" type="password" autoComplete="off" value={cfg.gemini.apiKey ?? ""} placeholder={cfg.gemini.apiKeySet ? "O'zgartirish uchun yangisini kiriting" : "AIza…"} onChange={(e) => G({ apiKey: e.target.value })} /></div>
              <div className="grid2">
                <div className="field"><label>Model</label><input className="input" value={cfg.gemini.model} onChange={(e) => G({ model: e.target.value })} /></div>
                <div className="field"><label>Temperature</label><input className="input" inputMode="decimal" value={cfg.gemini.temperature} onChange={(e) => G({ temperature: num(e.target.value) })} /></div>
                <div className="field"><label>Timeout (soniya)</label><input className="input" inputMode="numeric" value={cfg.gemini.timeoutSec} onChange={(e) => G({ timeoutSec: num(e.target.value) })} /></div>
              </div>
              <label className="chk"><input type="checkbox" checked={cfg.gemini.useSearchGrounding} onChange={(e) => G({ useSearchGrounding: e.target.checked })} /><span>Hozirgi faktlar uchun Google Search</span></label>
            </div>

            <div className="sect">Claude (montaj)</div>
            <div className="card formcard">
              <div className="grid2">
                <div className="field"><label>Model (bo&apos;sh — standart)</label><input className="input" value={cfg.claude.model} onChange={(e) => C({ model: e.target.value })} /></div>
                <div className="field"><label>Timeout (daqiqa)</label><input className="input" inputMode="numeric" value={cfg.claude.timeoutMinutes} onChange={(e) => C({ timeoutMinutes: num(e.target.value) })} /></div>
                <div className="field"><label>Maks. byudjet USD (0 — cheklovsiz)</label><input className="input" inputMode="decimal" value={cfg.claude.maxBudgetUsd} onChange={(e) => C({ maxBudgetUsd: num(e.target.value) })} /></div>
              </div>
            </div>

            <VoiceProfiles
              profiles={cfg.voice.profiles} settings={cfg.voice.settings} defaultProfile={cfg.defaults.voiceProfile} info={voiceInfo}
              onProfiles={(p) => setCfg((c) => c && { ...c, voice: { ...c.voice, profiles: p } })}
              onSettings={(s) => setCfg((c) => c && { ...c, voice: { ...c.voice, settings: s } })}
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

            <Library toast={toast} />

            <div className="sect">Limitlar</div>
            <div className="card formcard">
              <div className="grid2">
                <div className="field"><label>Ishga tushish urinishlari</label><input className="input" inputMode="numeric" value={cfg.limits.maxRunAttempts} onChange={(e) => L({ maxRunAttempts: num(e.target.value) })} /></div>
                <div className="field"><label>QA tuzatish sikllari</label><input className="input" inputMode="numeric" value={cfg.limits.maxQaFixCycles} onChange={(e) => L({ maxQaFixCycles: num(e.target.value) })} /></div>
                <div className="field"><label>Min. bo&apos;sh disk (GB)</label><input className="input" inputMode="decimal" value={cfg.limits.minFreeDiskGb} onChange={(e) => L({ minFreeDiskGb: num(e.target.value) })} /></div>
                <div className="field"><label>TTS uchun min. RAM (GB)</label><input className="input" inputMode="decimal" value={cfg.limits.minFreeRamGbForTts} onChange={(e) => L({ minFreeRamGbForTts: num(e.target.value) })} /></div>
              </div>
            </div>

            <div className="sect">OpenFilm render</div>
            <div className="card formcard">
              <div className="grid2">
                <div className="field"><label>Parallel renderer (8 GB RAM: 1)</label><input className="input" inputMode="numeric" value={cfg.openFilm.renderWorkers} onChange={(e) => O({ renderWorkers: num(e.target.value) })} /></div>
                <div className="field"><label>FPS</label><input className="input" inputMode="numeric" value={cfg.openFilm.fps} onChange={(e) => O({ fps: num(e.target.value) })} /></div>
                <div className="field"><label>Loudness (LUFS)</label><input className="input" inputMode="decimal" value={cfg.openFilm.loudnessLufs} onChange={(e) => O({ loudnessLufs: num(e.target.value) })} /></div>
                <div className="field"><label>Render timeout (daqiqa)</label><input className="input" inputMode="numeric" value={cfg.openFilm.renderTimeoutMinutes} onChange={(e) => O({ renderTimeoutMinutes: num(e.target.value) })} /></div>
              </div>
              <label className="chk"><input type="checkbox" checked={cfg.openFilm.burnSubtitles} onChange={(e) => O({ burnSubtitles: e.target.checked })} /><span>Subtitrlarni videoga kuydirish</span></label>
              <label className="chk"><input type="checkbox" checked={cfg.openFilm.lookFullAfterRender} onChange={(e) => O({ lookFullAfterRender: e.target.checked })} /><span>Renderdan keyin to&apos;liq ko&apos;rik (look --full)</span></label>
            </div>

            <div className="sect">YouTube</div>
            <div className="card formcard">
              <div className="field"><label>OAuth Client ID</label><input className="input" value={cfg.youtube.clientId} autoComplete="off" onChange={(e) => Y({ clientId: e.target.value })} /></div>
              <div className="field"><label>Client Secret {cfg.youtube.clientSecretSet ? "· saqlangan ✓" : "· kiritilmagan"}</label>
                <input className="input" type="password" autoComplete="off" value={cfg.youtube.clientSecret ?? ""} placeholder={cfg.youtube.clientSecretSet ? "O'zgartirish uchun yangisini kiriting" : ""} onChange={(e) => Y({ clientSecret: e.target.value })} /></div>
              <div className="field">
                <label>Kanal {yt?.connected ? `· ulangan ✓ ${yt.channelTitle}` : "· ulanmagan"}</label>
                <div className="grid2">
                  <button className="btn primary block" disabled={ytBusy || !yt?.clientConfigured || dirty} onClick={connectYt}>{yt?.connected ? "Qayta ulash" : "YouTube'ni ulash"}</button>
                  <button className="btn block" disabled={!yt?.connected} onClick={disconnectYt}>Uzish</button>
                </div>
                <button className="btn block" disabled={ytBusy || !yt?.connected} onClick={verifyYt}>Ulanishni tekshirish</button>
                {ytCheck ? <div className={ytCheck.ok ? "hint" : "err"}>{ytCheck.ok ? "✓ " : "✗ "}{ytCheck.message}</div> : null}
                {!yt?.clientConfigured ? <div className="hint">Avval Client ID va Secret&apos;ni kiritib, «Saqlash» ni bosing.</div> : dirty ? <div className="hint">Avval o&apos;zgarishlarni saqlang.</div> : null}
                <div className="hint">Google Cloud → Credentials → <b>Web application</b> turidagi OAuth client yarating va «Authorized redirect URIs» ga shu manzilni qo&apos;shing:<br /><code style={{ wordBreak: "break-all" }}>{yt?.redirectUri ?? "…"}</code></div>
              </div>
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
              <div className="field"><label>Standart teglar (vergul bilan)</label><input className="input" value={cfg.youtube.defaultTags} onChange={(e) => Y({ defaultTags: e.target.value })} /></div>
              <div className="field"><label>Standart tavsif (har video tavsifi oxiriga qo'shiladi)</label><textarea className="textarea" rows={4} maxLength={2000} placeholder="Masalan: kanal havolalari, ijtimoiy tarmoqlar, ogohlantirish…" value={cfg.youtube.defaultDescription} onChange={(e) => Y({ defaultDescription: e.target.value })} /></div>
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
        <div className="grid2" style={{ position: "sticky", bottom: 78 }}>
          <button className="btn block" disabled={!dirty || saving} onClick={() => { if (schSaved) setSch(schSaved); if (cfgSaved) setCfg(toDraft(cfgSaved)); }}>Qaytarish</button>
          <button className="btn primary block" disabled={!dirty || saving} onClick={save}>{saving ? "Saqlanmoqda…" : "Saqlash"}</button>
        </div>
      </div>
    </>
  );
}

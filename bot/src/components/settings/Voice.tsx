"use client";

/**
 * Sozlamalar → Ovoz (dizayn: Ovoz MJiFi5; «Mening ovozim» ekrani: Ovoz · yozish vecihh, Ovoz · yuborish wGmITJ).
 * Standart ovoz plitkasi bosilganda darhol saqlanadi (defaults.voiceProfile); har profil kartasi o'z tezligi va tilini o'zi saqlaydi.
 */
import { useEffect, useState } from "react";
import { Plus, RefreshCw } from "lucide-react";
import { api, haptic } from "@/lib/client";
import { useBotConfig } from "@/lib/settings-data";
import { assetAllowed } from "@/lib/assets-shared";
import type { State, VoiceInfo, VoiceProfileCfg } from "@/lib/types";
import { Alert, Btn, PageHead, Skel, SkelCard, useBusy } from "../ui";
import { VoiceCloneEntry, VoiceCloneScreen, useVoiceClone } from "../VoiceClone";
import { AddProfileSheet, DefaultTiles, ProfileCard, buildTiles } from "../VoiceProfiles";
import p from "../VoiceProfiles.module.css";
import type { SettingsPageProps } from "./types";

export default function Voice({ state, toast, back, onChanged, setSubBack }: SettingsPageProps) {
  const { config, error, reload, saveConfig } = useBotConfig();
  const clone = useVoiceClone(toast);
  const busy = useBusy((m) => { haptic.fail(); toast(m, true); });
  const [view, setView] = useState<"main" | "clone">("main");
  const [adding, setAdding] = useState(false);
  const [picking, setPicking] = useState<string | null>(null);
  const [showAll, setShowAll] = useState(false);
  /** Ochiq kartalar: belgilanmagan bo'lsa standart profil (yoki yagona profil) ochiq turadi. */
  const [open, setOpen] = useState<Record<string, boolean>>({});
  /** Kompyuterlardagi VoiceStudio ma'lumoti (dvigatellar, lokal profillar). null - yuklanmoqda. */
  const [info, setInfo] = useState<VoiceInfo[] | null>(null);
  useEffect(() => {
    api<{ voice: VoiceInfo[] }>("/api/library").then((d) => setInfo(d.voice)).catch(() => setInfo([]));
  }, []);

  const go = (v: "main" | "clone") => { haptic.tap(); setView(v); window.scrollTo({ top: 0 }); };

  // «Mening ovozim» ochiq: Telegram'ning «orqaga» tugmasi Ovoz sahifasiga qaytaradi (Sozlamalar markaziga emas).
  useEffect(() => {
    if (view !== "clone") return;
    setSubBack(() => { haptic.tap(); setView("main"); window.scrollTo({ top: 0 }); });
    return () => setSubBack(null);
  }, [view, setSubBack]);

  if (view === "clone") {
    return (
      <VoiceCloneScreen clone={clone} toast={toast} back={() => go("main")}
        onChanged={() => { void reload(); onChanged(); }} /* klon profili server tomonda qo'shildi/olib tashlandi */ />
    );
  }

  const cat = state?.catalog;
  const assets = cat?.assets;
  // Har kanalning o'z ovozlari: faol kanalniki va umumiylari (boshqa kanallarniki «Boshqa kanallar» tugmasi bilan).
  const allProfiles = config?.voice.profiles ?? [];
  const multi = !!assets?.channel && assets.channels.length > 0;
  const hiddenCount = allProfiles.filter((x) => !assetAllowed(assets, "voice", x.name)).length;
  const profiles = showAll ? allProfiles : allProfiles.filter((x) => assetAllowed(assets, "voice", x.name));
  const activeTitle = assets?.channels.find((c) => c.id === assets.channel)?.title ?? "";
  const defaultProfile = config?.defaults.voiceProfile ?? "";
  const tiles = config ? buildTiles(allProfiles.filter((x) => assetAllowed(assets, "voice", x.name)), cat?.known ? cat.voiceProfiles : [], clone.st) : [];
  const localProfiles = info?.[0]?.profiles ?? [];

  /** Saqlash + umumiy holatni yangilash (Hub qisqa matni, mavzu oynasidagi ovozlar). Xato bo'lsa throw - chaqirgan tugma toast qiladi. */
  async function persist(patch: Parameters<typeof saveConfig>[0]) {
    await saveConfig(patch);
    onChanged();
  }

  async function pick(name: string) {
    setPicking(name);
    const ok = await busy.run(`def:${name}`, () => persist((d) => ({ ...d, defaults: { ...d.defaults, voiceProfile: name } })));
    setPicking(null);
    if (ok === false) return;
    haptic.ok();
    toast(name ? `Standart ovoz: ${name}. Yangi mavzulardan boshlab qo'llanadi.` : "Standart ovoz: kompyuterdagi standart.");
  }

  const saveProfile = (name: string) => ({ language, speed }: { language: string; speed: number }) =>
    persist((d) => ({ ...d, voice: { ...d.voice, profiles: d.voice.profiles.map((x) => (x.name === name ? { ...x, language, speed } : x)) } }));
  const deleteProfile = (name: string) => () =>
    persist((d) => ({
      ...d,
      voice: { ...d.voice, profiles: d.voice.profiles.filter((x) => x.name !== name) },
      defaults: d.defaults.voiceProfile === name ? { ...d.defaults, voiceProfile: "" } : d.defaults,
    }));
  const addProfile = (np: VoiceProfileCfg) => persist((d) => ({ ...d, voice: { ...d.voice, profiles: [...d.voice.profiles, np] } }));

  async function importLocal() {
    const ok = await busy.run("import", () => persist((d) => ({ ...d, voice: { ...d.voice, profiles: localProfiles.map((x) => ({ ...x })) } })));
    if (ok !== false) toast("Kompyuterdagi ro'yxat botga olindi. Endi profillarni shu yerda tahrirlaysiz.");
  }

  /** Profilni kanalga biriktirish ("" - umumiy). Javobdagi yangi holat - katalog va standart shu kanal bo'yicha yangilanadi. */
  const assign = (name: string) => async (channel: string) => {
    const r = await api<{ ok: boolean; state: State }>("/api/channel-assets", { kind: "voice", name, channel });
    onChanged(r.state);
    await reload(); // standart ovoz (kanal bo'yicha) o'zgargan bo'lishi mumkin
    const title = assets?.channels.find((c) => c.id === channel)?.title;
    toast(channel ? `«${name}» endi faqat «${title}» kanalida ishlatiladi.` : `«${name}» umumiy: hamma kanalda ishlatiladi.`);
  };

  const isOpen = (name: string) => open[name] ?? (name === defaultProfile || profiles.length === 1);

  return (
    <div className="page narrow">
      <PageHead back={{ label: "Sozlamalar", onClick: back }} title="Ovoz" sub="Videolaringiz qaysi ovozda va qanday tezlikda o'qiladi" />

      {!config && error ? (
        <Alert>
          <div className={p.stack}>
            <span>Ovoz sozlamalari yuklanmadi: {error}</span>
            <Btn size="sm" tone="secondary" icon={RefreshCw} loading={busy.is("reload")} onClick={() => void busy.run("reload", reload)}>Qayta urinish</Btn>
          </div>
        </Alert>
      ) : null}

      {/* Standart ovoz */}
      <section className={p.section} aria-label="Standart ovoz">
        <div className="section-label"><span>Standart ovoz</span><span className={p.labelHint}>bosing — tanlanadi</span></div>
        {!config ? (
          !error ? <div className={p.tiles} aria-busy="true" aria-label="Yuklanmoqda">{[0, 1, 2].map((i) => <Skel key={i} h={116} r={20} />)}</div> : null
        ) : (
          <>
            {tiles.length ? <DefaultTiles tiles={tiles} value={defaultProfile} pending={picking} onPick={(n) => void pick(n)} /> : null}
            {!defaultProfile ? (
              <Alert tone="note">Standart tanlanmagan: yangi mavzular kompyuterdagi standart ovozda o&apos;qiladi.</Alert>
            ) : (
              <button type="button" className={`linkbtn ${p.reset}`} disabled={picking !== null} onClick={() => void pick("")}>
                {picking === "" ? "Saqlanmoqda…" : "Kompyuterdagi standartga qaytarish"}
              </button>
            )}
            {allProfiles.length === 0 ? (
              <Alert tone="note">
                <div className={p.stack}>
                  <span>Botda profil ro&apos;yxati hali yo&apos;q: kompyuterdagi lokal ro&apos;yxat ishlatilmoqda ({info === null ? "…" : localProfiles.map((x) => x.name).join(", ") || "bo'sh"}).</span>
                  {localProfiles.length > 0 ? (
                    <Btn size="sm" tone="secondary" loading={busy.is("import")} onClick={() => void importLocal()}>Shu ro&apos;yxatni botga olish (keyin tahrirlash mumkin)</Btn>
                  ) : null}
                </div>
              </Alert>
            ) : null}
          </>
        )}
      </section>

      {/* Tezlik va til: har profil alohida saqlanadi */}
      {!config ? (!error ? <SkelCard lines={4} /> : null) : profiles.length ? (
        <section className={p.section} aria-label="Tezlik va til">
          <div className="section-label"><span>Tezlik va til</span><span className={p.labelHint}>har profil alohida saqlanadi</span></div>
          {multi ? (
            <div className="hint">
              «{activeTitle}» kanali ovozlari va umumiylari: mavzu yaratishda (bot va Claude Code) faqat shular tanlanadi. Yangi profil shu kanalga qo&apos;shiladi.
              {hiddenCount ? <> {" "}<button type="button" className="linkbtn" onClick={() => setShowAll((v) => !v)}>
                {showAll ? "Faqat shu kanalnikini ko'rsatish" : `Boshqa kanallarniki ham (${hiddenCount})`}</button></> : null}
            </div>
          ) : null}
          <div className="cards">
            {profiles.map((x) => (
              <ProfileCard key={x.name} profile={x} isDefault={x.name === defaultProfile} canDelete={allProfiles.length > 1} toast={toast}
                expanded={isOpen(x.name)} onToggle={() => setOpen((o) => ({ ...o, [x.name]: !isOpen(x.name) }))}
                onSave={saveProfile(x.name)} onDelete={deleteProfile(x.name)} onOpenClone={() => go("clone")}
                channel={multi && !state?.account?.guest ? { owner: assets!.voices[x.name] ?? "", channels: assets!.channels, onAssign: assign(x.name) } : undefined} />
            ))}
          </div>
        </section>
      ) : null}

      {config ? <Btn tone="secondary" block icon={Plus} onClick={() => { haptic.tap(); setAdding(true); }}>Profil qo&apos;shish</Btn> : null}

      {/* Mening ovozim */}
      <section className={p.section} aria-label="Mening ovozim">
        <div className="section-label"><span>Mening ovozim</span></div>
        <VoiceCloneEntry st={clone.st} onOpen={() => go("clone")} />
      </section>

      {adding && config ? <AddProfileSheet info={info} profiles={allProfiles} toast={toast} onClose={() => setAdding(false)} onAdd={addProfile} /> : null}
    </div>
  );
}

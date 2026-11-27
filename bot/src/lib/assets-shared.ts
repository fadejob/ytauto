import type { ChannelAssets } from "./types";

/** Shablon/ovoz shu kanalda tanlanadimi: biriktirilmagan (umumiy) yoki shu kanalniki. Bot (brauzer) va server bir xil qoida. */
export function assetAllowed(a: ChannelAssets | undefined, kind: "template" | "voice", name: string): boolean {
  if (!a?.channel) return true;
  const owner = (kind === "template" ? a.templates : a.voices)[name];
  return !owner || owner === a.channel;
}

/** Biriktirilgan kanal nomi ("" - umumiy). */
export function assetOwnerTitle(a: ChannelAssets | undefined, kind: "template" | "voice", name: string): string {
  const owner = a ? (kind === "template" ? a.templates : a.voices)[name] : undefined;
  return owner ? a!.channels.find((c) => c.id === owner)?.title || owner : "";
}

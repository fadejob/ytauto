/**
 * Kanal kontekstining shared_settings kalitlari. Har YouTube kanalining o'z konteksti bor: `channel.context:<kanal id>`.
 * `channel.context` - FAOL kanal kontekstining nusxasi (eski desktop versiyalari va kanal id'si yo'q vazifalar shuni o'qiydi).
 */
export const BLOCKS_KEY = "channel.context";
export const blocksKey = (channelId: string) => (channelId ? `${BLOCKS_KEY}:${channelId}` : BLOCKS_KEY);

import { handler } from "@/lib/api";
import { channelAction, getChannelState, type ChannelActionInput } from "@/lib/channel";

/** Kanal tahlili fonda (after) shu route'ning vaqt chegarasida ishlaydi: 3 video + yakuniy xulosa ~1–3 daqiqa. */
export const maxDuration = 300;

export const GET = handler(async () => getChannelState());
/** Javobda kanalning yangi holati ham (shu tranzaksiyada) - ilova alohida GET /api/channel kutmaydi. */
export const POST = handler(async (req) => {
  const r = await channelAction((await req.json()) as ChannelActionInput);
  return { ...r, channel: await getChannelState() };
});

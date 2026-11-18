import { handler } from "@/lib/api";
import { channelAction, getChannelState, type ChannelActionInput } from "@/lib/channel";

/** Kanal tahlili fonda (after) shu route'ning vaqt chegarasida ishlaydi: 3 video + yakuniy xulosa ~1–3 daqiqa. */
export const maxDuration = 300;

export const GET = handler(async () => getChannelState());
export const POST = handler(async (req) => channelAction((await req.json()) as ChannelActionInput));

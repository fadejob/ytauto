import { handler } from "@/lib/api";
import { getJobLogs } from "@/lib/history";

export const GET = handler(async (req) => {
  const id = Number(new URL(req.url).searchParams.get("id"));
  if (!Number.isInteger(id) || id <= 0) throw new Error("Video raqami noto'g'ri.");
  const logs = await getJobLogs(id);
  if (!logs) throw new Error(`#${id} topilmadi.`);
  return logs;
});

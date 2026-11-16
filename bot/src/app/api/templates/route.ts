import { handler } from "@/lib/api";
import { commitUpload, listUploads, prepareUpload } from "@/lib/templates";

export const GET = handler(async () => listUploads());

export const POST = handler(async (req) => {
  const b = (await req.json()) as Record<string, unknown>;
  if (b.action === "prepare") return prepareUpload({ name: String(b.name ?? ""), kind: String(b.kind ?? ""), filename: String(b.filename ?? ""), size: Number(b.size) });
  if (b.action === "commit")
    return commitUpload({
      groupId: String(b.groupId ?? ""), path: String(b.path ?? ""), name: String(b.name ?? ""), kind: String(b.kind ?? ""),
      size: Number(b.size), machines: Array.isArray(b.machines) ? b.machines.map(String) : [],
    });
  throw new Error("Noma'lum amal.");
});

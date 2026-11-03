// Receives a log someone chose to send from CamRemote's Send log button, and keeps it for 90 days.
// The apps send nothing until that button is pressed. Read logs with: npx wrangler kv key list --binding LOGS --remote
const MAX_BYTES = 1024 * 1024;
const KEEP_SECONDS = 90 * 24 * 3600;
const LETTERS = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method !== "POST" || url.pathname !== "/log") return new Response("Not found", { status: 404 });

    // A few a minute from one address is plenty for a person, and keeps junk from filling the store.
    const { success } = await env.LIMIT.limit({ key: request.headers.get("CF-Connecting-IP") ?? "" });
    if (!success) return answer(429, "Too many logs from here. Try again in a minute.");
    if (Number(request.headers.get("Content-Length") ?? 0) > MAX_BYTES) return answer(413, "That log is too big.");

    let sent;
    try {
      sent = await request.json();
    } catch {
      return answer(400, "That wasn't a log.");
    }
    const log = typeof sent.log === "string" ? sent.log : "";
    if (log.length === 0 || log.length > MAX_BYTES) return answer(400, "That log is empty or too big.");
    const app = String(sent.app ?? "").slice(0, 80);
    const note = String(sent.note ?? "").slice(0, 2000);

    const id = Array.from(crypto.getRandomValues(new Uint8Array(6)), b => LETTERS[b % LETTERS.length]).join("");
    const key = `${new Date().toISOString().slice(0, 19)}Z ${id}`;
    const text = `App: ${app}\nNote: ${note || "(none)"}\n\n${log}`;
    try {
      await env.LOGS.put(key, text, { expirationTtl: KEEP_SECONDS, metadata: { app, note: note.slice(0, 200), bytes: log.length } });
    } catch {
      return answer(503, "The log couldn't be saved right now. Try again later.");
    }
    return Response.json({ id });
  },
};

function answer(status, error) {
  return Response.json({ error }, { status });
}

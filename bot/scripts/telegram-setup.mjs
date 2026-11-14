// Botning pastki "menu" tugmasini web app'ga ulaydi (bir marta, deploy'dan keyin).
// Ishlatish:  WEBAPP_URL=https://sizning-domen.vercel.app node --env-file=.env.local scripts/telegram-setup.mjs
const token = process.env.TELEGRAM_BOT_TOKEN;
const url = process.env.WEBAPP_URL;
if (!token) throw new Error("TELEGRAM_BOT_TOKEN yo'q (.env.local).");
if (!url?.startsWith("https://")) throw new Error("WEBAPP_URL https:// bilan boshlanishi kerak (Telegram faqat HTTPS web app ochadi).");

async function call(method, body) {
  const res = await fetch(`https://api.telegram.org/bot${token}/${method}`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
  });
  const data = await res.json();
  if (!data.ok) throw new Error(`${method}: ${data.description}`);
  console.log(`✓ ${method}`);
}

await call("setChatMenuButton", { menu_button: { type: "web_app", text: "YtAuto", web_app: { url } } });
await call("setMyDescription", { description: "YtAuto: mavzular, hozir chiqarish va promptlar. Pastdagi “YtAuto” tugmasini bosing." });
console.log(`Tayyor: botda pastdagi "YtAuto" tugmasi ${url} ni ochadi.`);

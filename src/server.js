import { pgAdapter } from "./db.js";
import { runMigrations } from "./migrate.js";
import { supabaseVerifier } from "./auth.js";
import { createApp } from "./app.js";

const need = (k) => { const v = process.env[k]; if (!v) { console.error(`Missing required env var ${k}`); process.exit(1); } return v; };

const config = {
  adminEmail: (process.env.ADMIN_EMAIL || "shivanshu.rai@fiftyfivetech.io").toLowerCase(),
  domain: (process.env.ALLOWED_DOMAIN || "fiftyfivetech.io").toLowerCase(),
  freeSeats: Number(process.env.FREE_SEATS || 3),
  corsOrigins: (process.env.CORS_ORIGINS || "").split(",").map((s) => s.trim()).filter(Boolean),
};

const db = pgAdapter(need("DATABASE_URL"));
await runMigrations(db);
const verify = supabaseVerifier({ url: need("SUPABASE_URL"), jwtSecret: process.env.SUPABASE_JWT_SECRET });
const app = createApp({ db, verify, config });
const port = Number(process.env.PORT || 3000);
const server = app.listen(port, () => console.log(`API listening on ${port}`));
const stop = () => server.close(() => db.close().finally(() => process.exit(0)));
process.on("SIGTERM", stop);
process.on("SIGINT", stop);

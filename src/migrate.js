import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { pgAdapter } from "./db.js";

const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "migrations");

export async function runMigrations(db) {
  await db.exec("create table if not exists schema_migrations (name text primary key, applied_at timestamptz not null default now())");
  const done = new Set((await db.query("select name from schema_migrations")).rows.map((r) => r.name));
  for (const file of readdirSync(dir).filter((f) => f.endsWith(".sql")).sort()) {
    if (done.has(file)) continue;
    await db.exec(readFileSync(path.join(dir, file), "utf8"));
    await db.query("insert into schema_migrations (name) values ($1)", [file]);
    console.log("applied", file);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  if (!process.env.DATABASE_URL) { console.error("DATABASE_URL is not set"); process.exit(1); }
  const db = pgAdapter(process.env.DATABASE_URL);
  runMigrations(db).then(() => db.close()).catch((e) => { console.error(e.message); process.exit(1); });
}

// Tiny database adapter so the same code runs on Postgres (production) and PGlite (tests).
import pg from "pg";

export function pgAdapter(connectionString) {
  const local = /localhost|127\.0\.0\.1/.test(connectionString);
  const pool = new pg.Pool({ connectionString, max: 5, idleTimeoutMillis: 20000, ssl: local ? false : { rejectUnauthorized: false } });
  return {
    query: (text, params) => pool.query(text, params),
    exec: (sql) => pool.query(sql),
    async tx(fn) {
      const client = await pool.connect();
      try {
        await client.query("begin");
        const out = await fn((text, params) => client.query(text, params));
        await client.query("commit");
        return out;
      } catch (e) {
        await client.query("rollback").catch(() => {});
        throw e;
      } finally {
        client.release();
      }
    },
    close: () => pool.end(),
  };
}

export function pgliteAdapter(db) {
  return {
    query: (text, params) => db.query(text, params),
    exec: (sql) => db.exec(sql),
    tx: (fn) => db.transaction((t) => fn((text, params) => t.query(text, params))),
    close: () => db.close(),
  };
}

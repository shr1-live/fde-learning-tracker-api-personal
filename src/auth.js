// Authentication: verify the Supabase-issued JWT, then load or create the app user.
import { createRemoteJWKSet, jwtVerify } from "jose";

export function supabaseVerifier({ url, jwtSecret }) {
  const issuer = `${url.replace(/\/$/, "")}/auth/v1`;
  const jwks = jwtSecret ? null : createRemoteJWKSet(new URL(`${issuer}/.well-known/jwks.json`));
  const secret = jwtSecret ? new TextEncoder().encode(jwtSecret) : null;
  return async (token) => {
    const { payload } = await jwtVerify(token, secret ?? jwks, { issuer, audience: "authenticated" });
    if (!payload.sub) throw new Error("no subject");
    return { id: String(payload.sub), email: String(payload.email || "").toLowerCase() };
  };
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function makeAuth({ db, verify, adminEmail, domain, freeSeats }) {
  async function loadUser(claims) {
    return db.tx(async (q) => {
      await q("select pg_advisory_xact_lock(7001)");
      const isAdmin = claims.email === adminEmail;
      let row = (await q("select * from app_users where id = $1", [claims.id])).rows[0];
      if (!row) {
        // The same person re-created in the auth provider gets a new id; keep their data by re-linking on email.
        const byEmail = (await q("select * from app_users where email = $1", [claims.email])).rows[0];
        if (byEmail) {
          row = (await q("update app_users set id = $1, last_seen_at = now() where email = $2 returning *", [claims.id, claims.email])).rows[0];
        } else {
          const seats = Number((await q("select count(*)::int as n from app_users where access_status = 'approved' and role <> 'admin'")).rows[0].n);
          const status = isAdmin || seats < freeSeats ? "approved" : "pending";
          row = (await q(
            "insert into app_users (id, email, role, access_status) values ($1, $2, $3, $4) returning *",
            [claims.id, claims.email, isAdmin ? "admin" : "member", status],
          )).rows[0];
          await q("insert into audit_log (actor_id, action, target, detail) values ($1, 'user.first_seen', $2, $3)", [claims.id, claims.id, JSON.stringify({ status })]);
        }
      } else {
        await q("update app_users set last_seen_at = now() where id = $1", [claims.id]);
      }
      if (isAdmin && (row.role !== "admin" || row.access_status !== "approved")) {
        row = (await q("update app_users set role = 'admin', access_status = 'approved' where id = $1 returning *", [row.id])).rows[0];
      }
      return row;
    });
  }

  const authenticate = async (req, res, next) => {
    try {
      const m = /^Bearer (.+)$/i.exec(req.headers.authorization || "");
      if (!m) return res.status(401).json({ error: "Sign in required" });
      let claims;
      try { claims = await verify(m[1]); } catch { return res.status(401).json({ error: "Invalid or expired session" }); }
      if (!UUID.test(claims.id) || !claims.email.endsWith("@" + domain)) return res.status(403).json({ error: "This account is not allowed" });
      req.user = await loadUser(claims);
      next();
    } catch (e) { next(e); }
  };

  const requireApproved = (req, res, next) =>
    req.user.access_status === "approved" ? next() : res.status(403).json({ error: "Your access is waiting for approval", access: req.user.access_status });

  const requireRole = (...roles) => (req, res, next) =>
    roles.includes(req.user.role) ? next() : res.status(403).json({ error: "Not allowed" });

  return { authenticate, requireApproved, requireRole };
}

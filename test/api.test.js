import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { PGlite } from "@electric-sql/pglite";
import { pgliteAdapter } from "../src/db.js";
import { runMigrations } from "../src/migrate.js";
import { createApp } from "../src/app.js";

const id = (n) => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const U = {
  admin: { id: id(1), email: "boss@fiftyfivetech.io" },
  b: { id: id(2), email: "b@fiftyfivetech.io" },
  c: { id: id(3), email: "c@fiftyfivetech.io" },
  d: { id: id(4), email: "d@fiftyfivetech.io" },
  e: { id: id(5), email: "e@fiftyfivetech.io" }, // 5th person: no free seat left
  outsider: { id: id(9), email: "x@gmail.com" },
};
let server, base, pg, raw;

before(async () => {
  raw = new PGlite();
  pg = pgliteAdapter(raw);
  await runMigrations(pg);
  const verify = async (token) => {
    const u = Object.values(U).find((x) => x.id === token);
    if (!u) throw new Error("bad token");
    return u;
  };
  const app = createApp({ db: pg, verify, config: { adminEmail: "boss@fiftyfivetech.io", domain: "fiftyfivetech.io", freeSeats: 3, corsOrigins: ["https://app.example"] } });
  await new Promise((r) => { server = app.listen(0, r); });
  base = `http://127.0.0.1:${server.address().port}`;
});
after(async () => { server?.close(); await raw?.close(); });

async function call(method, path, user, body) {
  const res = await fetch(base + path, {
    method,
    headers: { "content-type": "application/json", ...(user ? { authorization: `Bearer ${user.id}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  return { status: res.status, json };
}

test("public endpoints and security headers", async () => {
  assert.equal((await call("GET", "/health")).status, 200);
  const stacks = await call("GET", "/api/stacks");
  assert.equal(stacks.json.length, 4);
  assert.ok((await call("GET", "/api/topics")).json.length >= 10);
  const r = await fetch(base + "/health");
  assert.equal(r.headers.get("x-powered-by"), null);
  assert.equal(r.headers.get("x-content-type-options"), "nosniff");
});

test("authentication: no token, bad token and wrong domain are refused", async () => {
  assert.equal((await call("GET", "/api/me")).status, 401);
  assert.equal((await call("GET", "/api/me", { id: "nope" })).status, 401);
  assert.equal((await call("GET", "/api/me", U.outsider)).status, 403);
});

test("access: admin and first 3 members approved; the 5th person waits", async () => {
  assert.deepEqual((await call("GET", "/api/me", U.admin)).json.role, "admin");
  for (const k of ["b", "c", "d"]) assert.equal((await call("GET", "/api/me", U[k])).json.access, "approved", k);
  const e = await call("GET", "/api/me", U.e);
  assert.equal(e.json.access, "pending");
  const feed = await call("GET", "/api/feed", U.e);
  assert.equal(feed.status, 403);
  assert.equal((await call("POST", "/api/content", U.e, {})).status, 403);
});

test("only the admin can approve people or change roles", async () => {
  assert.equal((await call("GET", "/api/admin/users", U.b)).status, 403);
  assert.equal((await call("POST", `/api/admin/users/${U.e.id}/access`, U.b, { status: "approved" })).status, 403);
  assert.equal((await call("POST", `/api/admin/users/${U.admin.id}/access`, U.admin, { status: "rejected" })).status, 400);
  const ok = await call("POST", `/api/admin/users/${U.e.id}/access`, U.admin, { status: "approved" });
  assert.equal(ok.status, 200);
  assert.equal((await call("GET", "/api/feed", U.e)).status, 200);
  assert.equal((await call("POST", `/api/admin/users/${U.admin.id}/role`, U.admin, { role: "member" })).status, 404);
  assert.equal((await call("POST", `/api/admin/users/${U.c.id}/role`, U.admin, { role: "reviewer" })).status, 200);
});

test("content review flow: nothing is public until approved, stack filtering works", async () => {
  await call("PATCH", "/api/me", U.b, { stack_id: "node" });
  await call("PATCH", "/api/me", U.d, { stack_id: "python" });
  const general = await call("POST", "/api/content", U.b, { topic: "rag", stack_id: null, kind: "article", title: "RAG in plain words", body_md: "Retrieve, then generate.", submit: true });
  const py = await call("POST", "/api/content", U.b, { topic: "rag", stack_id: "python", kind: "link", title: "Python RAG tutorial", url: "https://example.com/py", submit: true });
  assert.equal(general.status, 201);
  assert.equal(general.json.status, "in_review");

  assert.equal((await call("GET", "/api/feed", U.d)).json.items.length, 0, "unreviewed content must not be public");
  assert.equal((await call("GET", `/api/content/${general.json.id}`, U.d)).status, 404);
  assert.equal((await call("GET", `/api/content/${general.json.id}`, U.b)).status, 200, "author can see own draft");

  assert.equal((await call("GET", "/api/review/queue", U.d)).status, 403, "members cannot see the queue");
  const queue = await call("GET", "/api/review/queue", U.c);
  assert.equal(queue.json.length, 2);

  assert.equal((await call("POST", `/api/review/${general.json.id}`, U.d, { decision: "approve" })).status, 403);
  assert.equal((await call("POST", `/api/review/${general.json.id}`, U.c, { decision: "reject" })).status, 400, "comment required");
  assert.equal((await call("POST", `/api/review/${general.json.id}`, U.c, { decision: "approve" })).status, 200);
  assert.equal((await call("POST", `/api/review/${py.json.id}`, U.c, { decision: "approve" })).status, 200);
  assert.equal((await call("POST", `/api/review/${py.json.id}`, U.c, { decision: "approve" })).status, 409, "already decided");

  const forPy = (await call("GET", "/api/feed", U.d)).json.items.map((i) => i.title);
  assert.deepEqual(forPy.sort(), ["Python RAG tutorial", "RAG in plain words"]);
  const forNode = (await call("GET", "/api/feed", U.b)).json.items.map((i) => i.title);
  assert.deepEqual(forNode, ["RAG in plain words"], "node user gets general only");
  assert.equal((await call("GET", "/api/feed?stack=general", U.d)).json.items.length, 1);
  assert.equal((await call("GET", "/api/feed?topic=agents", U.d)).json.items.length, 0);

  assert.equal((await call("PATCH", `/api/content/${general.json.id}`, U.b, { title: "Sneaky edit" })).status, 409, "approved items are locked");
  const detail = await call("GET", `/api/content/${general.json.id}`, U.d);
  assert.equal(detail.status, 200);
  assert.equal(detail.json.author_id, undefined, "internal ids are not exposed");
});

test("reviewers cannot approve their own work; changes requested can be edited and resubmitted", async () => {
  const mine = await call("POST", "/api/content", U.c, { topic: "mcp", stack_id: null, kind: "note", title: "My MCP note", body_md: "text", submit: true });
  assert.equal((await call("POST", `/api/review/${mine.json.id}`, U.c, { decision: "approve" })).status, 403);
  const other = await call("POST", "/api/content", U.b, { topic: "mcp", stack_id: null, kind: "note", title: "Needs work", body_md: "draft", submit: true });
  assert.equal((await call("POST", `/api/review/${other.json.id}`, U.c, { decision: "request_changes", comment: "Add an example" })).status, 200);
  const mineList = await call("GET", "/api/content/mine", U.b);
  assert.equal(mineList.json.find((x) => x.id === other.json.id).last_review_comment, "Add an example");
  const fixed = await call("PATCH", `/api/content/${other.json.id}`, U.b, { body_md: "now with an example", submit: true });
  assert.equal(fixed.json.status, "in_review");
  assert.equal((await call("PATCH", `/api/content/${other.json.id}`, U.d, { title: "hijack" })).status, 404, "others cannot edit");
});

test("input validation and abuse limits", async () => {
  const base = { topic: "rag", stack_id: null, kind: "article", title: "Valid title", body_md: "x" };
  assert.equal((await call("POST", "/api/content", U.b, { ...base, kind: "video" })).status, 400, "video needs a link");
  assert.equal((await call("POST", "/api/content", U.b, { ...base, kind: "link", url: "http://insecure.example" })).status, 400, "https only");
  assert.equal((await call("POST", "/api/content", U.b, { ...base, kind: "link", url: "javascript:alert(1)" })).status, 400);
  assert.equal((await call("POST", "/api/content", U.b, { ...base, topic: "nope-topic" })).status, 400);
  assert.equal((await call("POST", "/api/content", U.b, { ...base, title: "x" })).status, 400);
  assert.equal((await call("POST", "/api/content", U.b, { ...base, extra: 1, topic: "'; drop table app_users;--" })).status, 400);
  assert.equal((await call("GET", "/api/feed?limit=9999", U.b)).status, 400);
  assert.equal((await call("GET", "/api/feed?before=garbage", U.b)).status, 400);
  assert.equal((await call("GET", "/api/content/not-a-uuid", U.b)).status, 400);
  const big = await fetch(base_url(), { method: "POST", headers: { "content-type": "application/json", authorization: `Bearer ${U.b.id}` }, body: JSON.stringify({ x: "a".repeat(70000) }) });
  assert.equal(big.status, 413);
  assert.equal((await call("GET", "/api/users/everyone", U.b)).status, 404);
  const still = await pg.query("select count(*)::int n from app_users");
  assert.ok(still.rows[0].n >= 5, "table still exists");
});
function base_url() { return `${base}/api/content`; }

test("programs: shared six-week programme, personal progress, private plans stay private", async () => {
  const list = await call("GET", "/api/programs", U.b);
  const six = list.json.find((p) => p.slug === "fde-six-week");
  assert.equal(six.total, 6);
  const detail = await call("GET", "/api/programs/fde-six-week", U.b);
  const first = detail.json.items[0];
  assert.equal((await call("PUT", `/api/programs/fde-six-week/items/${first.id}/progress`, U.b, { done: true })).status, 200);
  assert.equal((await call("GET", "/api/programs/fde-six-week", U.b)).json.items[0].done, true);
  assert.equal((await call("GET", "/api/programs/fde-six-week", U.d)).json.items[0].done, false, "progress is per person");

  const mine = await call("POST", "/api/programs", U.b, { title: "My own plan" });
  assert.equal(mine.status, 201);
  const slugB = mine.json.slug;
  const step = await call("POST", `/api/programs/${slugB}/items`, U.b, { title: "Build a demo" });
  assert.equal(step.status, 201);
  assert.equal((await call("GET", `/api/programs/${slugB}`, U.d)).status, 404, "private plan hidden from others");
  assert.equal((await call("POST", `/api/programs/${slugB}/items`, U.d, { title: "intrude" })).status, 404);
  assert.equal((await call("PUT", `/api/programs/${slugB}/items/${step.json.id}/progress`, U.d, { done: true })).status, 404);
  assert.equal((await call("POST", `/api/programs/fde-six-week/items`, U.b, { title: "edit shared" })).status, 404, "system program is read-only");
  assert.equal((await call("GET", "/api/programs", U.d)).json.some((p) => p.slug === slugB), false);
});

test("re-creating a user in the auth provider keeps their data (re-link by email)", async () => {
  const before = (await call("GET", "/api/me", U.b)).json;
  const reborn = { id: id(77), email: U.b.email };
  U.reborn = reborn;
  const after = (await call("GET", "/api/me", reborn)).json;
  assert.equal(after.email, before.email);
  assert.equal(after.stack_id, before.stack_id);
  assert.equal(after.id, reborn.id);
  assert.ok((await call("GET", "/api/content/mine", reborn)).json.length >= 1, "content followed the person");
});

test("admin audit trail exists", async () => {
  const n = await pg.query("select count(*)::int n from audit_log where action like 'review.%' or action like 'admin.%'");
  assert.ok(n.rows[0].n >= 5);
});

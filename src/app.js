import express from "express";
import helmet from "helmet";
import cors from "cors";
import rateLimit from "express-rate-limit";
import { z } from "zod";
import { makeAuth } from "./auth.js";

const MAX_ACTIVE_DRAFTS = 20;
const MAX_PROGRAMS = 5;
const MAX_ITEMS = 60;

const uuid = z.string().uuid();
const slug = z.string().regex(/^[a-z0-9-]{2,60}$/);
const contentFields = {
  topic: z.string().regex(/^[a-z0-9-]{2,40}$/),
  stack_id: z.enum(["dotnet", "node", "java", "python"]).nullable(),
  kind: z.enum(["article", "video", "link", "note"]),
  title: z.string().trim().min(3).max(140),
  summary: z.string().trim().max(400).optional(),
  body_md: z.string().max(20000).optional(),
  url: z.string().url().max(500).refine((u) => u.startsWith("https://"), "Links must use https").optional(),
};
const contentRules = (v, ctx) => {
  if ((v.kind === "video" || v.kind === "link") && !v.url) ctx.addIssue({ code: "custom", path: ["url"], message: "A link is required for this kind" });
  if ((v.kind === "article" || v.kind === "note") && !v.body_md?.trim()) ctx.addIssue({ code: "custom", path: ["body_md"], message: "Text is required for this kind" });
};
const createContent = z.object({ ...contentFields, submit: z.boolean().default(false) }).superRefine(contentRules);
const patchContent = z.object({
  topic: contentFields.topic.optional(), stack_id: contentFields.stack_id.optional(), kind: contentFields.kind.optional(),
  title: contentFields.title.optional(), summary: contentFields.summary, body_md: contentFields.body_md, url: contentFields.url,
  submit: z.boolean().optional(),
}).strict();

const PUBLIC_COLS = "c.id, c.title, c.summary, c.kind, c.url, c.stack_id, c.approved_at, t.slug as topic, t.title as topic_title, coalesce(u.display_name, split_part(u.email, '@', 1)) as author";

export function createApp({ db, verify, config }) {
  const { authenticate, requireApproved, requireRole } = makeAuth({ db, verify, adminEmail: config.adminEmail, domain: config.domain, freeSeats: config.freeSeats });
  const app = express();
  app.disable("x-powered-by");
  app.set("trust proxy", 1);
  app.use(helmet());
  app.use(cors({ origin: (origin, cb) => cb(null, !origin || config.corsOrigins.includes(origin)), methods: ["GET", "POST", "PATCH", "PUT", "DELETE"], maxAge: 600 }));
  app.use(express.json({ limit: "64kb" }));
  app.use(rateLimit({ windowMs: 60_000, limit: 240, standardHeaders: true, legacyHeaders: false }));
  const writeLimit = rateLimit({ windowMs: 60_000, limit: 40, standardHeaders: true, legacyHeaders: false, keyGenerator: (req) => req.user?.id || req.ip, skip: (req) => req.method === "GET" });
  const wrap = (fn) => (req, res, next) => Promise.resolve(fn(req, res, next)).catch(next);
  const audit = (q, actor, action, target, detail) => q("insert into audit_log (actor_id, action, target, detail) values ($1,$2,$3,$4)", [actor, action, target, detail ? JSON.stringify(detail) : null]);

  // ---- public
  app.get("/health", wrap(async (_req, res) => { await db.query("select 1"); res.json({ ok: true }); }));
  app.get("/api/stacks", wrap(async (_req, res) => res.json((await db.query("select id, label from stacks order by sort")).rows)));
  app.get("/api/topics", wrap(async (_req, res) => res.json((await db.query("select slug, title, description from topics order by sort")).rows)));

  // ---- everything below needs a signed-in user
  const api = express.Router();
  api.use(authenticate, writeLimit);

  api.get("/me", (req, res) => {
    const { id, email, display_name, stack_id, role, access_status } = req.user;
    res.json({ id, email, display_name, stack_id, role, access: access_status });
  });

  api.use(requireApproved);

  api.patch("/me", wrap(async (req, res) => {
    const body = z.object({ display_name: z.string().trim().max(80).optional(), stack_id: z.enum(["dotnet", "node", "java", "python"]).nullable().optional() }).strict().parse(req.body);
    const row = (await db.query(
      "update app_users set display_name = coalesce($2, display_name), stack_id = case when $3::boolean then $4 else stack_id end where id = $1 returning id, email, display_name, stack_id, role",
      [req.user.id, body.display_name ?? null, "stack_id" in body, body.stack_id ?? null],
    )).rows[0];
    res.json(row);
  }));

  // ---- feed: approved content only; general items for everyone plus the chosen stack's items
  api.get("/feed", wrap(async (req, res) => {
    const qs = z.object({
      topic: z.string().regex(/^[a-z0-9-]{2,40}$/).optional(),
      stack: z.enum(["general", "dotnet", "node", "java", "python"]).optional(),
      limit: z.coerce.number().int().min(1).max(30).default(15),
      before: z.string().max(120).optional(),
    }).parse(req.query);
    const stack = qs.stack ?? req.user.stack_id ?? "general";
    const params = [stack === "general" ? null : stack];
    let where = "c.status = 'approved' and (c.stack_id is null or c.stack_id = $1)";
    if (qs.topic) { params.push(qs.topic); where += ` and t.slug = $${params.length}`; }
    if (qs.before) {
      const [ts, id] = Buffer.from(qs.before, "base64url").toString().split("|");
      if (!ts || !uuid.safeParse(id).success || Number.isNaN(Date.parse(ts))) return res.status(400).json({ error: "Bad cursor" });
      params.push(ts, id); where += ` and (c.approved_at, c.id) < ($${params.length - 1}::timestamptz, $${params.length}::uuid)`;
    }
    params.push(qs.limit + 1);
    const rows = (await db.query(
      `select ${PUBLIC_COLS} from content_items c join topics t on t.id = c.topic_id join app_users u on u.id = c.author_id
       where ${where} order by c.approved_at desc, c.id desc limit $${params.length}`, params)).rows;
    const more = rows.length > qs.limit;
    const items = more ? rows.slice(0, qs.limit) : rows;
    const last = items[items.length - 1];
    res.json({ items, next: more && last ? Buffer.from(`${new Date(last.approved_at).toISOString()}|${last.id}`).toString("base64url") : null });
  }));

  api.get("/content/mine", wrap(async (req, res) => {
    res.json((await db.query(
      `select c.id, c.title, c.kind, c.status, c.stack_id, t.slug as topic, c.updated_at,
         (select comment from reviews r where r.content_id = c.id order by r.created_at desc limit 1) as last_review_comment
       from content_items c join topics t on t.id = c.topic_id where c.author_id = $1 order by c.updated_at desc limit 100`, [req.user.id])).rows);
  }));

  api.get("/content/:id", wrap(async (req, res) => {
    const id = uuid.parse(req.params.id);
    const row = (await db.query(
      `select ${PUBLIC_COLS}, c.body_md, c.status, c.author_id from content_items c join topics t on t.id = c.topic_id join app_users u on u.id = c.author_id where c.id = $1`, [id])).rows[0];
    const privileged = row && (row.author_id === req.user.id || req.user.role !== "member");
    if (!row || (row.status !== "approved" && !privileged)) return res.status(404).json({ error: "Not found" });
    const { author_id, ...out } = row; // never expose internal ids
    res.json(out);
  }));

  api.post("/content", wrap(async (req, res) => {
    const b = createContent.parse(req.body);
    const out = await db.tx(async (q) => {
      const active = Number((await q("select count(*)::int n from content_items where author_id = $1 and status in ('draft','in_review','changes_requested')", [req.user.id])).rows[0].n);
      if (active >= MAX_ACTIVE_DRAFTS) return { error: `You have ${MAX_ACTIVE_DRAFTS} items in progress. Finish or delete some first.` };
      const topic = (await q("select id from topics where slug = $1", [b.topic])).rows[0];
      if (!topic) return { error: "Unknown topic" };
      const row = (await q(
        `insert into content_items (topic_id, stack_id, kind, title, summary, body_md, url, author_id, status, submitted_at)
         values ($1,$2,$3,$4,$5,$6,$7,$8,$9, case when $9 = 'in_review' then now() end) returning id, status`,
        [topic.id, b.stack_id, b.kind, b.title, b.summary ?? null, b.body_md ?? null, b.url ?? null, req.user.id, b.submit ? "in_review" : "draft"])).rows[0];
      await audit(q, req.user.id, "content.create", row.id, { status: row.status });
      return { row };
    });
    if (out.error) return res.status(out.error === "Unknown topic" ? 400 : 429).json({ error: out.error });
    res.status(201).json(out.row);
  }));

  api.patch("/content/:id", wrap(async (req, res) => {
    const id = uuid.parse(req.params.id);
    const b = patchContent.parse(req.body);
    const out = await db.tx(async (q) => {
      const cur = (await q("select * from content_items where id = $1 and author_id = $2 for update", [id, req.user.id])).rows[0];
      if (!cur) return { code: 404, error: "Not found" };
      if (!["draft", "changes_requested"].includes(cur.status)) return { code: 409, error: "This item can no longer be edited" };
      let topicId = cur.topic_id;
      if (b.topic) { const t = (await q("select id from topics where slug = $1", [b.topic])).rows[0]; if (!t) return { code: 400, error: "Unknown topic" }; topicId = t.id; }
      const next = {
        kind: b.kind ?? cur.kind, title: b.title ?? cur.title, summary: b.summary ?? cur.summary,
        body_md: b.body_md ?? cur.body_md, url: b.url ?? cur.url, stack_id: "stack_id" in b ? b.stack_id : cur.stack_id,
      };
      const check = z.object({ kind: contentFields.kind, url: z.string().nullable(), body_md: z.string().nullable() }).superRefine((v, ctx) => contentRules({ ...v, url: v.url ?? undefined, body_md: v.body_md ?? undefined }, ctx)).safeParse(next);
      if (!check.success) return { code: 400, error: check.error.issues[0].message };
      const row = (await q(
        `update content_items set topic_id=$2, kind=$3, title=$4, summary=$5, body_md=$6, url=$7, stack_id=$8, updated_at=now(),
           status = case when $9::boolean then 'in_review' else status end,
           submitted_at = case when $9::boolean then now() else submitted_at end
         where id = $1 returning id, status`,
        [id, topicId, next.kind, next.title, next.summary, next.body_md, next.url, next.stack_id, !!b.submit])).rows[0];
      await audit(q, req.user.id, b.submit ? "content.submit" : "content.edit", id);
      return { row };
    });
    if (out.error) return res.status(out.code).json({ error: out.error });
    res.json(out.row);
  }));

  api.delete("/content/:id", wrap(async (req, res) => {
    const r = await db.query("delete from content_items where id = $1 and author_id = $2 and status in ('draft','changes_requested','rejected') returning id", [uuid.parse(req.params.id), req.user.id]);
    r.rows.length ? res.status(204).end() : res.status(404).json({ error: "Not found, or already published" });
  }));

  // ---- review team
  api.get("/review/queue", requireRole("reviewer", "admin"), wrap(async (_req, res) => {
    res.json((await db.query(
      `select c.id, c.title, c.kind, c.stack_id, c.submitted_at, t.slug as topic, coalesce(u.display_name, split_part(u.email,'@',1)) as author
       from content_items c join topics t on t.id = c.topic_id join app_users u on u.id = c.author_id
       where c.status = 'in_review' order by c.submitted_at limit 100`)).rows);
  }));

  api.post("/review/:id", requireRole("reviewer", "admin"), wrap(async (req, res) => {
    const id = uuid.parse(req.params.id);
    const b = z.object({ decision: z.enum(["approve", "request_changes", "reject"]), comment: z.string().trim().max(2000).optional() }).strict().parse(req.body);
    if (b.decision !== "approve" && !b.comment) return res.status(400).json({ error: "A comment is required when you do not approve" });
    const out = await db.tx(async (q) => {
      const cur = (await q("select id, author_id, status from content_items where id = $1 for update", [id])).rows[0];
      if (!cur) return { code: 404, error: "Not found" };
      if (cur.status !== "in_review") return { code: 409, error: "This item is not waiting for review" };
      const own = cur.author_id === req.user.id;
      if (own && req.user.role !== "admin") return { code: 403, error: "You cannot review your own submission" };
      const status = { approve: "approved", request_changes: "changes_requested", reject: "rejected" }[b.decision];
      await q("update content_items set status = $2, approved_at = case when $2 = 'approved' then now() end, updated_at = now() where id = $1", [id, status]);
      await q("insert into reviews (content_id, reviewer_id, decision, comment) values ($1,$2,$3,$4)", [id, req.user.id, b.decision, b.comment ?? null]);
      await audit(q, req.user.id, "review." + b.decision, id, own ? { self_review: true } : null);
      return { status };
    });
    out.error ? res.status(out.code).json({ error: out.error }) : res.json({ status: out.status });
  }));

  // ---- learning programs and progress
  const visibleProgram = async (slugValue, userId) =>
    (await db.query("select id, slug, title, description, visibility, owner_id from programs where slug = $1 and (visibility = 'public' or owner_id = $2)", [slugValue, userId])).rows[0];

  api.get("/programs", wrap(async (req, res) => {
    res.json((await db.query(
      `select p.slug, p.title, p.description, p.visibility,
         (select count(*)::int from program_items i where i.program_id = p.id) as total,
         (select count(*)::int from progress g join program_items i on i.id = g.program_item_id where i.program_id = p.id and g.user_id = $1) as done
       from programs p where p.visibility = 'public' or p.owner_id = $1 order by p.visibility, p.created_at`, [req.user.id])).rows);
  }));

  api.post("/programs", wrap(async (req, res) => {
    const b = z.object({ title: z.string().trim().min(2).max(120), description: z.string().trim().max(600).optional() }).strict().parse(req.body);
    const out = await db.tx(async (q) => {
      const n = Number((await q("select count(*)::int n from programs where owner_id = $1", [req.user.id])).rows[0].n);
      if (n >= MAX_PROGRAMS) return { code: 429, error: `You can have up to ${MAX_PROGRAMS} plans` };
      const base = b.title.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 40) || "plan";
      const s = `${base}-${Math.random().toString(36).slice(2, 8)}`;
      const row = (await q("insert into programs (slug, title, description, visibility, owner_id) values ($1,$2,$3,'private',$4) returning slug, title", [s, b.title, b.description ?? null, req.user.id])).rows[0];
      return { row };
    });
    out.error ? res.status(out.code).json({ error: out.error }) : res.status(201).json(out.row);
  }));

  api.get("/programs/:slug", wrap(async (req, res) => {
    const p = await visibleProgram(slug.parse(req.params.slug), req.user.id);
    if (!p) return res.status(404).json({ error: "Not found" });
    const items = (await db.query(
      `select i.id, i.position, i.title, i.description, i.content_id, (g.user_id is not null) as done
       from program_items i left join progress g on g.program_item_id = i.id and g.user_id = $2 where i.program_id = $1 order by i.position`, [p.id, req.user.id])).rows;
    res.json({ slug: p.slug, title: p.title, description: p.description, visibility: p.visibility, mine: p.owner_id === req.user.id, items });
  }));

  api.post("/programs/:slug/items", wrap(async (req, res) => {
    const b = z.object({ title: z.string().trim().min(1).max(140), description: z.string().trim().max(600).optional() }).strict().parse(req.body);
    const p = await visibleProgram(slug.parse(req.params.slug), req.user.id);
    if (!p || p.owner_id !== req.user.id) return res.status(404).json({ error: "Not found" });
    const out = await db.tx(async (q) => {
      const n = (await q("select count(*)::int n, coalesce(max(position), -1) + 1 as next from program_items where program_id = $1", [p.id])).rows[0];
      if (Number(n.n) >= MAX_ITEMS) return { error: `A plan can have up to ${MAX_ITEMS} steps` };
      return { row: (await q("insert into program_items (program_id, position, title, description) values ($1,$2,$3,$4) returning id, position, title, description", [p.id, n.next, b.title, b.description ?? null])).rows[0] };
    });
    out.error ? res.status(429).json({ error: out.error }) : res.status(201).json(out.row);
  }));

  api.delete("/programs/:slug/items/:id", wrap(async (req, res) => {
    const p = await visibleProgram(slug.parse(req.params.slug), req.user.id);
    if (!p || p.owner_id !== req.user.id) return res.status(404).json({ error: "Not found" });
    const r = await db.query("delete from program_items where id = $1 and program_id = $2 returning id", [uuid.parse(req.params.id), p.id]);
    r.rows.length ? res.status(204).end() : res.status(404).json({ error: "Not found" });
  }));

  api.put("/programs/:slug/items/:id/progress", wrap(async (req, res) => {
    const b = z.object({ done: z.boolean() }).strict().parse(req.body);
    const p = await visibleProgram(slug.parse(req.params.slug), req.user.id);
    const itemId = uuid.parse(req.params.id);
    const item = p && (await db.query("select id from program_items where id = $1 and program_id = $2", [itemId, p.id])).rows[0];
    if (!item) return res.status(404).json({ error: "Not found" });
    if (b.done) await db.query("insert into progress (user_id, program_item_id) values ($1,$2) on conflict do nothing", [req.user.id, itemId]);
    else await db.query("delete from progress where user_id = $1 and program_item_id = $2", [req.user.id, itemId]);
    res.json({ done: b.done });
  }));

  // ---- admin
  api.get("/admin/users", requireRole("admin"), wrap(async (_req, res) => {
    res.json((await db.query("select id, email, display_name, stack_id, role, access_status, created_at, last_seen_at from app_users order by created_at desc limit 500")).rows);
  }));

  api.post("/admin/users/:id/access", requireRole("admin"), wrap(async (req, res) => {
    const b = z.object({ status: z.enum(["approved", "rejected", "pending"]) }).strict().parse(req.body);
    const id = uuid.parse(req.params.id);
    if (id === req.user.id) return res.status(400).json({ error: "You cannot change your own access" });
    const r = await db.tx(async (q) => {
      const row = (await q("update app_users set access_status = $2 where id = $1 and role <> 'admin' returning id, email, access_status", [id, b.status])).rows[0];
      if (row) await audit(q, req.user.id, "admin.access", id, { status: b.status });
      return row;
    });
    r ? res.json(r) : res.status(404).json({ error: "Not found" });
  }));

  api.post("/admin/users/:id/role", requireRole("admin"), wrap(async (req, res) => {
    const b = z.object({ role: z.enum(["member", "reviewer"]) }).strict().parse(req.body);
    const id = uuid.parse(req.params.id);
    const r = await db.tx(async (q) => {
      const row = (await q("update app_users set role = $2 where id = $1 and role <> 'admin' returning id, email, role", [id, b.role])).rows[0];
      if (row) await audit(q, req.user.id, "admin.role", id, { role: b.role });
      return row;
    });
    r ? res.json(r) : res.status(404).json({ error: "Not found" });
  }));

  app.use("/api", api);
  app.use((_req, res) => res.status(404).json({ error: "Not found" }));
  // eslint-disable-next-line no-unused-vars
  app.use((err, _req, res, _next) => {
    if (err instanceof z.ZodError) return res.status(400).json({ error: "Invalid input", issues: err.issues.map((i) => ({ path: i.path.join("."), message: i.message })) });
    if (err?.type === "entity.too.large") return res.status(413).json({ error: "Request too large" });
    if (err?.type === "entity.parse.failed") return res.status(400).json({ error: "Invalid JSON" });
    console.error("unhandled", err?.stack || err);
    res.status(500).json({ error: "Something went wrong" });
  });
  return app;
}

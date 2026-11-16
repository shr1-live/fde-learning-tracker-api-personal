using FdeTracker.Api.Data;
using FdeTracker.Api.Models;
using FdeTracker.Api.ViewModels;

namespace FdeTracker.Api.Repositories;

public sealed class ContentRepository
{
    const string PublicCols = "c.id, c.title, c.summary, c.kind, c.url, c.stack_id, c.approved_at, t.slug as topic, t.title as topic_title, coalesce(u.display_name, split_part(u.email, '@', 1)) as author";

    /// <summary>Approved items: general ones for everyone plus the chosen stack's. Returns up to limit + 1 rows so the caller can tell if there is more.</summary>
    public Task<List<ContentRecord>> Feed(Session s, string stack, string? topic, (DateTime Ts, Guid Id)? cursor, int limit)
    {
        var where = "c.status = 'approved' and (c.stack_id is null or c.stack_id = @stack)";
        if (topic is not null) where += " and t.slug = @topic";
        if (cursor is not null) where += " and (c.approved_at, c.id) < (@ts::timestamptz, @cid::uuid)";
        return s.Query<ContentRecord>(
            $"""
             select {PublicCols} from content_items c join topics t on t.id = c.topic_id join app_users u on u.id = c.author_id
             where {where} order by c.approved_at desc, c.id desc limit @take
             """,
            new { stack = stack == "general" ? null : stack, topic, ts = cursor?.Ts, cid = cursor?.Id, take = limit + 1 });
    }

    public Task<List<MyContentView>> Mine(Session s, Guid userId) =>
        s.Query<MyContentView>(
            """
            select c.id, c.title, c.kind, c.status, c.stack_id, t.slug as topic, c.updated_at,
              (select comment from reviews r where r.content_id = c.id order by r.created_at desc limit 1) as last_review_comment
            from content_items c join topics t on t.id = c.topic_id where c.author_id = @userId order by c.updated_at desc limit 100
            """, new { userId });

    public Task<ContentRecord?> Find(Session s, Guid id) =>
        s.First<ContentRecord>(
            $"select {PublicCols}, c.body_md, c.status, c.author_id from content_items c join topics t on t.id = c.topic_id join app_users u on u.id = c.author_id where c.id = @id", new { id });

    public Task<int> ActiveCount(Session s, Guid userId) =>
        s.Scalar<int>("select count(*)::int from content_items where author_id = @userId and status in ('draft','in_review','changes_requested')", new { userId });

    public Task<int?> TopicId(Session s, string slug) => s.First<int?>("select id from topics where slug = @slug", new { slug });

    public Task<ContentSavedView> Insert(Session s, int topicId, string? stackId, string kind, string title, string? summary, string? bodyMd, string? url, Guid authorId, bool submit) =>
        s.First<ContentSavedView>(
            """
            insert into content_items (topic_id, stack_id, kind, title, summary, body_md, url, author_id, status, submitted_at)
            values (@topicId, @stackId, @kind, @title, @summary, @bodyMd, @url, @authorId, @status, case when @status = 'in_review' then now() end)
            returning id, status
            """,
            new { topicId, stackId, kind, title, summary, bodyMd, url, authorId, status = submit ? "in_review" : "draft" })!;

    public Task<ContentRow?> LockOwned(Session s, Guid id, Guid authorId) =>
        s.First<ContentRow>("select * from content_items where id = @id and author_id = @authorId for update", new { id, authorId });

    public Task<ContentSavedView> Update(Session s, Guid id, ContentRow next, bool submit) =>
        s.First<ContentSavedView>(
            """
            update content_items set topic_id=@TopicId, kind=@Kind, title=@Title, summary=@Summary, body_md=@BodyMd, url=@Url, stack_id=@StackId, updated_at=now(),
              status = case when @submit then 'in_review' else status end,
              submitted_at = case when @submit then now() else submitted_at end
            where id = @id returning id, status
            """,
            new { id, next.TopicId, next.Kind, next.Title, next.Summary, next.BodyMd, next.Url, next.StackId, submit })!;

    public async Task<bool> DeleteOwned(Session s, Guid id, Guid authorId) =>
        await s.Exec("delete from content_items where id = @id and author_id = @authorId and status in ('draft','changes_requested','rejected')", new { id, authorId }) > 0;
}

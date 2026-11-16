using System.Globalization;
using FdeTracker.Api.Common;
using FdeTracker.Api.Data;
using FdeTracker.Api.Models;
using FdeTracker.Api.Models.Requests;
using FdeTracker.Api.Repositories;
using FdeTracker.Api.ViewModels;

namespace FdeTracker.Api.Services;

public sealed class ContentService(Db db, ContentRepository content, AuditRepository audit)
{
    const int MaxActiveDrafts = 20;

    public async Task<FeedPageView> Feed(AppUser user, FeedQuery q)
    {
        var stack = q.Stack ?? user.StackId ?? "general";
        var rows = await db.Read(s => content.Feed(s, stack, q.Topic, q.Before is null ? null : DecodeCursor(q.Before), q.Limit));
        var more = rows.Count > q.Limit;
        var items = (more ? rows.Take(q.Limit) : rows).ToList();
        return new FeedPageView
        {
            Items = items.Select(FeedItemView.From).ToList(),
            Next = more && items.Count > 0 ? EncodeCursor(items[^1]) : null,
        };
    }

    public Task<List<MyContentView>> Mine(Guid userId) => db.Read(s => content.Mine(s, userId));

    /// <summary>Approved items are visible to everyone; anything else only to its author and the review team.</summary>
    public async Task<ContentDetailView> Get(AppUser user, Guid id)
    {
        var row = await db.Read(s => content.Find(s, id));
        var privileged = row is not null && (row.AuthorId == user.Id || user.Role != "member");
        if (row is null || (row.Status != "approved" && !privileged)) throw ApiException.NotFound();
        return ContentDetailView.From(row);
    }

    public Task<ContentSavedView> Create(Guid userId, CreateContentRequest b) => db.Tx(async s =>
    {
        if (await content.ActiveCount(s, userId) >= MaxActiveDrafts)
            throw new ApiException(429, $"You have {MaxActiveDrafts} items in progress. Finish or delete some first.");
        var topicId = await content.TopicId(s, b.Topic) ?? throw ApiException.BadRequest("Unknown topic");
        var row = await content.Insert(s, topicId, b.StackId, b.Kind, b.Title, b.Summary, b.BodyMd, b.Url, userId, b.Submit);
        await audit.Record(s, userId, "content.create", row.Id.ToString(), new { status = row.Status });
        return row;
    });

    public Task<ContentSavedView> Update(Guid userId, Guid id, PatchContentRequest b) => db.Tx(async s =>
    {
        var cur = await content.LockOwned(s, id, userId) ?? throw ApiException.NotFound();
        if (cur.Status is not ("draft" or "changes_requested")) throw new ApiException(409, "This item can no longer be edited");
        var topicId = cur.TopicId;
        if (b.Topic is not null) topicId = await content.TopicId(s, b.Topic) ?? throw ApiException.BadRequest("Unknown topic");
        var next = new ContentRow
        {
            TopicId = topicId, Kind = b.Kind ?? cur.Kind, Title = b.Title ?? cur.Title, Summary = b.Summary ?? cur.Summary,
            BodyMd = b.BodyMd ?? cur.BodyMd, Url = b.Url ?? cur.Url, StackId = b.StackIdSet ? b.StackId : cur.StackId,
        };
        if (ContentRules.Check(next.Kind, next.Url, next.BodyMd) is { } e) throw ApiException.BadRequest(e.Message);
        var submit = b.Submit == true;
        var row = await content.Update(s, id, next, submit);
        await audit.Record(s, userId, submit ? "content.submit" : "content.edit", id.ToString());
        return row;
    });

    public async Task Delete(Guid userId, Guid id)
    {
        if (!await db.Read(s => content.DeleteOwned(s, id, userId))) throw ApiException.NotFound("Not found, or already published");
    }

    // Cursor = base64url("<approved_at round-trip>|<id>"): opaque to clients.
    static string EncodeCursor(ContentRecord r) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{r.ApprovedAt!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}|{r.Id}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static (DateTime, Guid) DecodeCursor(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            return (DateTime.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(), Guid.ParseExact(parts[1], "D"));
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            throw ApiException.BadRequest("Bad cursor");
        }
    }
}

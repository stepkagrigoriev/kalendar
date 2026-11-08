using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kalendar.WebApi.Tasks;

internal sealed record TaskQuery(FilterDefinition<TaskDocument> Filter, string Sort, int Limit, int Offset)
{
    public static TaskQuery Parse(IQueryCollection query, ObjectId userId, DateTime now)
    {
        var builder = Builders<TaskDocument>.Filter;
        var filter = builder.Eq(x => x.UserId, userId);
        DateTime? from = query.ContainsKey("from") ? TaskInput.ParseDate(query["from"]) : null;
        DateTime? to = query.ContainsKey("to") ? TaskInput.ParseDate(query["to"]) : null;
        if (from.HasValue && to.HasValue && (to <= from || to.Value - from.Value > TimeSpan.FromDays(62))) throw new FormatException();
        if (from.HasValue) filter &= builder.Gte(x => x.DueAt, from.Value);
        if (to.HasValue) filter &= builder.Ne(x => x.DueAt, null) & builder.Lt(x => x.DueAt, to.Value);
        if (query.TryGetValue("status", out var status))
        {
            if (status.ToString() is not ("open" or "completed")) throw new FormatException();
            filter &= builder.Eq(x => x.Status, status.ToString());
        }
        if (query.TryGetValue("priority", out var priority))
        {
            if (priority.ToString() is not ("low" or "medium" or "high")) throw new FormatException();
            filter &= builder.Eq(x => x.Priority, priority.ToString());
        }
        if (query.TryGetValue("listId", out var list))
        {
            if (list == "none") filter &= builder.Eq(x => x.ListId, null);
            else if (ObjectId.TryParse(list, out var listId)) filter &= builder.Eq(x => x.ListId, listId);
            else throw new FormatException();
        }
        foreach (var tag in query["tag"])
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 50) throw new FormatException();
            filter &= builder.Regex("tags", new BsonRegularExpression($"^{Regex.Escape(tag.Trim())}$", "i"));
        }
        if (query.TryGetValue("overdue", out var overdue))
        {
            if (!bool.TryParse(overdue, out var value)) throw new FormatException();
            if (value) filter &= builder.Eq(x => x.Status, "open") & builder.Ne(x => x.DueAt, null) & builder.Lt(x => x.DueAt, now);
        }
        if (query.TryGetValue("q", out var search))
        {
            if (search.ToString().Length > 100) throw new FormatException();
            var regex = new BsonRegularExpression(Regex.Escape(search.ToString()), "i");
            filter &= builder.Regex(x => x.Title, regex) | builder.Regex(x => x.Description, regex);
        }
        var sort = query.ContainsKey("sort") ? query["sort"].ToString() : "dueAt";
        if (sort is not ("dueAt" or "priority" or "createdAt")) throw new FormatException();
        var limit = query.ContainsKey("limit") && int.TryParse(query["limit"], out var size) ? size : query.ContainsKey("limit") ? throw new FormatException() : 50;
        var offset = query.ContainsKey("offset") && int.TryParse(query["offset"], out var start) ? start : query.ContainsKey("offset") ? throw new FormatException() : 0;
        if (limit is < 1 or > 100 || offset < 0) throw new FormatException();
        return new(filter, sort, limit, offset);
    }
}

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Kalendar.WebApi.Tasks;

public sealed class TaskListDocument
{
    public ObjectId Id { get; set; }
    public ObjectId UserId { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TaskDocument
{
    public ObjectId Id { get; set; }
    public ObjectId UserId { get; set; }
    public ObjectId? ListId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime? DueAt { get; set; }
    public string Priority { get; set; } = "medium";
    public string Status { get; set; } = "open";
    public string[] Tags { get; set; } = [];
    public Reminder Reminder { get; set; }
    public Recurrence Recurrence { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    [BsonIgnoreIfNull]
    public string RecurrenceKey { get; set; }

    public object Response() => new
    {
        id = Id.ToString(), userId = UserId.ToString(), listId = ListId?.ToString(), Title, Description,
        DueAt, Priority, Status, Tags, Reminder, Recurrence, CreatedAt, UpdatedAt, CompletedAt
    };
}

public sealed class Reminder
{
    public DateTime? At { get; set; }
    public DateTime? SentAt { get; set; }
}

public sealed class Recurrence
{
    public string Frequency { get; set; } = "daily";
    public int Interval { get; set; } = 1;
    [BsonIgnoreIfNull]
    public string[] DaysOfWeek { get; set; }
    public DateTime? EndAt { get; set; }
}

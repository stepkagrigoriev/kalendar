using MongoDB.Bson;

namespace Kalendar.Worker.Scheduling;

public sealed class BackgroundJob
{
    public ObjectId Id { get; set; }
    public ObjectId UserId { get; set; }
    public ObjectId TaskId { get; set; }
    public string Kind { get; set; }
    public DateTime TargetAt { get; set; }
    public DateTime ScheduledAt { get; set; }
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public string LastError { get; set; }
    public string LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

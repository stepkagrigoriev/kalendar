namespace Kalendar.Contracts;

public sealed record TaskChanged(string TaskId, string UserId, DateTimeOffset OccurredAt);
public sealed record TaskDeleted(string TaskId, string UserId, DateTimeOffset OccurredAt);
public sealed record NotificationRequested(string NotificationId, string UserId, string TaskId, string Title, string Text, DateTimeOffset OccurredAt);
public sealed record BackgroundJobFailed(string JobId, string Kind, string TaskId, string UserId, string LastError);

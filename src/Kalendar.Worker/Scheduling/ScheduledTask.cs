namespace Kalendar.Worker.Scheduling;

public sealed record ScheduledTask(string Id, string UserId, string Title, string Description, DateTime? DueAt,
    string Status, TaskReminder Reminder, TaskRecurrence Recurrence);
public sealed record TaskReminder(DateTime? At, DateTime? SentAt);
public sealed record TaskRecurrence(string Frequency, int Interval, string[] DaysOfWeek, DateTime? EndAt);

public static class RecurrenceDates
{
    static readonly string[] Days = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    public static DateTime? Next(DateTime current, TaskRecurrence rule)
    {
        DateTime next;
        switch (rule.Frequency)
        {
            case "daily": next = current.AddDays(rule.Interval); break;
            case "monthly": next = current.AddMonths(rule.Interval); break;
            case "weekly":
                var days = rule.DaysOfWeek.Select(day => Array.IndexOf(Days, day)).Order().ToArray();
                var currentDay = ((int)current.DayOfWeek + 6) % 7;
                var later = days.Where(day => day > currentDay).ToArray();
                var distance = later.Length > 0 ? later[0] - currentDay : rule.Interval * 7 - currentDay + days[0];
                next = current.AddDays(distance);
                break;
            default: throw new ArgumentException("Неизвестная частота повторения.");
        }
        return rule.EndAt is { } end && next > end ? null : next;
    }

    public static DateTime Milliseconds(DateTime date) => new(date.Ticks - date.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    public static string Key(string kind, string taskId, DateTime target) => $"{kind}:{taskId}:{Milliseconds(target):O}";
    public static bool IsCurrent(BackgroundJob job, ScheduledTask task, DateTime now) =>
        job.UserId.ToString() == task.UserId && (job.Kind == "reminder"
            ? task.Status == "open" && task.Reminder?.At == job.TargetAt && task.Reminder.SentAt is null && job.TargetAt >= now.AddHours(-24)
            : task.Recurrence is not null && task.DueAt == job.TargetAt && Next(task.DueAt.Value, task.Recurrence) is not null);
}

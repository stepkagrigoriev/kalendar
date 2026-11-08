using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace Kalendar.WebApi.Tasks;

static class TaskInput
{
    internal static readonly string[] WeekDays = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"];

    public static Dictionary<string, string[]> Apply(JsonElement input, TaskDocument task)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["Ожидается JSON-объект."];
            return errors;
        }
        foreach (var field in input.EnumerateObject())
        {
            try
            {
                switch (field.Name)
                {
                    case "title": task.Title = field.Value.GetString()?.Trim() ?? ""; break;
                    case "description": task.Description = field.Value.GetString() ?? ""; break;
                    case "priority": task.Priority = field.Value.GetString(); break;
                    case "dueAt": task.DueAt = Date(field.Value); break;
                    case "listId":
                        task.ListId = field.Value.ValueKind == JsonValueKind.Null ? null : ObjectId.Parse(field.Value.GetString());
                        break;
                    case "tags":
                        var tags = field.Value.EnumerateArray().Select(x => x.GetString()?.Trim() ?? "").ToArray();
                        if (tags.Length > 20 || tags.Any(x => x.Length is < 1 or > 50))
                            throw new FormatException();
                        task.Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        break;
                    case "reminder":
                        if (field.Value.ValueKind == JsonValueKind.Null)
                            task.Reminder = null;
                        else
                        {
                            if (field.Value.TryGetProperty("sentAt", out _))
                                throw new FormatException();
                            var at = Date(field.Value.GetProperty("at"));
                            task.Reminder = new Reminder { At = at, SentAt = at == task.Reminder?.At ? task.Reminder?.SentAt : null };
                        }
                        break;
                    case "recurrence":
                        if (field.Value.ValueKind == JsonValueKind.Null)
                            task.Recurrence = null;
                        else
                        {
                            var recurrence = field.Value;
                            task.Recurrence = new Recurrence
                            {
                                Frequency = recurrence.GetProperty("frequency").GetString(),
                                Interval = recurrence.GetProperty("interval").GetInt32(),
                                DaysOfWeek = recurrence.TryGetProperty("daysOfWeek", out var days)
                                    ? days.EnumerateArray().Select(x => x.GetString()).Distinct().ToArray() : null,
                                EndAt = recurrence.TryGetProperty("endAt", out var end) ? Date(end) : null
                            };
                        }
                        break;
                    default: errors[field.Name] = ["Поле недоступно для изменения."]; break;
                }
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
            {
                errors[field.Name] = ["Некорректное значение поля."];
            }
        }
        if (task.Title.Length is < 1 or > 200) errors["title"] = ["Название должно содержать от 1 до 200 символов."];
        if (task.Description.Length > 5000) errors["description"] = ["Описание не должно превышать 5000 символов."];
        if (task.Priority is not ("low" or "medium" or "high")) errors["priority"] = ["Неизвестный приоритет."];
        if (task.Recurrence is { } rule &&
            (task.DueAt is null || rule.Frequency is not ("daily" or "weekly" or "monthly") ||
             rule.Interval is < 1 or > 365 || rule.EndAt < task.DueAt ||
             (rule.Frequency == "weekly" ? rule.DaysOfWeek is not { Length: > 0 } || rule.DaysOfWeek.Any(x => !WeekDays.Contains(x)) : rule.DaysOfWeek is not null)))
            errors["recurrence"] = ["Некорректное правило повторения или отсутствует срок задачи."];
        return errors;
    }

    internal static DateTime? Date(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : ParseDate(value.GetString());

    internal static DateTime ParseDate(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new FormatException("Укажите дату со смещением UTC.");
        return DateTime.UnixEpoch.AddMilliseconds(result.ToUnixTimeMilliseconds());
    }
}

using System.Text.Json;
using Kalendar.WebApi.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using MongoDB.Bson;
using Xunit;

namespace Kalendar.WebApi.Tests;

public class TaskValidationTests
{
    [Theory]
    [InlineData("{\"title\":\" Задача \"}", true)]
    [InlineData("{\"title\":\"\"}", false)]
    [InlineData("{\"title\":\"Тест\",\"priority\":\"urgent\"}", false)]
    [InlineData("{\"title\":\"Тест\",\"tags\":[\"\"]}", false)]
    [InlineData("{\"title\":\"Тест\",\"dueAt\":\"2026-11-08T10:00:00\"}", false)]
    [InlineData("{\"title\":\"Тест\",\"dueAt\":\"2026-11-08T10:00:00+05:00\"}", true)]
    [InlineData("{\"title\":\"Тест\",\"reminder\":{\"at\":null,\"sentAt\":null}}", false)]
    [InlineData("{\"title\":\"Тест\",\"recurrence\":{\"frequency\":\"daily\",\"interval\":1}}", false)]
    [InlineData("{\"title\":\"Тест\",\"listId\":\"broken\"}", false)]
    [InlineData("{\"title\":\"Тест\",\"status\":\"completed\"}", false)]
    [InlineData("[]", false)]
    public void InputValidation(string json, bool valid)
    {
        var task = new TaskDocument();
        Assert.Equal(valid, TaskInput.Apply(JsonDocument.Parse(json).RootElement, task).Count == 0);
    }

    [Fact]
    public void PatchPreservesUnspecifiedFieldsAndNormalizesTags()
    {
        var task = new TaskDocument { Title = "Исходная", Description = "Описание", DueAt = DateTime.UtcNow };
        var patch = JsonDocument.Parse("{\"tags\":[\" Работа \",\"работа\",\"ВУЗ\"],\"dueAt\":null}").RootElement;
        Assert.Empty(TaskInput.Apply(patch, task));
        Assert.Equal(["Работа", "ВУЗ"], task.Tags);
        Assert.Equal("Исходная", task.Title);
        Assert.Equal("Описание", task.Description);
        Assert.Null(task.DueAt);
    }

    [Theory]
    [InlineData("daily", 1, null, true)]
    [InlineData("monthly", 365, null, true)]
    [InlineData("daily", 0, null, false)]
    [InlineData("daily", 366, null, false)]
    [InlineData("weekly", 1, "[\"monday\",\"friday\"]", true)]
    [InlineData("weekly", 1, "[]", false)]
    [InlineData("weekly", 1, "[\"invalid\"]", false)]
    [InlineData("weekly", 1, null, false)]
    [InlineData("daily", 1, "[\"monday\"]", false)]
    public void RecurrenceValidation(string frequency, int interval, string? days, bool valid)
    {
        var task = new TaskDocument { Title = "Повторение", DueAt = DateTime.UtcNow };
        var daysProperty = days is null ? "" : $",\"daysOfWeek\":{days}";
        var json = $"{{\"recurrence\":{{\"frequency\":\"{frequency}\",\"interval\":{interval}{daysProperty}}}}}";
        Assert.Equal(valid, TaskInput.Apply(JsonDocument.Parse(json).RootElement, task).Count == 0);
    }

    [Theory]
    [InlineData("limit", "0")]
    [InlineData("offset", "-1")]
    [InlineData("limit", "101")]
    [InlineData("limit", "broken")]
    [InlineData("sort", "unknown")]
    [InlineData("status", "unknown")]
    [InlineData("priority", "unknown")]
    [InlineData("listId", "broken")]
    [InlineData("from", "not-a-date")]
    [InlineData("overdue", "broken")]
    public void QueryRejectsInvalidParameters(string key, string value)
    {
        var query = new QueryCollection(new Dictionary<string, StringValues> { [key] = value });
        Assert.Throws<FormatException>(() => TaskQuery.Parse(query, ObjectId.GenerateNewId(), DateTime.UtcNow));
    }
}

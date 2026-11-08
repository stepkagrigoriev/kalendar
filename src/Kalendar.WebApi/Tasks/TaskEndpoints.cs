using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kalendar.WebApi.Tasks;

static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        var tasks = app.MapGroup("/api/tasks").RequireAuthorization();
        tasks.MapPost("", CreateAsync);
        tasks.MapGet("", QueryAsync);
        tasks.MapGet("/{id}", async (string id, ClaimsPrincipal principal, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out var taskId)) return Results.NotFound();
            var userId = UserId(principal);
            var task = await Tasks(db).Find(x => x.Id == taskId && x.UserId == userId).FirstOrDefaultAsync(ct);
            return task is null ? Results.NotFound() : Results.Ok(task.Response());
        });
        tasks.MapPatch("/{id}", PatchAsync);
        tasks.MapPost("/{id}/complete", (string id, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, TaskEvents events, CancellationToken ct) =>
            ChangeStatusAsync(id, true, principal, db, clock, events, ct));
        tasks.MapPost("/{id}/reopen", (string id, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, TaskEvents events, CancellationToken ct) =>
            ChangeStatusAsync(id, false, principal, db, clock, events, ct));
        tasks.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, IMongoDatabase db, TaskEvents events, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out var taskId)) return Results.NotFound();
            var userId = UserId(principal);
            var result = await Tasks(db).DeleteOneAsync(x => x.Id == taskId && x.UserId == userId, ct);
            if (result.DeletedCount > 0) await events.DeletedAsync(id, userId.ToString(), ct);
            return result.DeletedCount == 0 ? Results.NotFound() : Results.NoContent();
        });

        var lists = app.MapGroup("/api/lists").RequireAuthorization();
        lists.MapPost("", (JsonElement body, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
            SaveListAsync(null, body, principal, db, clock, ct));
        lists.MapPatch("/{id}", (string id, JsonElement body, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, CancellationToken ct) =>
            SaveListAsync(id, body, principal, db, clock, ct));
        lists.MapGet("", async (ClaimsPrincipal principal, IMongoDatabase db, CancellationToken ct) =>
        {
            var userId = UserId(principal);
            var result = await Lists(db).Find(x => x.UserId == userId).SortBy(x => x.Name).ToListAsync(ct);
            return Results.Ok(result.Select(ListResponse));
        });
        lists.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, IMongoDatabase db, CancellationToken ct) =>
        {
            if (!ObjectId.TryParse(id, out var listId)) return Results.NotFound();
            var userId = UserId(principal);
            if (!await Lists(db).Find(x => x.Id == listId && x.UserId == userId).AnyAsync(ct)) return Results.NotFound();
            await Tasks(db).UpdateManyAsync(x => x.UserId == userId && x.ListId == listId,
                Builders<TaskDocument>.Update.Set(x => x.ListId, null), cancellationToken: ct);
            var result = await Lists(db).DeleteOneAsync(x => x.Id == listId && x.UserId == userId, ct);
            return result.DeletedCount == 0 ? Results.NotFound() : Results.NoContent();
        });
    }

    static ObjectId UserId(ClaimsPrincipal principal) => ObjectId.Parse(principal.FindFirstValue("sub"));
    internal static IMongoCollection<TaskDocument> Tasks(IMongoDatabase db) => db.GetCollection<TaskDocument>("tasks");
    static IMongoCollection<TaskListDocument> Lists(IMongoDatabase db) => db.GetCollection<TaskListDocument>("taskLists");
    static object ListResponse(TaskListDocument list) => new { id = list.Id.ToString(), list.Name, list.Color, list.CreatedAt, list.UpdatedAt };

    static async Task<IResult> CreateAsync(JsonElement body, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, TaskEvents events, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var task = new TaskDocument { Id = ObjectId.GenerateNewId(), UserId = UserId(principal), CreatedAt = now, UpdatedAt = now };
        var errors = TaskInput.Apply(body, task);
        await ValidateListAsync(task, db, errors, ct);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        await Tasks(db).InsertOneAsync(task, cancellationToken: ct);
        await events.ChangedAsync(task, ct);
        return Results.Created($"/api/tasks/{task.Id}", task.Response());
    }

    static async Task ValidateListAsync(TaskDocument task, IMongoDatabase db, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (task.ListId is { } listId && !await Lists(db).Find(x => x.Id == listId && x.UserId == task.UserId).AnyAsync(ct))
            errors["listId"] = ["Список не найден."];
    }

    static async Task<IResult> PatchAsync(string id, JsonElement body, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, TaskEvents events, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var taskId)) return Results.NotFound();
        var userId = UserId(principal);
        var task = await Tasks(db).Find(x => x.Id == taskId && x.UserId == userId).FirstOrDefaultAsync(ct);
        if (task is null) return Results.NotFound();
        var originalReminderAt = task.Reminder?.At;
        var errors = TaskInput.Apply(body, task);
        await ValidateListAsync(task, db, errors, ct);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        var updates = new List<UpdateDefinition<TaskDocument>>();
        var update = Builders<TaskDocument>.Update;
        foreach (var field in body.EnumerateObject())
        {
            switch (field.Name)
            {
                case "title": updates.Add(update.Set(x => x.Title, task.Title)); break;
                case "description": updates.Add(update.Set(x => x.Description, task.Description)); break;
                case "priority": updates.Add(update.Set(x => x.Priority, task.Priority)); break;
                case "dueAt": updates.Add(update.Set(x => x.DueAt, task.DueAt)); break;
                case "listId": updates.Add(update.Set(x => x.ListId, task.ListId)); break;
                case "tags": updates.Add(update.Set(x => x.Tags, task.Tags)); break;
                case "recurrence": updates.Add(update.Set(x => x.Recurrence, task.Recurrence)); break;
                case "reminder":
                    if (task.Reminder?.At != originalReminderAt) updates.Add(update.Set(x => x.Reminder, task.Reminder));
                    break;
            }
        }
        if (updates.Count == 0) return Results.Ok(task.Response());
        updates.Add(update.Set(x => x.UpdatedAt, clock.GetUtcNow().UtcDateTime));
        var result = await Tasks(db).FindOneAndUpdateAsync(x => x.Id == taskId && x.UserId == userId, update.Combine(updates),
            new FindOneAndUpdateOptions<TaskDocument> { ReturnDocument = ReturnDocument.After }, ct);
        if (result is not null) await events.ChangedAsync(result, ct);
        return result is null ? Results.NotFound() : Results.Ok(result.Response());
    }

    static async Task<IResult> ChangeStatusAsync(string id, bool completed, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, TaskEvents events, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out var taskId)) return Results.NotFound();
        var userId = UserId(principal);
        var status = completed ? "completed" : "open";
        var now = clock.GetUtcNow().UtcDateTime;
        var task = await Tasks(db).FindOneAndUpdateAsync(x => x.Id == taskId && x.UserId == userId && x.Status != status,
            Builders<TaskDocument>.Update.Set(x => x.Status, status).Set(x => x.CompletedAt, completed ? now : (DateTime?)null).Set(x => x.UpdatedAt, now),
            new FindOneAndUpdateOptions<TaskDocument> { ReturnDocument = ReturnDocument.After }, ct);
        if (task is not null) await events.ChangedAsync(task, ct);
        task ??= await Tasks(db).Find(x => x.Id == taskId && x.UserId == userId).FirstOrDefaultAsync(ct);
        return task is null ? Results.NotFound() : Results.Ok(task.Response());
    }

    static async Task<IResult> SaveListAsync(string id, JsonElement body, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(principal);
        TaskListDocument list;
        if (id is null) list = new TaskListDocument { Id = ObjectId.GenerateNewId(), UserId = userId, CreatedAt = clock.GetUtcNow().UtcDateTime };
        else
        {
            if (!ObjectId.TryParse(id, out var listId)) return Results.NotFound();
            list = await Lists(db).Find(x => x.Id == listId && x.UserId == userId).FirstOrDefaultAsync(ct);
            if (list is null) return Results.NotFound();
        }
        var errors = new Dictionary<string, string[]>();
        if (body.ValueKind != JsonValueKind.Object) return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["Ожидается JSON-объект."] });
        foreach (var field in body.EnumerateObject())
        {
            if (field.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                errors[field.Name] = ["Ожидается строка."];
            else if (field.Name == "name") list.Name = field.Value.GetString()?.Trim() ?? "";
            else if (field.Name == "color") list.Color = field.Value.GetString();
            else errors[field.Name] = ["Неизвестное поле."];
        }
        if (list.Name.Length is < 1 or > 100) errors["name"] = ["Название должно содержать от 1 до 100 символов."];
        if (list.Color is not null && !Regex.IsMatch(list.Color, "^#[0-9a-fA-F]{6}$")) errors["color"] = ["Ожидается цвет #RRGGBB."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        list.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        try
        {
            if (id is null)
            {
                await Lists(db).InsertOneAsync(list, cancellationToken: ct);
                return Results.Created($"/api/lists/{list.Id}", ListResponse(list));
            }
            var update = Builders<TaskListDocument>.Update;
            var changes = new List<UpdateDefinition<TaskListDocument>> { update.Set(x => x.UpdatedAt, list.UpdatedAt) };
            if (body.TryGetProperty("name", out _)) changes.Add(update.Set(x => x.Name, list.Name));
            if (body.TryGetProperty("color", out _)) changes.Add(update.Set(x => x.Color, list.Color));
            var result = await Lists(db).FindOneAndUpdateAsync(x => x.Id == list.Id && x.UserId == userId, update.Combine(changes),
                new FindOneAndUpdateOptions<TaskListDocument> { ReturnDocument = ReturnDocument.After }, ct);
            return result is null ? Results.NotFound() : Results.Ok(ListResponse(result));
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return Results.Problem(statusCode: 409, title: "Список с таким названием уже существует.");
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            return Results.Problem(statusCode: 409, title: "Список с таким названием уже существует.");
        }
    }

    static async Task<IResult> QueryAsync(HttpRequest request, ClaimsPrincipal principal, IMongoDatabase db, TimeProvider clock, CancellationToken ct)
    {
        try
        {
            var query = TaskQuery.Parse(request.Query, UserId(principal), clock.GetUtcNow().UtcDateTime);
            var collection = Tasks(db);
            var total = await collection.CountDocumentsAsync(query.Filter, cancellationToken: ct);
            var pipeline = collection.Aggregate().Match(query.Filter);
            if (query.Sort == "priority")
                pipeline = pipeline.AppendStage<TaskDocument>(new BsonDocument("$addFields", new BsonDocument("priorityWeight",
                    new BsonDocument("$indexOfArray", new BsonArray { new BsonArray { "high", "medium", "low" }, "$priority" }))));
            pipeline = pipeline.Sort(new BsonDocument { { query.Sort == "priority" ? "priorityWeight" : query.Sort, 1 }, { "_id", 1 } })
                .Skip(query.Offset).Limit(query.Limit);
            if (query.Sort == "priority") pipeline = pipeline.Project<TaskDocument>(new BsonDocument("priorityWeight", 0));
            var items = await pipeline.ToListAsync(ct);
            return Results.Ok(new { items = items.Select(x => x.Response()), total });
        }
        catch (FormatException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Некорректные параметры выборки."] });
        }
    }
}

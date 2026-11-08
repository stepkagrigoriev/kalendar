using Kalendar.Contracts;
using MassTransit;
using MongoDB.Bson;
using LogContext = Serilog.Context.LogContext;
using Kalendar.Worker.Scheduling;
using System.Diagnostics;

namespace Kalendar.Worker;

sealed class TaskChangedConsumer(ILogger<TaskChangedConsumer> logger, InternalApiClient api, JobStore store) : IConsumer<TaskChanged>
{
    public async Task Consume(ConsumeContext<TaskChanged> context)
    {
        if (!ObjectId.TryParse(context.Message.TaskId, out _) || !ObjectId.TryParse(context.Message.UserId, out _))
            throw new ArgumentException("Некорректный идентификатор задачи или пользователя.");
        using var messageId = LogContext.PushProperty("messageId", context.MessageId);
        using var traceId = LogContext.PushProperty("traceId", Activity.Current?.TraceId.ToString());
        using var spanId = LogContext.PushProperty("spanId", Activity.Current?.SpanId.ToString());
        using var correlationId = LogContext.PushProperty("correlationId", context.CorrelationId ?? context.MessageId);
        logger.LogInformation("Получено изменение задачи {TaskId} пользователя {UserId}", context.Message.TaskId, context.Message.UserId);
        var task = await api.GetAsync(context.Message.TaskId, context.Message.UserId, context.CancellationToken);
        if (task is null) await store.CancelAsync(context.Message.TaskId, context.Message.UserId, context.CancellationToken);
        else await store.PlanAsync(task, context.CancellationToken);
    }
}

sealed class TaskDeletedConsumer(ILogger<TaskDeletedConsumer> logger, JobStore store) : IConsumer<TaskDeleted>
{
    public Task Consume(ConsumeContext<TaskDeleted> context)
    {
        if (!ObjectId.TryParse(context.Message.TaskId, out _) || !ObjectId.TryParse(context.Message.UserId, out _))
            throw new ArgumentException("Некорректный идентификатор задачи или пользователя.");
        using var messageId = LogContext.PushProperty("messageId", context.MessageId);
        using var traceId = LogContext.PushProperty("traceId", Activity.Current?.TraceId.ToString());
        using var spanId = LogContext.PushProperty("spanId", Activity.Current?.SpanId.ToString());
        using var correlationId = LogContext.PushProperty("correlationId", context.CorrelationId ?? context.MessageId);
        logger.LogInformation("Получено удаление задачи {TaskId} пользователя {UserId}", context.Message.TaskId, context.Message.UserId);
        return store.CancelAsync(context.Message.TaskId, context.Message.UserId, context.CancellationToken);
    }
}

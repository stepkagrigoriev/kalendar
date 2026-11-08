using Kalendar.Contracts;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using System.Diagnostics;
using LogContext = Serilog.Context.LogContext;

namespace Kalendar.NotificationService;

sealed class NotificationRequestedConsumer(IHubContext<NotificationHub> hub) : IConsumer<NotificationRequested>
{
    public async Task Consume(ConsumeContext<NotificationRequested> context)
    {
        var message = context.Message;
        using var messageId = LogContext.PushProperty("messageId", context.MessageId);
        using var traceId = LogContext.PushProperty("traceId", Activity.Current?.TraceId.ToString());
        using var spanId = LogContext.PushProperty("spanId", Activity.Current?.SpanId.ToString());
        using var correlationId = LogContext.PushProperty("correlationId", context.CorrelationId ?? context.MessageId);
        if (string.IsNullOrWhiteSpace(message.NotificationId) || !ValidId(message.UserId) || !ValidId(message.TaskId))
            throw new ArgumentException("Некорректные идентификаторы уведомления.");
        try
        {
            await hub.Clients.User(message.UserId).SendAsync("notification", new
            {
                notificationId = message.NotificationId,
                taskId = message.TaskId,
                title = message.Title,
                text = message.Text,
                occurredAt = message.OccurredAt
            }, context.CancellationToken);
        }
        catch
        {
            Telemetry.SignalRErrors.Inc();
            throw;
        }
    }

    static bool ValidId(string value) => value is { Length: 24 } && value.All(Uri.IsHexDigit);
}

sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}

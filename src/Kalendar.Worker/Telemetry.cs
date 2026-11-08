using System.Diagnostics;
using MassTransit;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;

namespace Kalendar.Worker;

static class Telemetry
{
    public static readonly IMetricFactory Factory = Metrics.WithLabels(new Dictionary<string, string> { ["service"] = "Kalendar.Worker" });
    public static readonly Counter Messages = Factory.CreateCounter("rabbitmq_messages_total", "Результаты обработки сообщений.",
        new CounterConfiguration { LabelNames = ["result"] });
    public static readonly Histogram MessageDuration = Factory.CreateHistogram("rabbitmq_message_duration_seconds", "Длительность обработки сообщения.");
    public static readonly Counter Notifications = Factory.CreateCounter("notification_requests_total", "Опубликованные запросы уведомлений.");
    public static readonly Counter SignalRErrors = Factory.CreateCounter("signalr_delivery_errors_total", "Ошибки отправки в SignalR.");

    public static void AddTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddConsumeObserver<MessageMetrics>();
        services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("Kalendar.Worker"))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
                    .AddSource("MassTransit", "MongoDB.Driver.Core.Extensions.DiagnosticSources")
                    .AddProcessor(new PrivateDataFilter());
                var endpoint = configuration["OpenTelemetry:Endpoint"];
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                        throw new InvalidOperationException("Некорректный адрес OpenTelemetry:Endpoint.");
                    tracing.AddOtlpExporter(options => options.Endpoint = uri);
                }
            });
    }

    public static void UseTelemetry(this WebApplication app)
    {
        Messages.WithLabels("success").Inc(0);
        Messages.WithLabels("error").Inc(0);
        Notifications.Inc(0);
        SignalRErrors.Inc(0);
        app.UseHttpMetrics();
        app.MapMetrics();
    }
}

sealed class PrivateDataFilter : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        activity.SetTag("url.query", null);
        activity.SetTag("db.statement", null);
        activity.SetTag("db.query.text", null);
        foreach (var tag in new[] { "url.full", "http.url" })
        {
            if (activity.GetTagItem(tag) is string value && Uri.TryCreate(value, UriKind.Absolute, out var uri))
                activity.SetTag(tag, uri.GetLeftPart(UriPartial.Path));
        }
    }
}

sealed class MessageMetrics(ILogger<MessageMetrics> logger) : IConsumeObserver
{
    public Task PreConsume<T>(ConsumeContext<T> context) where T : class
    {
        context.GetOrAddPayload(() => Stopwatch.StartNew());
        return Task.CompletedTask;
    }

    public Task PostConsume<T>(ConsumeContext<T> context) where T : class
    {
        Telemetry.Messages.WithLabels("success").Inc();
        Observe(context);
        return Task.CompletedTask;
    }

    public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
    {
        Telemetry.Messages.WithLabels("error").Inc();
        logger.LogError("Ошибка обработки {MessageType}, MessageId={MessageId}: {ErrorType}", typeof(T).Name, context.MessageId, exception.GetType().Name);
        Observe(context);
        return Task.CompletedTask;
    }

    static void Observe<T>(ConsumeContext<T> context) where T : class
    {
        if (context.TryGetPayload<Stopwatch>(out var stopwatch))
            Telemetry.MessageDuration.Observe(stopwatch.Elapsed.TotalSeconds);
    }
}

using System.Text;
using Kalendar.Contracts;
using MassTransit;

namespace Kalendar.Worker.Scheduling;

sealed record SchedulerSettings(int PollSeconds, int LeaseSeconds, int MaxAttempts, int ReconcileMinutes)
{
    public static SchedulerSettings Read(IConfiguration configuration)
    {
        int Read(string name, int fallback)
        {
            var raw = configuration[$"Scheduler:{name}"];
            if (raw is null) return fallback;
            if (!int.TryParse(raw, out var value) || value <= 0) throw new InvalidOperationException($"Некорректная настройка Scheduler:{name}.");
            return value;
        }
        return new(Read("PollIntervalSeconds", 5), Read("LeaseSeconds", 60), Read("MaxAttempts", 5), Read("ReconcileIntervalMinutes", 10));
    }
}

static class SchedulerRegistration
{
    public static void AddScheduler(this IServiceCollection services, IConfiguration configuration)
    {
        var key = configuration["InternalApi:Key"];
        var address = configuration["WebApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32 ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Не заданы корректные InternalApi:Key и WebApi:BaseUrl.");
        services.AddSingleton(SchedulerSettings.Read(configuration));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<JobStore>();
        services.AddHttpClient<InternalApiClient>(client =>
        {
            client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("X-Internal-Api-Key", key);
        });
        services.AddHostedService<Scheduler>();
    }
}

sealed class Scheduler(JobStore store, InternalApiClient api, IBus bus, TimeProvider clock,
    SchedulerSettings settings, ILogger<Scheduler> logger) : BackgroundService
{
    internal static TimeSpan RetryDelay(int attempts) => TimeSpan.FromSeconds(attempts switch
    {
        1 => 10,
        2 => 60,
        3 => 300,
        4 => 900,
        _ => 3600
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReconcile = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (clock.GetUtcNow() >= nextReconcile)
                {
                    try { await ReconcileAsync(stoppingToken); }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Сверка расписания отложена: {ErrorType}", exception.GetType().Name);
                    }
                    nextReconcile = clock.GetUtcNow().AddMinutes(settings.ReconcileMinutes);
                }
                while (await store.ClaimAsync(settings.LeaseSeconds, stoppingToken) is { } job)
                    await ExecuteJobAsync(job, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Ошибка цикла планировщика: {ErrorType}", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), clock, stoppingToken);
        }
    }

    internal async Task ExecuteJobAsync(BackgroundJob job, CancellationToken ct)
    {
        using var leaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = job.LeaseUntil.GetValueOrDefault(clock.GetUtcNow().UtcDateTime) - clock.GetUtcNow().UtcDateTime;
        leaseTimeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var operationToken = leaseTimeout.Token;
        try
        {
            var task = await api.GetAsync(job.TaskId.ToString(), job.UserId.ToString(), operationToken);
            if (task is null || !RecurrenceDates.IsCurrent(job, task, clock.GetUtcNow().UtcDateTime))
            {
                await store.FinishAsync(job, "cancelled", ct, job.Attempts);
                return;
            }
            if (job.Kind == "reminder")
            {
                await bus.Publish(new NotificationRequested(job.IdempotencyKey, task.UserId, task.Id, task.Title,
                    task.DueAt is { } due ? $"Срок задачи: {due:O}" : "Задача без срока", clock.GetUtcNow()), operationToken);
                Telemetry.Notifications.Inc();
                await api.ConfirmAsync(job, operationToken);
            }
            else if (!await api.RepeatAsync(job, RecurrenceDates.Next(task.DueAt.Value, task.Recurrence).Value, operationToken))
            {
                await store.FinishAsync(job, "cancelled", ct, job.Attempts);
                return;
            }
            await store.FinishAsync(job, "completed", ct, job.Attempts);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            var attempts = job.Attempts + 1;
            var failed = attempts >= settings.MaxAttempts;
            var error = exception.GetType().Name;
            var saved = await store.FinishAsync(job, failed ? "failed" : "pending", ct, attempts, error,
                failed ? null : clock.GetUtcNow().UtcDateTime + RetryDelay(attempts));
            if (failed && saved)
            {
                var destination = await bus.GetSendEndpoint(new Uri("queue:worker-background-job-failed"));
                await destination.Send(new BackgroundJobFailed(job.Id.ToString(), job.Kind, job.TaskId.ToString(), job.UserId.ToString(), error), ct);
            }
        }
    }

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var cursor = "";
        while (true)
        {
            var page = await api.ScheduledAsync(cursor, ct);
            foreach (var task in page)
            {
                await store.PlanAsync(task, ct);
                seen.Add(task.Id);
            }
            if (page.Length < 500) break;
            cursor = page[^1].Id;
        }
        foreach (var job in await store.PendingAsync(ct))
        {
            if (!seen.Add(job.TaskId.ToString())) continue;
            var task = await api.GetAsync(job.TaskId.ToString(), job.UserId.ToString(), ct);
            if (task is null) await store.CancelAsync(job.TaskId.ToString(), job.UserId.ToString(), ct);
            else await store.PlanAsync(task, ct);
        }
    }
}

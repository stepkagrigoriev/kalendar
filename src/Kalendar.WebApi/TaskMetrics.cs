using MongoDB.Bson;
using MongoDB.Driver;
using Prometheus;

namespace Kalendar.WebApi;

sealed class TaskMetrics(IMongoDatabase database, ILogger<TaskMetrics> logger) : BackgroundService
{
    static readonly Gauge Pending = Metrics.CreateGauge("tasks_pending_total", "Количество незавершённых задач.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await database.GetCollection<BsonDocument>("tasks").CountDocumentsAsync(new BsonDocument("status", "open"), cancellationToken: stoppingToken);
                Pending.Set(count);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Не удалось обновить метрики задач: {ErrorType}", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}

using MongoDB.Bson;
using MongoDB.Driver;
using Prometheus;

namespace Kalendar.Worker;

sealed class JobMetrics(IMongoDatabase database, ILogger<JobMetrics> logger) : BackgroundService
{
    static readonly Gauge Jobs = Metrics.CreateGauge("worker_background_jobs", "Количество фоновых заданий по статусам.",
        new GaugeConfiguration { LabelNames = ["status"] });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var status in new[] { "pending", "processing", "completed", "cancelled", "failed" })
                {
                    var count = await database.GetCollection<BsonDocument>("backgroundJobs").CountDocumentsAsync(new BsonDocument("status", status), cancellationToken: stoppingToken);
                    Jobs.WithLabels(status).Set(count);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Не удалось обновить метрики заданий: {ErrorType}", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}

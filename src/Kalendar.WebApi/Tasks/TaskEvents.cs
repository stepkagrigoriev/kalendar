using Kalendar.Contracts;
using MassTransit;

namespace Kalendar.WebApi.Tasks;

sealed class TaskEvents(IPublishEndpoint publisher, ILogger<TaskEvents> logger, TimeProvider clock)
{
    public async Task ChangedAsync(TaskDocument task, CancellationToken ct)
    {
        try
        {
            await publisher.Publish(new TaskChanged(task.Id.ToString(), task.UserId.ToString(), clock.GetUtcNow()), ct);
        }
        catch (Exception exception)
        {
            logger.LogError("Не удалось опубликовать TaskChanged для {TaskId}: {ErrorType}", task.Id, exception.GetType().Name);
        }
    }

    public async Task DeletedAsync(string taskId, string userId, CancellationToken ct)
    {
        try
        {
            await publisher.Publish(new TaskDeleted(taskId, userId, clock.GetUtcNow()), ct);
        }
        catch (Exception exception)
        {
            logger.LogError("Не удалось опубликовать TaskDeleted для {TaskId}: {ErrorType}", taskId, exception.GetType().Name);
        }
    }
}

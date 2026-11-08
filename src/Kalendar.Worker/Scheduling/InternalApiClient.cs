using System.Net;

namespace Kalendar.Worker.Scheduling;

sealed class InternalApiClient(HttpClient client)
{
    public async Task<ScheduledTask> GetAsync(string taskId, string userId, CancellationToken ct)
    {
        using var response = await client.GetAsync($"/internal/tasks/{Uri.EscapeDataString(taskId)}?userId={Uri.EscapeDataString(userId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScheduledTask>(ct);
    }

    public async Task<ScheduledTask[]> ScheduledAsync(string after, CancellationToken ct)
    {
        using var response = await client.GetAsync($"/internal/tasks/scheduled?limit=500&after={Uri.EscapeDataString(after)}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScheduledTask[]>(ct) ?? [];
    }

    public async Task ConfirmAsync(BackgroundJob job, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync($"/internal/tasks/{job.TaskId}/reminder-sent",
            new { userId = job.UserId.ToString(), reminderAt = job.TargetAt }, ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }

    public async Task<bool> RepeatAsync(BackgroundJob job, DateTime dueAt, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync($"/internal/tasks/{job.TaskId}/recurrence-instances",
            new { userId = job.UserId.ToString(), dueAt, job.IdempotencyKey }, ct);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ark.Sync;

/// <summary>
/// Profile 定时调度：每 30s 扫描启用调度的 Profile，cron 命中即触发运行。
/// 错过触发（服务关闭期间）不补跑，仅记录日志。同一 Profile 上一个任务未结束时跳过。
/// </summary>
public sealed class SyncScheduler(
    ISyncProfileStore profiles,
    ISyncSchedulerCallbacks callbacks,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("同步调度器已启动（间隔 {Interval}s）", Tick.TotalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "调度扫描失败");
            }
            try
            {
                await Task.Delay(Tick, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        logger.LogInformation("同步调度器已停止");
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        foreach (var p in await profiles.ListProfilesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!p.ScheduleEnabled || string.IsNullOrWhiteSpace(p.Cron)) continue;

            DateTimeOffset next;
            try
            {
                next = CronExpression.Parse(p.Cron).NextOccurrence(p.LastRunAt ?? p.CreatedAt);
            }
            catch (FormatException ex)
            {
                logger.LogWarning("Profile {Name} 的 cron 表达式无效: {Message}", p.Name, ex.Message);
                continue;
            }
            if (next > now) continue;

            if (await callbacks.TryRunProfileAsync(p.Id, p.Name))
            {
                logger.LogInformation("调度触发 Profile {Name}（cron: {Cron}）", p.Name, p.Cron);
                await profiles.SetLastRunAsync(p.Id, now, ct);
            }
            else
            {
                logger.LogWarning("Profile {Name} 触发失败（连接缺失或上一任务仍在运行）", p.Name);
            }
        }
    }
}

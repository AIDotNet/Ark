using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ark.Api.Infrastructure;
using Ark.Core.Errors;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;
using Ark.Sync;

namespace Ark.Api.Endpoints;

/// <summary>端点共享的 Provider 解析与序列化工具。</summary>
public static class SyncApi
{
    public static (ConnectionSpec Source, ConnectionSpec Target) ResolveSpecs(
        SyncPlanRequest req, ArkRepository repo)
    {
        var source = repo.GetConnection(req.SourceConnectionId) ?? throw ArkException.NotFound("源连接不存在");
        var target = repo.GetConnection(req.TargetConnectionId) ?? throw ArkException.NotFound("目标连接不存在");
        return (source.ToSpec(repo.Pw()), target.ToSpec(repo.Pw()));
    }

    public static (ConnectionSpec Source, ConnectionSpec Target) ResolveSpecs(
        CompareRequest req, ArkRepository repo)
    {
        var source = repo.GetConnection(req.SourceConnectionId) ?? throw ArkException.NotFound("源连接不存在");
        var target = repo.GetConnection(req.TargetConnectionId) ?? throw ArkException.NotFound("目标连接不存在");
        return (source.ToSpec(repo.Pw()), target.ToSpec(repo.Pw()));
    }

    public static readonly JsonSerializerOptions JsonOpts = ArkSyncStore.JsonOpts;

    /// <summary>
    /// SSE 推送任务状态直到终态或客户端断开。
    /// 事件驱动：任务线程每次写日志/进度即发信号唤醒本推送循环（不再轮询），
    /// 150ms 合并窗口聚合并行表分支的连续信号，空闲 15s 发心跳注释防中间层闲置断连。
    /// </summary>
    public static async Task PumpTaskEventsAsync(HttpContext http, SyncTaskManager mgr, Guid taskId, CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.WriteAsync("retry: 2000\n\n", ct);
        await http.Response.Body.FlushAsync(ct);

        using var sub = mgr.Subscribe(taskId);
        string? lastJson = null;
        try
        {
            while (!ct.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
            {
                var snap = await mgr.GetDtoAsync(taskId, ct);
                if (snap is null)
                {
                    await http.Response.WriteAsync($"event: error\ndata: task {taskId} not found\n\n", ct);
                    break;
                }
                var json = JsonSerializer.Serialize(snap.Value.Dto, JsonOpts);
                if (json != lastJson)
                {
                    lastJson = json;
                    await http.Response.WriteAsync($"event: state\ndata: {json}\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
                if (snap.Value.Status is SyncTaskStatus.Completed or SyncTaskStatus.Failed
                    or SyncTaskStatus.Cancelled or SyncTaskStatus.PartiallyFailed or SyncTaskStatus.Interrupted)
                    break;

                var heartbeat = Task.Delay(15000, ct);
                var signal = sub.WhenChangedAsync(ct);
                if (await Task.WhenAny(signal, heartbeat) == heartbeat)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
                else
                {
                    // 合并窗口：并行表分支的连续信号聚齐后再取快照，避免逐帧全量推送
                    await Task.Delay(150, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或服务停机
        }
        catch (ChannelClosedException)
        {
            // 任务结束清理信号通道
        }
    }

    /// <summary>导出执行计划中实际执行的 SQL 为脚本。</summary>
    public static string BuildScript(ExecutionPlan plan, ISet<string> skipActionIds, bool confirmDestructive)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"-- Ark 同步脚本（计划 {plan.Id:N}，生成于 {plan.CreatedAt:O}）");
        sb.AppendLine($"-- {plan.Options.SourceDatabase} → {plan.Options.TargetDatabase}");
        sb.AppendLine();
        foreach (var te in plan.Tables)
        {
            var actions = te.StructureActions
                .Concat(te.PostCopyActions)
                .Where(a => !skipActionIds.Contains(a.Id))
                .Where(a => confirmDestructive || !a.IsDestructive)
                .ToList();
            if (actions.Count == 0) continue;
            sb.AppendLine($"-- ===== {te.Target.Table} =====");
            foreach (var a in actions)
            {
                foreach (var w in a.Warnings) sb.AppendLine($"-- ! {w}");
                sb.AppendLine(a.Sql.TrimEnd(';') + ";");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/sync").WithTags("Sync");

        // 生成同步计划快照（预览；执行时原样提交该快照，零重规划）
        g.MapPost("/plan", async (SyncPlanRequest req, ArkRepository repo, CancellationToken ct) =>
        {
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(req, repo);
            await using var source = ProviderRegistry.Create(srcSpec);
            await using var target = ProviderRegistry.Create(tgtSpec);
            return await new SyncPlanner().BuildPlanAsync(source, target, req, ct);
        });

        // 提交执行（快照原样执行）
        g.MapPost("/tasks", (SyncExecuteRequest req, ArkRepository repo, SyncTaskManager mgr) =>
        {
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(req.Plan.Options, repo);
            var state = mgr.SubmitSync(req, null, srcSpec, tgtSpec, ProviderRegistry.Create);
            return state.ToDto();
        });

        g.MapGet("/tasks", async (SyncTaskManager mgr,
            TaskKind? kind, SyncTaskStatus? status, Guid? profileId,
            int limit = 50, int offset = 0, CancellationToken ct = default) =>
        {
            var list = await mgr.ListAsync(kind, status, profileId,
                limit == 0 ? 50 : Math.Clamp(limit, 1, 200), Math.Max(0, offset), ct);
            return list.Select(t => t.State.ToDto()).ToList();
        });

        g.MapGet("/tasks/{taskId:guid}", async (Guid taskId, SyncTaskManager mgr, CancellationToken ct) =>
        {
            var stored = await mgr.GetStoredAsync(taskId, ct)
                         ?? throw ArkException.NotFound($"任务 {taskId} 不存在");
            return stored.State.ToDto();
        });

        g.MapPost("/tasks/{taskId:guid}/cancel", (Guid taskId, SyncTaskManager mgr) =>
        {
            mgr.Cancel(taskId);
            return new { ok = true };
        });

        g.MapPost("/tasks/{taskId:guid}/resume", async (
            Guid taskId, ArkRepository repo, SyncTaskManager mgr, CancellationToken ct) =>
        {
            var stored = await mgr.GetStoredAsync(taskId, ct)
                         ?? throw ArkException.NotFound($"任务 {taskId} 不存在");
            if (stored.Plan is null) throw ArkException.Validation("任务缺少计划快照，无法续传");
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(stored.Plan.Options, repo);
            return mgr.SubmitResume(stored, srcSpec, tgtSpec, ProviderRegistry.Create, resume: true).ToDto();
        });

        g.MapPost("/tasks/{taskId:guid}/retry", async (
            Guid taskId, ArkRepository repo, SyncTaskManager mgr, CancellationToken ct) =>
        {
            var stored = await mgr.GetStoredAsync(taskId, ct)
                         ?? throw ArkException.NotFound($"任务 {taskId} 不存在");
            if (stored.Plan is null) throw ArkException.Validation("任务缺少计划快照，无法重试");
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(stored.Plan.Options, repo);
            return mgr.SubmitResume(stored, srcSpec, tgtSpec, ProviderRegistry.Create, resume: false).ToDto();
        });

        // SSE 进度流
        g.MapGet("/tasks/{taskId:guid}/events", [Api.Filters.SkipEnvelope] async (
            HttpContext http, Guid taskId, SyncTaskManager mgr, CancellationToken ct) =>
            await SyncApi.PumpTaskEventsAsync(http, mgr, taskId, ct));

        // 导出实际执行的 SQL 脚本（跳过包络）
        g.MapGet("/tasks/{taskId:guid}/export-script", [Api.Filters.SkipEnvelope] async (
            HttpContext http, Guid taskId, SyncTaskManager mgr, CancellationToken ct) =>
        {
            var stored = await mgr.GetStoredAsync(taskId, ct)
                         ?? throw ArkException.NotFound($"任务 {taskId} 不存在");
            if (stored.Plan is null || stored.Request is null)
                throw ArkException.Validation("任务缺少计划快照");
            var script = SyncApi.BuildScript(stored.Plan, new HashSet<string>(stored.Request.SkipActionIds), stored.Request.ConfirmDestructive);
            http.Response.ContentType = "application/sql; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=ark-sync-{taskId:N}.sql";
            await http.Response.WriteAsync(script, ct);
        });

        return app;
    }
}

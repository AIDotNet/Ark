using System.Text.Json;
using Ark.Api.Endpoints;
using Ark.Api.Infrastructure;
using Ark.Core.Sync;
using Ark.Sync;

namespace Ark.Api.Services;

/// <summary>调度回调：解析 Profile 配置 → 生成计划快照 → 提交任务。</summary>
public sealed class SchedulerCallbacks(
    ArkRepository repo,
    ISyncProfileStore profiles,
    ISyncTaskStore tasks,
    SyncTaskManager mgr,
    ILogger<SchedulerCallbacks> logger) : ISyncSchedulerCallbacks
{
    public async Task<bool> TryRunProfileAsync(Guid profileId, string name)
    {
        try
        {
            var profile = await profiles.GetProfileAsync(profileId);
            if (profile is null) return false;
            if (await tasks.CountActiveByProfileAsync(profileId) > 0)
            {
                logger.LogWarning("Profile {Name} 上一个任务仍在运行，跳过本次触发", name);
                return false;
            }
            var config = JsonSerializer.Deserialize<SyncPlanRequest>(profile.ConfigJson, SyncApi.JsonOpts);
            if (config is null)
            {
                logger.LogError("Profile {Name} 配置无法解析", name);
                return false;
            }
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(config, repo);
            await using var source = ProviderRegistry.Create(srcSpec);
            await using var target = ProviderRegistry.Create(tgtSpec);
            var plan = await new SyncPlanner().BuildPlanAsync(source, target, config);
            mgr.SubmitSync(new SyncExecuteRequest { Plan = plan, ConfirmDestructive = false }, profileId, srcSpec, tgtSpec,
                ProviderRegistry.Create);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "调度触发 Profile {Name} 失败", name);
            return false;
        }
    }

    public Task RecordScheduleMissedAsync(Guid profileId, string name)
    {
        logger.LogWarning("Profile {Name} 错过调度触发（服务未运行期间不补跑）", name);
        return Task.CompletedTask;
    }
}

using System.Text.Json;
using Ark.Api.Infrastructure;
using Ark.Core.Errors;
using Ark.Core.Sync;
using Ark.Sync;

namespace Ark.Api.Endpoints;

/// <summary>同步 Profile（配置存档 + 调度）CRUD 与运行。</summary>
public static class ProfileEndpoints
{
    public sealed record SaveProfileRequest(string Name, SyncPlanRequest Config, string? Cron, bool ScheduleEnabled);

    public sealed record ScheduleRequest(string? Cron, bool ScheduleEnabled);

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/sync/profiles").WithTags("SyncProfiles");

        g.MapGet("/", async (ISyncProfileStore store, CancellationToken ct) =>
            (await store.ListProfilesAsync(ct)).Select(ToDto).ToList());

        g.MapPost("/", async (SaveProfileRequest req, ISyncProfileStore store, CancellationToken ct) =>
        {
            ValidateCron(req.Cron, req.ScheduleEnabled);
            var now = DateTimeOffset.UtcNow;
            var entity = new SyncProfileEntity
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                ConfigJson = JsonSerializer.Serialize(req.Config, SyncApi.JsonOpts),
                Cron = req.Cron,
                ScheduleEnabled = req.ScheduleEnabled,
                CreatedAt = now,
                UpdatedAt = now,
            };
            return ToDto(await store.SaveProfileAsync(entity, ct));
        });

        g.MapPut("/{profileId:guid}", async (
            Guid profileId, SaveProfileRequest req, ISyncProfileStore store, CancellationToken ct) =>
        {
            var existing = await store.GetProfileAsync(profileId, ct)
                           ?? throw ArkException.NotFound($"Profile {profileId} 不存在");
            ValidateCron(req.Cron, req.ScheduleEnabled);
            var updated = existing with
            {
                Name = req.Name,
                ConfigJson = JsonSerializer.Serialize(req.Config, SyncApi.JsonOpts),
                Cron = req.Cron,
                ScheduleEnabled = req.ScheduleEnabled,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            return ToDto(await store.SaveProfileAsync(updated, ct));
        });

        g.MapPatch("/{profileId:guid}/schedule", async (
            Guid profileId, ScheduleRequest req, ISyncProfileStore store, CancellationToken ct) =>
        {
            var existing = await store.GetProfileAsync(profileId, ct)
                           ?? throw ArkException.NotFound($"Profile {profileId} 不存在");
            ValidateCron(req.Cron, req.ScheduleEnabled);
            var updated = existing with
            {
                Cron = req.Cron,
                ScheduleEnabled = req.ScheduleEnabled,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            return ToDto(await store.SaveProfileAsync(updated, ct));
        });

        g.MapDelete("/{profileId:guid}", async (Guid profileId, ISyncProfileStore store, CancellationToken ct) =>
        {
            await store.DeleteProfileAsync(profileId, ct);
            return new { ok = true };
        });

        // 运行：用 Profile 配置重新生成计划快照并提交
        g.MapPost("/{profileId:guid}/run", async (
            Guid profileId, ISyncProfileStore store, ISyncTaskStore tasks, ArkRepository repo, SyncTaskManager mgr,
            CancellationToken ct) =>
        {
            var profile = await store.GetProfileAsync(profileId, ct)
                          ?? throw ArkException.NotFound($"Profile {profileId} 不存在");
            if (await tasks.CountActiveByProfileAsync(profileId, ct) > 0)
                throw ArkException.Validation("该 Profile 上一个任务仍在运行");
            var config = JsonSerializer.Deserialize<SyncPlanRequest>(profile.ConfigJson, SyncApi.JsonOpts)
                         ?? throw ArkException.Validation("Profile 配置无法解析");
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(config, repo);
            await using var source = ProviderRegistry.Create(srcSpec);
            await using var target = ProviderRegistry.Create(tgtSpec);
            var plan = await new SyncPlanner().BuildPlanAsync(source, target, config, ct);
            var request = new SyncExecuteRequest { Plan = plan, ConfirmDestructive = false };
            return mgr.SubmitSync(request, profileId, srcSpec, tgtSpec, ProviderRegistry.Create).ToDto();
        });

        return app;
    }

    private static void ValidateCron(string? cron, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(cron)) return;
        try
        {
            CronExpression.Parse(cron);
        }
        catch (FormatException ex)
        {
            throw ArkException.Validation($"cron 表达式无效: {ex.Message}");
        }
    }

    internal static object ToDto(SyncProfileEntity e)
    {
        string? nextRun = null;
        if (e.ScheduleEnabled && !string.IsNullOrWhiteSpace(e.Cron))
        {
            try
            {
                nextRun = CronExpression.Parse(e.Cron)
                    .NextOccurrence(e.LastRunAt ?? DateTimeOffset.Now).ToString("O");
            }
            catch
            {
                // 无效 cron 由编辑接口拦截；列表展示不阻塞
            }
        }
        return new
        {
            id = e.Id,
            name = e.Name,
            config = JsonSerializer.Deserialize<JsonElement>(e.ConfigJson),
            cron = e.Cron,
            scheduleEnabled = e.ScheduleEnabled,
            nextRun,
            lastRunAt = e.LastRunAt,
            createdAt = e.CreatedAt,
            updatedAt = e.UpdatedAt,
        };
    }
}

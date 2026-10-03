using Ark.Api.Endpoints;
using Ark.Api.Filters;
using Ark.Api.Infrastructure;
using Ark.Api.Services;
using Ark.Sync;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.OpenApi;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// 枚举以字符串形式序列化/反序列化（如 dialect: "PostgreSQL"）
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// 数据目录（仓库根/data）
var dataDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "..", "data"));
Directory.CreateDirectory(dataDir);

// Data Protection：密钥默认持久化到用户目录（~/.aspnet/DataProtection-Keys），固定应用名保证跨重启可解密
builder.Services.AddDataProtection().SetApplicationName("Ark");
builder.Services.AddSingleton<PasswordProtector>();
builder.Services.AddSingleton(sp =>
{
    var pw = sp.GetRequiredService<PasswordProtector>();
    return new ArkRepository(Path.Combine(dataDir, "ark.db"), pw);
});
builder.Services.AddSingleton<ArkSyncStore>();
builder.Services.AddSingleton<ISyncTaskStore>(sp => sp.GetRequiredService<ArkSyncStore>());
builder.Services.AddSingleton<ISyncProfileStore>(sp => sp.GetRequiredService<ArkSyncStore>());
builder.Services.AddSingleton<SyncTaskManager>(sp =>
    new SyncTaskManager(sp.GetRequiredService<ISyncTaskStore>(), maxConcurrent: 2));
builder.Services.AddSingleton<ISyncSchedulerCallbacks, SchedulerCallbacks>();
builder.Services.AddHostedService<SyncScheduler>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AiAssistantService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseCors();

// 前端 SPA（wwwroot，容器镜像构建时由 frontend/dist 拷入；开发环境走 vite dev server，无 wwwroot 时自动跳过）
app.UseDefaultFiles();
app.UseStaticFiles();

// 全局兜底异常处理（/api）：请求体 JSON 反序列化失败发生在端点过滤器之前，
// UnifiedResponseFilter 捕获不到 → 这里转换为统一包络；其余未捕获异常也不得泄漏堆栈
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        var (code, status, message) = ex switch
        {
            BadHttpRequestException or System.Text.Json.JsonException =>
                (Ark.Core.Responses.ArkErrorCodes.Validation, 400, "请求体无效：JSON 语法错误或字段类型/结构不匹配"),
            Ark.Core.Errors.ArkException ark => (ark.Code, ark.HttpStatus, ark.Message),
            _ => (Ark.Core.Responses.ArkErrorCodes.Unknown, 500, "服务器内部错误"),
        };
        // Web 默认（camelCase）与端点包络保持一致；错误路径不依赖 DI，避免二次异常
        var jsonOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(
                new Ark.Core.Responses.ApiEnvelope<object>(code, message, null, context.TraceIdentifier, DateTimeOffset.UtcNow),
                jsonOpts));
    }
});

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// /api 根组：统一响应包络（最外层） + 请求校验（内层）
var api = app.MapGroup("/api")
    .AddEndpointFilter<UnifiedResponseFilter>()
    .AddEndpointFilter<ValidationFilter>();

api.MapConnectionEndpoints();
api.MapMetadataEndpoints();
api.MapTableEndpoints();
api.MapRowEndpoints();
api.MapQueryEndpoints();
api.MapSyncEndpoints();
api.MapCompareEndpoints();
api.MapProfileEndpoints();
api.MapImportExportEndpoints();
api.MapSettingsEndpoints();

// 启动恢复：遗留的 Running/Queued 任务标记为 Interrupted（可续传）
await app.Services.GetRequiredService<ISyncTaskStore>().MarkStartupInterruptedAsync();

app.Run();

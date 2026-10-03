using System.Text;
using Ark.Api.Infrastructure;
using Ark.Api.Services;
using Ark.Core.Dtos;
using Ark.Core.Errors;
using Ark.Core.Querying;

namespace Ark.Api.Endpoints;

/// <summary>AI 设置（/settings/ai）与 AI 助手代理端点（/ai/*）。</summary>
public static class SettingsEndpoints
{
    private const string SystemSql =
        "你是资深 SQL 工程师。根据用户给出的数据库 schema 与自然语言需求，输出一条可直接执行的 SQL。" +
        "严格遵守目标方言语法；只输出 SQL 代码本身，不要 Markdown 代码块标记，不要任何解释。";

    private const string SystemExplain =
        "你是数据库专家。用户给出一条 SQL 与表结构，请用中文简明解释：这条 SQL 做什么、可能的性能问题与优化建议。" +
        "用要点列表，直接给结论；若涉及改写优化，给出优化后的 SQL。";

    private const string SystemFix =
        "你是 SQL 调试专家。用户给出一条执行失败的 SQL、错误信息与表结构，请定位原因并给出修正后的 SQL。" +
        "按以下格式回答：第一行原是「原因：<中文原因>」，随后一行「修正后的 SQL：」，最后一行只放修正后的 SQL（无代码块标记）。";

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("AI 与设置");

        g.MapGet("/settings/ai", (AiAssistantService ai) => ai.Load());

        g.MapPut("/settings/ai", (SaveAiSettingsRequest req, AiAssistantService ai) => ai.Save(req));

        g.MapPost("/ai/test", async (AiAssistantService ai, CancellationToken ct) =>
        {
            try
            {
                var text = await ai.ChatAsync([("user", "请只回复两个字：正常")], ct);
                return new AiTestResponse(true, string.IsNullOrWhiteSpace(text) ? "（空回复）" : text.Trim());
            }
            catch (ArkException ex)
            {
                return new AiTestResponse(false, ex.Message);
            }
            catch (Exception ex)
            {
                return new AiTestResponse(false, ex.Message);
            }
        });

        g.MapPost("/ai/sql", async (AiSqlRequest req, ArkRepository repo, AiAssistantService ai, CancellationToken ct) =>
        {
            var schema = await BuildSchemaContextAsync(req.ConnectionId, req.Database, req.Schema, repo, ct);
            var text = await ai.ChatAsync(
            [
                ("system", SystemSql),
                ("user", $"数据库 schema：\n{schema}\n\n需求：{req.Prompt}"),
            ], ct);
            return new AiTextResponse(StripCodeFence(text));
        });

        g.MapPost("/ai/explain", async (AiExplainRequest req, ArkRepository repo, AiAssistantService ai, CancellationToken ct) =>
        {
            var schema = await BuildSchemaContextAsync(req.ConnectionId, req.Database, req.Schema, repo, ct);
            var text = await ai.ChatAsync(
            [
                ("system", SystemExplain),
                ("user", $"表结构：\n{schema}\n\nSQL：\n{req.Sql}"),
            ], ct);
            return new AiTextResponse(text);
        });

        g.MapPost("/ai/fix", async (AiFixRequest req, ArkRepository repo, AiAssistantService ai, CancellationToken ct) =>
        {
            var schema = await BuildSchemaContextAsync(req.ConnectionId, req.Database, req.Schema, repo, ct);
            var text = await ai.ChatAsync(
            [
                ("system", SystemFix),
                ("user", $"表结构：\n{schema}\n\nSQL：\n{req.Sql}\n\n错误信息：\n{req.Error ?? "（未提供）"}"),
            ], ct);
            return new AiTextResponse(text);
        });

        return app;
    }

    /// <summary>拉取连接的补全元数据并压缩为文本上下文（截断至 ~8k 字符防 prompt 爆炸）。</summary>
    private static async Task<string> BuildSchemaContextAsync(
        Guid connectionId, string database, string? schema, ArkRepository repo, CancellationToken ct)
    {
        var e = repo.GetConnection(connectionId) ?? throw ArkException.NotFound($"连接 {connectionId} 不存在");
        await using var provider = ProviderRegistry.Create(e, repo.Pw());
        var cs = await provider.GetCompletionSchemaAsync(database, schema, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"数据库方言: {e.Dialect}");
        foreach (var t in cs.Tables)
        {
            sb.Append($"- {t.Name}{(t.Kind == "view" ? "（视图）" : "")}: ");
            sb.AppendLine(string.Join(", ", t.Columns.Select(c => $"{c.Name} {c.DataType}")));
        }
        var text = sb.ToString();
        return text.Length <= 8000 ? text : text[..8000] + "\n…（schema 过长已截断）";
    }

    /// <summary>剥掉模型偶尔添加的 ```sql 围栏。</summary>
    private static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;
        var firstBreak = t.IndexOf('\n');
        if (firstBreak < 0) return t;
        t = t[(firstBreak + 1)..];
        var endIdx = t.LastIndexOf("```", StringComparison.Ordinal);
        if (endIdx >= 0) t = t[..endIdx];
        return t.Trim();
    }
}

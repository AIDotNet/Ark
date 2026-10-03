using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ark.Api.Infrastructure;
using Ark.Core.Dtos;
using Ark.Core.Errors;
using Ark.Core.Responses;

namespace Ark.Api.Services;

/// <summary>
/// AI 助手：设置存取（settings 表，API Key 经 Data Protection 加密，GET 永不回明文）
/// + OpenAI 兼容 /chat/completions 调用（非流式）。
/// </summary>
public sealed class AiAssistantService(ArkRepository repo, PasswordProtector pw, IHttpClientFactory httpFactory)
{
    private const string SettingsKey = "ai";
    private const string DefaultBaseUrl = "https://api.openai.com/v1";
    private const string DefaultModel = "gpt-4o-mini";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ArkRepository _repo = repo;
    private readonly PasswordProtector _pw = pw;
    private readonly IHttpClientFactory _httpFactory = httpFactory;

    private sealed record AiSettingsRecord(bool Enabled, string BaseUrl, string Model, string? ApiKeyEnc);

    public AiSettingsDto Load()
    {
        var s = ReadRecord();
        return new AiSettingsDto(s.Enabled, s.BaseUrl, s.Model, s.ApiKeyEnc is not null);
    }

    public AiSettingsDto Save(SaveAiSettingsRequest req)
    {
        var existing = ReadRecord();
        var apiKeyEnc = req.ApiKey switch
        {
            null => existing.ApiKeyEnc,   // 未提交 → 保持不变
            "" => null,                   // 空字符串 → 清除
            var k => _pw.Protect(k),      // 其他 → 重新加密存储
        };
        var record = new AiSettingsRecord(
            req.Enabled,
            string.IsNullOrWhiteSpace(req.BaseUrl) ? existing.BaseUrl : req.BaseUrl.TrimEnd('/'),
            string.IsNullOrWhiteSpace(req.Model) ? existing.Model : req.Model.Trim(),
            apiKeyEnc);
        _repo.SetSetting(SettingsKey, JsonSerializer.Serialize(record, WebJson));
        return Load();
    }

    /// <summary>调用 Chat Completions（OpenAI 兼容），返回首条回复文本。</summary>
    public async Task<string> ChatAsync(IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct = default)
    {
        var s = ReadRecord();
        if (!s.Enabled)
            throw new ArkException(ArkErrorCodes.AiNotConfigured, "AI 助手未启用，请先在设置中开启并填写接口配置", 400);
        if (string.IsNullOrWhiteSpace(s.BaseUrl))
            throw new ArkException(ArkErrorCodes.AiNotConfigured, "AI 接口 Base URL 未配置", 400);

        var http = _httpFactory.CreateClient("ark-ai");
        using var req = new HttpRequestMessage(HttpMethod.Post, s.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = s.Model,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                temperature = 0.2,
                stream = false,
            }),
        };
        var apiKey = _pw.Unprotect(s.ApiKeyEnc);
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new ArkException(ArkErrorCodes.AiFailed,
                $"AI 接口返回 HTTP {(int)res.StatusCode}：{Compact(body, 300)}", 502);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (Exception)
        {
            throw new ArkException(ArkErrorCodes.AiFailed, $"AI 接口响应格式无法解析：{Compact(body, 300)}", 502);
        }
    }

    private AiSettingsRecord ReadRecord()
    {
        var raw = _repo.GetSetting(SettingsKey);
        if (raw is null) return new AiSettingsRecord(false, DefaultBaseUrl, DefaultModel, null);
        return JsonSerializer.Deserialize<AiSettingsRecord>(raw, WebJson)
               ?? new AiSettingsRecord(false, DefaultBaseUrl, DefaultModel, null);
    }

    private static string Compact(string s, int max) => s.Length <= max ? s.Replace('\n', ' ') : s[..max].Replace('\n', ' ') + "…";
}

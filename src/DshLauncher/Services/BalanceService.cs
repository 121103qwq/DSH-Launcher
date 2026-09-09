using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshLauncher.Services;

public sealed record BalanceSnapshot(
    string Currency,
    string Total,
    string Granted,
    string ToppedUp,
    bool Available);

public sealed record BalanceResult(bool Ok, BalanceSnapshot? Data, string? Error);

/// <summary>
/// DeepSeek 余额查询（借鉴 MarcoG-h/DSH-Launcher 的 balance.ts，思路借鉴）。
///
/// 安全约束：凭据只在内存中读取使用，不落盘、不写日志、不进诊断包；
/// 该能力默认关闭，需用户在 设置/诊断 → 余额显示 中显式启用。
/// 读取来源：所选实例 DSH_HOME 下的 .credentials.yaml（dsh 自身存放 API Key 的位置）。
/// </summary>
public sealed class BalanceService
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly Regex ApiKeyPattern = new(
        @"^\s*DEEPSEEK_API_KEY\s*[:=]\s*[""']?([^""'\r\n]+)[""']?\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public async Task<BalanceResult> GetAsync(string dshHome, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dshHome) || !Directory.Exists(dshHome))
        {
            return new BalanceResult(false, null, "实例 DSH_HOME 不存在，无法读取凭据。");
        }

        var key = TryReadApiKey(Path.Combine(dshHome, ".credentials.yaml"));
        if (string.IsNullOrWhiteSpace(key))
        {
            return new BalanceResult(false, null, "未找到 DEEPSEEK_API_KEY（请在 DSH 界面配置，或确认 .credentials.yaml 存在）。");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                LauncherLog.Warn("余额接口返回非成功状态。", ErrorCodes.E3002, new { status = code });
                return new BalanceResult(false, null, $"余额接口返回 HTTP {code}。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.TryGetProperty("balance_infos", out var infos)
                || infos.ValueKind != JsonValueKind.Array
                || infos.GetArrayLength() == 0)
            {
                return new BalanceResult(false, null, "余额响应缺少 balance_infos。");
            }

            var info = infos[0];
            var snapshot = new BalanceSnapshot(
                GetString(info, "currency") ?? "CNY",
                GetString(info, "total_balance") ?? "-",
                GetString(info, "granted_balance") ?? "-",
                GetString(info, "topped_up_balance") ?? "-",
                root.TryGetProperty("is_available", out var available) && available.ValueKind == JsonValueKind.True);
            return new BalanceResult(true, snapshot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            LauncherLog.Warn("余额查询失败。", ErrorCodes.E3002, new { error = ex.Message });
            return new BalanceResult(false, null, "余额查询失败：" + ex.Message);
        }
    }

    private static string? TryReadApiKey(string credentialsPath)
    {
        try
        {
            if (!File.Exists(credentialsPath))
            {
                return null;
            }

            var match = ApiKeyPattern.Match(File.ReadAllText(credentialsPath));
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Warn("读取凭据文件失败（仅记录路径与原因，不记录内容）。", ErrorCodes.E3002,
                new { path = credentialsPath, error = ex.Message });
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

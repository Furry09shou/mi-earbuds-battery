using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MiEarbuds.App.Core;

/// <summary>GitHub Releases 更新检查。</summary>
public sealed record UpdateInfo(string Version, string Url);

public static class UpdateChecker
{
    public const string ReleasesUrl = "https://github.com/Furry09shou/mi-earbuds-battery/releases";
    private const string LatestApi = "https://api.github.com/repos/Furry09shou/mi-earbuds-battery/releases/latest";

    /// <summary>返回新版本信息；已是最新或解析失败返回 null，网络异常抛出。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MiEarbuds-App");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var resp = await http.GetAsync(LatestApi, ct);
        if (!resp.IsSuccessStatusCode) return null;   // 404 = 仓库还没有任何 Release
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
        var url = doc.RootElement.TryGetProperty("html_url", out var u)
            ? u.GetString() ?? ReleasesUrl
            : ReleasesUrl;

        if (!Version.TryParse(tag, out var remote)) return null;
        var local = typeof(UpdateChecker).Assembly.GetName().Version!;
        return remote > local ? new UpdateInfo(remote.ToString(), url) : null;
    }
}

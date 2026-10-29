using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DiskWriteMonitor.Core;

/// <summary>
/// 每天一次检查 GitHub Release（对应 macOS v2.4 的行为）。
/// 除这里之外，应用不发起任何网络请求。
/// </summary>
public sealed class UpdateChecker
{
    public sealed record Release(string Version, string Url);

    private static readonly HttpClient Http = CreateClient();

    private readonly AppSettings _settings;
    private readonly object _gate = new();

    public Release? Available { get; private set; }
    public string? DismissedVersion { get; private set; }

    /// <summary>有新版本或忽略状态变化时触发，由界面刷新提示条。</summary>
    public event Action? Changed;

    public UpdateChecker(AppSettings settings)
    {
        _settings = settings;
        DismissedVersion = settings.DismissedVersion;
    }

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) return "0.0.0";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    public bool ShouldShowBanner =>
        Available is not null && !string.Equals(Available.Version, DismissedVersion, StringComparison.Ordinal);

    /// <summary>启动时调用：距上次检查不足 24 小时就不发请求。</summary>
    public void CheckIfDue()
    {
        var last = _settings.LastUpdateCheck;
        if (last is not null && DateTime.Now - last.Value < TimeSpan.FromHours(24)) return;
        _ = CheckAsync();
    }

    public async Task CheckAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var resp = await Http.GetAsync(
                "https://api.github.com/repos/ArchMageLaLa/disk-write-monitor-windows/releases/latest", cts.Token);

            if (!resp.IsSuccessStatusCode) return;

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProp)) return;
            if (!root.TryGetProperty("html_url", out var urlProp)) return;

            var tag = tagProp.GetString() ?? string.Empty;
            var url = urlProp.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(url)) return;

            // 兜底：Windows 版的 release 必须带 .exe 资产。
            // 曾因仓库地址错写成 macOS 版（disk-write-monitor），mac 发 2.5.1 时这里误报了更新。
            // 只要有资产却都不是 .exe，就当不是本平台的发布，直接忽略。
            // 资产还没传完（空列表）时不做拦截，避免刚建 Release 的瞬间漏掉。
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                if (assetsProp.GetArrayLength() > 0)
                {
                    var hasExe = assetsProp.EnumerateArray().Any(a =>
                        a.TryGetProperty("name", out var nameProp) &&
                        (nameProp.GetString() ?? string.Empty)
                            .EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (!hasExe) return;
                }
            }

            var version = tag.StartsWith('v') ? tag[1..] : tag;

            _settings.LastUpdateCheck = DateTime.Now;
            _settings.Save();

            if (IsNewer(version, CurrentVersion))
            {
                lock (_gate) Available = new Release(version, url);
                Changed?.Invoke();
            }
        }
        catch
        {
            // 断网、超时、限流都属于正常情况，静默失败
        }
    }

    public void Dismiss()
    {
        var version = Available?.Version;
        if (version is null) return;

        DismissedVersion = version;
        _settings.DismissedVersion = version;
        _settings.Save();
        Changed?.Invoke();
    }

    /// <summary>按 . 分段转整数逐段比较，禁止字符串比较（2.10 &gt; 2.9）。</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var a = Parse(candidate);
        var b = Parse(current);
        var n = Math.Max(a.Count, b.Count);

        for (var i = 0; i < n; i++)
        {
            var left = i < a.Count ? a[i] : 0;
            var right = i < b.Count ? b[i] : 0;
            if (left != right) return left > right;
        }
        return false;
    }

    private static List<int> Parse(string version) =>
        version.Split('.', StringSplitOptions.RemoveEmptyEntries)
               .Select(p => int.TryParse(p.Trim(), out var n) ? n : 0)
               .ToList();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DiskWriteMonitor/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

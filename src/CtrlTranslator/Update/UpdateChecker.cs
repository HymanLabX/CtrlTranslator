using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Update;

/// <summary>GitHub 上一个可用的新版本。</summary>
public sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string Title,
    string Notes,
    string SetupFileName,
    string SetupDownloadUrl,
    long SetupSizeBytes,
    string ChecksumUrl,
    string HtmlUrl)
{
    public double SetupSizeMb => Math.Round(SetupSizeBytes / 1024d / 1024d, 1);
}

/// <summary>
/// 查 GitHub Releases 有没有新版本。
/// </summary>
/// <remarks>
/// 只走官方 API 直连，不做任何镜像回退 —— 镜像返回的内容不可信，而校验用的哈希本身
/// 也得从同一条链路拿，绕一圈并不会让事情更安全，只会多一堆会悄悄失效的代码。
/// 拉不到就当作"暂时没有新版本"，安安静静地什么都不做。
/// </remarks>
public static partial class UpdateChecker
{
    private const string Repository = "HuayouXiang/CtrlTranslator";
    private const string ChecksumAssetName = "SHA256SUMS.txt";

    /// <summary><c>/releases/latest</c> 天然跳过预发布，所以 rc / beta 不会被推给用户。</summary>
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    public static string ReleasesPageUrl { get; } = $"https://github.com/{Repository}/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion { get; } = ReadAssemblyVersion();

    /// <summary>
    /// 单独提出来是为了打断初始化顺序上的依赖：静态字段按<b>声明顺序</b>初始化，
    /// <see cref="Client"/> 排在 <see cref="CurrentVersion"/> 前面，
    /// 若 <see cref="CreateClient"/> 直接去读那个属性，读到的是还没赋值的 null，
    /// 整个类型初始化器会抛异常 —— 表现是自动更新永久静默失效，别的功能却一切正常。
    /// </summary>
    private static Version ReadAssemblyVersion() => Normalize(
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0));

    /// <summary>
    /// 返回比当前版本新的发布；没有新版本、拉取失败或产物不齐全时返回 null。
    /// </summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(
                LatestReleaseUrl,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.Info("自动更新", "仓库还没有正式发布版本，跳过本次检查。");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning("自动更新", $"检查更新失败，GitHub 返回 HTTP {(int)response.StatusCode}。");
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseRelease(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 连不上 GitHub 是常态（没挂代理、网络抖动），不值得打扰用户，记一行日志即可。
            Logger.Info("自动更新", $"检查更新没有完成：{exception.Message}");
            return null;
        }
    }

    private static ReleaseInfo? ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string tag = ReadString(root, "tag_name") ?? string.Empty;
        Version? version = ParseVersion(tag);
        if (version is null)
        {
            Logger.Warning("自动更新", $"看不懂的版本号「{tag}」，跳过本次检查。");
            return null;
        }

        if (version <= CurrentVersion)
        {
            Logger.Info("自动更新", $"已经是最新版本（本地 {CurrentVersion}，线上 {version}）。");
            return null;
        }

        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            Logger.Warning("自动更新", "这个 Release 没有任何产物，跳过。");
            return null;
        }

        string? setupName = null;
        string? setupUrl = null;
        long setupSize = 0;
        string? checksumUrl = null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = ReadString(asset, "name");
            string? url = ReadString(asset, "browser_download_url");
            if (name is null || url is null)
            {
                continue;
            }

            if (string.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
            }
            else if (SetupAssetPattern().IsMatch(name))
            {
                setupName = name;
                setupUrl = url;
                setupSize = asset.TryGetProperty("size", out JsonElement size)
                    && size.ValueKind == JsonValueKind.Number
                        ? size.GetInt64()
                        : 0;
            }
        }

        // 缺任何一个都不往下走：没有安装包无从更新，没有校验文件就不该下载并执行任何东西。
        if (setupName is null || setupUrl is null || checksumUrl is null)
        {
            Logger.Warning(
                "自动更新",
                $"Release {tag} 的产物不齐全（安装包：{setupName is not null}，校验文件：{checksumUrl is not null}），已跳过。");
            return null;
        }

        return new ReleaseInfo(
            tag,
            version,
            ReadString(root, "name") ?? tag,
            ReadString(root, "body") ?? string.Empty,
            setupName,
            setupUrl,
            setupSize,
            checksumUrl,
            ReadString(root, "html_url") ?? ReleasesPageUrl);
    }

    internal static Version? ParseVersion(string tag)
    {
        Match match = VersionPattern().Match(tag);
        return match.Success && Version.TryParse(match.Value, out Version? version)
            ? Normalize(version)
            : null;
    }

    /// <summary>
    /// 补齐到四段再比。<c>Version</c> 会把缺失的段当成 -1，
    /// 于是 <c>1.1</c> 会被判定成小于 <c>1.1.0.0</c> —— 一个很容易踩进去的坑。
    /// </summary>
    private static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // GitHub API 不带 User-Agent 会直接 403，这不是可选项。
        // 这里刻意调 ReadAssemblyVersion() 而不是读 CurrentVersion 属性，原因见该方法的注释。
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("CtrlTranslator", ReadAssemblyVersion().ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static HttpClient SharedClient => Client;

    [GeneratedRegex(@"\d+(\.\d+){0,3}")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^CtrlTranslator-Setup-.*\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex SetupAssetPattern();
}

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Update;

/// <summary>GitHub 上一个可用的新版本。</summary>
/// <param name="ExpectedSha256">
/// 安装包的 SHA256，取自 GitHub Releases API 里每个资产自带的 <c>digest</c> 字段
/// （形如 <c>sha256:858dbde8…</c>，这里只存去掉前缀后的十六进制串）。
/// 不需要额外发布校验文件 —— 这个值本来就在我们已经下载的那份 JSON 里。
/// </param>
public sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string Title,
    string Notes,
    string SetupFileName,
    string SetupDownloadUrl,
    long SetupSizeBytes,
    string ExpectedSha256,
    string HtmlUrl)
{
    public double SetupSizeMb => Math.Round(SetupSizeBytes / 1024d / 1024d, 1);
}

/// <summary>一次检查更新的结论。</summary>
public enum UpdateCheckStatus
{
    /// <summary>有可以安装的新版本。</summary>
    UpdateAvailable,

    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>查不到任何正式发布（仓库不存在、改成了私有、或者还没发过版）。</summary>
    NoRelease,

    /// <summary>没查成（网络不通、GitHub 报错、发布内容不完整）。</summary>
    Failed
}

/// <summary>
/// 检查更新的结果。以前只有"有新版本 / null"两种，于是"连不上 GitHub""仓库没了"
/// 在设置页上统统显示成「已经是最新版本」—— 这是一句假话。
/// </summary>
/// <param name="Detail">没查成时的原因，写给用户看的人话。</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, string? Detail = null);

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

    /// <summary>资产 digest 字段的算法前缀，例如 <c>sha256:858dbde8…</c>。</summary>
    private const string DigestPrefix = "sha256:";

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
    /// 查 GitHub 上的最新正式发布，告诉调用方是"有新版 / 已最新 / 查不到发布 / 没查成"中的哪一种。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(
                LatestReleaseUrl,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 404 有两种可能：仓库在但还没发过正式版，或者仓库本身不存在 / 变成了私有。
                Logger.Info("自动更新", "查不到正式发布（仓库还没发过版，或仓库不存在、不是公开的），跳过本次检查。");
                return new UpdateCheckResult(UpdateCheckStatus.NoRelease);
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning("自动更新", $"检查更新失败，GitHub 返回 HTTP {(int)response.StatusCode}。");
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    Detail: $"GitHub 返回了错误（HTTP {(int)response.StatusCode}）");
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
            Logger.Info("自动更新", $"检查更新没有完成：{Logger.Describe(exception)}");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: "连不上 GitHub");
        }
    }

    private static UpdateCheckResult ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string tag = ReadString(root, "tag_name") ?? string.Empty;
        Version? version = ParseVersion(tag);
        if (version is null)
        {
            Logger.Warning("自动更新", $"看不懂的版本号「{tag}」，跳过本次检查。");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: $"最新发布的版本号「{tag}」认不出来");
        }

        if (version <= CurrentVersion)
        {
            Logger.Info("自动更新", $"已经是最新版本（本地 {CurrentVersion}，线上 {version}）。");
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }

        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            Logger.Warning("自动更新", "这个 Release 没有任何产物，跳过。");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: $"新版本 {tag} 没有附带安装包");
        }

        string? setupName = null;
        string? setupUrl = null;
        long setupSize = 0;
        string? expectedSha256 = null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = ReadString(asset, "name");
            string? url = ReadString(asset, "browser_download_url");
            if (name is null || url is null || !SetupAssetPattern().IsMatch(name))
            {
                continue;
            }

            setupName = name;
            setupUrl = url;
            setupSize = asset.TryGetProperty("size", out JsonElement size)
                && size.ValueKind == JsonValueKind.Number
                    ? size.GetInt64()
                    : 0;

            // GitHub 自己就会给出每个资产的摘要，不用我们额外发一个校验文件。
            string? digest = ReadString(asset, "digest");
            if (digest is not null && digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                expectedSha256 = digest[DigestPrefix.Length..].Trim();
            }
        }

        if (setupName is null || setupUrl is null)
        {
            Logger.Warning("自动更新", $"Release {tag} 里没有安装包，已跳过。");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: $"新版本 {tag} 没有附带安装包");
        }

        // 拿不到摘要就不更新。宁可不更新，也不下载并执行一个无从验证的 exe ——
        // 这里不做任何"没有校验值就放行"的退路。
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
        {
            Logger.Warning(
                "自动更新",
                $"Release {tag} 的安装包没有可用的 digest 摘要，无法校验，已跳过。");
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                Detail: $"新版本 {tag} 缺少校验值，为了安全没有安装");
        }

        return new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new ReleaseInfo(
                tag,
                version,
                ReadString(root, "name") ?? tag,
                ReadString(root, "body") ?? string.Empty,
                setupName,
                setupUrl,
                setupSize,
                expectedSha256,
                ReadString(root, "html_url") ?? ReleasesPageUrl));
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

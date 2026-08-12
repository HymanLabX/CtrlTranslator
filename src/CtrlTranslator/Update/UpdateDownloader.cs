using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Update;

public sealed class UpdateDownloadException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp(ReceivedBytes / (double)TotalBytes, 0, 1)
        : 0;

    public string Describe() => TotalBytes > 0
        ? $"{ReceivedBytes / 1024d / 1024d:F1} / {TotalBytes / 1024d / 1024d:F1} MB"
        : $"{ReceivedBytes / 1024d / 1024d:F1} MB";
}

/// <summary>
/// 下载安装包并<b>强制校验 SHA256</b>，然后把安装程序拉起来。
/// </summary>
/// <remarks>
/// <para>
/// 校验这一步没有开关也没有例外。自动更新本质上就是"从网上取一个 exe 下来执行"，
/// 少了校验就是给这台机器开了个远程代码执行的口子。哈希对不上就删文件、报错，绝不执行。
/// </para>
/// <para>
/// 期望哈希取自 GitHub Releases API 里资产自带的 <c>digest</c> 字段，
/// <b>不需要额外发布校验文件</b>：那个值本来就在检查更新时已经下载的那份 JSON 里。
/// </para>
/// <para>
/// <b>这套机制防什么、不防什么</b>：元数据走 <c>api.github.com</c>、安装包走
/// <c>objects.githubusercontent.com</c>，两条不同链路互相印证，能挡住下载被截断、
/// CDN 出错或中途被改动。但它<b>挡不住仓库或 GitHub 账号本身被攻破</b> ——
/// 攻击者换掉资产后 GitHub 会重算并报告新的 digest，校验照样通过。
/// 要防住那种情况需要私钥离线的非对称签名（minisign 之类）；本项目的定位是
/// 自用与少数同事共享，明确不把"仓库被攻破"纳入威胁模型，故不引入密钥管理的长期负担。
/// </para>
/// </remarks>
public static class UpdateDownloader
{
    private static string WorkDirectory => Path.Combine(Path.GetTempPath(), "CtrlTranslator-update");

    public static async Task<string> DownloadAndVerifyAsync(
        ReleaseInfo release,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string targetPath = PrepareTargetPath(release.SetupFileName);

        await DownloadAsync(release, targetPath, progress, cancellationToken);

        string actualHash = await ComputeSha256Async(targetPath, cancellationToken);
        if (!string.Equals(actualHash, release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(targetPath);
            Logger.Error(
                "自动更新",
                $"安装包校验失败，已删除。期望 {release.ExpectedSha256}，实际 {actualHash}。");
            throw new UpdateDownloadException(
                "下载到的安装包和 GitHub 记录的校验值对不上，已经删掉了。可能是下载过程被打断或被中间设备改动过，请稍后重试。");
        }

        Logger.Info("自动更新", $"安装包已下载并通过 SHA256 校验：{targetPath}");
        return targetPath;
    }

    /// <summary>
    /// 启动安装程序并让调用方随即退出本进程。
    /// </summary>
    /// <remarks>
    /// 安装脚本的 <c>InitializeSetup</c> 里会等本进程的互斥体消失（最多 10 秒）再往下走，
    /// 所以这里不需要 sleep，也不需要生成什么批处理去等 —— 那类动作在杀毒软件眼里很难看。
    /// </remarks>
    public static void LaunchInstaller(string setupPath, bool elevate)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = setupPath,
            // /NORESTART：不允许安装程序重启电脑。装完拉起程序由 .iss 的 [Run] 负责。
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        };

        if (elevate)
        {
            startInfo.Verb = "runas";
        }

        Process.Start(startInfo);
        Logger.Info("自动更新", $"已启动安装程序（{(elevate ? "提权" : "当前用户")}），本进程即将退出。");
    }

    private static string PrepareTargetPath(string fileName)
    {
        string directory = WorkDirectory;
        try
        {
            // 每次更新前清空，免得上次失败留下的半个文件越积越多。
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            Logger.Warning("自动更新", $"清理更新临时目录失败，继续下载：{exception.Message}");
        }

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Path.GetFileName(fileName));
    }

    private static async Task DownloadAsync(
        ReleaseInfo release,
        string targetPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await UpdateChecker.SharedClient.GetAsync(
                release.SetupDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? release.SetupSizeBytes;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream target = new(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            byte[] buffer = new byte[81920];
            long received = 0;
            long lastReported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                // 每 512 KB 报一次就够了，报太密只会让界面忙着重绘。
                if (received - lastReported >= 512 * 1024 || received == total)
                {
                    lastReported = received;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            progress?.Report(new DownloadProgress(received, total > 0 ? total : received));
        }
        catch (OperationCanceledException)
        {
            TryDelete(targetPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(targetPath);
            throw new UpdateDownloadException(
                "安装包没有下载完成。如果你在国内且没开代理，从 GitHub 下载几十 MB 很容易断，可以稍后重试或手动下载。",
                exception);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            Logger.Warning("自动更新", $"删除临时安装包失败：{exception.Message}");
        }
    }
}

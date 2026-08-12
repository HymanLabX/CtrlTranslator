namespace CtrlTranslator.Settings;

/// <summary>
/// 一个服务商的默认连接参数。
/// </summary>
/// <param name="ExtraRequestBody">
/// 关闭"思考模式"的参数是<b>服务商特定</b>的：DeepSeek/智谱用 <c>thinking.type</c>，
/// 通义/硅基流动用 <c>enable_thinking</c>。它必须跟着档案走，否则切到别家会直接报错或白白思考。
/// </param>
/// <param name="Models">
/// 该服务商的常用模型，用来填充下拉框。<b>第一个就是 <paramref name="Model"/> 默认值。</b>
/// 这只是让用户开箱就能选，不保证覆盖全部 —— 服务商随时会上下线模型，
/// 想拿权威列表还是点「拉取可用模型」，下拉框也允许直接手输。
/// </param>
public sealed record ProviderPreset(
    string Name,
    string BaseUrl,
    string Model,
    string Description,
    string KeyUrl,
    string ExtraRequestBody,
    IReadOnlyList<string> Models);

/// <summary>
/// 内置服务商预设清单（SPEC §5.7）。设置窗口与引导向导共用这一份，避免两处各写一份走样。
/// </summary>
public static class ProviderPresets
{
    public const string CustomName = "自定义";

    private static readonly ProviderPreset[] Items =
    [
        new("DeepSeek",
            "https://api.deepseek.com/v1",
            "deepseek-v4-flash",
            "速度快，适合日常划词翻译",
            "https://platform.deepseek.com/api_keys",
            "{\"thinking\": {\"type\": \"disabled\"}}",
            ["deepseek-v4-flash", "deepseek-chat"]),
        new("智谱 GLM",
            "https://open.bigmodel.cn/api/paas/v4",
            "glm-4-flash",
            "国内服务，提供快速模型",
            "https://open.bigmodel.cn/usercenter/apikeys",
            "{\"thinking\": {\"type\": \"disabled\"}}",
            ["glm-4-flash", "glm-4-air", "glm-4.5"]),
        new("阿里通义",
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            "qwen-turbo",
            "阿里云百炼 OpenAI 兼容接口",
            "https://help.aliyun.com/zh/model-studio/get-api-key",
            "{\"enable_thinking\": false}",
            ["qwen-turbo", "qwen-plus"]),
        new("月之暗面 Kimi",
            "https://api.moonshot.cn/v1",
            "moonshot-v1-8k",
            "适合通用文本翻译",
            "https://platform.moonshot.cn/console/api-keys",
            AppSettings.DefaultExtraRequestBody,
            ["moonshot-v1-8k", "moonshot-v1-32k"]),
        new("硅基流动",
            "https://api.siliconflow.cn/v1",
            "Qwen/Qwen2.5-7B-Instruct",
            "提供多种开源模型",
            "https://cloud.siliconflow.cn/account/ak",
            "{\"enable_thinking\": false}",
            ["Qwen/Qwen2.5-7B-Instruct"]),
        // OpenRouter 聚合了几百个模型，列不完也不该乱猜，让用户去拉取。
        new("OpenRouter",
            "https://openrouter.ai/api/v1",
            string.Empty,
            "可从多个服务商和模型中选择",
            "https://openrouter.ai/docs/quickstart",
            AppSettings.DefaultExtraRequestBody,
            []),
        // 本地 Ollama 对请求体里的未知键更挑剔，且本来就没有思考开关，这里留空最稳。
        // 模型取决于用户本机 pull 过什么，给两个最常见的当起点。
        new("本地 Ollama",
            "http://localhost:11434/v1",
            "qwen2.5:7b",
            "数据留在本机，无需 API Key",
            "https://ollama.com/download",
            string.Empty,
            ["qwen2.5:7b", "llama3.1:8b"]),
        new(CustomName,
            string.Empty,
            string.Empty,
            "填写任意 OpenAI 兼容接口",
            string.Empty,
            AppSettings.DefaultExtraRequestBody,
            [])
    ];

    public static IReadOnlyList<ProviderPreset> All => Items;

    public static ProviderPreset Custom => Items[^1];

    public static ProviderPreset Find(string? name)
    {
        return Items.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal)) ?? Custom;
    }

    public static bool IsKnown(string? name) =>
        Items.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal));
}

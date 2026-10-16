using System.Text.Json;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Settings;

/// <summary>
/// 读写额外请求参数里"关闭思考模式"的那一个键，保留用户为其他服务商填写的所有键。
/// </summary>
/// <remarks>
/// 关思考的写法各家不同，这里认得的全部写法：
/// <list type="bullet">
/// <item>DeepSeek / 智谱：<c>{"thinking": {"type": "disabled"}}</c></item>
/// <item>通义 / 硅基流动：<c>{"enable_thinking": false}</c></item>
/// <item>vLLM / SGLang 自建服务：<c>{"chat_template_kwargs": {"enable_thinking": false}}</c></item>
/// <item>OpenAI GPT-5 系列：<c>{"reasoning_effort": "minimal"}</c>（或 <c>"none"</c>）</item>
/// <item>OpenRouter：<c>{"reasoning": {"effort": "minimal"}}</c> 或 <c>{"reasoning": {"enabled": false}}</c></item>
/// </list>
/// 以前只认第一种：通义用户按预设关了思考，首字一超时，提示却说"你的关闭思考模式开关关着"，
/// 点「立即关闭思考模式」还会往通义的请求里塞一个它不认识的 thinking 键。
/// </remarks>
internal static class ExtraRequestBodyHelper
{
    public static bool IsThinkingDisabled(string? extraRequestBody)
    {
        if (string.IsNullOrWhiteSpace(extraRequestBody))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(extraRequestBody);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return IsDisabledThinkingObject(root)
                || IsFalse(root, "enable_thinking")
                || (root.TryGetProperty("chat_template_kwargs", out JsonElement kwargs)
                    && kwargs.ValueKind == JsonValueKind.Object
                    && IsFalse(kwargs, "enable_thinking"))
                || IsMinimalEffort(root, "reasoning_effort")
                || (root.TryGetProperty("reasoning", out JsonElement reasoning)
                    && reasoning.ValueKind == JsonValueKind.Object
                    && (IsMinimalEffort(reasoning, "effort") || IsFalse(reasoning, "enabled")));
        }
        catch (JsonException exception)
        {
            Logger.Warning("设置", $"检查思考模式时发现额外请求参数不是合法 JSON，按未关闭处理：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// 打开或关掉"关闭思考模式"。关掉时按服务商写对应的那个键 ——
    /// 写错了键，轻则不生效，重则对方直接 400。
    /// </summary>
    /// <param name="baseUrl">档案的服务地址，用来判断该用哪种写法；为空时按 DeepSeek / 智谱的写法。</param>
    public static string SetThinkingDisabled(string? extraRequestBody, bool disabled, string? baseUrl = null)
    {
        Dictionary<string, object?>? values = TryReadObject(extraRequestBody);
        if (values is null)
        {
            if (!disabled)
            {
                Logger.Warning("设置", "额外请求参数不是合法 JSON，取消关闭思考时保留原内容不变。");
                return extraRequestBody ?? string.Empty;
            }

            Logger.Warning("设置", "额外请求参数不是合法 JSON，已用关闭思考模式的默认值覆盖。");
            values = [];
        }

        if (disabled)
        {
            switch (DetectStyle(baseUrl))
            {
                case ThinkingStyle.EnableThinkingFlag:
                    values["enable_thinking"] = false;
                    break;
                case ThinkingStyle.ReasoningEffort:
                    values["reasoning_effort"] = "minimal";
                    break;
                default:
                    values["thinking"] = new Dictionary<string, string> { ["type"] = "disabled" };
                    break;
            }
        }
        else
        {
            values.Remove("thinking");
            values.Remove("enable_thinking");
            values.Remove("reasoning_effort");
        }

        return JsonSerializer.Serialize(values);
    }

    private enum ThinkingStyle
    {
        ThinkingObject,
        EnableThinkingFlag,
        ReasoningEffort
    }

    /// <summary>按服务地址的主机名猜这家用哪种写法。认不出来就用 DeepSeek / 智谱那种（也是默认值）。</summary>
    private static ThinkingStyle DetectStyle(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return ThinkingStyle.ThinkingObject;
        }

        string host = uri.Host;
        if (host.EndsWith("dashscope.aliyuncs.com", StringComparison.OrdinalIgnoreCase)
            || host.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
        {
            return ThinkingStyle.EnableThinkingFlag;
        }

        return host.EndsWith("api.openai.com", StringComparison.OrdinalIgnoreCase)
            ? ThinkingStyle.ReasoningEffort
            : ThinkingStyle.ThinkingObject;
    }

    private static bool IsDisabledThinkingObject(JsonElement root)
    {
        return root.TryGetProperty("thinking", out JsonElement thinking)
            && thinking.ValueKind == JsonValueKind.Object
            && thinking.TryGetProperty("type", out JsonElement type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "disabled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFalse(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.False;
    }

    private static bool IsMinimalEffort(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && (string.Equals(value.GetString(), "minimal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.GetString(), "none", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, object?>? TryReadObject(string? extraRequestBody)
    {
        if (string.IsNullOrWhiteSpace(extraRequestBody))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(extraRequestBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new Dictionary<string, object?>();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.Clone();
            }

            return values;
        }
        catch (JsonException exception)
        {
            Logger.Warning("设置", $"修改思考模式时发现额外请求参数不是合法 JSON：{exception.Message}");
            return null;
        }
    }
}

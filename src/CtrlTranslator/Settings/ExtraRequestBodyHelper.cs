using System.Text.Json;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Settings;

/// <summary>
/// 只读写额外请求参数顶层的 thinking 键，保留用户为其他服务商填写的所有键。
/// </summary>
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
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("thinking", out JsonElement thinking)
                && thinking.ValueKind == JsonValueKind.Object
                && thinking.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "disabled", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            Logger.Warning("设置", $"检查思考模式时发现额外请求参数不是合法 JSON，按未关闭处理：{exception.Message}");
            return false;
        }
    }

    public static string SetThinkingDisabled(string? extraRequestBody, bool disabled)
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
            values["thinking"] = new Dictionary<string, string> { ["type"] = "disabled" };
        }
        else
        {
            values.Remove("thinking");
        }

        return JsonSerializer.Serialize(values);
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

using System.Security.Cryptography;
using System.Text;

namespace CtrlTranslator.Settings;

/// <summary>按默认模板指纹升级；手改内容保留，无法确认来源的旧模板也按手改处理。</summary>
internal static class BuiltInPromptUpgrade
{
    // 从仓库 M0、片段规则修复、M8、M10 的真实默认模板计算，兼容尚未记录指纹的旧配置。
    private static readonly Dictionary<string, HashSet<string>> LegacyDefaults = new(StringComparer.Ordinal)
    {
        ["academic/deep"] =
        [
            "44f28990c73429ebebbd96e49fd17447539f8dec0f95136d526cc52ac85f6ad8",
            "eb4c1064f4704cb9d314651fe8af25e6a791b68483f08564b89a59896dab9a85",
        ],
        ["academic/system"] =
        [
            "255a42efa7bf87888d23c84249399ed6d76b616b8c8814ce43a234757a4afbee",
            "5d46c893cebf9e54f27ec12f7b8344a70f6b49f1f57ee6dd2b5cd4d89f18bdc6",
            "facae855628356d2478669fe5c6811a98684e442d7d9db82c71f78f56a9d33a2",
        ],
        ["auto/deep"] =
        [
            "1e4fe2412a3db53df9607233563827cc516e25c1ff551cd2a36d2248f91d25ee",
            "740972e51febd200e40d501475dd6a16777ee66defb829235b5b27efbafdbc9a",
        ],
        ["auto/system"] =
        [
            "1b567a889a32e66f28783a5a5fe7e66563a97df3f7483173cee5101af97d3d71",
            "3a5b87a2d79a2ffc6772eaf50d394e47de52cddbfb4ccfbc38f13477c47e1d73",
            "72620c1035fe28cddc8f4d17ce495fde4f41d4107991ec71c436b1e787a1f49b",
        ],
        ["business/deep"] =
        [
            "9ecc489e16f4ce2bc214291a90ca56988357b8d7a43556d9cb7c267213370ba2",
            "c7304b6c2cf2d2b63b68cfb2f217adca020aa636c2ffd1bff35fbbd37c80c616",
        ],
        ["business/system"] =
        [
            "15923757d7d2586556b13d8196990a196c3d923a706cf669158e85ca5d999dcd",
            "2a45d6d82f1360fd9377d28dce0ddbdcbb7d8f76adbdc4752e100042dec9021b",
            "60ce12121534f8c309aee8b95cda119e6d58d522b8cbf3b0b0510d8458639784",
        ],
        ["social/deep"] =
        [
            "a7eb15fb0ab92d4cd8bb8a6a7690b6bec86d9e8ff5399a44734ae213f9ae679b",
            "d7bf8cd2bd25258784755fbf9462f8471fefa816599eb76826c545a45c34a8ed",
        ],
        ["social/system"] =
        [
            "362a1a289f2c49c9601c43d2572f3533de1f562099d68f15c493f431d8efd942",
            "92bfe2608fcfe8e1fa8461831dd0db89ecfac29e65750a0dc2adfc3fc54341c9",
            "dce355279babfb90b228de759c630e4729c25afdbc54c43ddab1df4dc2db517f",
        ],
    };

    public static string Fingerprint(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n").Trim())));

    public static void Apply(TranslationMode stored, TranslationMode latest, bool upgrade)
    {
        if (upgrade)
        {
            if (CanReplace(stored.SystemPromptTemplate, stored.DefaultSystemPromptHash, stored.Id + "/system"))
            {
                stored.SystemPromptTemplate = latest.SystemPromptTemplate;
            }

            if (CanReplace(stored.DeepPromptTemplate, stored.DefaultDeepPromptHash, stored.Id + "/deep"))
            {
                stored.DeepPromptTemplate = latest.DeepPromptTemplate;
            }
        }

        stored.DefaultSystemPromptHash = Fingerprint(latest.SystemPromptTemplate);
        stored.DefaultDeepPromptHash = Fingerprint(latest.DeepPromptTemplate);
        stored.IsBuiltIn = true;
    }

    private static bool CanReplace(string text, string? baseline, string legacyKey)
    {
        // 已记录过指纹后，以该版本的基线为准；清空深度模板也是用户的明确选择。
        string fingerprint = Fingerprint(text);
        return baseline is not null
            ? string.Equals(fingerprint, baseline, StringComparison.Ordinal)
            : string.IsNullOrWhiteSpace(text)
                || (LegacyDefaults.TryGetValue(legacyKey, out HashSet<string>? known) && known.Contains(fingerprint));
    }
}

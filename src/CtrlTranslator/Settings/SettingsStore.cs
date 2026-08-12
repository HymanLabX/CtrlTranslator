using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CtrlTranslator.Diagnostics;

namespace CtrlTranslator.Settings;

/// <summary>
/// 负责配置的加载、DPAPI 密钥保护和原子写入。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string SettingsPath { get; } = Path.Combine(Logger.DataDirectory, "settings.json");

    public string? LastWarning { get; private set; }

    public AppSettings LoadOrCreate()
    {
        LastWarning = null;
        if (!File.Exists(SettingsPath))
        {
            AppSettings newSettings = new();
            newSettings.Normalize();
            if (!TrySave(newSettings, out string? errorMessage))
            {
                LastWarning = errorMessage;
            }

            return newSettings;
        }

        try
        {
            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new JsonException("配置文件内容为空。");

            // 迁移必须在 Normalize 之前：Normalize 见档案列表为空会补一个空白档案，
            // 那样旧配置里的地址和 Key 就再也没人认领了。
            bool migrated = TryMigrateLegacyProfile(settings, json);

            // Normalize 会就地把过期的内置提示词刷成最新版、把超时钳到上限，所以必须在调用前
            // 记下是否需要改动，否则调用后再比较永远是"已最新"，改动就写不回磁盘。
            bool promptsOutdated = settings.PromptVersion < AppSettings.CurrentPromptVersion;
            bool timeoutsOutdated = HasOversizedTimeout(settings);
            settings.Normalize();
            if (promptsOutdated)
            {
                Logger.Info(
                    "设置",
                    $"内置模式提示词已升级到第 {AppSettings.CurrentPromptVersion} 版。");
            }

            if (timeoutsOutdated)
            {
                Logger.Info("设置", "旧配置里超过上限的超时值已下调到允许范围内。");
            }

            DecryptApiKeys(settings);

            if ((migrated || promptsOutdated || timeoutsOutdated)
                && !TrySave(settings, out string? saveError))
            {
                LastWarning = saveError;
            }

            return settings;
        }
        catch (JsonException exception)
        {
            return RecoverDamagedSettings(exception);
        }
        catch (NotSupportedException exception)
        {
            return RecoverDamagedSettings(exception);
        }
        catch (IOException exception)
        {
            Logger.Error("设置", "读取配置文件失败。", exception);
            LastWarning = "配置文件暂时无法读取，程序已使用默认设置。";
            return CreateNormalizedDefaults();
        }
        catch (UnauthorizedAccessException exception)
        {
            Logger.Error("设置", "没有权限读取配置文件。", exception);
            LastWarning = "配置文件无法读取，请检查数据文件夹权限。";
            return CreateNormalizedDefaults();
        }
    }

    public bool TrySave(AppSettings settings, out string? errorMessage)
    {
        string temporaryPath = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Logger.DataDirectory);
            ProtectApiKeys(settings);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(SettingsPath))
            {
                File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }

            errorMessage = null;
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error("设置", "保存配置文件失败。", exception);
            errorMessage = "设置保存失败，请检查数据文件夹是否可写。";

            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception cleanupException)
            {
                Logger.Error("设置", "清理配置临时文件失败。", cleanupException);
            }

            return false;
        }
    }

    private static AppSettings CreateNormalizedDefaults()
    {
        AppSettings settings = new();
        settings.Normalize();
        return settings;
    }

    private AppSettings RecoverDamagedSettings(Exception exception)
    {
        Logger.Error("设置", "配置文件已损坏，将备份后重建。", exception);
        string backupPath = SettingsPath + ".bak";

        try
        {
            File.Copy(SettingsPath, backupPath, overwrite: true);
        }
        catch (Exception backupException)
        {
            Logger.Error("设置", "备份损坏的配置文件失败。", backupException);
        }

        AppSettings settings = CreateNormalizedDefaults();
        LastWarning = "配置文件已损坏，程序已备份旧文件并恢复默认设置。";
        if (!TrySave(settings, out string? saveError))
        {
            LastWarning = $"{LastWarning} {saveError}";
        }

        return settings;
    }

    /// <summary>
    /// 把 v1.0 的单服务商配置（顶层的 BaseUrl / ApiKeyProtected / Model…）搬进「档案 1」。
    /// </summary>
    /// <remarks>
    /// 直接读原始 JSON 而不是在 <see cref="AppSettings"/> 上保留一份影子属性 ——
    /// 那样会在新配置文件里长期留下一堆没人再读的死字段，也容易被后来的人误用。
    /// </remarks>
    private static bool TryMigrateLegacyProfile(AppSettings settings, string json)
    {
        if (settings.Profiles is { Count: > 0 })
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string presetName = ReadString(root, "ProviderPreset") ?? ProviderPresets.CustomName;
        ProviderPreset preset = ProviderPresets.Find(presetName);
        string baseUrl = ReadString(root, "BaseUrl") ?? string.Empty;
        string model = ReadString(root, "Model") ?? string.Empty;

        var profile = new ApiProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = ProviderPresets.IsKnown(presetName) ? presetName : "我的服务商",
            ProviderPreset = preset.Name,
            BaseUrl = baseUrl,
            ApiKeyProtected = ReadString(root, "ApiKeyProtected") ?? string.Empty,
            Model = model,
            Temperature = ReadDouble(root, "Temperature") ?? 0.2,
            MaxTokens = ReadInt(root, "MaxTokens") ?? 2048,
            RequestTimeoutSeconds =
                ReadInt(root, "RequestTimeoutSeconds") ?? AppSettings.DefaultRequestTimeoutSeconds,
            FirstTokenTimeoutSeconds =
                ReadInt(root, "FirstTokenTimeoutSeconds") ?? AppSettings.DefaultFirstTokenTimeoutSeconds,
            // 字段缺失才补预设默认值；存的是空字符串则是用户的明确选择，原样保留。
            ExtraRequestBody = ReadString(root, "ExtraRequestBody") ?? preset.ExtraRequestBody
        };

        settings.Profiles = [profile];
        settings.CurrentProfileId = profile.Id;
        Logger.Info("设置", $"旧版单服务商配置已迁移为档案「{profile.Name}」。");
        return true;
    }

    private static bool HasOversizedTimeout(AppSettings settings)
    {
        return settings.Profiles.Any(profile =>
            profile.RequestTimeoutSeconds > AppSettings.DefaultRequestTimeoutSeconds
            || profile.FirstTokenTimeoutSeconds > AppSettings.DefaultFirstTokenTimeoutSeconds
            || profile.FirstTokenTimeoutSeconds >= profile.RequestTimeoutSeconds);
    }

    private void DecryptApiKeys(AppSettings settings)
    {
        foreach (ApiProfile profile in settings.Profiles)
        {
            DecryptApiKey(profile);
        }
    }

    private void DecryptApiKey(ApiProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKeyProtected))
        {
            profile.ApiKey = string.Empty;
            return;
        }

        try
        {
            byte[] cipherText = Convert.FromBase64String(profile.ApiKeyProtected);
            byte[] plainText = ProtectedData.Unprotect(cipherText, optionalEntropy: null, DataProtectionScope.CurrentUser);
            profile.ApiKey = Encoding.UTF8.GetString(plainText);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            Logger.Error("设置", $"档案「{profile.Name}」的 API Key 解密失败。", exception);
            profile.ApiKey = string.Empty;
            profile.ApiKeyProtected = string.Empty;
            LastWarning = $"档案「{profile.Name}」的密钥无法解密（可能是从其他电脑或其他 Windows 账户拷贝过来的），请重新填写。";
        }
    }

    private static void ProtectApiKeys(AppSettings settings)
    {
        foreach (ApiProfile profile in settings.Profiles)
        {
            if (string.IsNullOrEmpty(profile.ApiKey))
            {
                profile.ApiKeyProtected = string.Empty;
                continue;
            }

            byte[] plainText = Encoding.UTF8.GetBytes(profile.ApiKey);
            byte[] cipherText = ProtectedData.Protect(plainText, optionalEntropy: null, DataProtectionScope.CurrentUser);
            profile.ApiKeyProtected = Convert.ToBase64String(cipherText);
        }
    }

    private static JsonElement? FindProperty(JsonElement root, string propertyName)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return FindProperty(root, propertyName) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        return FindProperty(root, propertyName) is { ValueKind: JsonValueKind.Number } value
            && value.TryGetInt32(out int result)
                ? result
                : null;
    }

    private static double? ReadDouble(JsonElement root, string propertyName)
    {
        return FindProperty(root, propertyName) is { ValueKind: JsonValueKind.Number } value
            && value.TryGetDouble(out double result)
                ? result
                : null;
    }
}

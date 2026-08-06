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
            if (!TrySave(newSettings, out string? errorMessage))
            {
                LastWarning = errorMessage;
            }

            return newSettings;
        }

        try
        {
            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            bool extraRequestBodyMissing = !ContainsProperty(json, nameof(AppSettings.ExtraRequestBody));
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new JsonException("配置文件内容为空。");
            settings.Normalize();
            DecryptApiKey(settings);

            // 老配置没有此字段时立即补写，空字符串则视为用户的明确选择并原样保留。
            if (extraRequestBodyMissing && !TrySave(settings, out string? saveError))
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
            return new AppSettings();
        }
        catch (UnauthorizedAccessException exception)
        {
            Logger.Error("设置", "没有权限读取配置文件。", exception);
            LastWarning = "配置文件无法读取，请检查数据文件夹权限。";
            return new AppSettings();
        }
    }

    public bool TrySave(AppSettings settings, out string? errorMessage)
    {
        string temporaryPath = SettingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Logger.DataDirectory);
            ProtectApiKey(settings);
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

        AppSettings settings = new();
        LastWarning = "配置文件已损坏，程序已备份旧文件并恢复默认设置。";
        if (!TrySave(settings, out string? saveError))
        {
            LastWarning = $"{LastWarning} {saveError}";
        }

        return settings;
    }

    private void DecryptApiKey(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKeyProtected))
        {
            settings.ApiKey = string.Empty;
            return;
        }

        try
        {
            byte[] cipherText = Convert.FromBase64String(settings.ApiKeyProtected);
            byte[] plainText = ProtectedData.Unprotect(cipherText, optionalEntropy: null, DataProtectionScope.CurrentUser);
            settings.ApiKey = Encoding.UTF8.GetString(plainText);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            Logger.Error("设置", "API Key 解密失败。", exception);
            settings.ApiKey = string.Empty;
            settings.ApiKeyProtected = string.Empty;
            LastWarning = "密钥无法解密（可能是从其他电脑或其他 Windows 账户拷贝过来的），请重新填写。";
        }
    }

    private static void ProtectApiKey(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ApiKey))
        {
            settings.ApiKeyProtected = string.Empty;
            return;
        }

        byte[] plainText = Encoding.UTF8.GetBytes(settings.ApiKey);
        byte[] cipherText = ProtectedData.Protect(plainText, optionalEntropy: null, DataProtectionScope.CurrentUser);
        settings.ApiKeyProtected = Convert.ToBase64String(cipherText);
    }

    private static bool ContainsProperty(string json, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }
}

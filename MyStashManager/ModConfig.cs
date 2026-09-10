using System;
using System.IO;
using UnityEngine;

namespace IndependentStash
{
    /// <summary>
    /// 模组配置类 - 管理模组的配置设置
    /// </summary>
    public static class ModConfig
    {
        /// <summary>
        /// 打开/关闭仓库的按键。
        /// <para>
        /// 默认 <see cref="KeyCode.F8"/>。刻意避开 <c>KeyCode.BackQuote</c>：
        /// 游戏内置控制台 <c>Duckov.Consoles.DConsole</c> 也用该键（作弊模式下生效），
        /// 同键会导致一次按键同时触发控制台与仓库。
        /// </para>
        /// </summary>
        public static KeyCode OpenStashKey { get; private set; } = KeyCode.F8;

        /// <summary>
        /// 仓库容量（默认 5000，与原版硬编码一致；最小 1）
        /// </summary>
        public static int Capacity { get; private set; } = 5000;

        /// <summary>
        /// 仓库相对父级 lootbox 的位置偏移（默认 (0, -0.5, 0)，
        /// 用于避免与官方 PlayerStorage 的位置键冲突，请勿随意改动）
        /// </summary>
        public static Vector3 StashOffset { get; private set; } = new Vector3(0f, -0.5f, 0f);

        /// <summary>
        /// 是否把独立仓库注入官方 PlayerStorage 的交互组
        /// （<c>InteractableBase.otherInterablesInGroup</c>）。
        /// <para>
        /// 默认 false：仓库仅通过热键打开，不改变官方仓库的交互表现。
        /// 设为 true 时会同时把官方 lootbox 的 <c>interactableGroup</c> 置为 true，
        /// 使玩家在官方仓库附近按切换键可以切到独立仓库。
        /// </para>
        /// </summary>
        public static bool InjectToInteractGroup { get; private set; } = true;

        /// <summary>
        /// 保存防抖间隔（秒）。仅用于抑制同一帧/连续调用产生的重复写盘，
        /// 游戏存档事件触发的保存不受此限制（见 MyStashManager.Save）。
        /// </summary>
        public static float SaveDebounceSeconds { get; private set; } = 0.1f;

        /// <summary>
        /// 轮换备份份数（默认 10，与游戏存档的 .bac.01~.bac.10 一致）。最小 0（关闭轮换）。
        /// </summary>
        public static int BackupCount { get; private set; } = 10;

        /// <summary>
        /// 备份存放目录（相对于模组目录；也可填绝对路径）。
        /// <para>
        /// 默认 <c>"backups"</c>，即备份放在<b>模组文件夹</b>下的 <c>backups/</c> 子目录，
        /// 而不是游戏存档目录（<c>persistentDataPath/Mod_IndependentStash/</c>）。
        /// 这样可避开 Steam 云同步 / 安全软件对存档目录的清理。
        /// </para>
        /// <para>设为空字符串表示"与主存档同目录"（旧行为）。</para>
        /// </summary>
        public static string BackupFolder { get; private set; } = "backups";

        /// <summary>
        /// 模组所在目录（由 ModBehaviour.OnAfterSetup 注入，用于解析相对备份路径）。
        /// </summary>
        public static string? ModDirectory { get; private set; }

        /// <summary>设置模组目录（由 ModBehaviour 调用）。</summary>
        public static void SetModDirectory(string? dir)
        {
            ModDirectory = dir;
        }

        /// <summary>
        /// 配置版本。0 = 旧版（无此键）；1 = 含 InjectToInteractGroup / SaveDebounceSeconds / BackupCount。
        /// </summary>
        public static int ConfigVersion { get; private set; } = 0;

        /// <summary>
        /// 当前配置文件路径（用于迁移写回）。
        /// </summary>
        private static string? _configPath;

        /// <summary>
        /// 本次加载是否缺少版本键（需要迁移写回）。
        /// </summary>
        private static bool _needsMigration;

        /// <summary>
        /// 加载配置文件
        /// </summary>
        /// <param name="configPath">配置文件路径</param>
        public static void Load(string configPath)
        {
            _configPath = configPath;
            _needsMigration = false;
            try
            {
                if (!File.Exists(configPath))
                {
                    // 如果配置文件不存在，创建默认配置
                    CreateDefault(configPath);
                    return;
                }

                string[] lines = File.ReadAllLines(configPath);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//")) continue;

                    string[] parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim();
                    string value = parts[1].Trim();

                    if (key.Equals("OpenStashKey", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse(value, true, out KeyCode parsedKey))
                        {
                            OpenStashKey = parsedKey;
                            Debug.Log($"[IndependentStash] 配置已加载: OpenStashKey = {OpenStashKey}");
                        }
                        else
                        {
                            Debug.LogWarning($"[IndependentStash] 配置中的按键无效: {value}。使用默认值 {OpenStashKey}");
                        }
                    }
                    else if (key.Equals("Capacity", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out int parsedCapacity) && parsedCapacity >= 1)
                        {
                            Capacity = parsedCapacity;
                            Debug.Log($"[IndependentStash] 配置已加载: Capacity = {Capacity}");
                        }
                        else
                        {
                            Debug.LogWarning($"[IndependentStash] 配置中的仓库容量无效: {value}。使用默认值 {Capacity}");
                        }
                    }
                    else if (key.Equals("StashOffsetX", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("StashOffsetY", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("StashOffsetZ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(value, out float parsedOffset))
                        {
                            if (key.Equals("StashOffsetX", StringComparison.OrdinalIgnoreCase)) StashOffset = new Vector3(parsedOffset, StashOffset.y, StashOffset.z);
                            else if (key.Equals("StashOffsetY", StringComparison.OrdinalIgnoreCase)) StashOffset = new Vector3(StashOffset.x, parsedOffset, StashOffset.z);
                            else StashOffset = new Vector3(StashOffset.x, StashOffset.y, parsedOffset);
                            Debug.Log($"[IndependentStash] 配置已加载: {key} = {parsedOffset}");
                        }
                        else
                        {
                            Debug.LogWarning($"[IndependentStash] 配置中的偏移无效: {key} = {value}。使用默认值");
                        }
                    }
                    else if (key.Equals("InjectToInteractGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryParseBool(value, out bool parsedInject))
                        {
                            InjectToInteractGroup = parsedInject;
                            Debug.Log($"[IndependentStash] 配置已加载: InjectToInteractGroup = {InjectToInteractGroup}");
                        }
                        else
                        {
                            Debug.LogWarning($"[IndependentStash] 配置中的 InjectToInteractGroup 无效: {value}。使用默认值 {InjectToInteractGroup}");
                        }
                    }
                    else if (key.Equals("SaveDebounceSeconds", StringComparison.OrdinalIgnoreCase))
                    {
                        if (float.TryParse(value, out float parsedDebounce) && parsedDebounce >= 0f)
                        {
                            SaveDebounceSeconds = parsedDebounce;
                            Debug.Log($"[IndependentStash] 配置已加载: SaveDebounceSeconds = {SaveDebounceSeconds}");
                        }
                        else
                        {
                            Debug.LogWarning($"[IndependentStash] 配置中的保存防抖无效: {value}。使用默认值 {SaveDebounceSeconds}");
                        }
                    }
                    else if (key.Equals("BackupCount", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out int parsedBackup) && parsedBackup >= 0)
                        {
                            BackupCount = parsedBackup;
                            Debug.Log($"[IndependentStash] 配置已加载: BackupCount = {BackupCount}");
                        }
                        else
                        {
                            Debug.LogWarning($"[IndependentStash] 配置中的备份份数无效: {value}。使用默认值 {BackupCount}");
                        }
                    }
                    else if (key.Equals("ConfigVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out int parsedVersion) && parsedVersion >= 0)
                        {
                            ConfigVersion = parsedVersion;
                        }
                    }
                    else if (key.Equals("BackupFolder", StringComparison.OrdinalIgnoreCase))
                    {
                        // 允许空值（= 与主存档同目录）
                        BackupFolder = value.Trim();
                        Debug.Log($"[IndependentStash] 配置已加载: BackupFolder = {(string.IsNullOrEmpty(BackupFolder) ? "(与主存档同目录)" : BackupFolder)}");
                    }
                }

                // 旧版配置（无 ConfigVersion 键）：把新增项补写到文件末尾，避免用户无从得知新配置
                _needsMigration = ConfigVersion < CURRENT_CONFIG_VERSION;
                if (_needsMigration)
                {
                    MigrateConfigFile(configPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 加载配置失败: {ex}");
            }
        }

        /// <summary>当前配置格式版本。0 = 旧版；1 = 含 InjectToInteractGroup/SaveDebounceSeconds/BackupCount；2 = 含 BackupFolder。</summary>
        private const int CURRENT_CONFIG_VERSION = 2;

        /// <summary>
        /// 为旧版 config.ini 追加缺失的新配置项（保留原有内容与用户设置）。
        /// </summary>
        private static void MigrateConfigFile(string configPath)
        {
            try
            {
                using (StreamWriter writer = File.AppendText(configPath))
                {
                    writer.WriteLine();
                    writer.WriteLine($"# --- 以下配置项由模组自动补充（配置版本 {CURRENT_CONFIG_VERSION}）---");
                    writer.WriteLine($"# Added automatically by the mod (config version {CURRENT_CONFIG_VERSION})");
                    writer.WriteLine("# 注意：OpenStashKey 默认值已由 BackQuote(`) 改为 F8，");
                    writer.WriteLine("#       因为 BackQuote 被游戏内置控制台占用。如需沿用旧按键请自行修改。");
                    writer.WriteLine("# Note: default OpenStashKey changed from BackQuote(`) to F8");
                    writer.WriteLine("#       because BackQuote is used by the in-game console.");
                    writer.WriteLine($"InjectToInteractGroup = {InjectToInteractGroup.ToString().ToLowerInvariant()}");
                    writer.WriteLine($"SaveDebounceSeconds = {SaveDebounceSeconds:0.###}");
                    writer.WriteLine($"BackupCount = {BackupCount}");
                    writer.WriteLine("# 备份目录（默认存到模组文件夹下；留空 = 与主存档同目录）");
                    writer.WriteLine($"BackupFolder = {BackupFolder}");
                    writer.WriteLine($"ConfigVersion = {CURRENT_CONFIG_VERSION}");
                }
                ConfigVersion = CURRENT_CONFIG_VERSION;
                Debug.Log($"[IndependentStash] 配置已迁移到版本 {CURRENT_CONFIG_VERSION}（追加新增配置项）");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IndependentStash] 配置迁移写回失败（不影响运行）: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析布尔值（支持 true/false、1/0、on/off、yes/no）。
        /// </summary>
        private static bool TryParseBool(string value, out bool result)
        {
            result = false;
            if (string.IsNullOrWhiteSpace(value)) return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "on":
                case "yes":
                    result = true;
                    return true;
                case "false":
                case "0":
                case "off":
                case "no":
                    result = false;
                    return true;
                default:
                    return bool.TryParse(value, out result);
            }
        }

        /// <summary>
        /// 创建默认配置文件
        /// </summary>
        /// <param name="configPath">配置文件路径</param>
        private static void CreateDefault(string configPath)
        {
            try
            {
                using (StreamWriter writer = File.CreateText(configPath))
                {
                    writer.WriteLine("# 独立仓库 (Independent Stash) 配置文件");
                    writer.WriteLine("# Configuration file for Independent Stash");
                    writer.WriteLine();
                    writer.WriteLine("# 打开/关闭仓库的按键 (Unity KeyCode)");
                    writer.WriteLine("# Key to toggle the stash (Unity KeyCode)");
                    writer.WriteLine("# 常见按键 / Common keys: F8, F9, I, O, P, F1, F2...");
                    writer.WriteLine("# 注意：不要使用 BackQuote(`)，该键被游戏内置控制台占用。");
                    writer.WriteLine("# Note: avoid BackQuote(`) - it is used by the in-game console.");
                    writer.WriteLine($"OpenStashKey = {OpenStashKey}");
                    writer.WriteLine();
                    writer.WriteLine("# 仓库容量 / Stash capacity (>=1, 默认 5000)");
                    writer.WriteLine("Capacity = 5000");
                    writer.WriteLine();
                    writer.WriteLine("# 仓库位置偏移（相对官方仓库，避免位置键冲突，一般无需改动）");
                    writer.WriteLine("# Stash position offset relative to the official storage (default (0,-0.5,0))");
                    writer.WriteLine($"StashOffsetX = {StashOffset.x:0.###}");
                    writer.WriteLine($"StashOffsetY = {StashOffset.y:0.###}");
                    writer.WriteLine($"StashOffsetZ = {StashOffset.z:0.###}");
                    writer.WriteLine();
                    writer.WriteLine("# 是否把独立仓库注入官方仓库的交互组（true 时可在官方仓库旁切换键切换）");
                    writer.WriteLine("# Inject the stash into the official storage's interact group");
                    writer.WriteLine($"InjectToInteractGroup = {InjectToInteractGroup.ToString().ToLowerInvariant()}");
                    writer.WriteLine();
                    writer.WriteLine("# 保存防抖间隔（秒），仅抑制连续重复写盘，游戏存档事件不受限制");
                    writer.WriteLine("# Save debounce interval in seconds");
                    writer.WriteLine($"SaveDebounceSeconds = {SaveDebounceSeconds:0.###}");
                    writer.WriteLine();
                    writer.WriteLine("# 轮换备份份数（0 = 关闭；默认 10，与游戏 .bac.01~.bac.10 一致）");
                    writer.WriteLine("# Number of rotating backups (0 = disabled, default 10)");
                    writer.WriteLine($"BackupCount = {BackupCount}");
                    writer.WriteLine();
                    writer.WriteLine("# 备份存放目录（相对模组目录，也可填绝对路径；留空 = 与主存档同目录）");
                    writer.WriteLine("# Backup directory (relative to the mod folder, or an absolute path; empty = same folder as the main save)");
                    writer.WriteLine($"BackupFolder = {BackupFolder}");
                    writer.WriteLine();
                    writer.WriteLine("# 配置版本（请勿手动修改）");
                    writer.WriteLine("# Config version (do not edit)");
                    writer.WriteLine($"ConfigVersion = {CURRENT_CONFIG_VERSION}");
                }
                ConfigVersion = CURRENT_CONFIG_VERSION;
                Debug.Log($"[IndependentStash] 在 {configPath} 创建了默认配置");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 创建默认配置失败: {ex}");
            }
        }
    }
}

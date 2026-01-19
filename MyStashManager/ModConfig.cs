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
        /// 打开/关闭仓库的按键
        /// </summary>
        public static KeyCode OpenStashKey { get; private set; } = KeyCode.BackQuote;

        /// <summary>
        /// 加载配置文件
        /// </summary>
        /// <param name="configPath">配置文件路径</param>
        public static void Load(string configPath)
        {
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
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 加载配置失败: {ex}");
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
                    writer.WriteLine("# 常见按键 / Common keys: BackQuote (`), Tab, I, O, P, F1, F2...");
                    writer.WriteLine($"OpenStashKey = {OpenStashKey}");
                }
                Debug.Log($"[IndependentStash] 在 {configPath} 创建了默认配置");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 创建默认配置失败: {ex}");
            }
        }
    }
}

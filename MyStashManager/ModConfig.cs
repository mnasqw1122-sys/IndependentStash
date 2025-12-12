using System;
using System.IO;
using UnityEngine;

namespace IndependentStash
{
    public static class ModConfig
    {
        public static KeyCode OpenStashKey { get; private set; } = KeyCode.BackQuote;

        public static void Load(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    // Create default config if it doesn't exist
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
                            Debug.Log($"[IndependentStash] Config loaded: OpenStashKey = {OpenStashKey}");
                        }
                        else
                        {
                            Debug.LogWarning($"[IndependentStash] Invalid key in config: {value}. Using default {OpenStashKey}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] Failed to load config: {ex}");
            }
        }

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
                Debug.Log($"[IndependentStash] Created default config at {configPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] Failed to create default config: {ex}");
            }
        }
    }
}

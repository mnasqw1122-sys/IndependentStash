using System;
using System.IO;
using Duckov.UI;
using ItemStatsSystem;
using UnityEngine;
using Saves;
using UnityEngine.SceneManagement;
using Cysharp.Threading.Tasks;

namespace IndependentStash
{
    /// <summary>
    /// 模组行为类 - 处理模组的生命周期和事件
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        /// <summary>
        /// 设置完成后调用
        /// </summary>
        protected override void OnAfterSetup()
        {
            base.OnAfterSetup();
            ModConfig.Load(Path.Combine(info.path, "config.ini"));
        }

        /// <summary>
        /// 启用时调用
        /// </summary>
        private void OnEnable()
        {
            LevelManager.OnAfterLevelInitialized += OnAfterLevelInitialized;
            SavesSystem.OnCollectSaveData += OnCollectSaveData;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            
            MyStashManager.Initialize();
            MyStashManager.RegisterEvents();
        }

        /// <summary>
        /// 禁用时调用
        /// </summary>
        private void OnDisable()
        {
            LevelManager.OnAfterLevelInitialized -= OnAfterLevelInitialized;
            SavesSystem.OnCollectSaveData -= OnCollectSaveData;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            
            MyStashManager.Save();
            MyStashManager.UnregisterEvents();
        }

        /// <summary>
        /// 关卡初始化完成后调用
        /// </summary>
        private void OnAfterLevelInitialized()
        {
            // 延迟一帧以确保场景完全加载
            DelayedAttachAsync().Forget();
        }

        /// <summary>
        /// 场景加载完成后调用
        /// </summary>
        /// <param name="scene">加载的场景</param>
        /// <param name="mode">加载模式</param>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"[IndependentStash] 场景加载: {scene.name}");
            
            // 检查是否为基地关卡
            if (IsBaseLevel(scene.name))
            {
                DelayedAttachAsync().Forget();
            }
        }

        /// <summary>
        /// 场景卸载完成后调用
        /// </summary>
        /// <param name="scene">卸载的场景</param>
        private void OnSceneUnloaded(Scene scene)
        {
            Debug.Log($"[IndependentStash] 场景卸载: {scene.name}");
            
            if (IsBaseLevel(scene.name))
            {
                MyStashManager.Save();
            }
        }

        /// <summary>
        /// 收集保存数据时调用
        /// </summary>
        private void OnCollectSaveData()
        {
            try
            {
                MyStashManager.Save();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 保存钩子错误: {ex}");
            }
        }

        /// <summary>
        /// 应用程序退出时调用
        /// </summary>
        private void OnApplicationQuit()
        {
            try 
            { 
                MyStashManager.Save(); 
            } 
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 退出保存错误: {ex}");
            }
        }

        /// <summary>
        /// 更新方法 - 每帧调用
        /// </summary>
        private void Update()
        {
            if (Input.GetKeyDown(ModConfig.OpenStashKey))
            {
                MyStashManager.TryToggleStash();
            }
        }

        /// <summary>
        /// 延迟附加仓库对象
        /// </summary>
        private async UniTaskVoid DelayedAttachAsync()
        {
            // 等待0.1秒实时时间以确保初始化完成
            await UniTask.Delay(TimeSpan.FromSeconds(0.1f), ignoreTimeScale: true);

            if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel)
            {
                MyStashManager.AttachInteractableToPlayerStorage();
            }
        }

        /// <summary>
        /// 检查是否为基地关卡
        /// </summary>
        /// <param name="sceneName">场景名称</param>
        /// <returns>是否为基地关卡</returns>
        private bool IsBaseLevel(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            return sceneName.IndexOf("Base", StringComparison.OrdinalIgnoreCase) >= 0 
                || sceneName.Contains("基地");
        }
    }
}

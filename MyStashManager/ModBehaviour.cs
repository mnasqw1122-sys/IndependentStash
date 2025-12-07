using System;
using System.IO;
using Duckov.UI;
using ItemStatsSystem;
using UnityEngine;
using Saves;
using UnityEngine.SceneManagement;

namespace IndependentStash
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        void OnEnable()
        {
            LevelManager.OnAfterLevelInitialized += OnAfterLevelInitialized;
            SavesSystem.OnCollectSaveData += OnCollectSaveData;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            
            MyStashManager.Initialize();
            MyStashManager.RegisterEvents();
        }

        void OnDisable()
        {
            LevelManager.OnAfterLevelInitialized -= OnAfterLevelInitialized;
            SavesSystem.OnCollectSaveData -= OnCollectSaveData;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            
            MyStashManager.Save();
            MyStashManager.UnregisterEvents();
        }

        void OnAfterLevelInitialized()
        {
            // 延迟一帧调用，确保场景完全加载
            this.DelayedCall(0.1f, () =>
            {
                MyStashManager.AttachInteractableToPlayerStorage();
            });
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"IndependentStash: Scene loaded - {scene.name}");
            
            // 检查是否是基地场景
            if (scene.name.Contains("Base") || scene.name.Contains("基地"))
            {
                // 延迟一帧确保LevelManager已初始化
                this.DelayedCall(0.1f, () =>
                {
                    if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel)
                    {
                        MyStashManager.AttachInteractableToPlayerStorage();
                    }
                });
            }
        }

        void OnSceneUnloaded(Scene scene)
        {
            Debug.Log($"IndependentStash: Scene unloaded - {scene.name}");
            
            // 场景卸载时保存数据
            if (scene.name.Contains("Base") || scene.name.Contains("基地"))
            {
                MyStashManager.Save();
            }
        }

        void OnCollectSaveData()
        {
            try
            {
                MyStashManager.Save();
            }
            catch (System.Exception ex)
            {
                Debug.LogError("IndependentStash Save hook error: " + ex.Message);
            }
        }

        void OnApplicationQuit()
        {
            try { MyStashManager.Save(); } catch {}
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.BackQuote))
            {
                MyStashManager.TryToggleStash();
            }
        }
        
        // 辅助方法：延迟调用
        void DelayedCall(float delay, Action action)
        {
            StartCoroutine(DelayedCallCoroutine(delay, action));
        }
        
        System.Collections.IEnumerator DelayedCallCoroutine(float delay, Action action)
        {
            yield return new WaitForSecondsRealtime(delay);
            action?.Invoke();
        }
    }
}
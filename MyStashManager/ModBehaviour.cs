using System;
using Duckov.UI;
using ItemStatsSystem;
using UnityEngine;
using Saves;
using UnityEngine.SceneManagement;
using Cysharp.Threading.Tasks;

namespace IndependentStash
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private void OnEnable()
        {
            LevelManager.OnAfterLevelInitialized += OnAfterLevelInitialized;
            SavesSystem.OnCollectSaveData += OnCollectSaveData;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            
            MyStashManager.Initialize();
            MyStashManager.RegisterEvents();
        }

        private void OnDisable()
        {
            LevelManager.OnAfterLevelInitialized -= OnAfterLevelInitialized;
            SavesSystem.OnCollectSaveData -= OnCollectSaveData;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            
            MyStashManager.Save();
            MyStashManager.UnregisterEvents();
        }

        private void OnAfterLevelInitialized()
        {
            // Delay one frame to ensure scene is fully loaded
            DelayedAttachAsync().Forget();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"[IndependentStash] Scene loaded: {scene.name}");
            
            // Check if it's a base level
            if (IsBaseLevel(scene.name))
            {
                DelayedAttachAsync().Forget();
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            Debug.Log($"[IndependentStash] Scene unloaded: {scene.name}");
            
            if (IsBaseLevel(scene.name))
            {
                MyStashManager.Save();
            }
        }

        private void OnCollectSaveData()
        {
            try
            {
                MyStashManager.Save();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] Save hook error: {ex}");
            }
        }

        private void OnApplicationQuit()
        {
            try 
            { 
                MyStashManager.Save(); 
            } 
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] Quit save error: {ex}");
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.BackQuote))
            {
                MyStashManager.TryToggleStash();
            }
        }

        private async UniTaskVoid DelayedAttachAsync()
        {
            // Wait for 0.1s real time to ensure initialization
            await UniTask.Delay(TimeSpan.FromSeconds(0.1f), ignoreTimeScale: true);

            if (LevelManager.Instance != null && LevelManager.Instance.IsBaseLevel)
            {
                MyStashManager.AttachInteractableToPlayerStorage();
            }
        }

        private bool IsBaseLevel(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return false;
            return sceneName.IndexOf("Base", StringComparison.OrdinalIgnoreCase) >= 0 
                || sceneName.Contains("基地");
        }
    }
}

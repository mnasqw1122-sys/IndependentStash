using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using Duckov;
using Duckov.UI;
using Duckov.Utilities;
using ItemStatsSystem;
using ItemStatsSystem.Data;
using UnityEngine;
using UnityEngine.UI;
using Saves;
using Cysharp.Threading.Tasks;

namespace IndependentStash
{
    public static class MyStashManager
    {
        // Constants
        private const int CAPACITY = 5000;
        private const string SAVE_ROOT_DIR = "Mod_IndependentStash";
        private const string SAVE_FILE_NAME = "MyStash.sav";
        
        // ES3 Keys
        private const string KEY_INVENTORY = "IndependentStash/Inventory/MyStash";
        private const string KEY_VERSION = "IndependentStash/Version";
        private const string KEY_OLD_INVENTORY = "Inventory/MyStash";
        
        // Reflection Field Names
        private const string FIELD_STORE_ALL_BUTTON = "storeAllButton";
        private const string FIELD_ON_START_LOOT = "OnStartLoot";
        private const string FIELD_OTHER_INTERACTABLES = "otherInterablesInGroup";
        private const string FIELD_MARKER_VISIBLE = "interactMarkerVisible";
        private const string FIELD_DISPLAY_NAME_KEY = "displayNameKey";
        private const string FIELD_INVENTORY_REF = "inventoryReference";
        private const string FIELD_SHOW_SORT_BUTTON = "showSortButton";

        // State
        private static InventoryData? _snapshot;
        private static Inventory? _runtimeInventory;
        private static InteractableLootbox? _lootbox;
        private static string? _filePath;
        private static DateTime _lastSaveTime = DateTime.MinValue;

        // Reflection Cache
        private static FieldInfo? _storeAllButtonField;
        private static FieldInfo? _otherInterablesInGroupField;
        private static FieldInfo? _interactMarkerVisibleField;
        private static FieldInfo? _displayNameKeyField;
        private static FieldInfo? _inventoryReferenceField;
        private static FieldInfo? _showSortButtonField;
        private static FieldInfo? _onStartLootField;

        public static void RegisterEvents()
        {
            InteractableLootbox.OnStartLoot += OnStartLoot;
            InteractableLootbox.OnStopLoot += OnStopLoot;
        }

        public static void UnregisterEvents()
        {
            InteractableLootbox.OnStartLoot -= OnStartLoot;
            InteractableLootbox.OnStopLoot -= OnStopLoot;
        }

        public static void Initialize()
        {
            if (!string.IsNullOrWhiteSpace(_filePath)) return;

            try
            {
                string root = Path.Combine(Application.persistentDataPath, SAVE_ROOT_DIR);
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                
                _filePath = Path.Combine(root, SAVE_FILE_NAME);
                
                EnsureFileCached(_filePath);
                Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] Initialize failed: {ex}");
            }
        }

        private static void EnsureFileCached(string path)
        {
            try
            {
                ES3.CacheFile(path);
                if (!ES3.FileExists(path))
                {
                    ES3.Save("Created", true, path);
                    ES3.StoreCachedFile(path);
                    ES3.CacheFile(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IndependentStash] EnsureFileCached warning: {ex.Message}");
                // Try to recover
                try
                {
                    ES3.Save("Created", true, path);
                    ES3.StoreCachedFile(path);
                    ES3.CacheFile(path);
                }
                catch { }
            }
        }

        public static void Save()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_filePath)) Initialize();
                if (string.IsNullOrWhiteSpace(_filePath)) return;

                // Debounce save (1 second)
                if ((DateTime.UtcNow - _lastSaveTime) < TimeSpan.FromSeconds(1)) return;
                _lastSaveTime = DateTime.UtcNow;

                CreateBackup(_filePath);

                if (_runtimeInventory != null)
                {
                    _snapshot = InventoryData.FromInventory(_runtimeInventory);
                }

                if (_snapshot == null)
                {
                    _snapshot = CreateEmptySnapshot();
                }

                var settings = new ES3Settings(_filePath) { location = ES3.Location.File };
                ES3.Save(KEY_INVENTORY, _snapshot, _filePath, settings);
                ES3.Save(KEY_VERSION, 1.0f, _filePath, settings);

                try { ES3.CacheFile(_filePath); } catch { }
                ES3.StoreCachedFile(_filePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] Save failed: {ex}");
            }
        }

        public static void Load()
        {
            if (string.IsNullOrWhiteSpace(_filePath)) Initialize();
            if (string.IsNullOrWhiteSpace(_filePath)) return;

            try
            {
                var settings = new ES3Settings(_filePath) { location = ES3.Location.File };
                if (!ES3.FileExists(_filePath, settings)) return;

                if (ES3.KeyExists(KEY_INVENTORY, _filePath, settings))
                {
                    _snapshot = ES3.Load<InventoryData>(KEY_INVENTORY, _filePath, settings);
                }
                else if (ES3.KeyExists(KEY_OLD_INVENTORY, _filePath, settings))
                {
                    // Migration
                    _snapshot = ES3.Load<InventoryData>(KEY_OLD_INVENTORY, _filePath, settings);
                    ES3.Save(KEY_INVENTORY, _snapshot, _filePath, settings);
                    ES3.DeleteKey(KEY_OLD_INVENTORY, _filePath, settings);
                    Debug.Log("[IndependentStash] Migrated inventory data to new key.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] Load failed: {ex}");
            }
        }

        public static void AttachInteractableToPlayerStorage()
        {
            if (LevelManager.Instance == null || !LevelManager.Instance.IsBaseLevel) return;
            if (PlayerStorage.Instance == null) return;

            // Check if object was destroyed
            if (_lootbox != null && _lootbox.gameObject == null)
            {
                _lootbox = null;
                _runtimeInventory = null;
            }

            if (_lootbox == null)
            {
                CreateStashObject();
            }
            else
            {
                // Ensure it's still in the group
                TryInjectIntoGroup(PlayerStorage.Instance.InteractableLootBox, _lootbox);
            }

            // Ensure visible
            if (_lootbox != null && _lootbox.gameObject != null)
            {
                _lootbox.gameObject.SetActive(true);
            }
        }

        public static void TryToggleStash()
        {
            if (_lootbox == null || LootView.Instance == null) return;

            if (LootView.Instance.open && LootView.Instance.TargetInventory == _runtimeInventory)
            {
                LootView.Instance.Close();
            }
            else
            {
                if (LootView.Instance.open) LootView.Instance.Close();
                OpenStashInternal();
            }
        }

        #region Internal Logic

        private static void CreateStashObject()
        {
            var go = new GameObject("PlayerStorage_Independent");
            var parentLootbox = PlayerStorage.Instance.InteractableLootBox;
            
            go.transform.SetParent(parentLootbox.transform.parent, false);
            go.transform.SetPositionAndRotation(parentLootbox.transform.position, parentLootbox.transform.rotation);

            _lootbox = go.AddComponent<InteractableLootbox>();
            SetDisplayName(_lootbox, "我的仓库");
            _lootbox.InteractName = "我的仓库";
            _lootbox.useDefaultInteractName = false;
            
            // Disable Pick All (critical for independent stash)
            _lootbox.showPickAllButton = false;
            
            _lootbox.needInspect = false;
            _lootbox.hideIfEmpty = null;
            SetShowSortButton(_lootbox, true);
            _lootbox.MarkerActive = false;
            
            // Create Inventory
            if (_runtimeInventory == null)
            {
                CreateInventory();
            }
            
            // Tagging
            _lootbox.gameObject.tag = PlayerStorage.Instance.gameObject.tag;
            
            // Injection
            TryInjectIntoGroup(parentLootbox, _lootbox);
        }

        private static void CreateInventory()
        {
            var invGo = new GameObject("IndependentStashInventory");
            invGo.transform.SetParent(LevelManager.LootBoxInventoriesParent);
            
            _runtimeInventory = invGo.AddComponent<Inventory>();
            _runtimeInventory.SetCapacity(CAPACITY);
            
            EnsureFilterProvider(_runtimeInventory);
            SetInventoryReference(_lootbox!, _runtimeInventory);

            if (_snapshot != null)
            {
                LoadInventoryDataAsync(_snapshot, _runtimeInventory).Forget();
            }
        }

        private static void OnStartLoot(InteractableLootbox lootbox)
        {
            if (_lootbox != null && lootbox == _lootbox)
            {
                EnableStoreAllButtonAsync().Forget();
            }
        }

        private static void OnStopLoot(InteractableLootbox lootbox)
        {
            if (_lootbox != null && lootbox == _lootbox)
            {
                if (LootView.Instance != null)
                {
                    var btn = GetStoreAllButton(LootView.Instance);
                    if (btn != null) btn.onClick.RemoveListener(OnMyStoreAll);
                }
            }
        }

        private static async UniTaskVoid EnableStoreAllButtonAsync()
        {
            await UniTask.Yield(); // Wait for LootView to open/init

            if (LootView.Instance == null) return;

            var btn = GetStoreAllButton(LootView.Instance);
            if (btn != null)
            {
                btn.gameObject.SetActive(true);
                btn.onClick.RemoveListener(OnMyStoreAll); // Prevent duplicates
                btn.onClick.AddListener(OnMyStoreAll);
            }
        }

        private static void OnMyStoreAll()
        {
            if (LootView.Instance == null || _runtimeInventory == null) return;
            if (LootView.Instance.TargetInventory != _runtimeInventory) return;

            var character = LevelManager.Instance.MainCharacter;
            if (character?.CharacterItem?.Inventory == null) return;

            var sourceInventory = character.CharacterItem.Inventory;
            int lastItemPosition = sourceInventory.GetLastItemPosition();
            bool playedSound = false;

            for (int i = 0; i <= lastItemPosition; i++)
            {
                if (sourceInventory.lockedIndexes.Contains(i)) continue;

                Item itemAt = sourceInventory.GetItemAt(i);
                if (itemAt != null)
                {
                    if (!_runtimeInventory.AddAndMerge(itemAt)) break; // Inventory full

                    if (!playedSound)
                    {
                        AudioManager.PlayPutItemSFX(itemAt);
                        playedSound = true;
                    }
                }
            }
        }

        private static void OpenStashInternal()
        {
            if (_lootbox == null) return;

            if (_onStartLootField == null)
            {
                _onStartLootField = typeof(InteractableLootbox).GetField(FIELD_ON_START_LOOT, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (_onStartLootField != null)
            {
                var del = _onStartLootField.GetValue(null) as MulticastDelegate;
                if (del != null)
                {
                    foreach (var handler in del.GetInvocationList())
                    {
                        try
                        {
                            handler.Method.Invoke(handler.Target, new object[] { _lootbox });
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[IndependentStash] Error invoking OnStartLoot: {ex}");
                        }
                    }
                }
            }
        }

        private static void CreateBackup(string path)
        {
            if (!ES3.FileExists(path)) return;
            
            var backupPath = path + ".backup";
            try
            {
                ES3.CopyFile(path, backupPath);
                Debug.Log("[IndependentStash] Created backup save file");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IndependentStash] Failed to create backup: {ex.Message}");
            }
        }

        private static InventoryData CreateEmptySnapshot()
        {
            var temp = new GameObject("IndependentStashTempInv");
            var inv = temp.AddComponent<Inventory>();
            inv.SetCapacity(CAPACITY);
            var data = InventoryData.FromInventory(inv);
            UnityEngine.Object.Destroy(temp);
            return data;
        }

        private static async UniTaskVoid LoadInventoryDataAsync(InventoryData snapshot, Inventory inventory)
        {
            if (snapshot == null || inventory == null) return;

            try
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                await InventoryData.LoadIntoInventory(snapshot, inventory);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] LoadInventoryDataAsync failed: {ex}");
                
                // Recovery logic
                bool hasExistingItems = false;
                try
                {
                    if (inventory != null)
                        hasExistingItems = inventory.GetLastItemPosition() >= 0;
                }
                catch { }

                if (!hasExistingItems)
                {
                    Debug.LogWarning("[IndependentStash] Initializing empty inventory due to load failure");
                    var emptySnapshot = CreateEmptySnapshot();
                    await InventoryData.LoadIntoInventory(emptySnapshot, inventory);
                }
                else
                {
                    Debug.LogWarning("[IndependentStash] Preserving existing items despite load failure");
                }
            }
        }

        private static void EnsureFilterProvider(Inventory target)
        {
            if (target == null) return;
            
            var mine = target.GetComponent<InventoryFilterProvider>() ?? target.gameObject.AddComponent<InventoryFilterProvider>();

            var officialInv = GetInventoryReference(PlayerStorage.Instance.InteractableLootBox);
            var officialProvider = officialInv?.GetComponent<InventoryFilterProvider>();

            if (officialProvider?.entries != null && officialProvider.entries.Length > 0)
            {
                mine.entries = officialProvider.entries;
                return;
            }

            // Fallback default filters
            var tags = GameplayDataSettings.Tags;
            mine.entries = new InventoryFilterProvider.FilterEntry[]
            {
                new() { name = "ItemFilter_All", requireTags = Array.Empty<Tag>() },
                new() { name = "ItemFilter_Weapon", requireTags = new[] { tags.Gun } },
                new() { name = "ItemFilter_Bullet", requireTags = new[] { tags.Bullet } },
                new() { name = "ItemFilter_Equipment", requireTags = new[] { tags.Armor, tags.Helmat, tags.Backpack } },
                new() { name = "ItemFilter_Accessory", requireTags = new[] { TagUtilities.TagFromString("Attachment") ?? tags.Special } },
                new() { name = "ItemFilter_Totem", requireTags = new[] { TagUtilities.TagFromString("Totem") ?? tags.Special } },
                new() { name = "ItemFilter_Medic", requireTags = new[] { TagUtilities.TagFromString("Medicine") ?? tags.Special } },
                new() { name = "ItemFilter_Food", requireTags = new[] { TagUtilities.TagFromString("Food") ?? tags.Bait } },
                new() { name = "ItemFilter_Other", requireTags = new[] { tags.Special } }
            };
        }

        #endregion

        #region Reflection Helpers

        private static Button? GetStoreAllButton(LootView view)
        {
            if (_storeAllButtonField == null)
                _storeAllButtonField = typeof(LootView).GetField(FIELD_STORE_ALL_BUTTON, BindingFlags.Instance | BindingFlags.NonPublic);
            return _storeAllButtonField?.GetValue(view) as Button;
        }

        private static void TryInjectIntoGroup(InteractableBase master, InteractableBase other)
        {
            if (master == null || other == null) return;

            if (_otherInterablesInGroupField == null)
                _otherInterablesInGroupField = typeof(InteractableBase).GetField(FIELD_OTHER_INTERACTABLES, BindingFlags.Instance | BindingFlags.NonPublic);
            if (_interactMarkerVisibleField == null)
                _interactMarkerVisibleField = typeof(InteractableBase).GetField(FIELD_MARKER_VISIBLE, BindingFlags.Instance | BindingFlags.NonPublic);

            var list = _otherInterablesInGroupField?.GetValue(master) as List<InteractableBase>;
            if (list != null && !list.Contains(other))
            {
                list.Add(other);
            }
            
            _interactMarkerVisibleField?.SetValue(other, false);
        }

        private static void SetDisplayName(InteractableLootbox lootbox, string text)
        {
            if (_displayNameKeyField == null)
                _displayNameKeyField = typeof(InteractableLootbox).GetField(FIELD_DISPLAY_NAME_KEY, BindingFlags.Instance | BindingFlags.NonPublic);
            _displayNameKeyField?.SetValue(lootbox, text);
        }

        private static void SetInventoryReference(InteractableLootbox lootbox, Inventory inventory)
        {
            if (_inventoryReferenceField == null)
                _inventoryReferenceField = typeof(InteractableLootbox).GetField(FIELD_INVENTORY_REF, BindingFlags.Instance | BindingFlags.NonPublic);
            _inventoryReferenceField?.SetValue(lootbox, inventory);
        }

        private static Inventory? GetInventoryReference(InteractableLootbox lootbox)
        {
            if (_inventoryReferenceField == null)
                _inventoryReferenceField = typeof(InteractableLootbox).GetField(FIELD_INVENTORY_REF, BindingFlags.Instance | BindingFlags.NonPublic);
            return _inventoryReferenceField?.GetValue(lootbox) as Inventory;
        }

        private static void SetShowSortButton(InteractableLootbox lootbox, bool value)
        {
            if (_showSortButtonField == null)
                _showSortButtonField = typeof(InteractableLootbox).GetField(FIELD_SHOW_SORT_BUTTON, BindingFlags.Instance | BindingFlags.NonPublic);
            _showSortButtonField?.SetValue(lootbox, value);
        }

        #endregion
    }
}

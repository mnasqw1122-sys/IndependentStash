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
        const int CAPACITY = 5000;
        static InventoryData? _snapshot;
        static Inventory? _runtimeInventory;
        static InteractableLootbox? _lootbox;
        static string? _filePath;
        static DateTime _lastSaveTime = DateTime.MinValue;
        
        static FieldInfo? _storeAllButtonField;

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

        static void OnStartLoot(InteractableLootbox lootbox)
        {
            if (_lootbox != null && lootbox == _lootbox)
            {
                EnableStoreAllButtonAsync().Forget();
            }
        }

        static void OnStopLoot(InteractableLootbox lootbox)
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

        static async UniTaskVoid EnableStoreAllButtonAsync()
        {
            // 等待一帧，确保LootView已经打开并完成了初始化（LootView会在OnStartLoot时调用Open，Open中会重置按钮状态）
            await UniTask.Yield();
            
            if (LootView.Instance == null) return;
            
            var btn = GetStoreAllButton(LootView.Instance);
            if (btn != null)
            {
                // 强制显示按钮
                btn.gameObject.SetActive(true);
                
                // 移除可能的重复监听器
                btn.onClick.RemoveListener(OnMyStoreAll);
                // 添加我们的自定义逻辑
                btn.onClick.AddListener(OnMyStoreAll);
            }
        }

        static Button? GetStoreAllButton(LootView view)
        {
            if (_storeAllButtonField == null)
                _storeAllButtonField = typeof(LootView).GetField("storeAllButton", BindingFlags.Instance | BindingFlags.NonPublic);
            return _storeAllButtonField?.GetValue(view) as Button;
        }

        static void OnMyStoreAll()
        {
            // 再次检查当前打开的是否是我们的仓库
            if (LootView.Instance == null || _runtimeInventory == null) return;
            // LootView.TargetInventory 应该指向 _runtimeInventory
            if (LootView.Instance.TargetInventory != _runtimeInventory) return;
            
            var character = LevelManager.Instance.MainCharacter;
            if (character == null || character.CharacterItem == null) return;
            
            var sourceInventory = character.CharacterItem.Inventory;
            if (sourceInventory == null) return;

            int lastItemPosition = sourceInventory.GetLastItemPosition();
            bool playedSound = false;
            
            for (int i = 0; i <= lastItemPosition; i++)
            {
                if (sourceInventory.lockedIndexes.Contains(i)) continue;
                
                Item itemAt = sourceInventory.GetItemAt(i);
                if (itemAt != null)
                {
                    // 尝试添加到我们的仓库
                    if (!_runtimeInventory.AddAndMerge(itemAt)) break;
                    
                    if (!playedSound)
                    {
                        AudioManager.PlayPutItemSFX(itemAt);
                        playedSound = true;
                    }
                }
            }
        }

        public static void TryToggleStash()
        {
            if (_lootbox == null) return;
            if (LootView.Instance == null) return;

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

        static void OpenStashInternal()
        {
            var lootbox = _lootbox;
            if (lootbox == null) return;

            // 通过反射触发OnStartLoot事件，模拟打开仓库
            var field = typeof(InteractableLootbox).GetField("OnStartLoot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                var del = field.GetValue(null) as MulticastDelegate;
                if (del != null)
                {
                    foreach (var handler in del.GetInvocationList())
                    {
                        try
                        {
                            handler.Method.Invoke(handler.Target, new object[] { lootbox });
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError("Error invoking OnStartLoot: " + ex.Message);
                        }
                    }
                }
            }
        }

        public static void Initialize()
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                var root = Path.Combine(Application.persistentDataPath, "Mod_IndependentStash");
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                _filePath = Path.Combine(root, "MyStash.sav");
                try
                {
                    ES3.CacheFile(_filePath);
                    if (!ES3.FileExists(_filePath))
                    {
                        ES3.Save("Created", true, _filePath);
                        ES3.StoreCachedFile(_filePath);
                        ES3.CacheFile(_filePath);
                    }
                }
                catch
                {
                    try
                    {
                        ES3.Save("Created", true, _filePath);
                        ES3.StoreCachedFile(_filePath);
                        ES3.CacheFile(_filePath);
                    }
                    catch {}
                }
                Load();
            }
        }

        public static void AttachInteractableToPlayerStorage() 
        { 
            if (LevelManager.Instance == null || !LevelManager.Instance.IsBaseLevel) return; 
            if (PlayerStorage.Instance == null) return; 
            
            // 修复：检查_lootbox是否已被销毁，是则重新创建
            if (_lootbox != null && _lootbox.gameObject == null)
            {
                _lootbox = null;
                _runtimeInventory = null;
            }
            
            if (_lootbox == null) 
            { 
                // 创建一个空的游戏对象作为逻辑载体 
                var go = new GameObject("PlayerStorage_Independent"); 
                go.transform.SetParent(PlayerStorage.Instance.InteractableLootBox.transform.parent, false); 
                go.transform.position = PlayerStorage.Instance.InteractableLootBox.transform.position; 
                go.transform.rotation = PlayerStorage.Instance.InteractableLootBox.transform.rotation; 
                
                // 添加组件并手动配置 
                _lootbox = go.AddComponent<InteractableLootbox>(); 
                SetDisplayName(_lootbox, "我的仓库"); 
                _lootbox.InteractName = "我的仓库"; 
                _lootbox.useDefaultInteractName = false; 
                
                // 关键：禁用Pick All按钮，这是自动拾取的核心入口 
                _lootbox.showPickAllButton = false; 
                
                // 其他重要设置，匹配官方仓库 
                _lootbox.needInspect = false; 
                _lootbox.hideIfEmpty = null; 
                SetShowSortButton(_lootbox, true); 
                _lootbox.MarkerActive = false; 
                
                // 创建独立的库存对象，不与官方仓库共享 
                if (_runtimeInventory == null) 
                { 
                    var invGo = new GameObject("IndependentStashInventory"); 
                    invGo.transform.SetParent(LevelManager.LootBoxInventoriesParent); 
                    _runtimeInventory = invGo.AddComponent<Inventory>(); 
                    _runtimeInventory.SetCapacity(CAPACITY); 
                    EnsureFilterProvider(_runtimeInventory); 
                    SetInventoryReference(_lootbox, _runtimeInventory); 
                    
                    if (_snapshot != null) 
                    { 
                        // 异步加载但添加等待机制，确保物品数据正确加载 
                        LoadInventoryDataAsync(_snapshot, _runtimeInventory).Forget(); 
                    } 
                } 
                
                // 设置游戏对象的标签，避免被自动拾取类MOD识别 
                _lootbox.gameObject.tag = PlayerStorage.Instance.gameObject.tag; 
 
                // 将其注入到官方仓库组，使其作为选项出现在原仓库菜单中 
                TryInjectIntoGroup(PlayerStorage.Instance.InteractableLootBox, _lootbox); 
            } 
            else
            {
                // 修复：确保对象始终在组中
                TryInjectIntoGroup(PlayerStorage.Instance.InteractableLootBox, _lootbox);
            }
            
            // 确保仓库只在基地场景中可见和交互 
            if (_lootbox != null && _lootbox.gameObject != null)
            {
                _lootbox.gameObject.SetActive(true);
            }
            
            // inventory is created per-open; filters applied on creation 
        }

        // 缓存反射字段以提高性能
        static FieldInfo? _otherInterablesInGroupField;
        static FieldInfo? _interactMarkerVisibleField;
        
        static void TryInjectIntoGroup(InteractableBase master, InteractableBase other)
        {
            if (master == null || other == null) return;
            
            // 延迟初始化反射字段
            if (_otherInterablesInGroupField == null)
                _otherInterablesInGroupField = typeof(InteractableBase).GetField("otherInterablesInGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_interactMarkerVisibleField == null)
                _interactMarkerVisibleField = typeof(InteractableBase).GetField("interactMarkerVisible", BindingFlags.Instance | BindingFlags.NonPublic);
            
            if (_otherInterablesInGroupField == null) return;
            var list = _otherInterablesInGroupField.GetValue(master) as System.Collections.Generic.List<InteractableBase>;
            if (list == null) return;
            if (!list.Contains(other)) list.Add(other);
            if (_interactMarkerVisibleField != null) _interactMarkerVisibleField.SetValue(other, false);
        }

        // 缓存反射字段以提高性能
        static FieldInfo? _displayNameKeyField;
        static FieldInfo? _inventoryReferenceField;
        static FieldInfo? _showSortButtonField;
        
        static void SetDisplayName(InteractableLootbox lootbox, string text)
        {
            if (_displayNameKeyField == null)
                _displayNameKeyField = typeof(InteractableLootbox).GetField("displayNameKey", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_displayNameKeyField != null) _displayNameKeyField.SetValue(lootbox, text);
        }

        static void SetInventoryReference(InteractableLootbox lootbox, Inventory? inventory)
        {
            if (_inventoryReferenceField == null)
                _inventoryReferenceField = typeof(InteractableLootbox).GetField("inventoryReference", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_inventoryReferenceField != null && inventory != null) _inventoryReferenceField.SetValue(lootbox, inventory);
        }

        static Inventory? GetInventoryReference(InteractableLootbox lootbox)
        {
            if (_inventoryReferenceField == null)
                _inventoryReferenceField = typeof(InteractableLootbox).GetField("inventoryReference", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_inventoryReferenceField == null) return null;
            return _inventoryReferenceField.GetValue(lootbox) as Inventory;
        }

        static void SetShowSortButton(InteractableLootbox lootbox, bool value)
        {
            if (_showSortButtonField == null)
                _showSortButtonField = typeof(InteractableLootbox).GetField("showSortButton", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_showSortButtonField != null) _showSortButtonField.SetValue(lootbox, value);
        }

        static void EnsureFilterProvider(Inventory target)
        {
            if (target == null) return;
            InventoryFilterProvider? mine = target.GetComponent<InventoryFilterProvider>();
            if (mine == null) mine = target.gameObject.AddComponent<InventoryFilterProvider>();
            
            var officialInv = GetInventoryReference(PlayerStorage.Instance.InteractableLootBox);
            InventoryFilterProvider? officialProvider = null;
            if (officialInv != null) officialProvider = officialInv.GetComponent<InventoryFilterProvider>();
            if (officialProvider != null && officialProvider.entries != null && officialProvider.entries.Length > 0)
            {
                mine.entries = officialProvider.entries;
                return;
            }
            
            var tags = GameplayDataSettings.Tags;
            mine.entries = new InventoryFilterProvider.FilterEntry[]
            {
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_All", icon = null, requireTags = Array.Empty<Tag>()},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Weapon", icon = null, requireTags = new[]{ tags.Gun }},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Bullet", icon = null, requireTags = new[]{ tags.Bullet }},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Equipment", icon = null, requireTags = new[]{ tags.Armor, tags.Helmat, tags.Backpack }},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Accessory", icon = null, requireTags = new[]{ TagUtilities.TagFromString("Attachment") ?? tags.Special }},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Totem", icon = null, requireTags = new[]{ TagUtilities.TagFromString("Totem") ?? tags.Special }},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Medic", icon = null, requireTags = new[]{ TagUtilities.TagFromString("Medicine") ?? tags.Special }},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Food", icon = null, requireTags = new[]{ TagUtilities.TagFromString("Food") ?? tags.Bait }},
                new InventoryFilterProvider.FilterEntry{ name = "ItemFilter_Other", icon = null, requireTags = new[]{ tags.Special }}
            };
        }

        public static void Save() 
        { 
            try 
            { 
                if (string.IsNullOrWhiteSpace(_filePath)) Initialize(); 
                if (string.IsNullOrWhiteSpace(_filePath)) return; 
                if ((DateTime.UtcNow - _lastSaveTime) < TimeSpan.FromSeconds(1)) return; 
                _lastSaveTime = DateTime.UtcNow; 
                
                // 改进：创建备份文件
                if (ES3.FileExists(_filePath))
                {
                    var backupPath = _filePath + ".backup";
                    try
                    {
                        ES3.CopyFile(_filePath, backupPath);
                        Debug.Log("IndependentStash: Created backup save file");
                    }
                    catch (Exception backupEx)
                    {
                        Debug.LogWarning("IndependentStash: Failed to create backup: " + backupEx.Message);
                    }
                }
                
                if (_runtimeInventory != null) 
                { 
                    _snapshot = InventoryData.FromInventory(_runtimeInventory); 
                } 
                if (_snapshot == null) 
                { 
                    // ensure we at least persist an empty snapshot 
                    var temp = new GameObject("IndependentStashTempInv"); 
                    var inv = temp.AddComponent<Inventory>(); 
                    inv.SetCapacity(CAPACITY); 
                    _snapshot = InventoryData.FromInventory(inv); 
                    UnityEngine.Object.Destroy(temp); 
                } 
                
                // 使用唯一的键名，避免与其他mod冲突 
                var settings = new ES3Settings(_filePath) { location = ES3.Location.File }; 
                ES3.Save("IndependentStash/Inventory/MyStash", _snapshot, _filePath, settings); 
                ES3.Save("IndependentStash/Version", 1.0f, _filePath, settings); 
                
                try { ES3.CacheFile(_filePath); } catch { } 
                ES3.StoreCachedFile(_filePath); 
            } 
            catch (Exception ex) 
            { 
                Debug.LogError("IndependentStash Save failed: " + ex.Message); 
            } 
        }

        /// <summary> 
        /// 异步加载库存数据，确保加载完成并处理可能的错误 
        /// </summary> 
        static async UniTaskVoid LoadInventoryDataAsync(InventoryData snapshot, Inventory inventory) 
        { 
            if (snapshot == null || inventory == null) return; 
            
            try 
            { 
                // 修复：等待一帧，避免与场景加载冲突
                await UniTask.Yield(PlayerLoopTiming.Update);
                
                // 确保异步加载完成，避免物品数据不一致 
                await InventoryData.LoadIntoInventory(snapshot, inventory); 
            } 
            catch (Exception ex) 
            { 
                Debug.LogError("IndependentStash LoadInventoryDataAsync failed: " + ex.Message);
                
                // 改进：记录加载失败的快照信息，便于调试
                // 修正：移除对不存在的slots属性的访问
                Debug.LogWarning($"IndependentStash: Failed to load snapshot");
                
                // 改进：检查库存是否已有物品，避免不必要的覆盖
                bool hasExistingItems = false;
                try
                {
                    // 检查库存是否已有物品
                    if (inventory != null)
                    {
                        // 通过反射或其他方式检查库存中是否有物品
                        // 这里简化处理：如果有快照但加载失败，尽量不覆盖现有数据
                        hasExistingItems = inventory.GetLastItemPosition() >= 0;
                    }
                }
                catch
                {
                    // 如果检查失败，保守处理
                    hasExistingItems = false;
                }
                
                // 只有库存为空时才用空库存初始化
                if (!hasExistingItems)
                {
                    Debug.LogWarning("IndependentStash: Initializing empty inventory due to load failure");
                    var temp = new GameObject("IndependentStashTempInv"); 
                    var inv = temp.AddComponent<Inventory>(); 
                    inv.SetCapacity(CAPACITY); 
                    var emptySnapshot = InventoryData.FromInventory(inv); 
                    await InventoryData.LoadIntoInventory(emptySnapshot, inventory); 
                    UnityEngine.Object.Destroy(temp);
                }
                else
                {
                    Debug.LogWarning("IndependentStash: Preserving existing items despite load failure");
                }
            } 
        }
        
        public static void Load()
        {
            if (string.IsNullOrWhiteSpace(_filePath)) Initialize();
            if (string.IsNullOrWhiteSpace(_filePath)) return;
            var settings = new ES3Settings(_filePath) { location = ES3.Location.File };
            if (ES3.FileExists(_filePath, settings))
            {
                // 优先使用新的唯一键名，确保与其他mod隔离
                if (ES3.KeyExists("IndependentStash/Inventory/MyStash", _filePath, settings))
                {
                    _snapshot = ES3.Load<InventoryData>("IndependentStash/Inventory/MyStash", _filePath, settings);
                }
                // 向后兼容：支持加载旧版本保存的数据
                else if (ES3.KeyExists("Inventory/MyStash", _filePath, settings))
                {
                    _snapshot = ES3.Load<InventoryData>("Inventory/MyStash", _filePath, settings);
                    // 自动迁移到新版本键名
                    ES3.Save("IndependentStash/Inventory/MyStash", _snapshot, _filePath, settings);
                    ES3.DeleteKey("Inventory/MyStash", _filePath, settings);
                }
            }
        }

        
    }
}
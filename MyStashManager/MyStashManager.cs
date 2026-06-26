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
    /// <summary>
    /// 我的仓库管理器 - 负责独立仓库的创建、管理和数据持久化
    /// </summary>
    public static class MyStashManager
    {
        // 常量定义
        private const int CAPACITY = 5000; // 仓库容量
        private const string SAVE_ROOT_DIR = "Mod_IndependentStash"; // 保存根目录
        private const string SAVE_FILE_NAME = "MyStash.sav"; // 保存文件名
        
        // ES3 键名
        private const string KEY_INVENTORY = "IndependentStash/Inventory/MyStash"; // 库存数据键
        private const string KEY_VERSION = "IndependentStash/Version"; // 版本号键
        private const string KEY_OLD_INVENTORY = "Inventory/MyStash"; // 旧库存数据键（用于迁移）
        
        // 反射字段名
        private const string FIELD_STORE_ALL_BUTTON = "storeAllButton"; // 全部存储按钮字段
        private const string FIELD_ON_START_LOOT = "OnStartLoot"; // 开始 loot 事件字段
        private const string FIELD_OTHER_INTERACTABLES = "otherInterablesInGroup"; // 其他可交互对象字段
        private const string FIELD_MARKER_VISIBLE = "interactMarkerVisible"; // 交互标记可见性字段
        private const string FIELD_DISPLAY_NAME_KEY = "displayNameKey"; // 显示名称键字段
        private const string FIELD_INVENTORY_REF = "inventoryReference"; // 库存引用字段
        private const string FIELD_SHOW_SORT_BUTTON = "showSortButton"; // 显示排序按钮字段

        // 状态变量
        private static InventoryData? _snapshot; // 库存快照
        private static Inventory? _runtimeInventory; // 运行时库存
        private static InteractableLootbox? _lootbox; //  lootbox 交互对象
        private static string? _filePath; // 保存文件路径
        private static DateTime _lastSaveTime = DateTime.MinValue; // 上次保存时间
        private static bool _isDataReady = false; // 安全标志，防止用不完整数据覆盖保存

        // 反射缓存
        private static FieldInfo? _storeAllButtonField; // 全部存储按钮字段缓存
        private static FieldInfo? _otherInterablesInGroupField; // 其他可交互对象字段缓存
        private static FieldInfo? _interactMarkerVisibleField; // 交互标记可见性字段缓存
        private static FieldInfo? _displayNameKeyField; // 显示名称键字段缓存
        private static FieldInfo? _inventoryReferenceField; // 库存引用字段缓存
        private static FieldInfo? _showSortButtonField; // 显示排序按钮字段缓存
        private static FieldInfo? _onStartLootField; // 开始 loot 事件字段缓存

        /// <summary>
        /// 注册事件监听
        /// </summary>
        public static void RegisterEvents()
        {
            InteractableLootbox.OnStartLoot += OnStartLoot;
            InteractableLootbox.OnStopLoot += OnStopLoot;
        }

        /// <summary>
        /// 取消注册事件监听
        /// </summary>
        public static void UnregisterEvents()
        {
            InteractableLootbox.OnStartLoot -= OnStartLoot;
            InteractableLootbox.OnStopLoot -= OnStopLoot;
        }

        /// <summary>
        /// 初始化仓库管理器
        /// </summary>
        public static void Initialize()
        {
            if (!string.IsNullOrWhiteSpace(_filePath)) return;

            try
            {
                string root = Path.Combine(Application.persistentDataPath, SAVE_ROOT_DIR);
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);
                
                _filePath = Path.Combine(root, SAVE_FILE_NAME);
                
                EnsureFileCached(_filePath);
                
                // 在启动时备份现有保存，然后再处理
                CreateBackup(_filePath, "SessionStart");
                
                Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 初始化失败: {ex}");
            }
        }

        /// <summary>
        /// 确保文件已缓存
        /// </summary>
        /// <param name="path">文件路径</param>
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
                Debug.LogWarning($"[IndependentStash] EnsureFileCached 警告: {ex.Message}");
                // 尝试恢复
                try
                {
                    ES3.Save("Created", true, path);
                    ES3.StoreCachedFile(path);
                    ES3.CacheFile(path);
                }
                catch { }
            }
        }

        /// <summary>
        /// 保存仓库数据
        /// </summary>
        public static void Save()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_filePath)) Initialize();
                if (string.IsNullOrWhiteSpace(_filePath)) return;

                // 安全检查：如果数据加载不正确，请勿保存
                // 这可以防止用不完整/损坏的状态覆盖良好的保存
                if (!_isDataReady)
                {
                    Debug.LogWarning("[IndependentStash] 保存跳过: 数据未准备好或之前加载失败");
                    return;
                }

                // 防抖保存（1秒）
                if ((DateTime.UtcNow - _lastSaveTime) < TimeSpan.FromSeconds(1)) return;
                _lastSaveTime = DateTime.UtcNow;

                CreateBackup(_filePath, "LastGood");

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
                Debug.LogError($"[IndependentStash] 保存失败: {ex}");
            }
        }

        /// <summary>
        /// 加载仓库数据
        /// </summary>
        public static void Load()
        {
            if (string.IsNullOrWhiteSpace(_filePath)) Initialize();
            if (string.IsNullOrWhiteSpace(_filePath)) return;
            
            _isDataReady = false; // 重置准备标志

            try
            {
                var settings = new ES3Settings(_filePath) { location = ES3.Location.File };
                if (!ES3.FileExists(_filePath, settings))
                {
                    // 新文件，所以已准备好（空）
                    _isDataReady = true;
                    return;
                }

                if (ES3.KeyExists(KEY_INVENTORY, _filePath, settings))
                {
                    _snapshot = ES3.Load<InventoryData>(KEY_INVENTORY, _filePath, settings);
                }
                else if (ES3.KeyExists(KEY_OLD_INVENTORY, _filePath, settings))
                {
                    // 迁移数据
                    _snapshot = ES3.Load<InventoryData>(KEY_OLD_INVENTORY, _filePath, settings);
                    ES3.Save(KEY_INVENTORY, _snapshot, _filePath, settings);
                    ES3.DeleteKey(KEY_OLD_INVENTORY, _filePath, settings);
                    Debug.Log("[IndependentStash] 库存数据已迁移到新键");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 加载失败: {ex}");
                _isDataReady = false; // 标记为失败
            }
        }

        /// <summary>
        /// 将可交互对象附加到玩家存储
        /// </summary>
        public static void AttachInteractableToPlayerStorage()
        {
            if (LevelManager.Instance == null || !LevelManager.Instance.IsBaseLevel) return;
            if (PlayerStorage.Instance == null) return;

            // 检查 lootbox 对象是否已被销毁
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
                // 检查 _runtimeInventory 是否仍然有效（防止场景切换等导致引用失效）
                // 如果引用失效，inventoryReference 会变成 null，导致 InteractableLootbox.Inventory
                // 回退到 GetOrCreateInventory(this)，由于位置键冲突可能返回错误的库存
                if (_runtimeInventory == null || _runtimeInventory.gameObject == null)
                {
                    Debug.LogWarning("[IndependentStash] _runtimeInventory 已失效，正在重新创建...");
                    _runtimeInventory = null;
                    CreateInventory();
                }
                else
                {
                    // 确保 inventoryReference 仍然指向 _runtimeInventory
                    // 防止任何外部代码清除了引用
                    var currentRef = GetInventoryReference(_lootbox);
                    if (currentRef != _runtimeInventory)
                    {
                        SetInventoryReference(_lootbox, _runtimeInventory);
                    }
                }

                // 确保它仍然在组中
                TryInjectIntoGroup(PlayerStorage.Instance.InteractableLootBox, _lootbox);
            }

            // 确保可见
            if (_lootbox != null && _lootbox.gameObject != null)
            {
                _lootbox.gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// 尝试切换仓库显示
        /// </summary>
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

        #region 内部逻辑

        /// <summary>
        /// 创建仓库对象
        /// </summary>
        private static void CreateStashObject()
        {
            // 创建时处于非活动状态，防止 Awake 立即运行
            // 这允许我们在 Awake 触发前修复 null List<InteractableBase> 问题
            var go = new GameObject("PlayerStorage_Independent");
            go.SetActive(false); 
            
            var parentLootbox = PlayerStorage.Instance.InteractableLootBox;

            go.transform.SetParent(parentLootbox.transform.parent, false);

            // 关键修复：偏移位置以避免与父级 lootbox 的位置键冲突
            // InteractableLootbox.GetKey() 使用 transform.position * 10f 作为字典键
            // 如果两个 lootbox 在同一位置，GetOrCreateInventory 会返回错误的缓存库存
            // 偏移 0.5 单位（*10 = 5）确保键完全不同，且不影响交互组（组基于 otherInterablesInGroup 列表）
            var stashPos = parentLootbox.transform.position + new Vector3(0f, -0.5f, 0f);
            go.transform.SetPositionAndRotation(stashPos, parentLootbox.transform.rotation);

            _lootbox = go.AddComponent<InteractableLootbox>();

            // 修复：在 Awake 运行前通过反射初始化列表
            // InteractableBase.Awake 会遍历此列表，而通过 AddComponent 添加时它是 null
            try
            {
                if (_otherInterablesInGroupField == null)
                    _otherInterablesInGroupField = typeof(InteractableBase).GetField(FIELD_OTHER_INTERACTABLES, BindingFlags.Instance | BindingFlags.NonPublic);
                
                _otherInterablesInGroupField?.SetValue(_lootbox, new List<InteractableBase>());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] 修复 otherInterablesInGroup 失败: {ex}");
            }

            SetDisplayName(_lootbox, "我的仓库");
            _lootbox.InteractName = "我的仓库";
            _lootbox.useDefaultInteractName = false;
            
            // 禁用全部拾取（对独立仓库至关重要）
            _lootbox.showPickAllButton = false;
            
            _lootbox.needInspect = false;
            _lootbox.hideIfEmpty = null;
            SetShowSortButton(_lootbox, true);
            _lootbox.MarkerActive = false;
            
            // 创建库存
            if (_runtimeInventory == null)
            {
                CreateInventory();
            }
            
            // 标记
            _lootbox.gameObject.tag = PlayerStorage.Instance.gameObject.tag;
            
            // 现在一切设置完毕，激活对象
            go.SetActive(true);

            // 注入到组中
            TryInjectIntoGroup(parentLootbox, _lootbox);
        }

        /// <summary>
        /// 创建库存
        /// </summary>
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
            else
            {
                // 没有快照意味着新库存，所以数据已准备好
                _isDataReady = true;
            }
        }

        /// <summary>
        /// 开始 loot 时的回调
        /// </summary>
        /// <param name="lootbox">被 loot 的 lootbox</param>
        private static void OnStartLoot(InteractableLootbox lootbox)
        {
            if (_lootbox != null && lootbox == _lootbox)
            {
                EnableStoreAllButtonAsync().Forget();
            }
        }

        /// <summary>
        /// 停止 loot 时的回调
        /// </summary>
        /// <param name="lootbox">被 loot 的 lootbox</param>
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

        /// <summary>
        /// 异步启用全部存储按钮
        /// </summary>
        private static async UniTaskVoid EnableStoreAllButtonAsync()
        {
            await UniTask.Yield(); // 等待 LootView 打开/初始化

            if (LootView.Instance == null) return;

            var btn = GetStoreAllButton(LootView.Instance);
            if (btn != null)
            {
                btn.gameObject.SetActive(true);
                btn.onClick.RemoveListener(OnMyStoreAll); // 防止重复
                btn.onClick.AddListener(OnMyStoreAll);
            }
        }

        /// <summary>
        /// 全部存储按钮点击事件
        /// </summary>
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
                    if (!_runtimeInventory.AddAndMerge(itemAt)) break; // 库存已满

                    if (!playedSound)
                    {
                        AudioManager.PlayPutItemSFX(itemAt);
                        playedSound = true;
                    }
                }
            }
        }

        /// <summary>
        /// 内部打开仓库
        /// </summary>
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
                            Debug.LogError($"[IndependentStash] 调用 OnStartLoot 错误: {ex}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 创建备份
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <param name="suffix">备份后缀</param>
        private static void CreateBackup(string path, string suffix)
        {
            if (!ES3.FileExists(path)) return;
            
            var backupPath = $"{path}.{suffix}.backup";
            try
            {
                if (ES3.FileExists(backupPath))
                {
                    ES3.DeleteFile(backupPath);
                }
                ES3.CopyFile(path, backupPath);
                Debug.Log($"[IndependentStash] 创建备份保存文件: {suffix}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IndependentStash] 创建备份失败 ({suffix}): {ex.Message}");
            }
        }

        /// <summary>
        /// 创建空快照
        /// </summary>
        /// <returns>空的库存数据</returns>
        private static InventoryData CreateEmptySnapshot()
        {
            var temp = new GameObject("IndependentStashTempInv");
            var inv = temp.AddComponent<Inventory>();
            inv.SetCapacity(CAPACITY);
            var data = InventoryData.FromInventory(inv);
            UnityEngine.Object.Destroy(temp);
            return data;
        }

        /// <summary>
        /// 异步加载库存数据
        /// </summary>
        /// <param name="snapshot">库存快照</param>
        /// <param name="inventory">目标库存</param>
        private static async UniTaskVoid LoadInventoryDataAsync(InventoryData snapshot, Inventory inventory)
        {
            if (snapshot == null || inventory == null) return;

            // 加载时标记数据为未准备好
            _isDataReady = false;

            // 关键修复：设置 Loading = true 防止整理(Sort)等操作在加载期间执行
            // InventoryDisplay.OnSortButtonClicked 会检查 !Target.Loading
            // 如果不设置 Loading，用户可能在加载期间点击整理，导致物品状态不一致
            inventory.Loading = true;

            try
            {
                await UniTask.Yield(PlayerLoopTiming.Update);

                // 关键：我们将加载包装在 try-catch 中以检测部分失败
                // 如果失败，我们假设数据已损坏/不完整，并阻止保存
                await InventoryData.LoadIntoInventory(snapshot, inventory);

                // 如果我们到达这里，加载成功
                _isDataReady = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] LoadInventoryDataAsync 严重失败: {ex}");
                Debug.LogError("[IndependentStash] 保存现已禁用，以防止数据丢失。请检查您的模组。");

                // 我们故意在这里留下 _isDataReady = false

                // 恢复逻辑：尝试加载我们能加载的内容，或者如果完全损坏则初始化空库存
                // 但我们仍然不允许覆盖旧文件
                bool hasExistingItems = false;
                try
                {
                    if (inventory != null)
                        hasExistingItems = inventory.GetLastItemPosition() >= 0;
                }
                catch { }

                if (!hasExistingItems)
                {
                    Debug.LogWarning("[IndependentStash] 由于加载失败，初始化空库存（只读模式）");
                    try
                    {
                        var emptySnapshot = CreateEmptySnapshot();
                        await InventoryData.LoadIntoInventory(emptySnapshot, inventory);
                    }
                    catch {}
                }
            }
            finally
            {
                // 确保无论成功还是失败，Loading 标志都被重置
                if (inventory != null && inventory.gameObject != null)
                {
                    inventory.Loading = false;
                }
            }
        }

        /// <summary>
        /// 确保库存有过滤提供器
        /// </summary>
        /// <param name="target">目标库存</param>
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

            // 后备默认过滤器
            // 注意：我们这里无法访问原始图标，所以过滤按钮将没有图标但可以正常工作
            var tags = GameplayDataSettings.Tags;
            mine.entries = new InventoryFilterProvider.FilterEntry[]
            {
                new() { name = "ItemFilter_All", requireTags = Array.Empty<Tag>() }, // 全部
                new() { name = "ItemFilter_Weapon", requireTags = new[] { tags.Gun } }, // 武器
                new() { name = "ItemFilter_Bullet", requireTags = new[] { tags.Bullet } }, // 子弹
                new() { name = "ItemFilter_Equipment", requireTags = new[] { tags.Armor, tags.Helmat, tags.Backpack } }, // 装备
                new() { name = "ItemFilter_Accessory", requireTags = new[] { TagUtilities.TagFromString("Attachment") ?? tags.Special } }, // 配件
                new() { name = "ItemFilter_Totem", requireTags = new[] { TagUtilities.TagFromString("Totem") ?? tags.Special } }, // 图腾
                new() { name = "ItemFilter_Medic", requireTags = new[] { TagUtilities.TagFromString("Medicine") ?? tags.Special } }, // 医疗
                new() { name = "ItemFilter_Food", requireTags = new[] { TagUtilities.TagFromString("Food") ?? tags.Bait } }, // 食物
                new() { name = "ItemFilter_Other", requireTags = new[] { tags.Special } } // 其他
            };
        }

        #endregion

        #region 反射辅助方法

        /// <summary>
        /// 获取全部存储按钮
        /// </summary>
        /// <param name="view">LootView 实例</param>
        /// <returns>全部存储按钮</returns>
        private static Button? GetStoreAllButton(LootView view)
        {
            if (_storeAllButtonField == null)
                _storeAllButtonField = typeof(LootView).GetField(FIELD_STORE_ALL_BUTTON, BindingFlags.Instance | BindingFlags.NonPublic);
            return _storeAllButtonField?.GetValue(view) as Button;
        }

        /// <summary>
        /// 尝试将可交互对象注入到组中
        /// </summary>
        /// <param name="master">主可交互对象</param>
        /// <param name="other">其他可交互对象</param>
        private static void TryInjectIntoGroup(InteractableBase master, InteractableBase other)
        {
            if (master == null || other == null) return;

            try
            {
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
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] TryInjectIntoGroup 失败: {ex}");
            }
        }

        /// <summary>
        /// 设置显示名称
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <param name="text">显示文本</param>
        private static void SetDisplayName(InteractableLootbox lootbox, string text)
        {
            try
            {
                if (_displayNameKeyField == null)
                    _displayNameKeyField = typeof(InteractableLootbox).GetField(FIELD_DISPLAY_NAME_KEY, BindingFlags.Instance | BindingFlags.NonPublic);
                _displayNameKeyField?.SetValue(lootbox, text);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] SetDisplayName 失败: {ex}");
            }
        }

        /// <summary>
        /// 设置库存引用
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <param name="inventory">库存对象</param>
        private static void SetInventoryReference(InteractableLootbox lootbox, Inventory inventory)
        {
            try
            {
                if (_inventoryReferenceField == null)
                    _inventoryReferenceField = typeof(InteractableLootbox).GetField(FIELD_INVENTORY_REF, BindingFlags.Instance | BindingFlags.NonPublic);
                _inventoryReferenceField?.SetValue(lootbox, inventory);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] SetInventoryReference 失败: {ex}");
            }
        }

        /// <summary>
        /// 获取库存引用
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <returns>库存对象</returns>
        private static Inventory? GetInventoryReference(InteractableLootbox lootbox)
        {
            try
            {
                if (_inventoryReferenceField == null)
                    _inventoryReferenceField = typeof(InteractableLootbox).GetField(FIELD_INVENTORY_REF, BindingFlags.Instance | BindingFlags.NonPublic);
                return _inventoryReferenceField?.GetValue(lootbox) as Inventory;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] GetInventoryReference 失败: {ex}");
                return null;
            }
        }

        /// <summary>
        /// 设置是否显示排序按钮
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <param name="value">是否显示</param>
        private static void SetShowSortButton(InteractableLootbox lootbox, bool value)
        {
            try
            {
                if (_showSortButtonField == null)
                    _showSortButtonField = typeof(InteractableLootbox).GetField(FIELD_SHOW_SORT_BUTTON, BindingFlags.Instance | BindingFlags.NonPublic);
                _showSortButtonField?.SetValue(lootbox, value);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IndependentStash] SetShowSortButton 失败: {ex}");
            }
        }

        #endregion
    }
}

using System;
using System.IO;
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
        // 仓库容量已改为可配置：见 ModConfig.Capacity（默认 5000，config.ini 可调）
        private const string SAVE_ROOT_DIR = "Mod_IndependentStash"; // 保存根目录
        private const string SAVE_FILE_NAME = "MyStash.sav"; // 保存文件名
        private const string LOG_TAG = "[IndependentStash]";

        /// <summary>
        /// 仓库显示名称。
        /// 注意：这不是本地化键，需配合 ModBehaviour 中注册的
        /// LocalizationManager.SetOverrideText 覆盖文本使用，
        /// 否则未命中的键会被游戏显示为 "*我的仓库*"。
        /// </summary>
        public const string StashDisplayName = "我的仓库";

        // ES3 键名
        private const string KEY_INVENTORY = "IndependentStash/Inventory/MyStash"; // 库存数据键
        private const string KEY_VERSION = "IndependentStash/Version"; // 版本号键
        private const string KEY_OLD_INVENTORY = "Inventory/MyStash"; // 旧库存数据键（用于迁移）
        private const string KEY_UI_ICON_X = "UI/IconPosX"; // 更早期版本的孤儿键
        private const string KEY_UI_ICON_Y = "UI/IconPosY"; // 更早期版本的孤儿键

        // 状态变量
        private static InventoryData? _snapshot; // 库存快照
        private static Inventory? _runtimeInventory; // 运行时库存
        private static InteractableLootbox? _lootbox; //  lootbox 交互对象
        private static string? _filePath; // 保存文件路径
        private static string? _backupDir; // 备份目录（默认模组文件夹下的 backups/）
        private static DateTime _lastSaveTime = DateTime.MinValue; // 上次保存时间
        private static bool _isDataReady = false; // 安全标志，防止用不完整数据覆盖保存
        private static bool _initialized = false; // 初始化是否已成功执行

        /// <summary>
        /// 仓库运行时库存（只读，供外部诊断/扩展使用）。
        /// </summary>
        public static Inventory? RuntimeInventory => _runtimeInventory;

        /// <summary>
        /// 数据是否已就绪（就绪前不会写盘）。
        /// </summary>
        public static bool IsDataReady => _isDataReady;

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
            if (_initialized && !string.IsNullOrWhiteSpace(_filePath)) return;

            try
            {
                // 一次性校验本模组依赖的游戏私有成员，缺失时给出明确告警
                GameReflection.VerifyContract();

                string root = Path.Combine(Application.persistentDataPath, SAVE_ROOT_DIR);
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);

                _filePath = Path.Combine(root, SAVE_FILE_NAME);
                _backupDir = ResolveBackupDir(root);

                EnsureFileCached(_filePath);

                // 在启动时备份现有保存，然后再处理
                CreateBackup(_filePath, "SessionStart");

                Load();
                _initialized = true;

                Debug.Log($"{LOG_TAG} 存档: {_filePath}");
                Debug.Log($"{LOG_TAG} 备份目录: {_backupDir ?? "(与主存档同目录)"}");
            }
            catch (Exception ex)
            {
                _initialized = false;
                Debug.LogError($"{LOG_TAG} 初始化失败: {ex}");
            }
        }

        /// <summary>
        /// 解析备份目录：默认放在<b>模组文件夹</b>下的 backups/，避开游戏存档目录。
        /// <para>ModConfig.BackupFolder 为空 → 回退到主存档同目录（旧行为）。</para>
        /// </summary>
        /// <param name="saveRoot">主存档所在目录</param>
        private static string? ResolveBackupDir(string saveRoot)
        {
            string configured = ModConfig.BackupFolder;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null; // 与主存档同目录
            }

            string dir = configured;
            try
            {
                if (!Path.IsPathRooted(dir))
                {
                    // 相对路径 → 相对模组目录（info.path）
                    string modDir = ModConfig.ModDirectory ?? saveRoot;
                    dir = Path.Combine(modDir, configured);
                }

                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return dir;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LOG_TAG} 备份目录不可用（{dir}），回退到与主存档同目录: {ex.Message}");
                return null;
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
                if (!File.Exists(path))
                {
                    ES3.Save("Created", true, path);
                    ES3.StoreCachedFile(path);
                    ES3.CacheFile(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LOG_TAG} EnsureFileCached 警告: {ex.Message}");
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
        /// 保存仓库数据。
        /// </summary>
        /// <param name="force">
        /// true 时跳过防抖（用于游戏存档事件，必须与游戏落盘同步，避免出现
        /// "游戏存档已写入但仓库数据没写" 的不一致窗口）。
        /// </param>
        public static void Save(bool force = false)
        {
            try
            {
                // 只有成功初始化过才允许写盘，避免每次存档事件都触发一次失败的初始化重试
                if (!_initialized)
                {
                    if (string.IsNullOrWhiteSpace(_filePath)) Initialize();
                }

                string? path = _filePath;
                if (!_initialized || string.IsNullOrWhiteSpace(path)) return;

                // 安全检查：如果数据加载不正确，请勿保存
                // 这可以防止用不完整/损坏的状态覆盖良好的保存
                if (!_isDataReady)
                {
                    Debug.LogWarning($"{LOG_TAG} 保存跳过: 数据未准备好或之前加载失败");
                    return;
                }

                // 防抖保存（仅抑制连续重复写盘；force 时跳过）
                if (!force && ModConfig.SaveDebounceSeconds > 0f &&
                    (DateTime.UtcNow - _lastSaveTime) < TimeSpan.FromSeconds(ModConfig.SaveDebounceSeconds))
                {
                    return;
                }
                _lastSaveTime = DateTime.UtcNow;

                string filePath = path!;

                // 轮换备份：写盘前先把当前文件轮换为 .bac.01（与游戏存档备份策略一致）
                RotateBackup(filePath);

                if (_runtimeInventory != null)
                {
                    _snapshot = InventoryData.FromInventory(_runtimeInventory);
                }

                if (_snapshot == null)
                {
                    _snapshot = CreateEmptySnapshot();
                }

                var settings = new ES3Settings(filePath) { location = ES3.Location.File };
                ES3.Save(KEY_INVENTORY, _snapshot, filePath, settings);
                ES3.Save(KEY_VERSION, 1.0f, filePath, settings);

                try { ES3.CacheFile(filePath); } catch { }
                ES3.StoreCachedFile(filePath);

                // 诊断日志：确认保存确实落盘、以及备份是否生成（便于排查外部删除）
                int entryCount = _snapshot?.entries?.Count ?? 0;
                int backupCount = CountBackupFiles(filePath);
                Debug.Log($"{LOG_TAG} 保存完成: {entryCount} 条记录, 容量 {_runtimeInventory?.Capacity ?? -1}, " +
                          $"备份文件 {backupCount}/{ModConfig.BackupCount}" +
                          (force ? "（游戏存档事件）" : ""));
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_TAG} 保存失败: {ex}");
            }
        }

        /// <summary>
        /// 统计当前已存在的轮换备份文件数量（仅用于诊断）。
        /// </summary>
        private static int CountBackupFiles(string path)
        {
            int n = 0;
            int limit = Math.Max(ModConfig.BackupCount, 1);
            for (int i = 1; i <= limit; i++)
            {
                try
                {
                    if (FileOps.Exists(GetBackupPathByIndex(path, i, _backupDir))) n++;
                }
                catch { }
            }
            return n;
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
                if (!File.Exists(_filePath))
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
                    Debug.Log($"{LOG_TAG} 库存数据已迁移到新键");
                }

                // 无条件清理历史遗留键（旧版本写入），避免文件无限膨胀
                CleanupLegacyKeys(_filePath, settings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_TAG} 加载失败: {ex}");
                _isDataReady = false; // 标记为失败
            }
        }

        /// <summary>
        /// 清理历史版本遗留的 ES3 键。
        /// </summary>
        private static void CleanupLegacyKeys(string path, ES3Settings settings)
        {
            try
            {
                bool removed = false;
                if (ES3.KeyExists(KEY_OLD_INVENTORY, path, settings))
                {
                    ES3.DeleteKey(KEY_OLD_INVENTORY, path, settings);
                    removed = true;
                }
                if (ES3.KeyExists(KEY_UI_ICON_X, path, settings))
                {
                    ES3.DeleteKey(KEY_UI_ICON_X, path, settings);
                    removed = true;
                }
                if (ES3.KeyExists(KEY_UI_ICON_Y, path, settings))
                {
                    ES3.DeleteKey(KEY_UI_ICON_Y, path, settings);
                    removed = true;
                }

                if (removed)
                {
                    try { ES3.CacheFile(path); } catch { }
                    ES3.StoreCachedFile(path);
                    Debug.Log($"{LOG_TAG} 已清理历史遗留的存档键");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LOG_TAG} 清理遗留键失败（不影响运行）: {ex.Message}");
            }
        }

        /// <summary>
        /// 将可交互对象附加到玩家存储
        /// </summary>
        public static void AttachInteractableToPlayerStorage()
        {
            if (!IsBaseLevel())
            {
                return;
            }
            if (PlayerStorage.Instance == null)
            {
                Debug.LogWarning($"{LOG_TAG} 跳过挂载: PlayerStorage.Instance 为 null（基地对象尚未就绪）");
                return;
            }

            // 检查 lootbox 对象是否已被销毁
            if (_lootbox != null && _lootbox.gameObject == null)
            {
                _lootbox = null;
                _runtimeInventory = null;
            }

            if (_lootbox == null)
            {
                CreateStashObject();
                Debug.Log($"{LOG_TAG} 已创建独立仓库对象（库存 {(IsDataReady ? "已就绪" : "加载中")}）");
            }
            else
            {
                // 检查 _runtimeInventory 是否仍然有效（防止场景切换等导致引用失效）
                // 如果引用失效，inventoryReference 会变成 null，导致 InteractableLootbox.Inventory
                // 回退到 GetOrCreateInventory(this)，由于位置键冲突可能返回错误的库存
                if (_runtimeInventory == null || _runtimeInventory.gameObject == null)
                {
                    Debug.LogWarning($"{LOG_TAG} _runtimeInventory 已失效，正在重新创建...");
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
            if (_lootbox == null || LootView.Instance == null)
            {
                Debug.LogWarning($"{LOG_TAG} 无法打开仓库: " +
                                 $"lootbox={( _lootbox == null ? "null" : "ok")}, " +
                                 $"LootView.Instance={(LootView.Instance == null ? "null" : "ok")}");
                return;
            }

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
        /// 是否处于基地关卡（权威判定）。
        /// <para>
        /// 使用 <see cref="LevelConfig.IsBaseLevel"/>，而非按场景名包含 "Base" 猜测
        /// （突袭图 Level_SnowMilitaryBase 等也含 "Base"，会被误判）。
        /// </para>
        /// </summary>
        public static bool IsBaseLevel()
        {
            if (LevelManager.Instance == null) return false;
            try
            {
                return LevelConfig.IsBaseLevel;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LOG_TAG} 读取 LevelConfig.IsBaseLevel 失败，回退到 LevelManager.IsBaseLevel: {ex.Message}");
                return LevelManager.Instance.IsBaseLevel;
            }
        }

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
            // 偏移使用 ModConfig.StashOffset（默认 (0,-0.5,0)，可在 config.ini 调整）：
            // 避免与父级 lootbox 的位置键冲突（GetKey 用 position*10 取整哈希）
            var stashPos = parentLootbox.transform.position + ModConfig.StashOffset;
            go.transform.SetPositionAndRotation(stashPos, parentLootbox.transform.rotation);

            _lootbox = go.AddComponent<InteractableLootbox>();

            // 修复：在 Awake 运行前初始化列表
            // InteractableBase.Awake 会遍历此列表，而通过 AddComponent 添加时它是 null
            GameReflection.EnsureInteractableGroupList(_lootbox);

            SetDisplayName(_lootbox, StashDisplayName);
            _lootbox.InteractName = StashDisplayName;
            _lootbox.useDefaultInteractName = false;

            // 不需要设置 showPickAllButton：游戏 LootView.RefreshPickAllButton
            // 实际无条件隐藏"全部拾取"按钮（已通过 IL 验证），该字段不生效，
            // 此处不再设置，避免误导后续维护者。

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
            _runtimeInventory.SetCapacity(ModConfig.Capacity);

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

            // 首选：触发静态事件 InteractableLootbox.OnStartLoot，
            // 游戏 LootView 已订阅它（LootView.Awake），会完成 targetLootBox 设置 + Open()。
            if (GameReflection.RaiseStaticEvent(typeof(InteractableLootbox), GameReflection.EVENT_ON_START_LOOT, new object[] { _lootbox }))
            {
                return;
            }

            // 回退：直接把 LootView 的 targetLootBox 指向本仓库并打开
            // （当 OnStartLoot 无订阅者或游戏改动了事件实现时使用）
            var view = LootView.Instance;
            if (view == null)
            {
                Debug.LogWarning($"{LOG_TAG} 无法打开仓库：LootView.Instance 为 null");
                return;
            }

            if (!GameReflection.SetFieldValue(view, GameReflection.FIELD_LOOT_VIEW_TARGET_BOX, _lootbox))
            {
                Debug.LogError($"{LOG_TAG} 无法打开仓库：设置 LootView.targetLootBox 失败");
                return;
            }

            view.Open();
            Debug.LogWarning($"{LOG_TAG} 已通过回退路径打开仓库（OnStartLoot 事件未命中订阅者）");
        }

        /// <summary>
        /// 创建备份
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <param name="suffix">备份后缀</param>
        private static void CreateBackup(string path, string suffix)
        {
            // 注意：这里必须用 System.IO 而不是 ES3 —— 本游戏 ES3 默认 location = Cache，
            // 裸 ES3 调用不会落盘（详见 SystemIoBackupFileOps 注释）。
            if (!File.Exists(path)) return;

            var backupPath = GetBackupFilePath(path, suffix);
            try
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Copy(path, backupPath, overwrite: true);
                Debug.Log($"{LOG_TAG} 创建备份保存文件: {suffix} -> {backupPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LOG_TAG} 创建备份失败 ({suffix}): {ex.Message}");
            }
        }

        /// <summary>
        /// 生成备份文件路径。备份默认放在 <see cref="ModConfig.BackupFolder"/> 指定的目录
        /// （默认 = 模组文件夹下的 <c>backups/</c>），文件名沿用 <c>&lt;主存档名&gt;.&lt;后缀&gt;</c>。
        /// </summary>
        /// <param name="savePath">主存档路径</param>
        /// <param name="suffix">后缀（如 SessionStart / bac.01）</param>
        private static string GetBackupFilePath(string savePath, string suffix)
        {
            string? dir = _backupDir;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{LOG_TAG} 备份目录创建失败，回退到存档目录: {ex.Message}");
                    dir = null;
                }
            }
            return GetBackupFilePathIn(savePath, suffix, dir);
        }

        /// <summary>
        /// 轮换备份：把当前存档复制为 <c>.bac.01</c>，并把已有备份依次后移。
        /// <para>
        /// 命名与轮换策略对齐游戏存档：<c>MyStash.sav</c> + <c>MyStash.sav.bac.01</c> …
        /// <c>MyStash.sav.bac.NN</c>，其中 <c>.bac.01</c> 最新，<c>.bac.NN</c> 最旧。
        /// </para>
        /// </summary>
        /// <param name="path">主存档路径</param>
        private static void RotateBackup(string path)
        {
            int count = ModConfig.BackupCount;
            if (count <= 0) return;
            if (!File.Exists(path)) return;

            try
            {
                PerformBackupRotation(path, count, _backupDir);
            }
            catch (Exception ex)
            {
                // 打印完整异常（含类型与堆栈），便于定位问题
                Debug.LogWarning($"{LOG_TAG} 轮换备份失败（不影响保存）: {ex}");
            }
        }

        /// <summary>
        /// 备份轮换的纯文件操作（不写日志，便于离线测试）。
        /// <para>
        /// 文件操作通过 <see cref="FileOps"/> 注入，默认实现走 ES3；
        /// 测试时可替换为 <c>System.IO</c> 版本，从而脱离 Unity 运行。
        /// </para>
        /// </summary>
        /// <param name="path">主存档路径</param>
        /// <param name="count">保留份数</param>
        /// <param name="backupDir">备份目录；为 null/空 时与主存档同目录</param>
        internal static void PerformBackupRotation(string path, int count, string? backupDir = null)
        {
            // 从最旧的一份开始，依次向后挪一位，丢弃超出份数的旧备份
            string oldest = GetBackupPathByIndex(path, count, backupDir);
            if (FileOps.Exists(oldest))
            {
                FileOps.Delete(oldest);
            }

            for (int i = count - 1; i >= 1; i--)
            {
                string from = GetBackupPathByIndex(path, i, backupDir);
                if (!FileOps.Exists(from)) continue;

                string to = GetBackupPathByIndex(path, i + 1, backupDir);
                if (FileOps.Exists(to))
                {
                    FileOps.Delete(to);
                }
                FileOps.Copy(from, to);
            }

            string newest = GetBackupPathByIndex(path, 1, backupDir);
            if (FileOps.Exists(newest))
            {
                FileOps.Delete(newest);
            }
            FileOps.Copy(path, newest);
        }

        /// <summary>
        /// 备份轮换使用的文件操作抽象（可注入，默认 System.IO 实现）。
        /// </summary>
        public interface IBackupFileOps
        {
            bool Exists(string path);
            void Copy(string from, string to);
            void Delete(string path);
        }

        /// <summary>当前文件操作实现。</summary>
        public static IBackupFileOps FileOps { get; private set; } = new SystemIoBackupFileOps();

        /// <summary>替换文件操作实现（仅用于离线测试）。</summary>
        public static void SetFileOps(IBackupFileOps ops)
        {
            FileOps = ops ?? new SystemIoBackupFileOps();
        }

        /// <summary>
        /// 基于 <see cref="System.IO"/> 的实现。
        /// <para>
        /// <b>不能用 ES3</b>：本游戏的 <c>ES3Defaults</c> 资产把默认 <c>location</c> 设成了
        /// <c>Cache</c>（内存缓存），裸 ES3 调用（<c>ES3.CopyFile</c> / <c>ES3.FileExists</c>）
        /// 会在内存里操作、不落盘，且进程结束后缓存消失，导致备份文件永远不存在。
        /// 游戏自身的存档系统也是显式传 <c>location = ES3.Location.File</c> 才写盘的。
        /// </para>
        /// </summary>
        private sealed class SystemIoBackupFileOps : IBackupFileOps
        {
            public bool Exists(string path) => File.Exists(path);

            public void Copy(string from, string to)
            {
                string? dir = Path.GetDirectoryName(to);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.Copy(from, to, overwrite: true);
            }

            public void Delete(string path)
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// 轮换备份路径（index 从 1 开始，1 为最新）。
        /// </summary>
        internal static string GetBackupPathByIndex(string path, int index, string? backupDir = null)
        {
            return GetBackupFilePathIn(path, $"bac.{index:00}", backupDir);
        }

        /// <summary>
        /// 在指定备份目录下生成备份文件路径（不访问 <see cref="ModConfig"/>，便于离线测试）。
        /// </summary>
        /// <param name="savePath">主存档路径</param>
        /// <param name="suffix">后缀</param>
        /// <param name="backupDir">备份目录；为 null/空 时与主存档同目录</param>
        internal static string GetBackupFilePathIn(string savePath, string suffix, string? backupDir)
        {
            if (string.IsNullOrWhiteSpace(backupDir))
            {
                return $"{savePath}.{suffix}";
            }
            return Path.Combine(backupDir, $"{Path.GetFileName(savePath)}.{suffix}");
        }

        /// <summary>
        /// 创建空快照
        /// </summary>
        /// <returns>空的库存数据</returns>
        private static InventoryData CreateEmptySnapshot()
        {
            var temp = new GameObject("IndependentStashTempInv");
            try
            {
                var inv = temp.AddComponent<Inventory>();
                // 与真实库存保持一致的容量，避免快照里出现与配置不符的 capacity
                inv.SetCapacity(ModConfig.Capacity);
                return InventoryData.FromInventory(inv);
            }
            finally
            {
                UnityEngine.Object.Destroy(temp);
            }
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
                Debug.LogError($"{LOG_TAG} LoadInventoryDataAsync 严重失败: {ex}");
                Debug.LogError($"{LOG_TAG} 保存现已禁用，以防止数据丢失。请检查您的模组。");

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
                    Debug.LogWarning($"{LOG_TAG} 由于加载失败，初始化空库存（只读模式）");
                    try
                    {
                        var emptySnapshot = CreateEmptySnapshot();
                        await InventoryData.LoadIntoInventory(emptySnapshot, inventory);
                    }
                    catch { }
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
            // 注意：标签名必须是游戏的 Tag 资源名（不是本地化键、也不是本地化显示文本）：
            //   "Accessory"（配件，显示名 "Attachment"）而非 "Attachment"
            //   "Medic"（医疗用品，显示名 "Medicine"）而非 "Medicine"
            var tags = GameplayDataSettings.Tags;
            mine.entries = new InventoryFilterProvider.FilterEntry[]
            {
                new() { name = "ItemFilter_All", requireTags = Array.Empty<Tag>() }, // 全部
                new() { name = "ItemFilter_Weapon", requireTags = new[] { tags.Gun } }, // 武器
                new() { name = "ItemFilter_Bullet", requireTags = new[] { tags.Bullet } }, // 子弹
                new() { name = "ItemFilter_Equipment", requireTags = new[] { tags.Armor, tags.Helmat, tags.Backpack } }, // 装备
                new() { name = "ItemFilter_Accessory", requireTags = new[] { TagFromString("Accessory", tags.Special) } }, // 配件
                new() { name = "ItemFilter_Totem", requireTags = new[] { TagFromString("Totem", tags.Special) } }, // 图腾
                new() { name = "ItemFilter_Medic", requireTags = new[] { TagFromString("Medic", tags.Special) } }, // 医疗
                new() { name = "ItemFilter_Food", requireTags = new[] { TagFromString("Food", tags.Bait) } }, // 食物
                new() { name = "ItemFilter_Other", requireTags = new[] { tags.Special } } // 其他
            };
        }

        /// <summary>
        /// 按名字取 Tag，未命中时回退到 <paramref name="fallback"/>（避免 null 引发 NRE）。
        /// </summary>
        private static Tag TagFromString(string name, Tag fallback)
        {
            var tag = TagUtilities.TagFromString(name);
            return tag ?? fallback;
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
            return GameReflection.GetFieldValue<Button>(view, GameReflection.FIELD_STORE_ALL_BUTTON);
        }

        /// <summary>
        /// 尝试将可交互对象注入到组中
        /// </summary>
        /// <param name="master">主可交互对象</param>
        /// <param name="other">其他可交互对象</param>
        private static void TryInjectIntoGroup(InteractableBase master, InteractableBase other)
        {
            if (master == null || other == null) return;
            if (!ModConfig.InjectToInteractGroup)
            {
                // 保持官方仓库的交互表现不变：不注入、不改 interactableGroup
                return;
            }

            try
            {
                var list = GameReflection.GetFieldValue<List<InteractableBase>>(master, GameReflection.FIELD_OTHER_INTERACTABLES);
                if (list == null)
                {
                    // 官方 lootbox 的列表为 null（理论上不会，Awake 会初始化）→ 自行初始化
                    list = new List<InteractableBase>();
                    GameReflection.SetFieldValue(master, GameReflection.FIELD_OTHER_INTERACTABLES, list);
                }

                if (!list.Contains(other))
                {
                    list.Add(other);
                }

                // 只有 interactableGroup 为 true 时，GetInteractableList() 才会包含组内其他对象
                if (!master.interactableGroup)
                {
                    master.interactableGroup = true;
                }

                // 隐藏独立仓库自身的交互标记，避免基地里多出一个可交互物件
                GameReflection.SetFieldValue(other, GameReflection.FIELD_MARKER_VISIBLE, false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_TAG} TryInjectIntoGroup 失败: {ex}");
            }
        }

        /// <summary>
        /// 设置显示名称
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <param name="text">显示文本</param>
        private static void SetDisplayName(InteractableLootbox lootbox, string text)
        {
            GameReflection.SetFieldValue(lootbox, GameReflection.FIELD_DISPLAY_NAME_KEY, text);
        }

        /// <summary>
        /// 设置库存引用
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <param name="inventory">库存对象</param>
        private static void SetInventoryReference(InteractableLootbox lootbox, Inventory inventory)
        {
            GameReflection.SetFieldValue(lootbox, GameReflection.FIELD_INVENTORY_REF, inventory);
        }

        /// <summary>
        /// 获取库存引用
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <returns>库存对象</returns>
        private static Inventory? GetInventoryReference(InteractableLootbox lootbox)
        {
            return GameReflection.GetFieldValue<Inventory>(lootbox, GameReflection.FIELD_INVENTORY_REF);
        }

        /// <summary>
        /// 设置是否显示排序按钮
        /// </summary>
        /// <param name="lootbox">Lootbox 对象</param>
        /// <param name="value">是否显示</param>
        private static void SetShowSortButton(InteractableLootbox lootbox, bool value)
        {
            GameReflection.SetFieldValue(lootbox, GameReflection.FIELD_SHOW_SORT_BUTTON, value);
        }

        #endregion
    }
}

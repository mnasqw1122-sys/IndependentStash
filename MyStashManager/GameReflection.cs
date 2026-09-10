using System;
using System.Collections.Generic;
using System.Reflection;
using Duckov.UI;
using UnityEngine;
using UnityEngine.UI;

namespace IndependentStash
{
    /// <summary>
    /// 对游戏私有字段/静态事件后备字段的反射访问统一收口。
    /// <para>
    /// 游戏未提供公开 API 操作这些成员，因此这里集中反射访问并做缓存 / 继承链查找 / 异常保护，
    /// 避免在业务代码里散落反射逻辑（参考同项目 DailyQuests 的 InteractableGroupHelper 写法）。
    /// </para>
    /// <para>
    /// <b>契约自检</b>：<see cref="VerifyContract"/> 会在初始化时一次性检查所有依赖的私有成员，
    /// 任一缺失都会打印明确的错误日志（含缺失项清单），便于游戏更新后快速定位，
    /// 而不是等到运行时静默失效。
    /// </para>
    /// </summary>
    internal static class GameReflection
    {
        private const string LOG_TAG = "[IndependentStash]";

        // 依赖的成员名（与游戏 v2.3.30 实装 DLL 逐一核对过）
        public const string FIELD_STORE_ALL_BUTTON = "storeAllButton";
        public const string EVENT_ON_START_LOOT = "OnStartLoot";
        public const string FIELD_OTHER_INTERACTABLES = "otherInterablesInGroup";
        public const string FIELD_MARKER_VISIBLE = "interactMarkerVisible";
        public const string FIELD_DISPLAY_NAME_KEY = "displayNameKey";
        public const string FIELD_INVENTORY_REF = "inventoryReference";
        public const string FIELD_SHOW_SORT_BUTTON = "showSortButton";
        public const string FIELD_LOOT_VIEW_TARGET_BOX = "targetLootBox";

        private static readonly Dictionary<string, FieldInfo?> FieldCache = new Dictionary<string, FieldInfo?>();
        private static readonly Dictionary<string, EventInfo?> EventCache = new Dictionary<string, EventInfo?>();

        /// <summary>
        /// 契约自检是否已执行（只执行一次）。
        /// </summary>
        public static bool ContractVerified { get; private set; }

        /// <summary>
        /// 契约自检结果：缺失的成员名列表。为空表示全部命中。
        /// </summary>
        public static List<string> MissingMembers { get; } = new List<string>();

        #region 契约自检

        /// <summary>
        /// 一次性校验本模组依赖的全部游戏私有成员。
        /// </summary>
        /// <returns>全部命中返回 true；否则返回 false 并已打印缺失清单。</returns>
        public static bool VerifyContract()
        {
            if (ContractVerified) return MissingMembers.Count == 0;

            CollectMissingMembers();

            if (MissingMembers.Count > 0)
            {
                Debug.LogError($"{LOG_TAG} 反射契约自检失败，以下游戏成员缺失（游戏可能已更新）：" +
                               $"\n  - {string.Join("\n  - ", MissingMembers)}" +
                               "\n模组部分功能将不可用，请检查游戏版本或等待模组更新。");
                return false;
            }

            Debug.Log($"{LOG_TAG} 反射契约自检通过（{CheckedCount} 项依赖成员全部命中）");
            return true;
        }

        /// <summary>
        /// 纯逻辑版契约检查：只填充 <see cref="MissingMembers"/>，不写日志。
        /// <para>与 Unity 运行时解耦，便于离线单元测试。</para>
        /// </summary>
        public static void CollectMissingMembers()
        {
            ContractVerified = true;
            MissingMembers.Clear();

            CheckField(typeof(LootView), FIELD_STORE_ALL_BUTTON, typeof(Button));
            CheckField(typeof(LootView), FIELD_LOOT_VIEW_TARGET_BOX, typeof(InteractableLootbox));
            CheckField(typeof(InteractableBase), FIELD_OTHER_INTERACTABLES, typeof(List<InteractableBase>));
            CheckField(typeof(InteractableBase), FIELD_MARKER_VISIBLE, typeof(bool));
            CheckField(typeof(InteractableLootbox), FIELD_DISPLAY_NAME_KEY, typeof(string));
            CheckField(typeof(InteractableLootbox), FIELD_INVENTORY_REF, typeof(ItemStatsSystem.Inventory));
            CheckField(typeof(InteractableLootbox), FIELD_SHOW_SORT_BUTTON, typeof(bool));
            CheckEvent(typeof(InteractableLootbox), EVENT_ON_START_LOOT);
        }

        /// <summary>本次自检检查的成员总数。</summary>
        public const int CheckedCount = 8;

        private static void CheckField(Type type, string fieldName, Type expectedType)
        {
            var field = FindField(type, fieldName);
            if (field == null)
            {
                MissingMembers.Add($"{type.Name}.{fieldName} (字段不存在)");
                return;
            }
            if (expectedType != null && !TypeMatches(field.FieldType, expectedType))
            {
                MissingMembers.Add($"{type.Name}.{fieldName} (类型不匹配: 期望 {expectedType.Name}, 实际 {field.FieldType.Name})");
            }
        }

        /// <summary>
        /// 类型匹配判断，兼容泛型集合（如字段是 <c>List&lt;InteractableBase&gt;</c> 而期望 <c>List&lt;InteractableBase&gt;</c>）。
        /// </summary>
        private static bool TypeMatches(Type actual, Type expected)
        {
            if (expected.IsGenericTypeDefinition)
            {
                return actual.IsGenericType && actual.GetGenericTypeDefinition() == expected;
            }
            if (expected.IsGenericType && !expected.IsGenericTypeDefinition)
            {
                if (!actual.IsGenericType) return false;
                if (actual.GetGenericTypeDefinition() != expected.GetGenericTypeDefinition()) return false;

                var actualArgs = actual.GetGenericArguments();
                var expectedArgs = expected.GetGenericArguments();
                if (actualArgs.Length != expectedArgs.Length) return false;
                for (int i = 0; i < actualArgs.Length; i++)
                {
                    if (!TypeMatches(actualArgs[i], expectedArgs[i])) return false;
                }
                return true;
            }
            return expected.IsAssignableFrom(actual);
        }

        private static void CheckEvent(Type type, string eventName)
        {
            if (FindEvent(type, eventName) == null)
            {
                MissingMembers.Add($"{type.Name}.{eventName} (事件不存在)");
            }
        }

        #endregion

        #region 字段访问

        /// <summary>
        /// 沿继承链向上查找字段（私有字段可能定义在基类上）。
        /// </summary>
        /// <param name="type">起始类型</param>
        /// <param name="fieldName">字段名</param>
        /// <returns>找到的 FieldInfo，未找到返回 null</returns>
        public static FieldInfo? FindField(Type? type, string fieldName)
        {
            if (type == null) return null;

            string cacheKey = type.FullName + "::" + fieldName;
            if (FieldCache.TryGetValue(cacheKey, out var cached)) return cached;

            FieldInfo? field = null;
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                field = current.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) break;
            }

            FieldCache[cacheKey] = field;
            return field;
        }

        /// <summary>
        /// 读取实例字段值。
        /// </summary>
        public static T? GetFieldValue<T>(object? instance, string fieldName, T? fallback = default)
        {
            if (instance == null) return fallback;
            try
            {
                var field = FindField(instance.GetType(), fieldName);
                if (field == null) return fallback;
                var value = field.GetValue(instance);
                return value is T typed ? typed : fallback;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_TAG} 读取字段 {instance.GetType().Name}.{fieldName} 失败: {ex}");
                return fallback;
            }
        }

        /// <summary>
        /// 写入实例字段值。
        /// </summary>
        /// <returns>写入成功返回 true</returns>
        public static bool SetFieldValue(object? instance, string fieldName, object? value)
        {
            if (instance == null) return false;
            try
            {
                var field = FindField(instance.GetType(), fieldName);
                if (field == null)
                {
                    Debug.LogError($"{LOG_TAG} 写入字段失败：{instance.GetType().Name}.{fieldName} 不存在");
                    return false;
                }
                field.SetValue(instance, value);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_TAG} 写入字段 {instance.GetType().Name}.{fieldName} 失败: {ex}");
                return false;
            }
        }

        /// <summary>
        /// 确保 <see cref="InteractableBase.otherInterablesInGroup"/> 已初始化。
        /// <para>
        /// 通过 AddComponent 动态创建的 Interactable 该字段为 null，
        /// 而 <c>InteractableBase.Awake</c> 会无条件遍历它 → 必须先初始化，否则 NRE。
        /// </para>
        /// </summary>
        public static bool EnsureInteractableGroupList(InteractableBase owner)
        {
            if (owner == null) return false;
            try
            {
                var field = FindField(typeof(InteractableBase), FIELD_OTHER_INTERACTABLES);
                if (field == null) return false;
                if (field.GetValue(owner) == null)
                {
                    field.SetValue(owner, new List<InteractableBase>());
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_TAG} 初始化 {FIELD_OTHER_INTERACTABLES} 失败: {ex}");
                return false;
            }
        }

        #endregion

        #region 事件访问

        /// <summary>
        /// 查找事件（含继承链）。
        /// </summary>
        public static EventInfo? FindEvent(Type? type, string eventName)
        {
            if (type == null) return null;

            string cacheKey = type.FullName + "::" + eventName;
            if (EventCache.TryGetValue(cacheKey, out var cached)) return cached;

            EventInfo? eventInfo = null;
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                eventInfo = current.GetEvent(eventName,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (eventInfo != null) break;
            }

            EventCache[cacheKey] = eventInfo;
            return eventInfo;
        }

        /// <summary>
        /// 触发游戏内静态事件（C# event 的编译器后备字段与事件同名）。
        /// <para>
        /// 先用同名私有静态字段取订阅者列表（C# 事件的标准实现），
        /// 字段取不到时再退回 <see cref="EventInfo"/>，以兼容自定义 add/remove 访问器。
        /// </para>
        /// </summary>
        /// <param name="type">声明事件的类型</param>
        /// <param name="eventName">事件名</param>
        /// <param name="args">传给订阅者的参数</param>
        /// <returns>至少成功调用了一个订阅者返回 true</returns>
        public static bool RaiseStaticEvent(Type? type, string eventName, object?[] args)
        {
            if (type == null) return false;
            try
            {
                var backingField = FindField(type, eventName);
                var eventInfo = backingField == null ? FindEvent(type, eventName) : null;
                if (backingField == null && eventInfo == null)
                {
                    Debug.LogError($"{LOG_TAG} 触发静态事件失败：{type.Name}.{eventName} 不存在");
                    return false;
                }

                object? handler = backingField?.GetValue(null);
                if (handler is not MulticastDelegate multicast)
                {
                    // 无订阅者：不算错误
                    return false;
                }

                bool anyInvoked = false;
                foreach (var invocation in multicast.GetInvocationList())
                {
                    try
                    {
                        invocation.Method.Invoke(invocation.Target, args);
                        anyInvoked = true;
                    }
                    catch (TargetInvocationException tie)
                    {
                        Debug.LogError($"{LOG_TAG} 调用 {type.Name}.{eventName} 订阅者 {invocation.Method.Name} 抛出异常: {tie.InnerException ?? tie}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"{LOG_TAG} 调用 {type.Name}.{eventName} 订阅者失败: {ex}");
                    }
                }
                return anyInvoked;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_TAG} 触发静态事件 {type.Name}.{eventName} 失败: {ex}");
                return false;
            }
        }

        #endregion
    }
}

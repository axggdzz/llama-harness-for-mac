using System.Collections.Specialized;
using System.Net;

namespace LlamaHarness;

/// <summary>
/// 多槽亲和绑定（--parallel &gt; 1 时启用）：
/// - 指纹识别：从请求头识别四大业务，生成唯一亲和 Key（零客户端侵入）
/// - 槽位绑定：Key → 槽号，持久化 slot_bindings.json（重启恢复）
/// - 强占模式：preemptive=true 的绑定不可被驱逐；全被强占占满时排队等待（上限 30s），超时降级随机槽
/// - KV Cache 开关：驱逐时是否保存 KV Cache（kvCache=false → 直接丢弃不保存）
/// - LRU 驱逐：跳过强占绑定，驱逐最久未活跃的非强占绑定
/// - 未知请求：随机槽位，不建立永久绑定
/// </summary>
public sealed class SlotAffinity
{
    private readonly int _slotCount;
    private readonly object _gate = new();
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>绑定表持久化文件（exe 同目录）。</summary>
    private static readonly string BindingsPath = Path.Combine(AppContext.BaseDirectory, "slot_bindings.json");

    /// <summary>排队等待上限（秒）。全槽被强占时新请求最多等这么久。</summary>
    private const int MaxWaitSeconds = 30;

    internal struct Binding
    {
        public int Slot;
        public DateTime LastActive;
        public bool Preemptive;
        public bool KvCache;
    }

    public SlotAffinity(int slotCount)
    {
        _slotCount = Math.Max(1, slotCount);
        Load();
    }

    /// <summary>槽位数。</summary>
    public int SlotCount => _slotCount;

    /// <summary>指纹识别：返回亲和 Key；null = 未知请求（不建立绑定）。</summary>
    public static string? GetAffinityKey(NameValueCollection h)
    {
        // 优先级1：DSH 规则引擎（用户级永久绑定，最精准）
        if (TryGetHeader(h, "x-deepseek-harness-user-id", out var uid) && !string.IsNullOrEmpty(uid))
            return $"dsh_rule_{uid}";

        // 优先级2：WebUI（会话级绑定）
        if (TryGetHeader(h, "X-Conversation-Id", out var cid) && !string.IsNullOrEmpty(cid))
            return $"webui_{cid}";

        // 优先级3：Trae Work（独家特征头）
        if (TryGetHeader(h, "x-model-provider", out var mp) && string.Equals(mp, "custom_openai_compatible", StringComparison.OrdinalIgnoreCase))
            return "trae_global";

        // 优先级4：DSH 主 Agent（UA + X-Stainless 系列头）
        var ua = TryGetHeader(h, "User-Agent", out var uaVal) ? uaVal : "";
        bool hasStainless = false;
        foreach (var k in h.AllKeys)
        {
            if (k != null && k.StartsWith("X-Stainless-", StringComparison.OrdinalIgnoreCase)) { hasStainless = true; break; }
        }
        if (ua.Contains("deepseek-harness", StringComparison.OrdinalIgnoreCase) && hasStainless)
            return "dsh_agent_global";

        return null; // 未知请求 → 轮询，不绑定
    }

    /// <summary>根据亲和 Key 前缀派生应用显示名。</summary>
    public static string AppNameOf(string key)
    {
        if (key.StartsWith("trae_")) return "Trae Work";
        if (key.StartsWith("webui_")) return "WebUI";
        if (key.StartsWith("dsh_rule_")) return "DSH 规则引擎";
        if (key.StartsWith("dsh_agent_")) return "DSH 主 Agent";
        return "未知应用";
    }

    /// <summary>
    /// 获取请求的槽位：已绑定 → 其槽位（刷新活跃时间）；新 Key → 空闲槽或 LRU 驱逐。
    /// 全被强占占满 → 排队等待（上限 30s），超时降级随机槽。
    /// </summary>
    /// <param name="autoPreemptive">自动强占前缀集合（§4.2 主力会话冻结）：key 匹配任一前缀 → 强制 Preemptive=true（暂停 LRU 驱逐）。</param>
    /// <returns>(slot, key, isNewBinding, evictedKey, evictedSlot, evictedKvCache)</returns>
    public (int Slot, string? Key, bool NewBinding, string? Evicted, int EvictedSlot, bool EvictedKvCache) GetSlot(
        NameValueCollection headers, IReadOnlyList<string>? autoPreemptive = null)
    {
        lock (_gate)
        {
            var key = GetAffinityKey(headers);
            if (string.IsNullOrEmpty(key))
                return (Random.Shared.Next(_slotCount), null, false, null, -1, false);

            // §4.2：应用类型在自动强占集合 → 强制冻结（新绑定创建时 + 已有绑定每次访问）
            bool autoPre = autoPreemptive != null && autoPreemptive.Any(p => !string.IsNullOrEmpty(p) && key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (_bindings.TryGetValue(key, out var b))
            {
                _bindings[key] = new Binding { Slot = b.Slot, LastActive = DateTime.Now, Preemptive = b.Preemptive || autoPre, KvCache = b.KvCache };
                return (b.Slot, key, false, null, -1, false);
            }

            // 新 Key：优先分配无其他绑定的槽；全占则驱逐最久未活跃的非强占绑定
            string? evicted = null;
            int evictedSlot = -1;
            bool evictedKvCache = false;
            int slot = FindFreeSlotLocked();
            if (slot < 0)
            {
                // 找可驱逐目标（非强占）
                var lruKey = _bindings.Where(kv => !kv.Value.Preemptive).OrderBy(kv => kv.Value.LastActive).FirstOrDefault().Key;
                if (!string.IsNullOrEmpty(lruKey))
                {
                    slot = _bindings[lruKey].Slot;
                    evictedSlot = slot;
                    evictedKvCache = _bindings[lruKey].KvCache;
                    _bindings.Remove(lruKey);
                    evicted = lruKey;
                }
                else
                {
                    // 全被强占占满 → 排队等待
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < MaxWaitSeconds)
                    {
                        Thread.Sleep(1000);
                        slot = FindFreeSlotLocked();
                        if (slot >= 0) break;
                        // 检查是否有非强占绑定被释放（理论上不会，但安全起见）
                        var retry = _bindings.Where(kv => !kv.Value.Preemptive).OrderBy(kv => kv.Value.LastActive).FirstOrDefault();
                        if (retry.Key != "")
                        {
                            slot = retry.Value.Slot;
                            evictedSlot = slot;
                            evictedKvCache = retry.Value.KvCache;
                            _bindings.Remove(retry.Key);
                            evicted = retry.Key;
                            break;
                        }
                    }
                    if (slot < 0)
                    {
                        // 超时降级：随机槽，不建绑定
                        return (Random.Shared.Next(_slotCount), null, false, null, -1, false);
                    }
                }
            }
            _bindings[key] = new Binding { Slot = slot, LastActive = DateTime.Now, Preemptive = autoPre, KvCache = true };
            Save();
            return (slot, key, true, evicted, evictedSlot, evictedKvCache);
        }
    }

    /// <summary>指定 Key 当前是否为强占（Tool 链锁定判定用）。</summary>
    public bool IsPreemptive(string key)
    {
        lock (_gate)
        {
            return _bindings.TryGetValue(key, out var b) && b.Preemptive;
        }
    }

    /// <summary>设置指定 Key 的强占模式（UI 调用）。</summary>
    public void SetPreemptive(string key, bool value)
    {
        lock (_gate)
        {
            if (_bindings.TryGetValue(key, out var b))
            {
                _bindings[key] = new Binding { Slot = b.Slot, LastActive = b.LastActive, Preemptive = value, KvCache = b.KvCache };
                Save();
            }
        }
    }

    /// <summary>设置指定 Key 的 KV Cache 开关（UI 调用）。</summary>
    public void SetKvCache(string key, bool value)
    {
        lock (_gate)
        {
            if (_bindings.TryGetValue(key, out var b))
            {
                _bindings[key] = new Binding { Slot = b.Slot, LastActive = b.LastActive, Preemptive = b.Preemptive, KvCache = value };
                Save();
            }
        }
    }

    /// <summary>当前绑定快照（状态展示用，含应用名/强占/KV缓存配置）。</summary>
    public List<(string Key, string App, int Slot, DateTime LastActive, bool Preemptive, bool KvCache)> Snapshot()
    {
        lock (_gate)
        {
            return _bindings.Select(kv => (kv.Key, AppNameOf(kv.Key), kv.Value.Slot, kv.Value.LastActive, kv.Value.Preemptive, kv.Value.KvCache))
                              .OrderByDescending(t => t.LastActive).ToList();
        }
    }

    private int FindFreeSlotLocked()
    {
        var used = new HashSet<int>(_bindings.Values.Select(b => b.Slot));
        for (int i = 0; i < _slotCount; i++)
            if (!used.Contains(i)) return i;
        return -1; // 全占
    }

    /// <summary>从 slot_bindings.json 恢复绑定；兼容旧格式（缺字段取默认值）。</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(BindingsPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(BindingsPath));
            var root = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText());
            if (root?["bindings"] is not System.Text.Json.Nodes.JsonObject bn) return;
            foreach (var kv in bn)
            {
                var v = kv.Value;
                int slot = v?["slot"]?.GetValue<int>() ?? -1;
                string lastActive = v?["lastActive"]?.GetValue<string>() ?? "";
                bool preemptive = v?["preemptive"]?.GetValue<bool>() ?? false;
                bool kvCache = v?["kvCache"]?.GetValue<bool>() ?? true;
                if (slot < 0 || slot >= _slotCount) continue; // --parallel 缩减：丢弃越界绑定
                if (!DateTime.TryParse(lastActive, out var dt)) dt = DateTime.Now.AddDays(-30);
                _bindings[kv.Key] = new Binding { Slot = slot, LastActive = dt, Preemptive = preemptive, KvCache = kvCache };
            }
        }
        catch
        {
            // 绑定文件损坏：忽略，从零开始
        }
    }

    /// <summary>持久化绑定表（含应用名/强占/KV缓存配置）。</summary>
    private void Save()
    {
        try
        {
            var bindings = new System.Text.Json.Nodes.JsonObject();
            foreach (var kv in _bindings)
            {
                bindings[kv.Key] = new System.Text.Json.Nodes.JsonObject
                {
                    ["app"] = AppNameOf(kv.Key),
                    ["slot"] = kv.Value.Slot,
                    ["preemptive"] = kv.Value.Preemptive,
                    ["kvCache"] = kv.Value.KvCache,
                    ["lastActive"] = kv.Value.LastActive.ToString("o")
                };
            }
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["slotCount"] = _slotCount,
                ["bindings"] = bindings
            };
            File.WriteAllText(BindingsPath, obj.ToJsonString());
        }
        catch
        {
            // 持久化失败不影响路由（内存绑定仍有效）
        }
    }

    private static bool TryGetHeader(NameValueCollection h, string name, out string value)
    {
        value = "";
        try
        {
            value = h[name] ?? "";
            return true;
        }
        catch
        {
            return false;
        }
    }
}

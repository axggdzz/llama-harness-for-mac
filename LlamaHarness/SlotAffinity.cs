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
    /// <summary>槽位绑定持久化路径：项目目录下 config/slot_bindings.json。</summary>
    private static readonly string BindingsPath = Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json");

    internal struct Binding
    {
        public int Slot;
        public DateTime LastActive;
        public bool Preemptive;
        public bool KvCache;
    }

    /// <summary>排队等待上限（秒）。全槽被强占时新请求最多等这么久。</summary>
    private readonly int _maxWaitSeconds;

    public SlotAffinity(int slotCount, int maxWaitSeconds = 30)
    {
        _slotCount = Math.Max(1, slotCount);
        _maxWaitSeconds = Math.Max(1, maxWaitSeconds);
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
    /// 全被强占占满 → 排队等待（上限 _maxWaitSeconds），超时降级随机槽。
    /// E-5：两阶段——锁内只做判定（阶段 1），排队 Sleep 在锁外（阶段 2），
    /// 等待期间其他请求的 GetSlot/SetPreemptive/Snapshot 不再被阻塞（旧实现 Sleep-in-lock 最长卡 30s）。
    /// </summary>
    /// <param name="autoPreemptive">自动强占前缀集合（§4.2 主力会话冻结）：key 匹配任一前缀 → 强制 Preemptive=true（暂停 LRU 驱逐）。</param>
    /// <returns>(slot, key, isNewBinding, evictedKey, evictedSlot, evictedKvCache)</returns>
    public (int Slot, string? Key, bool NewBinding, string? Evicted, int EvictedSlot, bool EvictedKvCache) GetSlot(
        NameValueCollection headers, IReadOnlyList<string>? autoPreemptive = null)
    {
        var key = GetAffinityKey(headers);
        if (string.IsNullOrEmpty(key))
            return (Random.Shared.Next(_slotCount), null, false, null, -1, false);

        // §4.2：应用类型在自动强占集合 → 强制冻结（新绑定创建时 + 已有绑定每次访问）
        bool autoPre = autoPreemptive != null && autoPreemptive.Any(p => !string.IsNullOrEmpty(p) && key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        // ── 阶段 1（锁内）：已有绑定刷新 / 空闲槽 / LRU 驱逐判定 ──
        lock (_gate)
        {
            if (_bindings.TryGetValue(key, out var b))
            {
                _bindings[key] = new Binding { Slot = b.Slot, LastActive = DateTime.Now, Preemptive = b.Preemptive || autoPre, KvCache = b.KvCache };
                return (b.Slot, key, false, null, -1, false);
            }

            var alloc = TryAllocateLocked(key, autoPre);
            if (alloc.Slot != null)
                return (alloc.Slot!.Value, key, true, alloc.Evicted, alloc.EvictedSlot, alloc.EvictedKvCache);
            // 全被强占占满 → 锁外排队（E-5）
        }

        // ── 阶段 2（锁外）：排队等待（上限 _maxWaitSeconds），Sleep 不持锁 ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < _maxWaitSeconds)
        {
            Thread.Sleep(1000);
            lock (_gate)
            {
                var alloc = TryAllocateLocked(key, autoPre);
                if (alloc.Slot != null)
                    return (alloc.Slot!.Value, key, true, alloc.Evicted, alloc.EvictedSlot, alloc.EvictedKvCache);
            }
        }

        // 超时降级：随机槽，不建绑定
        return (Random.Shared.Next(_slotCount), null, false, null, -1, false);
    }

    /// <summary>锁内原子分配：空闲槽 → LRU 驱逐非强占 → 建绑定 + 持久化。
    /// Slot=null = 全被强占占满（调用方锁外排队）。重复 key 并发时采纳已有绑定（保持旧单锁语义）。</summary>
    private (int? Slot, string? Evicted, int EvictedSlot, bool EvictedKvCache) TryAllocateLocked(string key, bool autoPre)
    {
        if (_bindings.TryGetValue(key, out var existing))
            return (existing.Slot, null, -1, false); // 重复 key 并发：采纳已有绑定

        // 新 Key：优先分配无其他绑定的槽；全占则驱逐最久未活跃的非强占绑定
        int? slot = FindFreeSlotLocked();
        string? evicted = null;
        int evictedSlot = -1;
        bool evictedKvCache = false;
        if (slot < 0)
        {
            // 找可驱逐目标（非强占）
            var lruKey = _bindings.Where(kv => !kv.Value.Preemptive).OrderBy(kv => kv.Value.LastActive).FirstOrDefault().Key;
            if (!string.IsNullOrEmpty(lruKey))
            {
                slot = _bindings[lruKey].Slot;
                evictedSlot = slot.Value;
                evictedKvCache = _bindings[lruKey].KvCache;
                _bindings.Remove(lruKey);
                evicted = lruKey;
            }
            else
            {
                return (null, null, -1, false); // 全被强占占满
            }
        }
        _bindings[key] = new Binding { Slot = slot!.Value, LastActive = DateTime.Now, Preemptive = autoPre, KvCache = true };
        Save();
        return (slot.Value, evicted, evictedSlot, evictedKvCache);
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
    /// <summary>确保 config/ 目录存在（幂等）。</summary>
    private static void EnsureConfigDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "config");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    private void Save()
    {
        try
        {
            EnsureConfigDir();
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
        // NameValueCollection 索引器对缺失键返回 null 而不抛异常，无需 try/catch（审计：原死防御删除）
        value = h[name] ?? "";
        return true;
    }
}

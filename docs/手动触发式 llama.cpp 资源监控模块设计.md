# llama‑harness：手动触发式 llama.cpp 资源监控模块设计

> 需求：**手动触发采集，不做定时轮询**，点击 / 调用才拉取一次 `/slots`、`/props`、`/metrics`；原始数据先完整保存，后续再敲定刷新策略、UI 展示、告警逻辑。 环境：.NET 8 C#，对接你的 llama‑server（build 10481）。

## 采集内容清单（手动触发一次，一次性拉取 3 组数据源）

1. `/slots`：各个槽位运行时状态（slotId、状态、`tokens_cached`、是否正在推理）
2. `/props`：模型全局静态配置（总槽数、ctx‑size、模型路径）
3. `/metrics`：Prometheus 文本格式，全局显存、内存、KV 缓存总占用、吞吐指标

> 注意：该版本接口**没有单槽字节大小**，只拿到`tokens_cached`，后续做估算层。

### 数据存储思路

采集后把原始 http 响应完整保存（不要立刻丢弃原始 json/metrics 文本）。

- 保留原始报文，方便后续调试、排查；
- 再做一层结构化 DTO，供界面展示。

## 1、C# DTO 定义（数据契约）

```
/// <summary>
/// llama.cpp 手动采集的完整监控快照，一次触发生成一份快照
/// </summary>
public class LlamaCppMonitorSnapshot
{
    /// <summary>快照采集时间</summary>
    public DateTime CaptureAt { get; set; }

    /// <summary>/props原始json字符串</summary>
    public string RawPropsJson { get; set; } = "";
    /// <summary>/slots原始json字符串</summary>
    public string RawSlotsJson { get; set; } = "";
    /// <summary>/metrics原始文本</summary>
    public string RawMetricsText { get; set; } = "";

    /// <summary>解析之后的槽位信息</summary>
    public List<LlamaSlotInfo> Slots { get; set; } = new();
    /// <summary>全局属性</summary>
    public LlamaGlobalProps GlobalProps { get; set; } = new();
}

/// <summary>单个槽位信息，映射/slots接口返回</summary>
public class LlamaSlotInfo
{
    public int id { get; set; }
    public long id_task { get; set; }
    public string state_name { get; set; } = "";
    public int n_ctx { get; set; }
    public int tokens_cached { get; set; }
    public bool is_processing { get; set; }
    public double pp_tps { get; set; }
    public double tg_tps { get; set; }
}

/// <summary>/props全局配置</summary>
public class LlamaGlobalProps
{
    public int total_slots { get; set; }
    public int ctx_size { get; set; }
    public string model_path { get; set; } = "";
}
```

## 2、采集服务类（手动触发，无后台轮询）

```
public class LlamaCppMonitorCollector
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUri;

    public LlamaCppMonitorCollector(string baseAddress)
    {
        _baseUri = baseAddress.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
            BaseAddress = new Uri(_baseUri)
        };
    }

    /// <summary>
    /// 【手动触发】采集一次完整快照
    /// </summary>
    public async Task<LlamaCppMonitorSnapshot> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = new LlamaCppMonitorSnapshot
        {
            CaptureAt = DateTime.Now
        };

        // 1. 获取 /slots
        var slotsResp = await _httpClient.GetAsync("/slots", ct);
        snapshot.RawSlotsJson = await slotsResp.Content.ReadAsStringAsync(ct);
        snapshot.Slots = System.Text.Json.JsonSerializer.Deserialize<List<LlamaSlotInfo>>(snapshot.RawSlotsJson)!;

        // 2. 获取 /props
        var propsResp = await _httpClient.GetAsync("/props", ct);
        snapshot.RawPropsJson = await propsResp.Content.ReadAsStringAsync(ct);
        var propsObj = System.Text.Json.JsonSerializer.Deserialize<LlamaGlobalProps>(snapshot.RawPropsJson)!;
        snapshot.GlobalProps = propsObj;

        // 3. 获取 metrics，llama‑server启动必须带 --metrics 参数才会生效
        var metricsResp = await _httpClient.GetAsync("/metrics", ct);
        snapshot.RawMetricsText = await metricsResp.Content.ReadAsStringAsync(ct);

        return snapshot;
    }
}
```

> ⚠️ 重要前提：启动 llama‑server 必须带上 `--metrics` 参数，否则 `/metrics` 端点不存在。

```
.\llama-server.exe --metrics --parallel 2 --ctx-size 32768 ......
```

## 3、调用示例（手动点按钮 / 调用一次就采集一次）

```
//实例化
var collector = new LlamaCppMonitorCollector("http://127.0.0.1:8080");

//手动触发采集，不会后台自动刷
LlamaCppMonitorSnapshot snap = await collector.CaptureSnapshotAsync();

// 1.原始报文可以直接落日志/存文件，方便后期排查问题
string dumpText = $"===采集时间 {snap.CaptureAt:HH:mm:ss} ===\n" +
                  $"[Slots Raw]\n{snap.RawSlotsJson}\n" +
                  $"[Metrics Raw]\n{snap.RawMetricsText}";

// 2.访问结构化数据
foreach(var slot in snap.Slots)
{
    Console.WriteLine($"slot{slot.id} state:{slot.state_name} cached_tokens:{slot.tokens_cached}");
}
```

## 4、现阶段落地策略（完全贴合你的思路）

1. **不开启任何自动定时、后台轮询**，只对外暴露`CaptureSnapshotAsync()`方法；UI 按钮、调试命令触发才执行一次 http 请求。
2. **完整保留 Raw 原始报文**，不要只存解析后结构化对象；后续分析 KV 异常、slot 意外驱逐、metrics 指标解读，可以回看原始返回。
3. 当前阶段不做：内存 MiB 估算、告警、阈值判断、图表刷新。先把快照采集、存储做扎实。
4. 观测重点（拿到快照后可以直接看）
    - 绑定业务的 slot，`tokens_cached`是否保留，有没有莫名归零；
    - slot 状态机：`idle` / `generating` / `processing_prompt`；
    - `total_slots`确认和启动参数`--parallel`一致。

## 5、后续可扩展预留点（现在不用实现）

等采集链路稳定之后，后续再追加：

1. 解析 metrics 文本，提取全局 KV 占用、GPU 内存；
2. 根据模型参数，基于`tokens_cached`做单 slot KV 内存估算；
3. 快照存储：内存队列 / 本地 json 文件保存多份历史快照；
4. 可选：增加后台定时模式，可开关，调试使用。
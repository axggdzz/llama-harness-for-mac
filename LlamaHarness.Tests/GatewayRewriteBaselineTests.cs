using System.Text.Json.Nodes;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 阶段 0 行为基线：锁定当前 string 版改写函数的输出行为。
/// 批次 1 重构为 DOM 版后，这些测试改指向新签名并继续通过（行为等价验证）。
/// </summary>
public class GatewayRewriteBaselineTests
{
    // ---------- EnsureStreamTrue ----------

    [Fact]
    public void EnsureStreamTrue_StreamFalseBecomesTrue()
    {
        var json = @"{""model"":""m"",""stream"":false,""messages"":[]}";
        var result = SmartScheduler.EnsureStreamTrue(json);
        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!).AsObject();
        Assert.True(obj["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void EnsureStreamTrue_NoFieldInjectsStreamTrue()
    {
        var json = @"{""model"":""m""}";
        var result = SmartScheduler.EnsureStreamTrue(json);
        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!).AsObject();
        Assert.True(obj["stream"]!.GetValue<bool>());
    }

    // ---------- InjectNSlots ----------

    [Fact]
    public void InjectNSlots_AddsWhenMissing()
    {
        var json = @"{""messages"":[]}";
        var result = SmartScheduler.InjectNSlots(json, 3);
        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!).AsObject();
        Assert.Equal(3, obj["n_slots"]![0].AsValue().GetValue<int>());
    }

    [Fact]
    public void InjectNSlots_RespectsExistingClientValue()
    {
        var json = @"{""n_slots"":[1],""messages"":[]}";
        var result = SmartScheduler.InjectNSlots(json, 3);
        Assert.Null(result); // 已有 n_slots：不覆盖，返回 null（调用方透传）
    }

    // ---------- InjectThinkingMode ----------

    [Fact]
    public void InjectThinkingMode_OffStateInjectsEnableThinkingFalse()
    {
        var json = @"{""messages"":[{""role"":""user"",""content"":""hello""}]}";
        var level = SmartScheduler.ThinkingLevel.Off;
        var result = SmartScheduler.InjectThinkingMode(json, ref level, out _);
        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!).AsObject();
        var ctk = obj["chat_template_kwargs"]!.AsObject();
        Assert.False(ctk["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public void InjectThinkingMode_OnInstructionSwitchesToXHighAndStripsText()
    {
        var json = @"{""messages"":[{""role"":""user"",""content"":""请帮我开启思考模式并分析这个问题""}]}";
        var level = SmartScheduler.ThinkingLevel.Off;
        var result = SmartScheduler.InjectThinkingMode(json, ref level, out _);
        Assert.Equal(SmartScheduler.ThinkingLevel.XHigh, level);
        var obj = JsonNode.Parse(result!).AsObject();
        var content = obj["messages"]![0]!.AsObject()["content"]!.GetValue<string>();
        Assert.DoesNotContain("开启思考模式", content);
        var ctk = obj["chat_template_kwargs"]!.AsObject();
        Assert.True(ctk["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("xhigh", ctk["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void InjectThinkingMode_CleansClientReasoningEffort()
    {
        var json = @"{""reasoning_effort"":""high"",""messages"":[{""role"":""user"",""content"":""hi""}]}";
        var level = SmartScheduler.ThinkingLevel.Off;
        var result = SmartScheduler.InjectThinkingMode(json, ref level, out string? fix);
        Assert.NotNull(result);
        var obj = JsonNode.Parse(result!).AsObject();
        Assert.Null(obj["reasoning_effort"]); // 客户端自带字段被清洗
        Assert.NotNull(fix);                    // 有清洗说明
    }

    [Fact]
    public void InjectThinkingMode_InvalidJsonReturnsNull()
    {
        var level = SmartScheduler.ThinkingLevel.Off;
        var result = SmartScheduler.InjectThinkingMode("{not valid json", ref level, out _);
        Assert.Null(result); // DOM 解析失败 → 透传（null）
    }
}

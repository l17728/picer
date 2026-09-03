using System.Text.Json;
using Picer.Core.Vision;

namespace Picer.Core.Search;

public enum VoiceActionKind { Clarify, OpenModule, OpenLocation, SearchMedia }

public sealed record VoiceAction(VoiceActionKind Kind, string Reply, string? Target = null,
    string? Query = null, bool IncludeVideos = false);

public sealed class VoiceCommandAgent(OpenAiCompatibleChatClient chat)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<VoiceAction> PlanAsync(string utterance, IReadOnlyList<string> locationPaths,
        IReadOnlyList<string> people, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return new(VoiceActionKind.Clarify, "请告诉我想看什么。");
        var context = JsonSerializer.Serialize(new
        {
            locations = locationPaths.Take(500),
            people = people.Take(300),
            modules = new[] { "目录浏览", "搜索", "人物", "标签与相册", "视频" }
        });
        var content = await chat.CompleteAsync("""
            你是 Picer 私人媒体库的语音控制代理。依据给出的真实元数据理解用户意图。
            只输出 JSON：action、reply、target、query、includeVideos。
            action 只能是 clarify、open_module、open_location、search_media。
            open_location 的 target 必须逐字等于 locations 中的一条完整路径；不得虚构节点。
            用户只说国家而且该国家有多个城市时，先打开国家节点，同时在 reply 询问想看哪个城市。
            指令不足或目标有多个匹配时用 clarify，并列出不超过五个真实选项。
            search_media 的 query 保留用户完整的检索要求。任何删除、移动、训练或设置修改都必须拒绝并澄清。
            """, $"元数据：{context}\n用户：{utterance}", 500, cancellationToken).ConfigureAwait(false);
        AgentReply reply;
        try { reply = JsonSerializer.Deserialize<AgentReply>(OpenAiCompatibleChatClient.StripFence(content), Json)!; }
        catch (Exception exception) { throw new InvalidDataException("语音代理返回了无效动作。", exception); }
        var kind = reply.Action switch
        {
            "open_module" => VoiceActionKind.OpenModule,
            "open_location" => VoiceActionKind.OpenLocation,
            "search_media" => VoiceActionKind.SearchMedia,
            _ => VoiceActionKind.Clarify
        };
        var answer = new VoiceAction(kind, string.IsNullOrWhiteSpace(reply.Reply) ? "请再说明一下。" : reply.Reply,
            reply.Target, reply.Query, reply.IncludeVideos);
        return Validate(answer, locationPaths);
    }

    private static VoiceAction Validate(VoiceAction action, IReadOnlyList<string> locations)
    {
        if (action.Kind == VoiceActionKind.OpenLocation &&
            (string.IsNullOrWhiteSpace(action.Target) || !locations.Contains(action.Target, StringComparer.OrdinalIgnoreCase)))
            return new(VoiceActionKind.Clarify, "没有找到完全匹配的地点节点，请换一种说法或指定国家、省、市。", Query: action.Query);
        if (action.Kind == VoiceActionKind.OpenModule && action.Target is not ("目录浏览" or "搜索" or "人物" or "标签与相册" or "视频"))
            return new(VoiceActionKind.Clarify, "请说明要打开目录浏览、搜索、人物、标签与相册还是视频。");
        if (action.Kind == VoiceActionKind.SearchMedia && string.IsNullOrWhiteSpace(action.Query))
            return new(VoiceActionKind.Clarify, "请说明要搜索的人物、地点、时间或场景。");
        return action;
    }

    private sealed record AgentReply(string? Action, string? Reply, string? Target, string? Query, bool IncludeVideos);
}

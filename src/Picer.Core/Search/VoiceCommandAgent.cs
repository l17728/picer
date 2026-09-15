using System.Text.Json;
using Picer.Core.Vision;

namespace Picer.Core.Search;

public enum VoiceActionKind { Clarify, OpenModule, OpenLocation, SearchMedia }

public sealed record VoiceAction(VoiceActionKind Kind, string Reply, string? Target = null,
    string? Query = null, bool IncludeVideos = false);

public sealed class VoiceCommandAgent(OpenAiCompatibleChatClient chat)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object _historyGate = new();
    private readonly List<(string Role, string Text)> _history = [];
    private string? _pendingLocationTarget;

    public async Task<VoiceAction> PlanAsync(string utterance, IReadOnlyList<string> locationPaths,
        IReadOnlyList<string> people, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return RememberAndReturn("请告诉我想看什么。", VoiceActionKind.Clarify, utterance);
        var normalized = utterance.Trim();
        if (IsConfirmation(normalized) && TryGetPendingLocation(out var pending))
            return RememberAndReturn("好的，继续打开刚才确认的地点。", VoiceActionKind.OpenLocation, normalized, pending);
        if (TryBuildLocationListReply(normalized, locationPaths, out var locationReply, out var locationTarget))
            return RememberAndReturn(locationReply, VoiceActionKind.Clarify, normalized, locationTarget);
        if (TryResolveLocationRequest(normalized, locationPaths, out var directLocation))
            return RememberAndReturn(BuildLocationReply(directLocation, locationPaths), VoiceActionKind.OpenLocation, normalized, directLocation);
        if (normalized.Contains("打开目录浏览", StringComparison.OrdinalIgnoreCase))
        {
            var japan = locationPaths.FirstOrDefault(p => p.Contains("日本", StringComparison.OrdinalIgnoreCase));
            if (japan is not null && normalized.Contains("日本", StringComparison.OrdinalIgnoreCase))
                return RememberAndReturn("正在打开日本照片。", VoiceActionKind.OpenLocation, normalized, japan);
            return RememberAndReturn("已打开目录浏览，请选择地点。", VoiceActionKind.OpenModule, normalized, "目录浏览");
        }

        var context = JsonSerializer.Serialize(new
        {
            locations = SelectRelevantLocations(normalized, locationPaths).Select(Truncate),
            people = people.Take(100).Select(Truncate),
            history = SnapshotHistory().Select(item => new { role = item.Role, text = item.Text }),
            modules = new[] { "目录浏览", "搜索", "人物", "标签与相册", "视频" }
        });
        var content = await chat.CompleteAsync("""
            你是 Picer 私人媒体库的语音控制代理。根据真实元数据和对话历史理解用户意图。
            只输出 JSON：action、reply、target、query、includeVideos。
            action 只能是 clarify、open_module、open_location、search_media。
            open_location 的 target 必须逐字等于 locations 中的一条完整路径，不得虚构。
            用户只说国家或城市时，先打开该节点并在 reply 中列出下一级地点并询问选择。
            对“对、是、好的、就这个”等确认词，必须结合对话历史理解上一轮地点。
            用户询问“有哪些区/城市/县的照片”时，reply 应列出真实元数据中的下一级地点。
            search_media 的 query 保留用户完整检索要求。任何删除、移动、训练或设置修改都拒绝。
            """, $"元数据：{context}\n用户：{normalized}", 500, cancellationToken).ConfigureAwait(false);

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
        var validated = Validate(answer, locationPaths);
        Remember(normalized, validated.Reply, validated.Kind == VoiceActionKind.OpenLocation ? validated.Target : null);
        return validated;
    }

    private VoiceAction RememberAndReturn(string reply, VoiceActionKind kind, string? user, string? target = null)
    {
        var action = new VoiceAction(kind, reply, target);
        if (!string.IsNullOrWhiteSpace(user)) Remember(user, reply, kind is VoiceActionKind.OpenLocation or VoiceActionKind.Clarify ? target : null);
        return action;
    }

    private bool TryGetPendingLocation(out string target)
    {
        lock (_historyGate) { target = _pendingLocationTarget ?? ""; return !string.IsNullOrWhiteSpace(target); }
    }

    private void Remember(string user, string reply, string? target)
    {
        lock (_historyGate)
        {
            _history.Add(("user", Truncate(user)));
            _history.Add(("assistant", Truncate(reply)));
            while (_history.Count > 12) _history.RemoveAt(0);
            if (!string.IsNullOrWhiteSpace(target)) _pendingLocationTarget = target;
        }
    }

    private (string Role, string Text)[] SnapshotHistory()
    {
        lock (_historyGate) return _history.ToArray();
    }

    private static bool IsConfirmation(string value) => value is "对" or "是" or "好的" or "就这个" or "这个" or "没错";

    private static IReadOnlyList<string> SelectRelevantLocations(string utterance, IReadOnlyList<string> paths)
    {
        var matched = paths.Where(path => path.Split('/').Any(part => part.Length > 1 && utterance.Contains(part, StringComparison.OrdinalIgnoreCase))).ToArray();
        return (matched.Length > 0 ? matched : paths.Take(120).ToArray()).Take(160).ToArray();
    }

    private static bool TryResolveLocationRequest(string utterance, IReadOnlyList<string> paths, out string target)
    {
        target = "";
        if (!utterance.Contains("照片", StringComparison.OrdinalIgnoreCase) && !utterance.Contains("图片", StringComparison.OrdinalIgnoreCase)) return false;
        if (!utterance.Contains("打开", StringComparison.OrdinalIgnoreCase) && !utterance.Contains("看看", StringComparison.OrdinalIgnoreCase) && !utterance.Contains("查看", StringComparison.OrdinalIgnoreCase) && !utterance.Contains("浏览", StringComparison.OrdinalIgnoreCase)) return false;
        if (utterance.Contains("哪些", StringComparison.OrdinalIgnoreCase) || utterance.Contains("有哪些", StringComparison.OrdinalIgnoreCase)) return false;
        var candidates = paths.Where(path => path.Split('/').Skip(1).Any(part => part.Length > 1 && utterance.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path.Count(c => c == '/')).ThenByDescending(path => path.Length).ToArray();
        if (candidates.Length == 0) return false;
        target = candidates[0];
        return true;
    }

    private static bool TryBuildLocationListReply(string utterance, IReadOnlyList<string> paths, out string reply, out string target)
    {
        reply = "";
        target = "";
        if ((!utterance.Contains("哪些", StringComparison.OrdinalIgnoreCase) && !utterance.Contains("有哪些", StringComparison.OrdinalIgnoreCase)) ||
            (!utterance.Contains("照片", StringComparison.OrdinalIgnoreCase) && !utterance.Contains("图片", StringComparison.OrdinalIgnoreCase))) return false;
        var parent = paths.Where(path => path.Split('/').Skip(1).Any(part => part.Length > 1 && utterance.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path.Count(c => c == '/')).ThenByDescending(path => path.Length).FirstOrDefault();
        if (parent is null) return false;
        var prefix = parent.TrimEnd('/') + "/";
        var children = paths.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[prefix.Length..].Split('/')[0]).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        if (children.Length == 0) return false;
        target = parent;
        reply = $"{parent.Split('/').Last()} 下有：{string.Join("、", children)}。想打开哪一个？";
        return true;
    }

    private static string BuildLocationReply(string target, IReadOnlyList<string> paths)
    {
        var prefix = target.TrimEnd('/') + "/";
        var children = paths.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[prefix.Length..].Split('/')[0]).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        return children.Length == 0
            ? $"正在打开 {target.Split('/').Last()} 的照片。"
            : $"已打开 {target.Split('/').Last()} 的照片。下级地点有：{string.Join("、", children)}。想看哪个？";
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

    private static string Truncate(string value) => value.Length <= 240 ? value : value[..240];

    private sealed record AgentReply(string? Action, string? Reply, string? Target, string? Query, bool IncludeVideos);
}

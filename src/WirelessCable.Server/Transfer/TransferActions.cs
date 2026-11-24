using System.Reflection;
using System.Text.Json;

namespace WirelessCable.Server.Transfer;

/// <summary>
/// 能力矩阵：给定任务状态，某个动作**服务端接不接受**、**界面上显不显示**。
///
/// 表在仓库根的 `contracts/transfer-state-machine.json`（编译时嵌进程序集），
/// 手机端与前端读的是同一份——改语义只改那一份，三端测试同时红。
///
/// 为什么要它：2.10.33–2.11.0 之间，「暂停/继续/取消/删除」的判定散在
/// 服务端三处、两端界面各自的 if 里，改一处漏两处。这个类把判定收敛成一个入口：
/// 服务端用它拒不合法的动作，界面用它决定按钮出不出现。
/// </summary>
public static class TransferActions
{
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Cancel = "cancel";

    /// <summary>失败后重新发起。纯客户端动作，服务端状态机不参与。</summary>
    public const string Retry = "retry";

    public const string DeleteRecord = "deleteRecord";

    private const string ResourceName = "contracts/transfer-state-machine.json";

    private static readonly Lazy<JsonDocument> Document = new(Load, isThreadSafe: true);

    /// <summary>界面上是否显示这个动作的按钮。</summary>
    public static bool Shows(string action, string state) => Get(action, state, "ui");

    /// <summary>
    /// **这一条记录**能不能做这个动作。
    ///
    /// 只认 `outcome == "ok"`：`rejected` 表示"批量请求整体成功，但这一条被跳过"
    /// （比如批量删记录时混进了还在传输的），对单条而言就是不接受——
    /// 界面该据此把按钮藏起来，而不是让用户点了才发现被跳过。
    /// <see cref="Retry"/> 恒为 false：它是客户端自己重新发起，不经服务端。
    /// </summary>
    public static bool Accepts(string action, string state)
        => GetText(action, state, "outcome") == "ok";

    /// <summary>
    /// 服务端拒绝时该返回的错误码；接受（或纯客户端动作）返回 null。
    ///
    /// 单测据此断言"实现返回的错误码 == 契约表写的那个"，
    /// 避免出现 `transfer_state_invalid` 和 `transfer_cancelled` 混用这类问题。
    /// </summary>
    public static string? ErrorFor(string action, string state)
    {
        var rule = Rule(action, state);
        return rule is not null &&
               rule.Value.TryGetProperty("error", out var error)
            ? error.GetString()
            : null;
    }

    /// <summary>冲突解决建议里用的错误码集合（界面判断"这批能不能删"时够用）。</summary>
    public static IReadOnlyList<string> AllActions { get; } = [Pause, Resume, Cancel, Retry, DeleteRecord];

    private static bool Get(string action, string state, string property)
    {
        var rule = Rule(action, state);
        return rule is not null &&
               rule.Value.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static string? GetText(string action, string state, string property)
    {
        var rule = Rule(action, state);
        return rule is not null &&
               rule.Value.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static JsonElement? Rule(string action, string state)
    {
        if (!Document.Value.RootElement.TryGetProperty("actions", out var actions) ||
            !actions.TryGetProperty(action, out var rules) ||
            !rules.TryGetProperty(state, out var rule) ||
            rule.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return rule;
    }

    private static JsonDocument Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"程序集里没有契约表 {ResourceName}——检查 csproj 的 EmbeddedResource 是否被删了");

        return JsonDocument.Parse(stream);
    }
}

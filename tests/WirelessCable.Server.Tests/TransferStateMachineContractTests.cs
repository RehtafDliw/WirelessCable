using System.Text.Json;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Transfer;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 传输状态机契约测试：**逐格**验证服务端实现与
/// `contracts/transfer-state-machine.json` 一致。
///
/// 为什么要有它：暂停/继续/取消/删除的语义在 2.10.33–2.11.0 之间返工了六七轮，
/// 每次都是"改了一端、漏了另一处判断"，靠真机点才能发现。契约表把
/// 「某状态下做某动作会怎样」写成 36 格（9 状态 × 4 动作），
/// 这份测试逐格跑一遍实现；手机端另有 JVM 测试读**同一份** JSON。
///
/// 改语义的正确姿势：先改 JSON，两边测试会同时红，再改实现。
/// </summary>
public sealed class TransferStateMachineContractTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.Combine("D:\\", "tools", "WorkBuddySpace", "WirelessCable", ".testtmp"),
        "contract-" + Guid.NewGuid().ToString("N"));

    private SqliteDatabaseFactory _database = null!;
    private TransferRepository _repository = null!;
    private TransferEngine _engine = null!;
    private StoredDevice _device = null!;

    private string SharedDirectory => Path.Combine(_root, "shared");
    private string TempDirectory => Path.Combine(_root, "temp");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(SharedDirectory);
        Directory.CreateDirectory(TempDirectory);

        _database = new SqliteDatabaseFactory(Path.Combine(_root, "test.db"));
        SchemaInitializer.Initialize(_database);
        _repository = new TransferRepository(_database);

        _engine = new TransferEngine(_repository, new TransferOptions
        {
            SharedDirectory = SharedDirectory,
            TempDirectory = TempDirectory,
            FreeSpaceProbe = new EnoughSpace(),
        });

        _device = new StoredDevice
        {
            DeviceId = Guid.NewGuid().ToString("N"),
            DisplayName = "契约设备",
            Platform = "android",
            IsPaired = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试目录残留不影响结论
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ 契约用例

    /// <summary>
    /// 把契约表展开成 xunit 的用例：每个 (动作, 状态) 一格。
    ///
    /// 读文件而不是 InlineData：契约表是**共享的**，手机端测试读同一份；
    /// 写死在两处 C# / Kotlin 里就又变成"两套实现"了。
    /// </summary>
    public static IEnumerable<object[]> ContractCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ContractPath()));
        var states = doc.RootElement.GetProperty("states");
        // 动作里除了状态格，还可能有 note 之类的元数据字符串——只认状态名。
        var knownStates = states.EnumerateObject().Select(static s => s.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var action in doc.RootElement.GetProperty("actions").EnumerateObject())
        {
            foreach (var rule in action.Value.EnumerateObject())
            {
                if (!knownStates.Contains(rule.Name))
                {
                    continue;
                }

                var state = rule.Name;
                var body = rule.Value;

                // 纯客户端动作（retry）不经过服务端状态机，这里没有可断言的东西。
                if (body.TryGetProperty("outcome", out var kind) &&
                    kind.GetString() == "client")
                {
                    continue;
                }

                yield return
                [
                    action.Name,
                    state,
                    body.GetProperty("outcome").GetString()!,
                    body.TryGetProperty("error", out var e) ? e.GetString() : null,
                    body.TryGetProperty("resultState", out var r) ? r.GetString() : null,
                ];
            }
        }

        // 状态表本身也要被读到（避免 JSON 被改坏后测试静默变空）
        Assert.True(states.EnumerateObject().Any());
    }

    [Theory]
    [MemberData(nameof(ContractCases))]
    public async Task Action_OnState_MatchesContract(
        string action,
        string state,
        string outcome,
        string? expectedError,
        string? expectedResultState)
    {
        var task = await NewDownloadTaskAsync($"contract_{action}_{state}.bin");

        // 直接把库里的状态改成目标状态：这一层要验证的是**动作对状态的判定**，
        // 而不是"怎么走到这个状态"（后者由各状态自己的用例覆盖）。
        await _repository.TouchAsync(task.TaskId, state, DateTimeOffset.UtcNow);

        var (success, errorCode, rejected) = await InvokeAsync(action, task.TaskId);

        switch (outcome)
        {
            case "ok":
                Assert.True(success, $"{action} 在 {state} 应当成功，实际失败：{errorCode}");
                break;

            case "rejected":
                // 批量语义：请求本身成功，但这一条被跳过（"还在传输，请先取消"）
                Assert.True(success, $"{action} 在 {state} 应当整体成功（这一条跳过），实际失败：{errorCode}");
                Assert.Equal(1, rejected);
                break;

            case "error":
                Assert.False(success, $"{action} 在 {state} 应当失败，实际成功了");
                Assert.Equal(expectedError, errorCode);
                break;

            case "client":
                // ContractCases 已经过滤掉，走到这里说明契约表被改坏了
                Assert.Fail("client 动作不该出现在这组用例里");
                break;

            default:
                Assert.Fail($"契约表里出现了未知 outcome：{outcome}");
                break;
        }

        // 结果状态
        if (expectedResultState is null)
        {
            return;
        }

        var record = await _repository.GetAsync(task.TaskId);
        Assert.NotNull(record);

        switch (expectedResultState)
        {
            case "unchanged":
                Assert.Equal(state, record!.Status);
                break;

            case "active":
                Assert.True(
                    TransferStates.IsActive(record!.Status),
                    $"{action} 在 {state} 之后应当回到活跃态，实际是 {record.Status}");
                break;

            default:
                Assert.Equal(expectedResultState, record!.Status);
                break;
        }
    }

    [Fact]
    public void Embedded_table_is_readable()
    {
        // 契约表是编译进程序集的：单文件 exe 运行时没有仓库目录，
        // 读不到就会抛，界面上所有动作判定全崩。这里钉住"能读到"。
        Assert.True(TransferActions.Shows(TransferActions.DeleteRecord, TransferStates.Completed));
        Assert.False(TransferActions.Shows(TransferActions.DeleteRecord, TransferStates.Transferring));
    }

    [Theory]
    [InlineData(TransferActions.Cancel, TransferStates.Paused, true)]
    [InlineData(TransferActions.Cancel, TransferStates.Completed, false)]
    [InlineData(TransferActions.Resume, TransferStates.Paused, true)]
    [InlineData(TransferActions.Resume, TransferStates.Cancelled, false)]
    [InlineData(TransferActions.Pause, TransferStates.Transferring, true)]
    [InlineData(TransferActions.Pause, TransferStates.Paused, true)]
    [InlineData(TransferActions.DeleteRecord, TransferStates.Completed, true)]
    [InlineData(TransferActions.DeleteRecord, TransferStates.Transferring, false)]
    public void Accepts_follows_the_contract(string action, string state, bool expected)
        => Assert.Equal(expected, TransferActions.Accepts(action, state));

    [Fact]
    public void Resume_on_cancelled_uses_the_dedicated_code()
    {
        // 用错码的后果：两端会提示"任务已结束"而不是"已被对方取消，要删记录吗"。
        Assert.Equal(
            ErrorCodes.TransferCancelled,
            TransferActions.ErrorFor(TransferActions.Resume, TransferStates.Cancelled));
    }

    [Fact]
    public void Retry_is_client_side_only()
    {
        Assert.False(TransferActions.Accepts(TransferActions.Retry, TransferStates.Failed));
        Assert.True(TransferActions.Shows(TransferActions.Retry, TransferStates.Failed));
    }

    // ------------------------------------------------------------ 辅助

    private async Task<(bool Success, string? ErrorCode, int Rejected)> InvokeAsync(string action, string taskId)
    {
        switch (action)
        {
            case "pause":
            {
                var r = await _engine.PauseAsync(_device, taskId);
                return (r.Success, r.Error?.Code, 0);
            }

            case "resume":
            {
                var r = await _engine.ResumeAsync(_device, taskId);
                return (r.Success, r.Error?.Code, 0);
            }

            case "cancel":
            {
                var r = await _engine.CancelAsync(_device, taskId);
                return (r.Success, r.Error?.Code, 0);
            }

            case "deleteRecord":
            {
                var r = await _engine.DeleteRecordsAsync(_device, [taskId], deleteFiles: false);
                return (r.Success, r.Error?.Code, r.Value?.Rejected.Count ?? 0);
            }

            default:
                throw new InvalidOperationException($"契约表里有未知动作：{action}");
        }
    }

    /// <summary>
    /// 造一条 **download** 方向的任务。
    ///
    /// 刻意不用 upload：upload 任务在内存里挂着上传会话，会话分支不检查终态，
    /// 会把"已完成再取消"判成功——那是"进程还没重启"的瞬时状态，
    /// 拿它当契约会把真实语义测偏。download 的状态只看数据库，和两端看到的一致。
    /// </summary>
    private async Task<TransferTaskDto> NewDownloadTaskAsync(string fileName)
    {
        var source = Path.Combine(SharedDirectory, fileName);
        await File.WriteAllBytesAsync(source, new byte[4096]);

        var created = await _engine.CreateDownloadAsync(_device, source);
        Assert.True(created.Success, created.Error?.Message);
        return created.Value!;
    }

    /// <summary>
    /// 找到仓库根的 `contracts/transfer-state-machine.json`。
    /// 测试是从 bin/Release/net9.0 往上跑的，逐级找含 WirelessCable.sln 的目录。
    /// </summary>
    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "transfer-state-machine.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 contracts/transfer-state-machine.json（契约表是两端共享的，别删）");
    }

    /// <summary>磁盘永远够用：这里只验证状态机，不关心 507。</summary>
    private sealed class EnoughSpace : IFreeSpaceProbe
    {
        public long GetAvailableBytes(string path) => long.MaxValue;
    }
}

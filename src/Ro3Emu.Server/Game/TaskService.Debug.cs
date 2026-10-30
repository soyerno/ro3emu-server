using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>One quest of the loopback catalog: TaskConfig _iTaskID and its _iName text.</summary>
public sealed record QuestCatalogEntry(long Id, string Name);

/// <summary>One target of a quest: TaskInfo.CompleteNum against what the target requires.</summary>
public sealed record QuestProgress(long Done, long Need);

/// <summary>
/// A quest the role holds (DBTaskInfo.Infos) or has delivered (CompleteTaskNum): its TaskState name in lower case and how
/// many times it was delivered.
/// </summary>
public sealed record QuestStateEntry(long Id, string Name, string Status, IReadOnlyList<QuestProgress> Progress, int Completed);

/// <summary>The quests of an online role, for the loopback /ro3emu/quests?account= (docs/guide/gm-access.md).</summary>
public sealed record QuestState(string Account, string Role, IReadOnlyList<QuestStateEntry> Quests);

/// <summary>Read-only quest views for the loopback debugging tool; the changes go through !quest (<see cref="GmAsync"/>).</summary>
public sealed partial class TaskService
{
    /// <summary>Every TaskConfig quest, sorted by id; empty without the table.</summary>
    public IReadOnlyList<QuestCatalogEntry> Catalog() => [.. Defs().Keys.Order().Select(id => new QuestCatalogEntry(id, QuestName(id)))];

    /// <summary>The role's running, finished and delivered quests, sorted by id, read under the task lock.</summary>
    public async Task<QuestState> StateAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        List<QuestStateEntry> quests = [];
        if (session.Tasks is { } tasks)
        {
            foreach (long id in tasks.Infos.Keys.Union(tasks.CompleteTaskNum.Keys).Order())
            {
                tasks.Infos.TryGetValue(id, out var info);
                int completed = tasks.CompleteTaskNum.TryGetValue(id, out int n) ? n : 0;
                var status = info?.Status ?? Delivered;
                quests.Add(new QuestStateEntry(id, QuestName(id), status.ToString().ToLowerInvariant(), ProgressOf(session, id, info), completed));
            }
        }
        return new QuestState(session.Account ?? "", session.Role?.Name ?? "", quests);
    }

    private List<QuestProgress> ProgressOf(GameSession session, long id, TaskInfo? info)
    {
        if (info == null) return [];
        var targets = Defs().TryGetValue(id, out var def) ? def.Targets : [];
        return [.. info.CompleteNum.Select((done, i) => new QuestProgress(done, i < targets.Length ? Required(session, targets[i]) : 0))];
    }

    private string QuestName(long id) => _data.Row("TaskConfig", id) is { } row ? _data.TextOf(row, "_iName") : "";
}

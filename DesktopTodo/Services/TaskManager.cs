using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopTodo.Models;

namespace DesktopTodo.Services;

public class TaskManager
{
    private readonly CalDAVService _sync;
    private readonly HashSet<string> _deletedUids = new();
    private bool _online = true;

    public ObservableCollection<TaskData> Tasks { get; } = new();
    public event EventHandler<string>? StatusChanged;

    public TaskManager(CalDAVService sync) => _sync = sync;

    public async Task LoadFromRemoteAsync()
    {
        try
        {
            var remote = await _sync.FetchAllAsync();
            _online = true;
            Tasks.Clear();
            foreach (var t in remote.Where(t => !_deletedUids.Contains(t.Uid)))
                Tasks.Add(t);
            StatusChanged?.Invoke(this, $"已同步 {Tasks.Count} 个任务 (日历: {_sync.CalendarName})");
        }
        catch (Exception ex)
        {
            _online = false;
            StatusChanged?.Invoke(this, $"连接失败: {ex.Message}");
        }
    }

    public async Task AddAsync(string summary)
    {
        var task = new TaskData { Summary = summary, CreatedAt = DateTime.Now };
        Tasks.Insert(0, task);
        if (_online) await _sync.AddAsync(task);
    }

    public async Task UpdateAsync(TaskData task)
    {
        if (_online) await _sync.UpdateAsync(task);
    }

    public async Task ToggleCompleteAsync(TaskData task)
    {
        task.Completed = !task.Completed;
        task.CompletedAt = task.Completed ? DateTime.Now : null;
        await UpdateAsync(task);
    }

    public async Task DeleteAsync(TaskData task)
    {
        Tasks.Remove(task);
        _deletedUids.Add(task.Uid);
        if (_online) try { await _sync.DeleteAsync(task.Uid); } catch { }
    }

    public async Task SetPriorityAsync(TaskData task, int priority)
    {
        task.Priority = priority;
        await UpdateAsync(task);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DesktopTodo.Models;

namespace DesktopTodo.Services;

public class TaskManager
{
    private CalDAVService _sync;
    private readonly HashSet<string> _deletedUids = new();
    private bool _online = true;

    public ObservableCollection<TaskData> Tasks { get; } = new();
    public event EventHandler<string>? StatusChanged;
    public bool IsOnline => _online;

    public TaskManager(CalDAVService sync) => _sync = sync;

    /// <summary>保存设置后换上新的同步实例；集合对象不变，UI 绑定无需重建。</summary>
    public void ReplaceSync(CalDAVService sync)
    {
        _sync = sync;
        _online = true;
    }

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
            StatusChanged?.Invoke(this, $"同步失败: {ex.Message}");
        }
    }

    public async Task AddAsync(string summary)
    {
        var task = new TaskData { Summary = summary, CreatedAt = DateTime.Now };
        try { await _sync.AddAsync(task); }
        catch (Exception ex) { ReportFailure("新增", ex); throw; }
        // 写入成功才出现在列表里，避免「看似添加成功、下次同步蒸发」。
        Tasks.Insert(0, task);
    }

    public async Task UpdateAsync(TaskData task)
    {
        try { await _sync.UpdateAsync(task); }
        catch (Exception ex) { ReportFailure("更新", ex); throw; }
    }

    public async Task ToggleCompleteAsync(TaskData task)
    {
        // XAML 使用 CheckBox.Click（仅用户点击触发），绑定初始化不会再反向写回服务器。
        task.CompletedAt = task.Completed ? DateTime.Now : null;
        await UpdateAsync(task);
        // 失败时不回滚 UI 状态，下次成功同步会用服务端数据覆盖并给出提示。
    }

    public async Task DeleteAsync(TaskData task)
    {
        try { await _sync.DeleteAsync(task); }
        catch (Exception ex) { ReportFailure("删除", ex); throw; }
        // 删除失败 → 任务保留在列表，不再静默隐藏。
        Tasks.Remove(task);
        _deletedUids.Add(task.Uid);
    }

    public async Task SetPriorityAsync(TaskData task, int priority)
    {
        int old = task.Priority;
        task.Priority = priority;
        try { await UpdateAsync(task); }
        catch { task.Priority = old; throw; }
    }

    private void ReportFailure(string action, Exception ex)
    {
        _online = false;
        StatusChanged?.Invoke(this, $"{action}未同步：{ex.Message}（下次同步后恢复）");
    }
}

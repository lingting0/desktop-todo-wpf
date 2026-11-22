using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopTodo.Models;

namespace DesktopTodo.Services;

public sealed class LocalTaskStore : ITaskStore
{
    private readonly string path;
    public string CalendarName => "本机清单";

    public LocalTaskStore(string? filePath = null) => path = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTodo", "tasks.json");

    private List<TaskData> Read()
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<List<TaskData>>(file) ?? throw new JsonException();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException ex) { throw new InvalidDataException("本机任务文件损坏，原文件已保留：" + path, ex); }
    }

    public Task ConnectAsync() => Task.CompletedTask;
    public Task<List<TaskData>> FetchAllAsync() => Task.FromResult(Read());
    public Task AddAsync(TaskData task) => Change(tasks => tasks.Insert(0, task));
    public Task DeleteAsync(TaskData task) => Change(tasks => tasks.RemoveAll(existing => existing.Uid == task.Uid));
    public Task UpdateAsync(TaskData task) => Change(tasks =>
    {
        int index = tasks.FindIndex(existing => existing.Uid == task.Uid);
        if (index < 0) throw new IOException("任务已被移除，请刷新本机清单");
        tasks[index] = task;
    });

    private Task Change(Action<List<TaskData>> edit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var guard = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var tasks = Read();
        edit(tasks);
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp))
            {
                JsonSerializer.Serialize(file, tasks);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return Task.CompletedTask;
    }

    public void Dispose() { }
}

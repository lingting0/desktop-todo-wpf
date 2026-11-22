using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DesktopTodo.Models;

namespace DesktopTodo.Services;

public interface ITaskStore : IDisposable
{
    string CalendarName { get; }
    Task ConnectAsync();
    Task<List<TaskData>> FetchAllAsync();
    Task AddAsync(TaskData task);
    Task UpdateAsync(TaskData task);
    Task DeleteAsync(TaskData task);
}

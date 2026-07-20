using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopTodo.Models;

public class TaskData : INotifyPropertyChanged
{
    private string _uid = Guid.NewGuid().ToString();
    private string _summary = "";
    private bool _completed;
    private int _priority;

    public string Uid { get => _uid; set { _uid = value; OnPropertyChanged(); } }
    public string Summary { get => _summary; set { _summary = value; OnPropertyChanged(); } }
    public bool Completed { get => _completed; set { _completed = value; OnPropertyChanged(); } }
    public int Priority { get => _priority; set { _priority = value; OnPropertyChanged(); } }
    public DateTime? CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DueDate { get; set; }
    public int Order { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

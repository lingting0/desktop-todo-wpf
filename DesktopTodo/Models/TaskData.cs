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
    private DateTime? _createdAt;
    private DateTime? _completedAt;
    private DateTime? _dueDate;
    private int _order;

    public string Uid { get => _uid; set { _uid = value; OnPropertyChanged(); } }
    public string Summary { get => _summary; set { _summary = value; OnPropertyChanged(); } }
    public bool Completed { get => _completed; set { _completed = value; OnPropertyChanged(); } }
    public int Priority { get => _priority; set { _priority = value; OnPropertyChanged(); } }

    public DateTime? CreatedAt { get => _createdAt; set { _createdAt = value; OnPropertyChanged(); } }
    public DateTime? CompletedAt { get => _completedAt; set { _completedAt = value; OnPropertyChanged(); } }
    public DateTime? DueDate { get => _dueDate; set { _dueDate = value; OnPropertyChanged(); } }
    public int Order { get => _order; set { _order = value; OnPropertyChanged(); } }

    // ── 同步元数据（不参与 UI 绑定）──

    /// <summary>服务端 ETag，PUT/DELETE 时用 If-Match 做乐观并发控制。</summary>
    public string? Etag { get; set; }

    /// <summary>资源 href，优先于 UID 拼接的地址。</summary>
    public string? Href { get; set; }

    /// <summary>
    /// 服务端原始 VTODO 文本。写回时以它为底稿，只替换本程序管理的字段，
    /// 保留 DESCRIPTION / CATEGORIES / DUE / X-* 等未知属性，避免数据被抹掉。
    /// </summary>
    public string? RawICal { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

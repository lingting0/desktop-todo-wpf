# Desktop Todo — Windows 桌面待办卡片

与 Nextcloud Tasks 通过 CalDAV 协议实时同步。

> ⚡ 5 MB 绿色免安装，秒启动，零外部依赖。

## 效果

桌面上的半透明卡片，始终置顶，与 Nextcloud Tasks 双向同步。

## 技术

- **C# WPF (.NET 8)**，Windows 自带运行时
- CalDAV 协议纯手写（PROPFIND / REPORT / PUT / DELETE + iCalendar）
- **零 NuGet 包依赖**——HTTP / XML / JSON 全部 .NET 内置

## 功能

- 始终置顶（可切换）、无边框、可拖动、可缩放
- 贴边吸附（多显示器）
- 添加 / 编辑 / 删除 / 完成任务
- 右键任务设优先级（颜色标记：红高 / 橙中 / 蓝低）
- 5 套主题（暗黑 / 亮白 / 深蓝 / 墨绿 / 暗紫）
- 系统托盘最小化
- 定时自动同步
- 设置窗口 —— 不用手写 JSON

## 构建

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download)
2. 复制 `config.example.json` 为 `config.json`，填入你的 Nextcloud 信息
3. 把 `ToDoIcon.png` 放到 `DesktopTodo/` 目录下
4. 运行 `build.bat`
5. 输出在 `publish\` 文件夹，拷贝到任意位置运行

## 运行

编辑 `config.json`：

```json
{
    "nextcloud_url": "https://your-nc.example.com/remote.php/dav/",
    "username": "你的NC用户名",
    "app_password": "你的应用密码"
}
```

应用密码在 Nextcloud 网页端生成：设置 → 安全 → 设备与会话 → 创建新应用密码。

## 打包

输出文件夹约 5 MB，拷贝即用。开机自启：创建 `DesktopTodo.exe` 快捷方式丢进 Windows 启动文件夹。

## License

MIT

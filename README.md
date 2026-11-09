# Desktop Todo — Windows 桌面待办卡片

与 Nextcloud Tasks 通过 CalDAV 协议同步的桌面置顶卡片。

## 运行要求

- Windows + **.NET 8 Desktop Runtime**（Windows 不自带，需单独安装：<https://dotnet.microsoft.com/download/dotnet/8.0>）
- 项目零 NuGet 依赖；`publish` 约 1.8 MB，但**不是绿色免安装**——运行该程序的电脑必须装 .NET 8 桌面运行时。

## 首次使用

1. 运行 `publish\DesktopTodo.exe`
2. 点标题栏 ⚙ 打开设置，填写 Nextcloud URL、用户名、应用密码（密码为遮蔽输入）
3. 点 **「测试连接」**：用当前填写的值（不落盘、不影响现有连接）实际连一次服务器，返回日历名与任务数
4. 确认成功后点 **「保存」**：设置**立即生效**——自动重建同步服务、重置定时同步并重新拉取数据，**无需关闭或重启程序**

### 设置项

| 项 | 说明 |
|---|---|
| Nextcloud URL | 例：`https://nc.example.com:8443/remote.php/dav/` |
| 用户名 / 应用密码 | 应用密码在 Nextcloud：设置 → 安全 → 设备与会话 → 创建新应用密码 |
| 日历名称 | 按名称选择 VTODO 日历；留空时回退到任务最多的日历 |
| 同步间隔（秒） | 最小 30，保存后定时器立即按新间隔重建 |
| 自签证书 SHA-256 指纹 | 可选。留空 = 系统严格 TLS 校验；填入后仅信任该指纹对应的证书 |
| 主题 / 开机自启 | 开机自启写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

> **关于 TLS 与 http**：系统严格证书校验只作用于 `https`。使用自签证书时，需在设置里填入该证书的 SHA-256 指纹才会被信任；留空则拒绝一切无效证书。`http://` 地址**可以正常使用**（不走 TLS 握手），但应用密码会以明文经网络传输，仅建议在内网可信环境临时使用。

### 凭据存放

应用密码保存在 **Windows 凭据管理器**（控制面板 → 凭据管理器 → Windows 凭据，目标名 `DesktopTodo/Nextcloud/...`），**不写入 `config.json`，也不进入 publish 目录**。`config.json` 只保存 URL、用户名和界面偏好。

旧版配置里若仍带 `app_password`，新版首次启动会自动迁移到凭据管理器并从 JSON 中删除。由于该密码曾以明文出现在多处，**迁移完成后仍建议到 Nextcloud 后台吊销并重新生成**。

自签证书指纹可在浏览器中查看证书详细信息 →「指纹 (SHA-256)」，填时去掉冒号与空格。

## 同步行为

- 定时全量拉取，添加 / 编辑 / 完成 / 删除 / 优先级变更立即写回服务器。
- 使用 ETag + `If-Match` / `If-None-Match` 做乐观并发；遇到 412 会提示「已被其他设备修改」，不会静默覆盖。
- 写回时以服务端原始 VTODO 为底稿，只替换本程序管理的字段，保留 `DESCRIPTION`、`CATEGORIES`、`DUE`、`X-*` 等未知属性。
- 写操作失败会在状态栏提示，任务不会被静默隐藏或丢弃。
- **已知限制**：目前没有离线队列，断网时列表为空且写操作失败提示；恢复后下一次定时同步会拉取最新数据。

## 构建

需要 .NET 8 SDK，运行 `build.bat`：

- 先清空 `publish`（避免旧 `config.json`、旧 dll 残留），再 `dotnet publish`
- 检查每步返回码，失败不再打印成功信息
- **输出不包含 `config.json`**；设置由程序首次运行时创建
- 脚本优先使用用户级 SDK（`%USERPROFILE%\.dotnet\dotnet.exe`，无管理员权限时的安装位置），否则用 PATH 中的 `dotnet`；`build.bat --no-pause` 可用于自动化构建

请勿手工把 `config.json` 放进 `publish` 目录，也不要把 publish 里的凭据类文件打包分发。

## 日志

未处理异常写入 `%LOCALAPPDATA%\DesktopTodo\error.log`，界面只显示简短提示。

## License

MIT（仓库尚未附 LICENSE 文件）。

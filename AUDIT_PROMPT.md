# Desktop Todo (WPF) — 代码审计提示词

你是一位资深的安全工程师和 .NET 架构师。请对以下 Windows WPF 桌面应用项目做一次完整的代码审计，覆盖安全、代码质量、架构设计、用户体验四个维度。请用中文输出报告。

## 项目位置

项目根目录：`F:\miniapp\desktop-todo-wpf\`

源码在 `DesktopTodo\` 子目录下。请按以下路径逐一读取并审查每个文件：

```
F:\miniapp\desktop-todo-wpf\DesktopTodo\App.xaml.cs
F:\miniapp\desktop-todo-wpf\DesktopTodo\MainWindow.xaml
F:\miniapp\desktop-todo-wpf\DesktopTodo\MainWindow.xaml.cs
F:\miniapp\desktop-todo-wpf\DesktopTodo\DesktopTodo.csproj
F:\miniapp\desktop-todo-wpf\DesktopTodo\Config\ConfigManager.cs
F:\miniapp\desktop-todo-wpf\DesktopTodo\Config\CredentialStore.cs
F:\miniapp\desktop-todo-wpf\DesktopTodo\config.example.json
F:\miniapp\desktop-todo-wpf\DesktopTodo\Services\CalDAVService.cs
F:\miniapp\desktop-todo-wpf\DesktopTodo\Services\TaskManager.cs
F:\miniapp\desktop-todo-wpf\DesktopTodo\Services\UpdateService.cs
F:\miniapp\desktop-todo-wpf\DesktopTodo\UpdateDialog.cs
F:\miniapp\desktop-todo-wpf\build.bat
F:\miniapp\desktop-todo-wpf\make-release.py
```

## 项目背景

- **技术栈**：C# .NET 8 WPF，零 NuGet 依赖（仅用 .NET BCL + WinForms 互操作）
- **功能**：常驻桌面的半透明待办卡片，通过 CalDAV 协议与 Nextcloud Tasks 双向同步
- **目标用户**：个人使用，但也已开源发布到 GitHub
- **安全基线**：应用密码存 Windows 凭据管理器（DPAPI），不写入 config.json；TLS 默认严格校验，自签证书用 SHA-256 指纹固定
- **已发布版本**：v1.1.1，含 GitHub Releases 自动更新功能

## 审计范围

上述文件涵盖了项目的全部源码。各文件的职责：

- `App.xaml.cs` — 应用入口、全局异常处理、--apply-update 更新器模式、--pretend-version 验收演练
- `MainWindow.xaml` / `MainWindow.xaml.cs` — 主窗口 UI、托盘、标题栏拖动/吸附、任务列表、设置对话框、更新对话框触发、反馈弹窗
- `DesktopTodo.csproj` — 项目配置、版本号
- `Config/ConfigManager.cs` — 配置读写、原子写入、旧版明文密码迁移、更新状态持久化、代理设置
- `Config/CredentialStore.cs` — Windows 凭据管理器封装（DPAPI）
- `config.example.json` — 配置模板（全占位值）
- `Services/CalDAVService.cs` — CalDAV 协议封装、TLS 校验/证书指纹固定、ETag/If-Match 乐观并发、保留未知 ICS 字段
- `Services/TaskManager.cs` — 任务增删改查、同步逻辑
- `Services/UpdateService.cs` — GitHub Releases 检测、下载、SHA-256 校验、安全解压、--apply-update 启动器、手动代理支持
- `UpdateDialog.cs` — 更新提示窗口（立即更新/稍后提醒/忽略此版本）
- `build.bat` — 本地编译脚本（CRLF + 纯 ASCII，保留 config.json）
- `make-release.py` — 一键打包 + 隐私扫描 + SHA-256 生成

## 审计要求

### 1. 安全审计（最高优先级）

重点检查：

- **凭据安全**：应用密码的存储、传输、内存生命周期；是否有泄露风险（日志、异常信息、序列化）
- **TLS/证书校验**：证书指纹固定逻辑是否可被绕过；http 模式下的安全提示是否充分
- **自动更新安全**：
  - 下载源验证（是否只接受 github.com/githubusercontent.com）
  - SHA-256 校验逻辑是否可靠
  - 解压防路径穿越（Zip Slip）是否完备
  - --apply-update 进程替换是否有竞态条件
  - 降级保护是否可被绕过
- **输入验证**：CalDAV URL、用户名、配置值是否有注入风险
- **反序列化**：JSON 反序列化是否有安全隐患
- **文件操作**：原子写入、配置损坏处理、备份逻辑
- **进程间通信**：--apply-update 的命令行参数传递是否安全

### 2. 代码质量

- **异常处理**：是否有未捕获异常、吞异常的范围、异常信息是否暴露敏感数据
- **资源释放**：HttpClient、NotifyIcon、FileStream 等 IDisposable 对象是否正确释放
- **线程安全**：UI 线程 vs 后台线程的交互、_syncing 标志的竞态、DispatcherTimer 回调
- **空引用**：可空引用类型是否正确处理
- **命名一致性**：变量/方法命名是否清晰一致

### 3. 架构设计

- **关注点分离**：UI、配置、同步、更新各层是否清晰
- **状态管理**：配置热更新（ApplySettingsAsync）是否会遗漏重建某些组件
- **扩展性**：添加新功能（如多日历、提醒）的难度
- **耦合度**：各组件之间的依赖是否合理

### 4. 用户体验

- **错误提示**：用户能否理解错误信息并知道下一步操作
- **边界情况**：首次启动无配置、服务器不可达、同步冲突、更新失败后的恢复路径
- **可发现性**：更新入口、反馈入口、设置入口是否容易被找到

## 输出格式

请按以下结构输出审计报告：

### 严重问题（必须修复）
每个问题包含：文件:行号、问题描述、风险等级、修复建议（含代码片段）

### 中等问题（建议修复）
同上格式

### 小问题 / 改进建议
简要列出即可

### 整体评价
一段话总结代码质量和安全状况

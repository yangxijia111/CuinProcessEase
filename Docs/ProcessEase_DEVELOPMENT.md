# ProcessEase 开发实施文档

版本：v0.1  
日期：2026-09-20

---

## 1. 技术方案

### 开发语言

```text
C#
```

### Runtime

```text
.NET 10
```

### UI

```text
WPF
XAML
```

### 底层能力

```text
System.Diagnostics
Win32 API
P/Invoke
Tool Help API
```

### 架构

```text
MVVM
```

但不引入大型 MVVM 框架。

自行实现少量：

```text
ObservableObject
RelayCommand
AsyncRelayCommand
```

减少第三方依赖。

---

## 2. 开发原则

项目不 Fork 任何现有任务管理器。

不复用：

- Process Hacker
- System Informer
- Task Explorer
- ProKill

等项目源码。

底层能力优先使用：

- .NET API
- Windows API

项目不开发：

- Kernel Driver
- Process Injection
- PPL Bypass

---

## 3. Solution 结构

最终建议：

```text
ProcessEase/
│
├─ ProcessEase.sln
│
├─ src/
│  │
│  ├─ ProcessEase.App/
│  │  ├─ Views/
│  │  ├─ ViewModels/
│  │  ├─ Controls/
│  │  ├─ Styles/
│  │  ├─ Converters/
│  │  └─ Assets/
│  │
│  ├─ ProcessEase.Core/
│  │  ├─ Models/
│  │  ├─ Interfaces/
│  │  ├─ Grouping/
│  │  ├─ Safety/
│  │  └─ Services/
│  │
│  ├─ ProcessEase.Windows/
│  │  ├─ ProcessApi/
│  │  ├─ Native/
│  │  ├─ Metadata/
│  │  └─ Security/
│  │
│  └─ ProcessEase.ElevatedHelper/
│
├─ tests/
│  │
│  ├─ ProcessEase.Core.Tests/
│  │  └─ ProcessEase.Windows.Tests/
│
├─ docs/
│  ├─ PRD.md
│  ├─ DEVELOPMENT.md
│  └─ TESTING.md
│
└─ README.md
```

依赖关系：

```text
ProcessEase.App
       ↓
ProcessEase.Core
       ↑
ProcessEase.Windows
```

Core 不允许依赖 WPF。

---

# Phase 0 —— 工程骨架

## 目标

建立一个：

```text
可编译
可启动
结构正确
可持续扩展
```

的 Windows 桌面项目。

暂时不读取真实进程。

## 工作内容

创建：

```text
ProcessEase.sln
```

Target：

```text
net10.0-windows
x64
```

项目：

```text
ProcessEase.App
ProcessEase.Core
ProcessEase.Windows
ProcessEase.Core.Tests
```

建立基础：

```text
ObservableObject
RelayCommand
App.xaml
MainWindow
MainViewModel
```

创建 Mock 数据：

```text
Google Chrome
WeChat
Steam
Visual Studio Code
```

用于制作第一版 UI。

## UI

实现：

- 顶部搜索框
- 资源概览
- 应用列表
- 详情侧栏
- 暂停刷新按钮
- 锁定列表按钮

第一阶段所有数据均为 Mock。

## 验收

必须满足：

```text
Build = 0 Error
启动无 Exception
窗口正常显示
应用列表能够显示 Mock 数据
点击应用可以打开详情
搜索 Mock 应用有效
深色 / 浅色至少一种完整可用
```

Git Tag：

```text
v0.0.1-p0
```

---

# Phase 1 —— Process Snapshot Engine

## 目标

完全不考虑 UI 分组。

第一步只做一件事：

> 正确读取 Windows 当前所有可以读取的进程。

## 核心模型

创建：

```text
ProcessSnapshot
```

包含：

```text
ProcessId
ParentProcessId
Name
ExecutablePath
StartTime
SessionId
WorkingSet
PrivateMemory
UserName
Architecture
IsElevated
```

部分无法读取的字段允许：

```text
null
```

不得因为一个进程 Access Denied 导致整个扫描失败。

## Windows API

主要使用：

```text
System.Diagnostics.Process
```

配合：

```text
CreateToolhelp32Snapshot
Process32First
Process32Next
```

获取：

```text
PID
PPID
```

禁止使用进程名猜 Parent Process。

## Process Identity

创建：

```text
ProcessIdentity
```

至少：

```text
PID
StartTime
```

用于避免 PID 重用。

## Snapshot Service

接口：

```text
IProcessSnapshotService
```

核心方法逻辑：

```text
CaptureAsync()
```

返回：

```text
ProcessSnapshotCollection
```

不得直接修改 UI。

## 错误处理

单个进程读取失败：

```text
AccessDenied
ProcessExited
Unknown
```

跳过对应字段。

不允许：

```text
throw → 整次扫描失败
```

## 验收

至少测试：

打开 Chrome。

确认：

```text
多个 chrome.exe
```

被成功读取。

关闭 Chrome 后：

旧 PID 从下一个 Snapshot 消失。

同时测试：

```text
系统进程
管理员程序
普通程序
快速启动退出程序
```

扫描线程不得导致 UI 卡顿。

Git Tag：

```text
v0.0.2-p1
```

---

# Phase 2 —— Process Tree

## 目标

建立完整：

```text
Parent
 ↓
Child
 ↓
Grandchild
```

关系。

## 数据模型

创建：

```text
ProcessNode
```

结构：

```text
ProcessNode
 ├─ Process
 ├─ Parent
 └─ Children[]
```

建立：

```text
ProcessTreeBuilder
```

输入：

```text
ProcessSnapshot[]
```

输出：

```text
ProcessTree[]
```

## 异常场景

必须处理：

- Parent 已退出
- Child 仍存在
- PPID 不存在
- PID 已被复用
- 循环异常数据

树构造不得死循环。

## 验收

Chrome / Edge 等多进程软件必须能得到类似：

```text
chrome.exe
├─ chrome.exe
├─ chrome.exe
│   └─ chrome.exe
└─ crashpad_handler.exe
```

树建立时间：

```text
500 processes < 50 ms
```

目标值。

Git Tag：

```text
v0.0.3-p2
```

---

# Phase 3 —— Application Grouping Engine

## 目标

这是项目核心 Phase。

将：

```text
Process
```

转换成：

```text
ApplicationGroup
```

## ApplicationIdentity

创建：

```text
ApplicationIdentity
```

包含：

```text
DisplayName
Executable
InstallDirectory
ProductName
CompanyName
PackageIdentity
Icon
Confidence
```

## ApplicationGroup

```text
ApplicationGroup

Identity
Processes[]
RootProcesses[]
CpuUsage
MemoryUsage
RiskLevel
```

## Grouping Pipeline

执行：

```text
Snapshot
   ↓
Process Tree
   ↓
Package Identity
   ↓
Directory Analysis
   ↓
Product Metadata
   ↓
Parent / Child Relationship
   ↓
Grouping Rules
   ↓
ApplicationGroup
```

## Confidence

建立：

```text
VeryHigh
High
Medium
Low
Unknown
```

低可信度禁止强制合并。

## 特殊情况

必须特别测试：

- Chrome
- Edge
- Discord 类 Electron 软件
- Steam 类 Launcher
- Java 程序
- Python 程序
- Node.js
- Microsoft Store 应用
- 无图标 exe
- Portable 软件

开发工具尤其注意：

```text
python.exe
node.exe
java.exe
```

不能把机器上所有：

```text
python.exe
```

自动识别成同一个软件。

需要结合：

- Parent Tree
- CommandLine
- Working Directory
- Executable Path

分析。

## 用户覆盖规则

设计：

```text
GroupingOverride
```

但这一 Phase 可以只实现数据模型，不实现 UI。

## 验收

普通用户运行多个常见软件后：

主界面应用数量必须明显少于：

```text
Process 数量
```

错误合并优先级必须：

```text
宁愿拆开
不要错合
```

Git Tag：

```text
v0.1.0-p3
```

---

# Phase 4 —— Safety Engine

## 目标

在实现 Kill 以前先实现：

> 什么不能 Kill。

## RiskLevel

```text
Normal
Elevated
System
Protected
Unknown
```

## System Process Rules

建立：

```text
ProcessSafetyService
```

维护核心系统保护规则。

至少保护：

```text
System
Registry
smss.exe
csrss.exe
wininit.exe
services.exe
lsass.exe
winlogon.exe
Secure System
```

同时保护：

```text
ProcessEase.exe
ProcessEase.ElevatedHelper.exe
```

## Additional Rules

结合：

- ExecutablePath
- SessionId
- Process Owner
- Known Critical List
- Access Rights

判断风险。

不能仅：

```text
Path contains System32
```

就决定安全与否。

## SafetyResult

每次操作必须先得到：

```text
SafetyResult
```

例如：

```text
Allowed = false
Reason = CriticalWindowsProcess
```

UI 根据 Reason 显示用户可以理解的文字。

## 验收

任何测试场景下：

```text
lsass.exe
csrss.exe
wininit.exe
```

结束按钮必须不可用。

不存在隐藏绕过机制。

Git Tag：

```text
v0.1.1-p4
```

---

# Phase 5 —— Desktop GUI MVP

## 目标

第一次形成真正可以日常使用的 ProcessEase。

## 首页

实现：

```text
应用列表
搜索
CPU
Memory
进程数量
应用数量
详情栏
```

## Stable List

后台 Snapshot 每：

```text
1000 ms
```

更新。

UI 使用 Diff：

```text
新增
删除
修改
```

不得：

```text
Items.Clear()
重新 Add 全部
```

避免列表闪烁和跳动。

## Sorting

默认：

```text
Application Name
```

资源变化不得改变位置。

用户主动选择：

```text
CPU
Memory
Process Count
```

才允许动态排序。

## Freeze

实现：

```text
锁定列表
```

和：

```text
暂停刷新
```

这是两个功能。

锁定：

```text
资源继续变化
位置不变
```

暂停：

```text
整个 Snapshot 停止更新
```

## Search

实时搜索：

- Application Name
- Process Name
- Executable Path
- Company

## System Processes

默认隐藏。

设置：

```text
显示 Windows 系统进程
```

开启后才能查看。

## 验收

实际运行：

- Chrome
- 微信
- Steam
- VS Code

用户必须能够在：

```text
5 秒以内
```

找到对应应用。

刷新过程中点击应用后：

当前选择不能因为刷新消失或跳走。

Git Tag：

```text
v0.2.0-p5
```

---

# Phase 6 —— Termination Engine

## 目标

实现核心：

```text
彻底结束软件
```

## TerminationService

创建：

```text
IProcessTerminationService
```

支持：

```text
CloseGracefully()
KillProcess()
KillTree()
KillApplication()
```

## Normal Close

首先尝试：

```text
CloseMainWindow
```

或标准窗口关闭消息。

给予：

```text
2~3 秒
```

合理等待。

## Force Kill

对于用户明确选择：

```text
强制结束
```

调用 Windows 支持的进程结束能力。

## Application Kill

流程：

```text
获取 ApplicationGroup
        ↓
重新 Snapshot
        ↓
重新验证 PID + StartTime
        ↓
Safety Check
        ↓
确定目标 Process Set
        ↓
结束
        ↓
重新 Snapshot
        ↓
检测残留
        ↓
生成 OperationResult
```

## OperationResult

包含：

```text
Requested
Succeeded
Failed
AlreadyExited
AccessDenied
Protected
ResidualProcesses
```

不能简单返回：

```text
bool
```

## UI

结束普通应用：

```text
确定结束 Google Chrome？

将结束：
12 个进程

未保存的数据可能丢失。

[取消]

[结束软件]
```

完成：

```text
✓ Google Chrome 已完全结束

12 个进程已结束
```

部分失败：

```text
⚠ 未完全结束

11 / 12 个进程已结束

Updater.exe
需要管理员权限
```

## 验收

使用测试程序建立：

```text
Parent
├─ Child1
├─ Child2
└─ Child3
```

执行：

```text
KillApplication
```

必须无残留。

系统进程不得受到影响。

Git Tag：

```text
v0.3.0-p6
```

---

# Phase 7 —— Elevated Helper

## 目标

解决：

```text
Access Denied
```

但不让 ProcessEase 主程序长期运行于管理员模式。

## 组件

新增：

```text
ProcessEase.ElevatedHelper.exe
```

默认不运行。

需要管理员权限时：

```text
Main App
   ↓
用户确认
   ↓
Windows UAC
   ↓
Elevated Helper
   ↓
完成 Operation
   ↓
退出
```

## IPC

主程序和 Helper 使用受控 IPC。

可以采用：

```text
Named Pipe
```

Helper 仅接受明确的操作请求。

例如：

```text
Kill PID
Expected StartTime
OperationId
```

Helper 必须重新验证目标。

不得盲目信任 Main App 传入 PID。

## 安全

Helper 不允许：

- 任意命令执行
- Shell Execute 任意字符串
- 运行用户指定 CMD
- 加载任意 DLL

Helper 唯一职责：

执行 ProcessEase 定义的有限高权限操作。

## 验收

普通权限启动 ProcessEase。

结束普通应用：

```text
不出现 UAC
```

结束管理员应用：

```text
出现一次 UAC
```

结束成功后：

```text
ElevatedHelper
```

必须退出。

Git Tag：

```text
v0.4.0-p7
```

---

# Phase 8 —— Resource Visualization

## 目标

增强用户看进程的体验。

不是制作专业性能分析器。

## CPU

计算：

```text
Application CPU
=
Sum(Process CPU)
```

记录最近：

```text
60 秒
```

## Memory

```text
Application Memory
=
Sum(Process WorkingSet)
```

## History

建立：

```text
ResourceSample
```

字段：

```text
Timestamp
CPU
Memory
```

每个 Application 保存：

```text
60 samples
```

固定上限。

## UI

应用详情：

- CPU 折线
- RAM 折线
- 当前 CPU
- 当前内存
- Peak CPU
- Peak Memory

不用第三方图表框架。

可使用 WPF：

```text
Polyline
Canvas
DrawingVisual
```

实现轻量图表。

## 首页

增加：

```text
高资源应用
```

例如：

```text
TOP CPU

Chrome       21%
Unity        14%
Discord       4%
```

## 验收

连续运行：

```text
30 分钟
```

历史数据内存不得无限增长。

图表刷新不能明显提升整体 CPU。

Git Tag：

```text
v0.5.0-p8
```

---

# Phase 9 —— UX / Polish

## 目标

把：

```text
程序员工具
```

变成：

```text
普通用户软件
```

## 完成

- 应用图标
- Fluent 风格
- 动画
- 空状态
- Loading 状态
- 错误提示
- 快捷键
- 窗口尺寸恢复
- DPI Scaling
- 深色主题
- 浅色主题

## 快捷键

建议：

```text
Ctrl + F
搜索

F5
刷新

Space
暂停刷新

Delete
结束所选应用
```

Delete 必须经过确认。

## Status

底部：

```text
127 Processes
21 Applications
Last Updated 11:42:07
```

## 验收

125%  
150%  
175%

Windows DPI 下不得：

- 文字截断
- 按钮重叠
- 内容错位

Git Tag：

```text
v0.6.0-p9
```

---

# Phase 10 —— Stability & Release

## 目标

达到可以公开发布的 v1.0。

## Stress Test

持续运行：

```text
8 小时
```

监控：

- ProcessEase CPU
- ProcessEase RAM
- Handle Count
- Thread Count

不得持续增长。

## Process Stress

测试：

```text
100
300
500
800
```

进程环境。

## Race Condition Test

快速执行：

- 启动 App
- 结束 App
- 启动 App
- 结束 App

测试：

- PID reuse
- Process exit during scan
- Process exit during kill
- Access denied
- Process spawn during kill

## Kill Race

重点测试：

```text
Kill Application
```

过程中软件产生新子进程。

结束以后必须：

```text
Re-scan
```

但最多执行有限次数。

禁止无限：

```text
Kill → Scan → Kill → Scan
```

循环。

建议：

```text
MaximumResidualPasses = 2
```

## Logging

实现：

```text
logs/
```

最大数量和文件大小限制。

例如：

```text
5 × 2 MB
```

自动滚动。

## Crash Handling

程序崩溃时：

不得影响 Windows 进程。

不得：

自动重新执行上一次 Kill。

## Release

输出：

```text
ProcessEase.exe
ProcessEase.ElevatedHelper.exe
```

第一版优先提供：

```text
Portable x64
```

之后再考虑 installer。

## v1.0 验收

必须满足：

```text
✓ 应用识别
✓ 应用分组
✓ 进程树
✓ 搜索
✓ 稳定列表
✓ CPU / RAM
✓ 资源图表
✓ 正常关闭
✓ Force Kill
✓ Kill Tree
✓ Kill Application
✓ 残留检测
✓ 管理员 Helper
✓ 系统进程保护
✓ 暂停刷新
✓ 锁定排序
✓ 系统进程隐藏
✓ Dark / Light
✓ DPI
✓ 日志
✓ 长时间运行稳定
```

Tag：

```text
v1.0.0
```

---

## 4. Phase 总路线

```text
P0
工程骨架 + Mock UI
 │
 ▼
P1
Process Snapshot
 │
 ▼
P2
Process Tree
 │
 ▼
P3
Application Grouping
 │
 ▼
P4
Safety Engine
 │
 ▼
P5
Desktop GUI MVP
 │
 ▼
P6
Termination Engine
 │
 ▼
P7
Elevated Helper
 │
 ▼
P8
Resource Visualization
 │
 ▼
P9
UX / Polish
 │
 ▼
P10
Stability / Release
 │
 ▼
ProcessEase 1.0
```

---

## 5. 最重要的开发约束

开发过程中始终遵守：

```text
Safety > Kill Ability
Correct Grouping > Aggressive Grouping
Application > Process
Simple UI > Professional Complexity
Stable List > Real-time Sorting
Explicit User Action > Automatic Killing
```

整个项目最核心的技术不是 UI，也不是 KillProcess。

而是：

```text
Process
     ↓
Process Tree
     ↓
Application Identity
     ↓
Application Group
     ↓
Safety Classification
     ↓
User Action
```

只要这一条链正确，ProcessEase 才真正区别于 Windows Task Manager。

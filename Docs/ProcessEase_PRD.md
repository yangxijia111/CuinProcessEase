# ProcessEase 需求规格说明书

版本：v0.1  
日期：2026-09-20  
平台：Windows 11  
项目类型：Windows 桌面进程/应用管理工具

---

## 1. 项目背景

Windows 自带任务管理器主要围绕“进程”设计。

普通用户真正关心的却往往不是：

- PID 16424
- chrome.exe
- msedgewebview2.exe
- updater.exe
- helper.exe

而是：

> “微信到底有没有完全退出？”

> “Chrome 为什么关掉窗口以后还有十几个进程？”

> “这个软件卡死了，我想把和它有关的东西全部关掉。”

> “任务管理器一直刷新乱跳，我根本点不到。”

当前任务管理器存在几个明显使用问题：

1. 一个应用可能对应多个进程。
2. 子进程名称可能和主程序完全不同。
3. 用户难以判断哪些进程属于同一个软件。
4. 系统进程数量庞大，干扰普通用户寻找目标。
5. CPU/内存实时变化会引起排序变化，列表不断跳动。
6. 普通用户不了解 PID、父进程、服务、Session 等概念。
7. “结束任务”和“结束整个软件”并不是同一个概念。
8. 某些应用退出后仍残留后台进程。
9. 部分进程权限较高，普通结束会失败，但系统没有给用户足够清晰的解释。

ProcessEase 的目标不是制作一个比任务管理器更复杂的任务管理器，而是：

> 将“管理进程”重新设计成“管理软件”。

---

## 2. 产品定位

ProcessEase 是一个面向普通 Windows 用户的桌面应用管理器。

核心理念：

**默认显示应用，而不是显示进程。**

例如 Windows 任务管理器显示：

```text
chrome.exe
chrome.exe
chrome.exe
chrome.exe
crashpad_handler.exe
msedgewebview2.exe
```

ProcessEase 应显示：

```text
Google Chrome

12 个相关进程
CPU：8.4%
内存：1.62 GB

[展开进程]
[结束软件]
```

只有用户主动展开时，才显示底层进程。

---

## 3. 核心目标

### 3.1 软件级聚合

自动识别属于同一软件的多个进程。

例如：

```text
Discord
 ├─ Discord.exe
 ├─ Discord.exe
 ├─ Discord.exe
 ├─ Squirrel.exe
 └─ crashpad_handler.exe
```

界面默认仅显示：

```text
Discord
5 个进程
```

### 3.2 一键彻底结束软件

用户点击：

```text
结束软件
```

系统应尝试结束该应用组中的全部相关进程，而不是只结束一个 PID。

完成后再次扫描。

如果仍存在残留：

```text
Discord
已结束 4 个进程
仍有 1 个进程运行
```

而不是简单告诉用户“操作完成”。

### 3.3 默认隐藏系统进程

普通模式不得让：

```text
System
Registry
smss.exe
csrss.exe
wininit.exe
services.exe
lsass.exe
svchost.exe
winlogon.exe
Secure System
```

等进程干扰用户。

系统进程进入：

```text
高级模式
    ↓
系统进程
```

普通用户默认看不到。

### 3.4 防止列表乱跳

ProcessEase 不允许因为 CPU 使用率变化导致整个应用列表每秒重新排序。

默认排序：

```text
应用名称
```

CPU / RAM 数据实时更新，但是：

**不改变应用所在位置。**

如果用户主动选择：

```text
按 CPU 排序
```

则允许排序变化。

同时提供：

```text
🔒 锁定列表
```

锁定后：

- 应用位置不变化
- CPU / RAM 数据仍然更新
- 新进程可以标记出现
- 不自动改变用户当前选择

以及：

```text
⏸ 暂停刷新
```

暂停后整个当前快照被冻结。

---

## 4. 用户群体

主要面向：

- 普通 Windows 用户
- 学生
- 游戏玩家
- 软件开发者
- 经常遇到软件卡死的人
- 需要清理后台程序的人

产品不要求用户理解：

- PID
- Handle
- Thread
- Token
- Session
- Job Object
- DLL
- Service Host

这些信息只允许出现在高级模式。

---

## 5. 主界面

推荐布局：

```text
┌───────────────────────────────────────────────────────┐
│ ProcessEase                   搜索软件...       ⚙    │
├───────────────────────────────────────────────────────┤
│                                                       │
│ 概览                                                  │
│                                                       │
│ CPU 23%       内存 48%        应用 17      进程 126   │
│                                                       │
├───────────────────────────────────────────────────────┤
│ [全部应用] [高资源] [后台运行] [系统进程]             │
│                                                       │
│ 排序：名称 ▼            🔒锁定列表     ⏸暂停刷新      │
├───────────────────────────────────────────────────────┤
│                                                       │
│ Google Chrome                                         │
│ chrome.exe                       12 个进程             │
│ CPU 8.4%     RAM 1.62 GB                              │
│                                  [详情] [结束软件]     │
│                                                       │
├───────────────────────────────────────────────────────┤
│ WeChat                                                │
│ WeChat.exe                        7 个进程             │
│ CPU 1.2%     RAM 624 MB                               │
│                                  [详情] [结束软件]     │
│                                                       │
├───────────────────────────────────────────────────────┤
│ Steam                                                 │
│ steam.exe                         9 个进程             │
│ CPU 0.4%     RAM 382 MB                               │
│                                  [详情] [结束软件]     │
│                                                       │
└───────────────────────────────────────────────────────┘
```

---

## 6. 应用详情页

点击某个软件以后打开右侧详情面板。

示例：

```text
Google Chrome

状态：运行中
进程：12
CPU：8.4%
内存：1.62 GB

安装位置：
C:\Program Files\Google\Chrome\...

启动时间：
10:42:16

────────────────────

进程树

chrome.exe
├─ chrome.exe
├─ chrome.exe
├─ chrome.exe
├─ chrome.exe
└─ crashpad_handler.exe

────────────────────

[正常关闭]

[强制结束]

[结束整个软件]
```

高级模式可额外显示：

```text
PID
PPID
命令行
Executable Path
Session
Architecture
Start Time
User
```

---

## 7. 搜索

搜索框需要同时支持：

应用名：

```text
Chrome
```

进程名：

```text
chrome.exe
```

路径：

```text
Google\Chrome
```

发布者：

```text
Google LLC
```

搜索结果仍然按照“应用组”展示。

---

## 8. 应用分组系统

这是 ProcessEase 最核心的功能。

每个进程首先形成：

```text
ProcessInfo
```

之后经过：

```text
ApplicationGroupingEngine
```

生成：

```text
ApplicationGroup
```

ApplicationGroup 示例：

```text
ApplicationGroup

Name = Google Chrome
MainExecutable = chrome.exe
Processes = 12
RootProcesses = 1
InstallDirectory = ...
Publisher = Google LLC
Confidence = High
```

---

## 9. 分组判断规则

禁止单纯根据进程名称分组。

推荐优先级：

### Level 1：软件包身份

如果两个进程具有相同 Package / App Identity，则直接归为同组。

可信度：

```text
Very High
```

### Level 2：父子进程关系

例如：

```text
Steam.exe
   ↓
steamwebhelper.exe
   ↓
steamwebhelper.exe
```

明显属于同一进程树。

可信度：

```text
High
```

### Level 3：安装目录

例如：

```text
C:\Program Files\AppX\App.exe

C:\Program Files\AppX\Helper.exe
```

路径属于同一应用安装目录。

可信度：

```text
High
```

### Level 4：文件元数据

读取：

```text
ProductName
CompanyName
FileDescription
OriginalFilename
```

用于辅助判断。

可信度：

```text
Medium
```

### Level 5：组合判断

路径 + Publisher + Process Tree + ProductName 可以共同提高可信度。

---

## 10. 不确定分组

如果 ProcessEase 无法确定两个进程是否属于同一应用：

**禁止强行合并。**

显示：

```text
未知应用

helper.exe
```

而不是错误归入其他应用。

允许用户：

```text
合并到某应用
```

或者：

```text
始终单独显示
```

用户规则保存到：

```text
GroupingOverrides.json
```

---

## 11. 系统进程安全机制

安全优先级必须高于“杀进程能力”。

以下类型默认：

```text
不可结束
```

包括但不限于：

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

ProcessEase 自身进程同样不得被自己结束。

系统进程显示：

```text
🔒 Windows 系统进程
```

按钮：

```text
结束软件
```

必须禁用。

不得提供任何：

- PPL 绕过
- 内核驱动杀进程
- Token 欺骗
- 系统保护绕过
- 代码注入

功能。

---

## 12. 风险等级

每个应用建立 RiskLevel：

```text
Normal
Elevated
System
Protected
Unknown
```

界面分别对应：

普通应用：

```text
结束软件
```

管理员应用：

```text
需要管理员权限
```

系统应用：

```text
Windows 系统组件
```

保护进程：

```text
Windows 不允许结束此进程
```

未知：

```text
无法判断安全性
查看详情
```

---

## 13. 三种结束模式

### 正常关闭

优先向应用窗口发送正常关闭请求。

目的：

允许应用保存数据。

### 强制结束

直接结束指定进程。

必须弹出提醒：

```text
未保存的数据可能丢失。
```

### 结束整个软件

这是 ProcessEase 的核心功能。

执行：

```text
ApplicationGroup
      ↓
获取当前 Process Snapshot
      ↓
确认所有成员 PID
      ↓
识别 Root Processes
      ↓
终止应用进程
      ↓
重新扫描
      ↓
寻找残留进程
      ↓
显示结果
```

完成结果：

```text
✓ 已完全结束

结束 12 个进程
耗时 0.7 秒
```

或者：

```text
⚠ 部分完成

结束 11 个进程

仍运行：
Updater.exe

原因：
需要管理员权限
```

---

## 14. 管理员权限

ProcessEase 默认：

**不得以管理员身份启动。**

只有用户操作管理员进程时：

```text
此应用以管理员权限运行。

是否允许 ProcessEase 临时获取管理员权限？

[取消]

[以管理员权限结束]
```

之后启动：

```text
ProcessEase.ElevatedHelper.exe
```

弹出 Windows UAC。

完成操作以后 Helper 立即退出。

主程序始终保持普通权限。

---

## 15. 资源监控

MVP：

- CPU
- 内存
- 进程数量

之后增加：

- CPU 60 秒历史
- 内存 60 秒历史
- 磁盘 IO
- 启动时间

网络 IO 不进入第一阶段。

---

## 16. 图形化资源显示

应用详情显示：

```text
CPU

30% ┤
    │       ╭─╮
20% ┤   ╭───╯ ╰──╮
    │ ╭─╯         ╰─
10% ┤─╯
    └────────────────
      60 秒
```

以及：

```text
Memory

820 MB
```

主页面允许：

```text
高 CPU
高内存
后台运行
```

筛选。

---

## 17. 刷新策略

默认刷新频率：

```text
1000 ms
```

后台线程采集。

UI 不允许直接同步枚举全部进程。

数据流程：

```text
Process Scanner
      ↓
Snapshot
      ↓
Grouping Engine
      ↓
Diff Engine
      ↓
ViewModel
      ↓
UI
```

只更新改变的数据。

禁止每秒重建整个应用列表。

---

## 18. PID 重用问题

不得仅使用：

```text
PID
```

识别唯一进程。

ProcessIdentity 至少包含：

```text
PID
StartTime
```

避免某个进程退出以后 PID 被 Windows 分配给其他进程而产生误操作。

---

## 19. 设置

设置页面：

```text
常规

☑ 开机启动
☑ 记住窗口大小
☑ 自动刷新

刷新间隔
1 秒

────────────────

显示

主题：
跟随系统

☑ 默认隐藏 Windows 系统进程
☑ 显示应用路径
☑ 显示资源使用

────────────────

安全

☑ 结束整个软件前确认

管理员权限：
需要时询问

────────────────

高级

☐ 显示系统进程
☐ 显示 PID
☐ 显示进程树
```

---

## 20. 日志

仅记录用户主动执行的管理操作。

例如：

```text
2026-09-20 14:31:02
Kill Application
Google Chrome
12 processes
Success
```

不得记录：

- 用户文档
- 浏览内容
- 输入内容
- 密码
- 窗口文本

默认无任何网络上传。

---

## 21. 隐私

ProcessEase 第一版：

```text
完全离线
```

不得：

- 发送遥测
- 上传进程列表
- 上传安装软件列表
- 上传设备数据

所有配置保存在本地。

---

## 22. 性能目标

目标环境：

Windows 11 x64。

目标：

启动时间：

```text
< 2 秒
```

正常后台 CPU：

```text
平均 < 2%
```

正常内存：

```text
< 150 MB
```

进程数量：

```text
500 个以内正常工作
```

刷新周期：

```text
1000 ms
```

UI 操作不得被进程扫描阻塞。

---

## 23. MVP 范围

第一正式可用版本必须具备：

```text
✓ 应用分组
✓ 进程树
✓ 搜索
✓ 系统进程隐藏
✓ CPU
✓ 内存
✓ 稳定列表
✓ 暂停刷新
✓ 锁定列表
✓ 正常关闭
✓ 强制结束
✓ 结束整个软件
✓ 系统进程保护
✓ 管理员权限提示
✓ 深色 / 浅色界面
```

---

## 24. MVP 不做

第一版明确不实现：

```text
进程注入
DLL 管理
内核驱动
PPL 绕过
Registry 编辑
服务管理
启动项管理
卸载软件
网络抓包
病毒检测
内存编辑
进程优先级优化
自动释放内存
```

---

## 25. 后续功能

未来可以考虑：

```text
自动结束规则
软件 Watchlist
后台残留提醒
托盘模式
右键「彻底结束此软件」
软件资源历史
资源异常提醒
自定义应用分组
应用启动时间统计
一键结束一组开发环境
游戏模式
开发模式
```

例如：

```text
开发模式

结束：
Node.js
Python
Unity
ADB

[全部结束]
```

但所有自动化 Kill 功能必须独立设计安全机制后才能上线。

---

## 26. 产品原则

ProcessEase 的核心判断标准不是：

> “我们还能显示多少系统信息？”

而是：

> “一个不了解 Windows 进程的人，能不能在 5 秒内安全结束一个软件？”

如果某项功能会增加普通用户理解成本，却不能明显改善“找到软件 → 判断软件 → 结束软件”的流程，则默认不进入主界面。

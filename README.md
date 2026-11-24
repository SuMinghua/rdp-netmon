# RDP 网络监视器 (RdpNetMonitor)

一个运行在 **RDP 服务端（被控端）** 的实时网络监视小工具，用于弥补 Windows 自带 RDP 诊断对话框的三点不足：

1. **官方对话框强制置顶、遮挡桌面** → 本工具是普通窗口，可最小化/隐藏到托盘，置顶和鼠标穿透均为可选开关；
2. **指标太单一** → 提供 TCP/UDP 吞吐、丢包/重传、FEC、UDP 占比、多层 RTT（TCP estats / RemoteFX 计数器 / ICMP）等丰富指标；
3. **运行数据无法留存** → 每秒指标落盘为 CSV，运行事件写日志文件，日志面板可选中复制。

## 功能特性

- 自动识别本机角色：有人 RDP 连入 → 服务端模式；本机发起连接 → 客户端模式
- 实时指标：远端 IP、客户端名/系统版本（WTS API）、会话 ID、UDP 协商/激活状态、TCP 3389 与 RDP-UDP 吞吐（速率 + 累计字节）、丢包率、重传率、FEC 率、UDP 流量占比、带宽估算
- RTT 判定链：TCP estats（最准，需管理员）→ RemoteFX 性能计数器（与 ICMP 交叉校验，空闲失真值自动标记 stale 并降级）→ ICMP Ping
- 双图表：吞吐曲线 + 延迟曲线，图例按实际宽度自动换行
- 日志面板：事件日志支持鼠标多选、`Ctrl+C` 复制选中、`Ctrl+A` 全选、右键菜单复制选中/全部
- 数据落盘：`logs/rdpnet_YYYYMMDD_HHMMSS.csv`（每秒指标）+ `logs/events_YYYYMMDD_HHMMSS.log`（运行事件）
- 托盘支持：隐藏到托盘、显示主窗口、取消鼠标穿透（穿透开启时出现）、复制诊断、退出
- 单实例运行（全局 Mutex）

## 环境要求

- Windows 10/11（x64；x86 未实测）
- .NET Framework 4.8（Win10 22H2 系统自带，无需安装）
- **TCP estats 连接级统计的启用需要管理员权限**（`SetPerTcpConnectionEStats` 要求）。非管理员运行时其余指标正常，estats 不可用并在日志中提示

## 构建

零第三方依赖，直接使用系统自带的 C# 编译器：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

或双击 `启动监视器.bat`（exe 不存在时自动构建再启动）。

产物为单文件 `RdpNetMonitor.exe`（AnyCPU，/optimize+）。仅支持 C# 5 语法（随系统 csc 版本）。

## 使用

1. 建议**以管理员身份运行**（estats 采集需要；也避免被低权限进程干扰）
2. 程序启动后自动检测连接状态并切换模式，无需配置
3. 右上角按钮：置顶 / 鼠标穿透 / 复制诊断 / 隐藏到托盘 / 退出
4. 鼠标穿透开启后窗口半透明且不响应点击，通过托盘菜单「取消鼠标穿透」恢复
5. 日志面板：拖动或 Shift 多选 → `Ctrl+C` 复制；`Ctrl+A` 全选；也可用右键菜单
6. 日志文件位于 exe 同目录 `logs\` 下

## 指标数据源

| 指标 | 数据源 |
| --- | --- |
| RDP-TCP / RDP-UDP 吞吐、丢包、重传、FEC | RemoteFX Network 性能计数器（实例 `RDP-tcp 1`）及 UDP 计数器 |
| TCP 连接级 RTT（SampleRtt、RttVar、重传、超时、拥塞信号） | TCP Extended Statistics（estats）PATH 结构，需管理员 |
| TCP RTT（备选） | RemoteFX TCP RTT 计数器，空闲时会失真（如固定 400ms），与 ICMP 交叉校验后标记 stale 并降级 |
| ICMP RTT | 对远端 IP 的 Ping |
| 客户端名/版本/会话 | WTS API（WTSEnumerateSessions / WTSQuerySessionInformation） |
| TCP 连接表（本机端口 3389） | GetExtendedTcpTable |

## 已知限制

- RemoteFX 性能计数器需要 RDP 会话建立后才存在
- estats 采集按连接生效，重新连接后需重新启用（程序自动处理）
- 详情见 [docs/NOTES.md](docs/NOTES.md)

## 目录结构

```
rdp-netmon/
├── RdpMonitorCore.cs   # 数据层：性能计数器、TCP 表、estats、WTS、ICMP
├── RdpNetMonitor.cs    # UI 层：窗口、图表、日志、托盘
├── build.ps1           # 构建脚本（系统 csc.exe）
├── 启动监视器.bat       # 一键构建+启动
├── docs/NOTES.md       # 实现笔记与排坑记录
├── CHANGELOG.md        # 版本历史
└── logs/               # 运行日志（不入库）
```

## License

[MIT](LICENSE)

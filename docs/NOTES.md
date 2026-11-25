# 实现笔记与排坑记录

本文记录开发过程中的关键技术决策与踩坑，供后续维护参考。

## 一、RTT 判定链

按优先级取第一个可信值：

1. **TCP estats SampleRtt**（连接级真实 RTT，最准）。仅当 PATH 类型采集已启用（EnableCollection=1）且数值通过合理性校验时采用：
   - SampleRtt ∈ [1, 5000] ms，RttVar ≤ 5000，重传/超时/拥塞信号计数不溢出
   - 与 ICMP 交叉校验：`SampleRtt ≤ 5 × ICMP RTT + 50`
2. **RemoteFX 性能计数器 TCP RTT**。该计数器在连接空闲时会返回陈旧失真值（实测固定 400ms，而 ICMP 仅 3ms）。stale 判定：`TcpRttMs > 3 × (ICMP RTT + 5)`，命中则标记 `RfxRttStale` 并跳过
3. **ICMP Ping** 兜底

## 二、TCP estats 接入（重灾区，必读）

estats 相关 API 全部位于 `iphlpapi.dll`，连接用 `MIB_TCPROW`（本地/远端地址端口、状态）定位。

### 坑 1：SetPerTcpConnectionEStats 只有 6 个参数

启用采集的 `SetPerTcpConnectionEStats` 原型是：

```
(Row, EstatsType, Rw, RwVersion, RwSize, Offset)
```

而读取的 `GetPerTcpConnectionEStats` 是 11 个参数（RW/ROS/ROD 三组各 3 个）。早期把 Set 也声明成 11 个参数——在 x64 上因寄存器传参意外"能用"，但这是错误声明，必须按 6 参数写。

### 坑 2：RW 缓冲区必须恰好 1 字节，否则 1784

`TCP_ESTATS_*_RW_v0` 结构体只是单个 `BOOLEAN EnableCollection`（1 字节）。**Windows 严格校验 RW 缓冲区大小**：传 4 字节会返回 `1784 ERROR_INVALID_USER_BUFFER`（实测验证：1 字节成功，4 字节失败）。这与"缓冲区至少 sizeof"的常见假设相反。

### 坑 3：每种统计类型必须独立启用

estats 按类型独立开关：`TcpConnectionEstatsData = 1`、`TcpConnectionEstatsPath = 3` 是**两个独立**的采集开关。只启用 DATA 时，PATH 的 ROD 结构返回的是**内核未初始化的垃圾数据**（实测：SampleRtt=3350135819 之类随机值），且没有任何错误码提示——Get 返回 0 但数据不可信。

微软文档原文："如果 SetPerTcpConnectionEStats 调用失败，则对 GetPerTcpConnectionEStats 的后续调用将返回无意义的随机数据"。因此正确做法：

1. 对用到的每种类型分别调用 Set 启用（本项目用 DATA + PATH）
2. 读取前先检查**对应类型自身**的 EnableCollection，为 0 则数据不可用

### 坑 4：需要管理员权限

非管理员进程调用 Set 返回错误码 5（拒绝访问）。程序以非管理员运行时 estats 不可用，日志会提示。

### 结构体布局

`TCP_ESTATS_PATH_ROD_v0` 为 40 个 ULONG（160 字节），字段顺序与当前 MS 文档逐一对过，无填充问题。注意 `TCP_ESTATS_SND_CONG_ROD_v0` 含 3 个 SIZE_T（x64 下 8 字节）——如将来使用需注意对齐。

## 三、其他 Win32 细节

- **GetExtendedTcpTable 端口字节序**：端口存于低位 16 位且为网络字节序，需 `Swap16` 解码：`(ushort)(((v >> 8) & 0xFF) | ((v << 8) & 0xFF00))`；地址同为网络字节序
- **WTS API**：`WTSEnumerateSessions` 必须 `CharSet.Unicode`；`WTSQuerySessionInformation` 取客户端名用 `WTSInfoClass=10`（LPWSTR）、客户端版本号用 `=9`（ULONG）

## 四、WinForms 踩坑

- **SplitContainer 尺寸异常**：控件尚未完成布局（宽 150）时在对象初始化器里设 `SplitterDistance` 或 `Panel1MinSize/Panel2MinSize` 会抛"SplitterDistance 必须在 Panel1MinSize 和 Width - Panel2MinSize 之间"——全部改到 `Shown` 事件里设置并做夹取
- **ListView 每秒刷新保持滚动位置**：`Clear()` + 重建会重置纵向滚动条。改为原地更新 `SubItems[1].Text`，仅当行结构（键集合）变化时才重建；列宽按 `ClientSize.Width`（已不含垂直滚动条）比例分配，避免横向滚动条
- **estats 行抖动**：estats 可用性切换会触发重建 → estats 相关行始终存在，不可用时显示占位值，不随可用性增删
- **图表图例重叠**：固定间距 + 固定起点会与标题重叠 → 用 `MeasureString` 实测宽度、标题独占一行（padT=46）、超宽自动换行
- **标签截断**：固定宽度标签显示不全 → AutoSize + FlowLayoutPanel 单行不换行
- **双行顶栏**：标签与按钮同排 FlowLayout 会互相挤压遮挡 → 按钮行 Dock=Top 右对齐 + 标签行 Dock=Fill
- **BeginInvoke 丢失**：监控线程在控件句柄创建前 BeginInvoke 会被丢弃 → 事件先入队（lock 保护），UI 定时器消费队列
- **提权重编译 CS0016**：运行中的 exe 被锁，重编译失败；需先结束进程再编译（低权限 taskkill 无法结束提权实例，需提权结束）

## 五、语言环境与构建约定

本项目采用的开发/编译环境：

- **语言**：C#，仅使用 C# 5 语法（受编译器版本限制，无字符串插值、无 null 条件运算符等）
- **运行时框架**：.NET Framework 4.8（Win10 22H2 系统自带，无需安装）
- **编译器**：系统自带 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（x64；32 位系统为 `Framework\` 目录），不使用 Visual Studio、MSBuild 或 NuGet
- **UI 库**：WinForms（System.Windows.Forms / System.Drawing）
- **引用程序集**：仅 System.dll、System.Core.dll、System.Drawing.dll、System.Windows.Forms.dll 四个框架程序集，零第三方依赖
- **编译参数**：`/nologo /target:winexe /platform:anycpu /optimize+`；注意 `/out`、`/target` 必须放在源文件之前
- **构建脚本**：build.ps1（调用 csc）+ 启动监视器.bat（exe 不存在时自动构建再启动）
- **选型理由**：目标机器是 RDP 服务器、没有安装开发工具，.NET Framework 4.8 + csc 是开箱即用的组合，产物为单个可独立分发的 exe。代价是语法停留在 C# 5，且 P/Invoke 声明需格外小心（estats 1784 问题即与此相关）
- **单实例 Mutex**：`Local\RdpNetMonitor.SingleInstance`
- **自检分支**：`--selftest N` 在 Mutex 获取**之前**运行，输出 `logs/selftest.json`（用于无 GUI 自检）

# 更新日志

## v1.1.0 (2026-11-25)

**TCP estats 修复（错误码 1784 / 数据不可靠）**

- 修正 `SetPerTcpConnectionEStats` 的 P/Invoke 声明（6 参数，原为错误的 11 参数）
- 启用采集的 RW 缓冲区改为恰好 1 字节（4 字节会被内核拒绝，返回 1784 ERROR_INVALID_USER_BUFFER）
- estats 按类型独立启用：同时启用 DATA 与 PATH 类型；EnableCollection 改为检查 PATH 类型自身标志（此前只启用 DATA 导致 PATH ROD 返回未初始化垃圾数据）
- 修复后 estats 输出真实值（SampleRtt/RttVar/超时计数等），日志不再出现 1784 或"数据不可靠"警告

**日志面板选中复制**

- 日志区域支持鼠标多选（MultiExtended）
- `Ctrl+C` 复制选中日志，`Ctrl+A` 全选
- 右键菜单：复制选中日志 / 复制全部日志
- 启动时在日志中提示复制操作方法

**仓库整理**

- 新增 README.md、docs/NOTES.md、CHANGELOG.md、LICENSE（MIT）、.gitignore
- 移除调试探针等临时文件

## v1.0.0 (2026-11-24)

**首个可用版本**

- 实时指标：TCP 3389 + RDP-UDP 吞吐/累计字节、丢包率、重传率、FEC 率、UDP 占比、带宽估算
- 三层 RTT：TCP estats → RemoteFX 计数器（ICMP 交叉校验 + 空闲 stale 检测）→ ICMP
- 服务端/客户端模式自动识别（WTS API 获取客户端名与版本）
- 吞吐 + 延迟双图表（图例自动换行）
- 每秒 CSV 落盘 + 事件日志文件
- 托盘、置顶、鼠标穿透（含托盘恢复入口）、复制诊断、单实例

**UI 修复（开发期内）**

- 顶栏按钮被标签遮挡 → 双行布局（按钮行右对齐 + 标签行自适应）
- 右侧数据面板横向滚动条、刷新重置纵向滚动条 → 列宽比例分配 + 原地更新行数据
- 图表图例与标题重叠 → 实测宽度换行、标题独占一行
- 头部 RTT 标签截断 → AutoSize
- 启动时 SplitterDistance 越界崩溃 → 布局参数移至 Shown 事件并夹取

# CCNest 技术架构与关键决策

本文记录 CCNest 的架构、关键技术决策及其理由、调研结论与安全边界。

## 1. 技术选型

| 选择 | 理由 |
| --- | --- |
| **C# / .NET 9 + WPF** | 需要大量 Windows 原生能力（WinINet、IP Helper、WFP、DPAPI、作业对象、命名管道 ACL、COM），.NET 对这些 API 的 P/Invoke 与 COM 互操作最成熟；WPF 稳定、无需额外运行时，适合小团队长期维护。自包含发布后用户无需安装 .NET。 |
| **WPF-UI 4.3（Fluent）+ CommunityToolkit.Mvvm** | 提供 Windows 11 原生风格（Mica、Fluent 控件与图标），MVVM 源生成器减少样板代码。导航使用自建 ListBox + DataTemplate，避免框架导航的复杂度。 |
| **mihomo v1.19.33（独立进程）** | 事实标准的 Clash 兼容内核，支持 `dialer-proxy` 链式拨号、listener 级 `proxy` 覆盖、内存载荷热重载（`PUT /configs` payload）、Windows TUN（sing-tun 内置 wintun）。以未修改的官方二进制作为独立进程运行（GPL-3.0 聚合分发）。 |
| **YamlDotNet** | 只读解析现有 Clash 配置、导入节点（保留引号标量的类型，避免 "012345" 这类密码被当成数字）。 |
| **Inno Setup 6** | 成熟、免费、支持中文界面与按机器安装。 |

## 2. 与现有 Mihomo / Clash 的集成方式（调研结论）

评估过三种方案：

1. **改写现有客户端配置**（向 Clash Verge Rev 的配置注入链式代理与规则）：Verge 会在切换订阅、重启时重新生成运行时配置，注入内容会丢失或与其 UI 状态冲突；修改用户配置文件风险高、难以干净恢复。**否决。**
2. **只通过控制接口操控现有实例**：mihomo API 没有“新增代理节点”的接口，只能整体替换配置（同样会被客户端覆盖）。**仅用于辅助功能**：检测、读取端口与 TUN 状态、节点测速、会话期间锁定策略组节点、在全局 TUN 模式下临时关闭其 TUN（均在结束时恢复）。
3. **CCNest 私有 mihomo 实例 + 经现有客户端本地端口串联**（采用）：
   ```
   应用 ──► CCNest mihomo (127.0.0.1:17897)
              CCNest-Gate(select: CCNest-Static | REJECT)
              CCNest-Static (二级静态代理, dialer-proxy: CCNest-First)
              CCNest-First  (现有 Clash 的 127.0.0.1:7897 / 手动代理 / 导入节点)
   ```
   现有客户端配置零修改，继续负责一级线路（节点选择、订阅、规则）；CCNest 完全掌控二级与保护逻辑。一级线路也可以是手动代理、导入到 CCNest 内核的节点，或不使用。

现有客户端的检测：进程（Verge Rev / Nyanpasu / FlClash / CFW / v2rayN / mihomo / sing-box）、监听端口与所属 PID（`GetExtendedTcpTable`）、端口协议探测（SOCKS5 握手 / HTTP 400 探测，不访问外网）、配置文件（Verge 运行时 `clash-verge.yaml` 等）、控制接口（TCP 与 `\\.\pipe\verge-mihomo` 命名管道，HTTP over named pipe）。

## 3. 不回退（fail-closed）的实现层次

CCNest 明确区分以下几个层次，并在界面“保护范围”中如实说明当前配置的保证：

| 层次 | 机制 | 能保证什么 |
| --- | --- | --- |
| 正常代理路由 | mihomo 规则 `MATCH,CCNest-Gate`；Gate 只有 `CCNest-Static` 与 `REJECT`，**没有 DIRECT、没有自动切换组** | 二级代理失败（不可达、认证失败、超时）时连接直接失败，不会落到一级或直连。经真实 mihomo 端到端测试验证。 |
| 线路验证 | 激活前逐级预检；入口闸门在启动时**关闭**，经探测端口（listener 级 `proxy: CCNest-Static`，绕过闸门）确认出口 IP 后才打开 | 未验证出口前没有任何业务流量离开。 |
| 故障检测 | 监控循环：内核进程存活、经完整链路的 URL 测速（`/proxies/CCNest-Static/delay`）、定期出口 IP 核对、外部修改检测（系统代理被改） | 二级中断 → “线路中断”状态并通知；出口变化 → 关闭闸门 + 断开现有连接；睡眠前关闭闸门，唤醒 / 网络变化后先复核再放行。 |
| 强制执行（可选，需管理员） | WFP 过滤规则（独立 provider + 最高权重 sublayer）；应用级：Claude 程序只允许回环且禁止直连一级端口；全局：只放行 TUN 接口、CCNest 内核、一级客户端内核、回环、DHCP、可选局域网 | 不遵循代理的流量、TUN 失效后的流量会被系统拒绝。WFP 只依赖 BFE 服务，**与 Defender 防火墙是否开启无关**（检测到防火墙关闭时会提示这一点）。 |
| 崩溃恢复 | write-ahead 会话日志、守护进程、提权助手看门狗、登录自启恢复项 | 见第 5 节。 |

为什么不能只依赖 mihomo TUN：内核崩溃时 wintun 适配器随进程消失，路由回落到物理网卡，流量会直连。因此全局模式必须配合 WFP 阻断；安全优先策略下崩溃后切换为“锁定”规则集。

## 4. 权限模型

- **主程序永远以普通用户运行**（manifest `asInvoker`）。
- 需要管理员的操作（WFP、TUN）由**同一可执行文件的 `--helper` 模式**完成：通过 UAC（`runas`）按需启动，生命周期仅限会话。
- 提权助手：
  - 命名管道 ACL 只允许发起者 SID（含 UAC 跨账户提权情况）、SYSTEM 与自身；连接后用 `GetNamedPipeClientProcessId` **校验对端必须是父进程**（测试验证外部进程被拒绝）。
  - 只接受固定的结构化命令（应用 / 移除 WFP 规则、启动 / 停止 TUN 内核、设置崩溃策略、退出），不执行任意命令。
  - 运行内核前校验 mihomo 的 SHA-256 与内置固定值一致，复制到 `%ProgramData%\CCNest\core`（仅管理员可写，所有者不可信时重建目录）后再运行；配置经校验（控制接口必须绑定 127.0.0.1、密钥长度、禁止 external-ui 等）。
  - 同时作为提权资源的看门狗：父进程意外消失时执行崩溃策略。
- 安装到 Program Files，保证被提权运行的文件不可被普通用户替换；不使用单文件发布（避免原生 DLL 解压到用户可写的临时目录后被提权进程加载）。

## 5. 会话生命周期与崩溃处理

**激活**：校验配置 → 检测环境（不改动任何东西）→ 预检双重线路（失败则零修改退出）→ 写入会话日志 → 每项修改先记录原始值（Pending）再执行（Applied）：登录自启恢复项 → 内核（闸门关闭）→ 经探测端口验证出口 → 开闸并再次验证 → 锁定一级节点（可选）→ 系统代理 / Claude Code settings.json / 环境变量 / 系统环境（可选）→ WFP（可选）并复验 → 启动守护进程。任一步失败自动按相反顺序回滚。

**正常结束**：停止监控 → 按相反顺序恢复每一项（恢复前检查当前值是否仍是 CCNest 写入的值；若被他人修改则标记“冲突”而**不覆盖**，可在恢复中心强制恢复）→ 关闭提权助手 → 归档会话日志 → 移除自启项。Windows 注销 / 关机（`SessionEnding`）时执行同样的快速恢复。

**异常情况**：

| 情况 | 行为 |
| --- | --- |
| CCNest 崩溃 / 被强制结束 | 内核随作业对象终止 → 指向 CCNest 的应用断网（不泄漏）。守护进程按策略处理：安全优先 → 保持并重新打开 CCNest 提示；可用性优先 → 立即恢复用户级设置。提权助手：安全优先 → 停 TUN、换成锁定规则（非动态会话，进程退出后保留）；可用性优先 → 动态会话规则随之消失。 |
| mihomo 崩溃 | 立即报告“内核已退出”，流量中断；自动重启内核（闸门关闭）并复核出口后放行。 |
| 二级代理断开 / 认证失败 | 连接失败（不回退）；连续失败达阈值显示“二级线路中断”并通知。 |
| 一级线路断开 | 同上（二级经一级拨号，一级断开即二级不可达）；检测页面会定位到一级节点测速失败。 |
| 出口 IP 变化 | 关闭闸门、断开现有连接，等待用户“重新验证”或“接受新 IP”。 |
| 睡眠 / 唤醒、网络变化 | 睡眠前关闭闸门；唤醒或地址变化（去抖动，激活后 20 秒内忽略自身 TUN 引起的变化）后复核出口再放行。 |
| Windows 重启（会话未结束） | 非持久 WFP 规则被系统清除；用户级设置仍指向 CCNest（相关应用断网而非直连）；登录自启项让 CCNest 启动并提示恢复。 |
| 拒绝管理员授权 | 需要管理员的模式激活被取消并回滚；恢复残留 WFP 规则时被拒绝则提示（重启亦可清除）。 |

## 6. Claude 各应用的网络路径（调研结论）

| 应用 | 网络行为 | CCNest 接入 |
| --- | --- | --- |
| Claude 网页版 | 浏览器 | 专属浏览器：独立 `--user-data-dir`，固定 `--proxy-server=http://127.0.0.1:端口`，`--force-webrtc-ip-handling-policy=disable_non_proxied_udp`，语言偏好写入资料的 Preferences。 |
| Claude Desktop | Electron/Chromium 网络栈，跟随 Windows 系统代理，支持 `--proxy-server` | 系统代理（WinINet per-connection API + 刷新通知）；通过 CCNest 启动时附加 `--proxy-server`（Squirrel 版直接启动，MSIX 版经 `IApplicationActivationManager`）。 |
| Claude Code | 读取 `HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY`（仅 HTTP 代理，不支持 SOCKS），也读取 `~/.claude/settings.json` 的 `env`（同样作用于 Desktop 的 Code 标签页） | 只合并 env 中的三个键（备份原文件；含注释 / 尾随逗号的文件拒绝改写），结束时按值恢复；另提供预设环境变量的专属终端。 |
| MCP 服务器 / 子进程 | 继承环境变量，但是否遵循代理取决于各自实现 | 标准模式不保证；全局 TUN 模式覆盖。 |
| 云端执行环境 | 运行在 Anthropic 服务器 | 无法控制（如实说明）。 |

## 7. Windows 环境设置的生效方式（调研结论）

| 设置 | 实现 | 生效 | 恢复 |
| --- | --- | --- | --- |
| 区域格式 | `Set-Culture`（恢复时再用 `SetLocaleInfo` 还原自定义格式） | 新启动的程序 | 同 |
| 主位置 | `SetUserGeoID` | 立即 | 同 |
| 首选语言顺序 | `Set-WinUserLanguageList`（保留输入法） | 新启动的程序 | 同 |
| 显示语言 | `Set-WinUILanguageOverride`（需已安装语言包） | **需注销** | **需注销** |
| 时区 | `tzutil /s`（普通用户具备 SeTimeZonePrivilege） | 立即 | 同 |

所有动态值通过环境变量传给内嵌的固定 PowerShell 脚本（`-EncodedCommand`），从不拼接进脚本文本。不修改系统区域设置、字体、驱动，也不下载语言包。

## 8. 安全与隐私

- 凭据：DPAPI（CurrentUser + 应用熵）加密存放于 `secrets\`；设置文件中不含密码；mihomo 配置经**标准输入**传入（`-f -`），从不落盘、不进命令行或环境变量。
- 日志与导出报告统一经过脱敏器（注册的密钥值 + URL 凭据 / password= / Authorization 头等模式）。
- mihomo 控制接口仅绑定 127.0.0.1，256 位随机密钥，CORS 只允许 `http://127.0.0.1`。
- 浏览器检测页由仅监听回环的最小 HTTP 服务提供：128 位路径令牌、校验 Host 头（防 DNS 重绑定）、只有“提供页面”和“接收结果”两个操作。
- 单实例转发只接受白名单参数（`--open-browser` / `--recover` / `--show` / `--activate`），管道仅限当前用户。
- 不从远端获取或执行任何命令；修复操作是代码中固定的有限集合，均先展示具体更改并记录原始值。
- 不提供指纹伪装；不实现针对受限地区的“风险评分”（参考项目 check-cc 中的此类功能被有意排除）。

## 9. 数据与升级

用户数据全部位于 `%LOCALAPPDATA%\CCNest`，与安装目录分离：升级 / 重装 / 卸载不会影响设置和专属浏览器资料。Chrome 与 Edge 的专属资料分别保存（两者格式不兼容）。“重置资料”是把目录移到 `archived-日期`，而不是删除。

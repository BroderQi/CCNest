# CCNest 测试与验证

## 1. 自动化测试

```powershell
dotnet test tests\CCNest.Tests\CCNest.Tests.csproj
```

共 93 个测试（xUnit，约 35 秒）。测试通过模块初始化器隔离所有副作用：数据目录指向临时文件夹，系统代理使用内存假后端，
Claude Code settings.json、用户环境变量与登录自启项都重定向到测试专用位置——**测试不会修改真实系统设置**。

| 测试类 | 数量 | 覆盖内容 |
| --- | --- | --- |
| `MihomoChainIntegrationTests` | 8 | **真实 mihomo 内核** + 进程内假一级 / 二级代理：链路顺序（一级→二级）、二级宕机时不回退、二级密码错误时不回退、闸门关闭时业务流量被拒而探测端口仍可验证、一级探测端口、Claude 域名分流、经完整链路测速、内核崩溃后端口拒绝连接 |
| `SessionLifecycleTests` | 4 | **真实 SessionManager + 真实 mihomo**：激活 → 验证出口 → 服务商更换 IP 时自动阻断 → 接受新 IP → 二级宕机进入“线路中断” → 正常结束并逐项恢复；期望 IP 不符时零修改中止；密码错误时给出准确原因；模拟崩溃后保持 fail-closed 状态，再由恢复流程全部还原 |
| `HelperAndWatchdogTests` | 4 | 提权助手管道协议：只服务父进程（外部进程被拒绝）、拒绝未知命令、拒绝不安全的 TUN 配置、非管理员时 WFP 操作返回错误；守护进程三种情况（可用性优先自动恢复、安全优先保持阻断、正常退出不处理） |
| `RestoreAndJournalTests` | 9 | 系统代理恢复与冲突检测、Claude settings.json 合并 / 恢复（保留无关键、检测用户修改、拒绝改写含注释文件）、会话日志多态序列化、孤儿会话检测（含 PID 复用保护）、DPAPI 加密与脱敏、用户环境变量（沙盒注册表键） |
| `MihomoConfigBuilderTests` | 11 | 闸门无 DIRECT 且启动关闭、二级经一级拨号、特殊字符密码原样传递、全局 MATCH、控制接口仅回环、Claude 分流规则、TUN 模式进程豁免与 fake-ip、导入节点（引号标量保持字符串）、非法节点类型、端口冲突、提权助手配置校验 |
| `KillSwitchPlanTests` | 4 | WFP 规则规划：全局模式四个层的兜底阻断、权重顺序（放行内核 > 阻断一级端口 > 放行回环）、锁定模式无例外、应用级规则只匹配指定程序 |
| `ProxyTunnelTests` / `ProxyEndpointTests` / `EndpointHeuristicsTests` | 11 | HTTP CONNECT / SOCKS5 多跳隧道与逐级错误归因（连接失败 / 需要认证 / 认证失败 / 协议错误）、服务商代理串格式解析、粘性会话 / 地区池用户名推测 |
| `MiscTests` | 8 | Cloudflare trace 解析、IP 比较、环回端口识别、WebRTC 候选分类、浏览器检测服务（页面令牌、Host 头校验、结果回传）、专属浏览器启动参数 |
| `NativeLayoutTests` | 1 | WFP / WinINet 结构体布局与 SDK 头文件一致（期望值来自用 MSVC 编译的 sizeof/offsetof 探针） |
| `LiveSystemSmokeTests` | 8 | **只读**调用真实 Windows API：WinINet 读取、TCP 监听表、网卡、防火墙 / BFE 状态、区域设置快照、Clash 检测、Claude 检测；非管理员无法添加 WFP 对象 |

## 2. 本次开发环境中实际执行过的验证

- 上述 93 个自动化测试全部通过（.NET 9.0.317）。
- 在真实机器上只读检测成功：Clash Verge Rev（服务模式，`verge-mihomo.exe`）、混合端口 7897、**命名管道控制接口** `\\.\pipe\verge-mihomo`（mihomo v1.19.20）、策略组与节点测速、Claude Code 2.1.309（npm 安装）、Edge 浏览器、Defender 防火墙三个配置文件均关闭而 BFE 运行中。
- 用真实二级代理配置做了逐级线路测试（只读）：准确识别出“当前一级节点无法访问互联网”和“本机无法直连该代理服务器”两类真实故障。
- WPF 界面：启动、欢迎 / 须知对话框、页面导航、真实环境检测、系统环境读取、关闭退出、单实例参数转发均通过 UI 自动化与截图验证。
- 发布产物：自包含发布可运行；`--helper` 在非管理员下拒绝运行（退出码 5）；Inno Setup 安装包与便携 zip 成功生成。

## 3. 未能在开发环境中执行的部分

开发会话没有管理员权限（UAC 需要人工确认），且当前机器没有可用的一级线路，因此以下功能**已实现但未实际运行**：

- 提权助手经 UAC 启动、WFP 规则的真实安装 / 移除（结构体布局已用 SDK 编译探针核对）。
- 全局 TUN 模式（CCNest 虚拟网卡、DNS 劫持、IPv6 接管）。
- 真实二级静态代理下的完整激活（端到端流程已用本地假出口验证）。
- 安装包的安装 / 升级 / 卸载流程。
- 睡眠唤醒、Windows 注销 / 重启、显示语言切换（需注销）等需要人工操作的场景。
- 真实修改 Windows 区域 / 语言 / 时区（读取路径已验证，写入路径未在真实系统执行，以免改动用户环境）。

请按下一节步骤在 Windows 上验证。

## 4. Windows 手动验证清单

准备：一台 Windows 10/11 x64（建议虚拟机快照），可用的 Clash / Mihomo 客户端，一个静态代理（HTTP 或 SOCKS5），管理员账户。
记录基线：`netsh winhttp show proxy`、`reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings"`、`Get-Culture`、`Get-WinUserLanguageList`、`tzutil /g`。

### 4.1 安装与基本流程
1. 运行安装包（应弹出 UAC），完成后以普通用户身份启动 CCNest。确认 `C:\Program Files\CCNest\core\mihomo.exe` 存在。
2. 网络线路：确认自动检测到客户端与端口；粘贴代理串并“保存并逐级测试”，六个步骤均通过；把出口设为期望 IP。
3. 概览：激活（标准模式），检查：
   - `reg query "HKCU\...\Internet Settings" /v ProxyServer` → `127.0.0.1:17897`
   - `%USERPROFILE%\.claude\settings.json` 的 `env` 中出现 `HTTPS_PROXY`，其他键不变
   - `curl -x http://127.0.0.1:17897 https://api.ipify.org` → 期望出口 IP
4. 打开专属浏览器：访问 `https://www.cloudflare.com/cdn-cgi/trace`，`ip=` 为期望出口；关闭后再打开，登录状态仍在。
5. 环境检测 → “检测专属浏览器环境”：WebRTC 一项应为“没有暴露任何公网 IP”。
6. 结束并恢复：第 3 步的值全部恢复为基线；`HKCU\...\Run` 中没有 `CCNest.Recovery`；`%LOCALAPPDATA%\CCNest\state\session.json` 不存在。

### 4.2 故障与不回退
| 场景 | 操作 | 期望 |
| --- | --- | --- |
| 二级认证失败 | 填错密码后测试 / 激活 | 测试指出“第 2 级代理认证失败”；激活零修改中止 |
| 二级断开 | 激活后在服务商后台禁用 IP 或拔掉二级 | 20–40 秒内显示“二级线路中断”；`curl -x http://127.0.0.1:17897 https://api.ipify.org` 失败，**不会**返回一级或本机 IP |
| 一级断开 | 激活后在 Clash 中切到不可用节点 | 同上，环境检测指出一级节点测速失败 |
| 出口变化 | 把期望 IP 改成别的值后点“重新验证” | 状态“已阻断”，闸门关闭，请求失败；“接受新 IP”后恢复 |
| mihomo 被终止 | `taskkill /im mihomo.exe /f` | 状态“内核已退出”，随后自动重启并复核 |
| CCNest 被强制结束（安全优先） | `taskkill /im CCNest.exe /f` | 守护进程重新打开 CCNest 并弹出“未正常结束的会话”；之前系统代理仍指向 17897（浏览器断网而非直连）；选择“恢复原始设置”后全部还原 |
| CCNest 被强制结束（可用性优先） | 设置中切换策略后重复上一步 | 系统代理等立即恢复为基线；重新打开的 CCNest 提示已自动恢复 |
| 睡眠唤醒 | 激活后睡眠再唤醒 | 日志出现“系统从睡眠中唤醒，将复核出口”，复核通过后恢复“受保护” |
| 网络切换 | 切换 Wi-Fi / 拔插网线 | 同上（去抖动约 4 秒） |
| 拒绝管理员授权 | 选择应用强制 / 全局 TUN 模式激活，在 UAC 点“否” | 提示需要管理员授权，所有更改已回滚 |
| 已运行的应用 | 激活前先打开 Claude Code / Claude Desktop | 激活结果中提示需要重启它们 |

### 4.3 强制保护（需要管理员）
1. **应用强制**：激活后执行 `netsh wfp show filters file=wfp.xml`，在 `wfp.xml` 中搜索 `CCNest:`，应看到针对 claude.exe 的“允许本机回环 / 阻止直接联网 / 禁止直连一级端口”规则。
   在 Claude Code 中设置 `HTTPS_PROXY=` 为空并直连 `api.anthropic.com` → 连接被拒绝（WSAEACCES）；结束会话后规则消失。
2. **全局 TUN**：确认现有 Clash 的 TUN 被临时关闭；`Get-NetAdapter` 出现 `CCNest`；任何程序访问 `https://api.ipify.org` 均返回期望出口；
   `curl -x socks5h://127.0.0.1:7897 https://api.ipify.org`（直接使用一级端口）应被拒绝。结束会话后 Clash 的 TUN 恢复、`CCNest` 网卡消失、WFP 规则消失。
3. **全局 TUN + 安全优先崩溃**：`taskkill /im CCNest.exe /f` → 除局域网外无法上网（锁定规则）；重新打开 CCNest 恢复，或重启电脑后规则消失。
4. **Defender 防火墙关闭**：在防火墙全部关闭的情况下重复 1–2，结果应一致（检测页会提示 WFP 不依赖防火墙）。
5. **残留规则清理**：在规则生效时卸载 CCNest，卸载完成后 `netsh wfp show filters` 中不应再有 `CCNest:` 规则。

### 4.4 IPv4 / IPv6 / DNS
- 有公网 IPv6 的网络中，标准模式下检测页提示 IPv6 直连风险；全局 TUN 模式下 `curl -6 https://api6.ipify.org` 不应返回本机 IPv6（失败或经代理）。
- 全局 TUN 模式下 `nslookup claude.ai` 返回 `198.19.x.x`（CCNest fake-ip），说明 DNS 被 CCNest 接管。
- 环境检测中的“DNS 泄漏检测”：解析服务器应位于二级代理一侧，而不是本地运营商。

### 4.5 Windows 环境设置
1. 系统环境页勾选区域格式、首选语言、时区，“预览并立即应用”：预览中逐项标明生效方式。
2. 新开的 PowerShell 中 `Get-Culture`、`Get-WinUserLanguageList`、`tzutil /g` 显示新值；恢复后回到基线（含自定义的短日期格式）。
3. 显示语言（已安装英语语言包时）：应用后提示需要注销；注销后生效；恢复同样需要注销。
4. 开启“自动设置时区”时，预览中出现可能被系统改回的提示。

### 4.6 升级与数据保留
安装新版本（或重装）后：设置、期望 IP、已保存的密码仍在；专属浏览器仍保持登录。卸载后 `%LOCALAPPDATA%\CCNest` 仍存在（卸载结束时会提示）。

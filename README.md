# CCNest · Claude 环境管家

CCNest 是一个面向 Windows 10/11 的本地桌面工具，用来为 Claude（网页版、Claude Desktop、Claude Code）建立一个**稳定、可验证、可恢复**的专属网络与浏览环境：

> 打开 CCNest → 激活 Claude 专属环境 → 正常使用 Claude → 结束时恢复激活前的 Windows 环境。

- **双重线路**：复用你正在运行的 Clash / Mihomo 作为一级线路，在其后串联你自己购买的静态住宅 / ISP 代理（HTTP 或 SOCKS5）。
- **出口验证**：逐级测试线路，比对期望出口 IP，并从 Claude 自己的 Cloudflare 边缘节点核对实际出口。
- **安全阻断**：二级代理失败时流量中断，**绝不悄悄回退**到一级线路或直连；出口 IP 变化时自动关闭入口闸门。
- **可选强制保护**：基于 Windows 筛选平台（WFP）的应用级 / 全局防泄漏规则，在 Windows Defender 防火墙关闭时同样有效。
- **专属浏览器**：永久保存、与日常浏览器完全隔离的 Chromium 资料，只能经 CCNest 线路联网。
- **环境检测与修复**：约 20 项检测（出口、线路、DNS、IPv6、路由、系统代理、环境变量、Claude 应用、区域语言等），可修复的问题在确认后一键修复，并可撤销。
- **Windows 英文环境**：按需切换区域格式、首选语言、时区、主位置与显示语言，先保存原始值，随时恢复。
- **会话生命周期**：所有修改先写入日志（write-ahead）再执行；正常关闭时恢复，异常退出后按你选择的崩溃策略处理。

> **使用须知**：请仅在 Claude 服务支持的国家和地区、并遵守 Anthropic 使用条款的前提下使用。CCNest 用于管理你已获授权的网络连接、防止路由错误（例如为组织 IP 白名单提供固定出口），**不用于、也不会帮助绕过任何账号限制、地区资格要求或平台风控**。CCNest 不提供代理服务，不收集任何数据，所有配置仅保存在本机。

---

## 安装

1. 运行 `CCNest-Setup-<版本>-x64.exe`（需要管理员权限，安装到 `Program Files`）。
   也可以使用便携版 `CCNest-<版本>-win-x64-portable.zip`：解压后运行 `CCNest.exe`。
   *便携版若放在普通用户可写的目录，出于安全考虑，“全局 TUN 模式”仍会把内核复制到仅管理员可写的目录并校验哈希后才以管理员权限运行。*
2. 安装包为自包含版本，无需另行安装 .NET。
3. 升级直接运行新版安装包即可。**设置、加密凭据和专属浏览器资料保存在 `%LOCALAPPDATA%\CCNest`，安装、升级和卸载都不会删除它们。**

## 快速开始

1. **网络线路** 页面：
   - 一级线路选择“现有 Clash / Mihomo”，CCNest 会自动检测客户端、本地端口和控制接口（支持 Clash Verge Rev 的命名管道控制接口）。
   - 二级代理：选择服务商（可选），把服务商给你的代理串（如 `IP:端口:用户名:密码`）粘贴到“快速填写”，点击识别。
   - 点击 **保存并逐级测试**，确认每一级都通过，然后把检测到的出口设为**期望出口 IP**。
2. **概览** 页面点击 **激活 Claude 环境**，确认更改预览。
3. 使用 **打开专属浏览器** 访问 claude.ai，或 **Claude Code 终端** / **启动 Claude Desktop**。
4. 结束时点击 **结束并恢复原始设置**，或直接关闭窗口并选择“结束并退出”。

## 三种保护模式

| 模式 | 需要管理员 | 覆盖范围 | 保证 |
| --- | --- | --- | --- |
| 标准模式 | 否 | 专属浏览器、Claude Code（settings.json / 专属终端）、遵循系统代理的应用 | 这些应用只认 CCNest 端口：线路或内核故障时断网，不会改走其他出口。不遵循代理的程序不受影响。 |
| 应用强制 | 是（UAC） | 标准模式 + WFP 规则 | Claude Desktop 与 Claude Code 程序本身只能连接本机回环（即 CCNest），任何绕过代理的直连都会被系统拒绝；它们也不能直接使用一级 Clash 端口。 |
| 全局 TUN | 是（UAC） | 整台电脑（含 IPv6、DNS） | CCNest 虚拟网卡接管系统流量；WFP 阻断一切绕过虚拟网卡的连接（只放行 CCNest 内核、一级客户端内核、本机回环、DHCP、可选局域网），并禁止其他程序直接使用一级 Clash 端口。 |

## 崩溃策略：安全优先 vs 可用性优先

两者在“CCNest 意外退出”时互相冲突，CCNest 不会同时承诺两者，而是让你明确选择：

- **安全优先（默认）**：不自动恢复。CCNest 内核随主程序一起终止（作业对象），指向 CCNest 的应用断网而不是改走其他出口；全局模式下 WFP 规则切换为“锁定”（只放行回环 / DHCP / 局域网）。守护进程会重新打开 CCNest 并提示你“恢复原始设置”或“重新激活”；会话期间还注册了登录自启，重启后同样会提示。重启电脑会清除 WFP 规则，但系统代理等设置仍指向已停止的 CCNest（相关应用仍无法联网），直到你在 CCNest 中处理。
- **可用性优先**：守护进程立即恢复原始设置，提权助手撤销 WFP 规则（动态会话），电脑恢复正常上网，但此后 Claude 流量不再经过二级出口。

## 已知限制与安全边界

- CCNest 无法控制运行在 Anthropic 服务器上的功能（网页版云端代码执行、Claude Code 云端会话等），它们的网络出口与本机无关。
- 标准模式下，不遵循代理设置的程序（以及 Claude Code 启动的部分子进程，如某些 MCP 服务器）不经过二级出口；需要强制覆盖请使用全局 TUN 模式。
- 环境变量只在进程启动时读取：激活前已运行的 Claude Code 会话需要重启；Claude Desktop 已建立的连接不会自动切换，建议激活后通过 CCNest 重新启动它。
- Windows 系统代理只修改默认（局域网）连接；VPN / 拨号连接有各自的代理设置。
- 凭据使用 Windows DPAPI（当前用户）加密：其他 Windows 账户和离线拷贝无法解密，但与所有用户级密钥存储一样，**以同一用户身份运行的恶意程序可以请求解密**。
- IP 类型（住宅 / 数据中心）与地理位置来自第三方数据库，仅供参考；CCNest 不提供任何“账号风险评分”。
- 显示语言切换需要已安装对应语言包，且应用和恢复都需要注销后才生效；CCNest 不修改系统区域设置、字体或驱动。
- “自动设置时区”开启时，Windows 可能会按位置把时区改回。
- 用户名中带有会话 / 地区参数的住宅代理通常不是固定 IP（CCNest 会给出推测性提示），会话到期换 IP 时 CCNest 会按设计阻断流量。

更多细节见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)（技术决策）与 [docs/TESTING.md](docs/TESTING.md)（测试与 Windows 手动验证步骤）。

## 从源码构建

需要 Windows 10/11 x64 与 .NET 9 SDK（`global.json` 允许 9.0.300 及以上的 9.0.x 功能带）。

```powershell
# 1. 获取并校验内置内核（mihomo v1.19.33，校验 GitHub 发布摘要与二进制 SHA-256）
powershell -ExecutionPolicy Bypass -File scripts\fetch-mihomo.ps1

# 2. 构建与测试
dotnet build CCNest.sln
dotnet test tests\CCNest.Tests\CCNest.Tests.csproj

# 3. 运行（调试版需要 .NET 9 桌面运行时）
dotnet run --project src\CCNest.App

# 4. 发布：自包含构建 + 便携 zip + Inno Setup 安装包
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

`build.ps1` 会自动寻找 Inno Setup 6 的 `ISCC.exe`（`.tools\innosetup-pkg`、或标准安装目录）；找不到时只生成便携版。产物位于 `artifacts\`。

### 目录结构

```
src/CCNest.Core     核心逻辑（无 UI）：代理链、mihomo 集成、WFP、系统设置、会话生命周期、诊断与修复
src/CCNest.App      WPF 桌面应用（WPF-UI Fluent 界面、托盘、单实例、提权助手 / 守护进程入口）
tests/CCNest.Tests  xUnit 测试（含真实 mihomo 端到端测试）
installer/          Inno Setup 脚本与简体中文语言文件
scripts/            构建、内核下载、开发用截图 / UI 自动化脚本
third_party/mihomo  内置 mihomo 内核（GPL-3.0，见 THIRD-PARTY-NOTICES.md）
```

### 运行时数据位置

| 路径 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\CCNest\config\settings.json` | 设置（不含密码） |
| `%LOCALAPPDATA%\CCNest\secrets\*.dpapi` | DPAPI 加密的密码、导入节点、控制接口密钥 |
| `%LOCALAPPDATA%\CCNest\state\` | 会话日志（write-ahead journal）、独立修改记录、历史会话 |
| `%LOCALAPPDATA%\CCNest\browser\<引擎>\UserData` | 永久专属浏览器资料（从不自动删除） |
| `%LOCALAPPDATA%\CCNest\logs\` | 运行日志（已脱敏，保留 14 天） |
| `%ProgramData%\CCNest\core\` | 提权助手使用的内核副本（仅管理员可写） |

## 许可

CCNest 以 [GNU General Public License v3.0](LICENSE) 发布。第三方组件许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

内置的 mihomo 内核二进制不在仓库中，由 `scriptsetch-mihomo.ps1` 从官方发布页下载并校验。

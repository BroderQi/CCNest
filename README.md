<div align="center">

<img src="src/CCNest.App/Assets/ccnest-256.png" width="104" alt="CCNest 图标" />

# CCNest · Claude 环境管家

**专心用 Claude，少折腾网络与环境。**

为 Claude 网页版、Claude Desktop 和 Claude Code，准备一个可核验出口、可恢复设置的 Windows 专属环境。

[![Windows 10 / 11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4?style=flat-square)](#安装) [![GitHub Release](https://img.shields.io/github/v/release/BroderQi/CCNest?style=flat-square&label=Release)](https://github.com/BroderQi/CCNest/releases/latest) [![License: GPL v3](https://img.shields.io/badge/License-GPL%20v3-3D8B63?style=flat-square)](LICENSE)

[下载使用](https://github.com/BroderQi/CCNest/releases/latest) · [快速开始](#快速开始) · [技术架构](docs/ARCHITECTURE.md) · [反馈问题](https://github.com/BroderQi/CCNest/issues)

</div>

## 为什么用 CCNest

用 Claude 时，代理线路、出口 IP、浏览器资料和系统设置往往分散在几个地方。CCNest 把这些配置放进一个桌面窗口：复用现有线路，激活前检查出口，使用中监测连接，结束时恢复原设置。

> 配好线路 → 验证出口 → 激活 Claude 环境 → 开始使用 → 结束并恢复。

| 你关心的事 | CCNest 怎么做 |
| --- | --- |
| **继续用现有 Clash / Mihomo** | 自动检测客户端、端口和控制接口，通过独立 mihomo 内核串联二级代理，无需改写现有客户端的配置文件。 |
| **确认 Claude 实际从哪里联网** | 逐级测试线路，核对期望出口 IP，并通过 Claude 的 Cloudflare 边缘节点检查实际出口。 |
| **给 Claude 留一个独立的浏览器空间** | 使用独立 Chromium 资料目录，与日常浏览器资料分开；资料长期保留，重新打开时可以继续使用已有登录状态。 |
| **排查环境问题有个入口** | 集中查看约 20 项检测，包括线路、出口、DNS、IPv6、路由、系统代理、环境变量和 Claude 应用；支持的修复操作先预览、再确认，也能撤销。 |
| **用完回到原来的设置** | 修改前记录原始值，正常结束时逐项恢复；发现设置被另行修改时提示冲突，避免直接覆盖。 |
| **按需要选择保护范围** | 提供标准、应用强制、全局 TUN 三种模式，从遵循代理的应用到整机流量，按使用场景选择。 |

被配置为经二级代理转发的连接遵循 **fail-closed（故障时阻断）**：二级代理失败时连接中断，不自动回退到一级线路或直连。启用出口变化阻断后，定期核验发现 IP 变化时会关闭入口闸门。各模式的覆盖范围见[三种保护模式](#三种保护模式)。

## 它怎样连接 Claude

常见的双重线路配置如下：

```text
Claude 网页版 / Claude Desktop / Claude Code
                    │
             CCNest 本地入口
                    │
          现有 Clash / Mihomo 线路
                    │
       自备静态住宅 / ISP 代理（HTTP / SOCKS5）
                    │
                  Claude
```

现有客户端继续负责一级线路的节点与订阅；CCNest 负责串联二级代理、验证出口和管理环境。一级线路也可选择手动代理、导入节点或不使用，具体设计见[技术架构](docs/ARCHITECTURE.md)。

CCNest 适合已有可用网络线路，希望集中管理 Claude 本机环境的 Windows 用户。**二级代理需要自行准备，CCNest 不提供代理服务。**

## 安装

运行环境：**Windows 10 / 11 x64**。

1. 打开 [Releases 下载页](https://github.com/BroderQi/CCNest/releases/latest)，从 **Assets** 下载打包好的程序。
2. 当前 `0.0.1` 版本提供 `CCNest_V0.0.1.zip`，解压后找到并运行 `CCNest.exe`。
3. 首次使用前，准备好自备二级代理；使用双重线路时，再确认一级线路可用。然后按下面的步骤配置。

<details>
<summary>安装包、便携版与升级说明</summary>

从源码运行发布脚本时，可生成以下两种包：

- **安装版**：`CCNest-Setup-<版本>-x64.exe`，需要管理员权限，安装到 `Program Files`；升级时直接运行新版安装包。
- **便携版**：`CCNest-<版本>-win-x64-portable.zip`，解压后运行 `CCNest.exe`。

发布脚本采用自包含构建，无需另行安装 .NET。便携版若放在普通用户可写的目录，全局 TUN 模式会先把内核复制到仅管理员可写的目录并校验哈希，再以管理员权限运行。

设置、加密凭据和专属浏览器资料保存在 `%LOCALAPPDATA%\CCNest`，与安装目录分开；安装、升级和卸载不会删除这些资料。

</details>

## 快速开始

1. **配好线路**：打开 **网络线路** 页面。
   - 一级线路选择“现有 Clash / Mihomo”，CCNest 会自动检测客户端、本地端口和控制接口（支持 Clash Verge Rev 的命名管道控制接口）。
   - 二级代理：选择服务商（可选），把服务商给你的代理串（如 `IP:端口:用户名:密码`）粘贴到“快速填写”，点击识别。
   - 点击 **保存并逐级测试**，确认每一级都通过，然后把检测到的出口设为**期望出口 IP**。
2. **激活环境**：在 **概览** 页面点击 **激活 Claude 环境**，查看并确认更改预览。
3. **开始使用**：点击 **打开专属浏览器** 访问 claude.ai，或选择 **Claude Code 终端** / **启动 Claude Desktop**。专属浏览器使用本机已安装的 Chromium 浏览器及独立资料目录，并固定通过 CCNest 线路联网。
4. **用完恢复**：点击 **结束并恢复原始设置**，或关闭窗口并选择“结束并退出”。

如需 Windows 英文环境，可按需切换区域格式、首选语言、时区、主位置和显示语言。每项修改都会先保存原始值；显示语言需要已有对应语言包，且应用与恢复都需注销后生效。

## 三种保护模式

| 模式 | 需要管理员 | 覆盖范围 | 工作方式 |
| --- | --- | --- | --- |
| 标准模式 | 否 | 专属浏览器、Claude Code（settings.json / 专属终端）、遵循系统代理的应用 | 这些应用只认 CCNest 端口：线路或内核故障时断网，不会改走其他出口。不遵循代理的程序不受影响。 |
| 应用强制 | 是（UAC） | 标准模式 + WFP 规则 | Claude Desktop 与 Claude Code 程序本身只能连接本机回环（即 CCNest），任何绕过代理的直连都会被系统拒绝；它们也不能直接使用一级 Clash 端口。 |
| 全局 TUN | 是（UAC） | 整台电脑（含 IPv6、DNS） | CCNest 虚拟网卡接管系统流量；WFP 阻断一切绕过虚拟网卡的连接（只放行 CCNest 内核、一级客户端内核、本机回环、DHCP、可选局域网），并禁止其他程序直接使用一级 Clash 端口。 |

应用强制和全局 TUN 使用 Windows 筛选平台（WFP），不依赖 Windows Defender 防火墙的开关。**这些模式的真实 WFP 安装、TUN 和 UAC 流程仍待实机验证**；完整测试记录与手动验证步骤见 [docs/TESTING.md](docs/TESTING.md)。

## 崩溃策略：安全优先 vs 可用性优先

所有环境修改都会先写入会话日志（write-ahead），再执行。正常结束时恢复原始值；意外退出时，按你选择的策略处理：

- **安全优先（默认）**：不自动恢复。CCNest 内核随主程序一起终止（作业对象），指向 CCNest 的应用断网而不是改走其他出口；全局模式下 WFP 规则切换为“锁定”（只放行回环 / DHCP / 局域网）。守护进程会重新打开 CCNest 并提示你“恢复原始设置”或“重新激活”；会话期间还注册了登录自启，重启后同样会提示。重启电脑会清除 WFP 规则，但系统代理等设置仍指向已停止的 CCNest（相关应用仍无法联网），直到你在 CCNest 中处理。
- **可用性优先**：守护进程立即恢复原始设置，提权助手撤销 WFP 规则（动态会话），电脑恢复正常上网，但此后 Claude 流量不再经过二级出口。

## 已知限制与安全边界

- **使用范围**：请仅在 Claude 服务支持的国家和地区、并遵守 Anthropic 使用条款的前提下使用。CCNest 用于管理已获授权的网络连接、防止路由错误（如组织 IP 白名单需要固定出口），不用于绕过账号限制、地区资格要求或平台风控。
- **本地数据与诊断请求**：设置、浏览器资料和加密凭据保存在本机。IP、地理位置与 DNS 等诊断会按检测项目请求第三方服务，请将这些请求纳入自己的网络与隐私要求。
- CCNest 无法控制运行在 Anthropic 服务器上的功能（网页版云端代码执行、Claude Code 云端会话等），它们的网络出口与本机无关。
- 标准模式下，不遵循代理设置的程序（以及 Claude Code 启动的部分子进程，如某些 MCP 服务器）不经过二级出口；需要强制覆盖请使用全局 TUN 模式。
- 环境变量只在进程启动时读取：激活前已运行的 Claude Code 会话需要重启；Claude Desktop 已建立的连接不会自动切换，建议激活后通过 CCNest 重新启动它。
- Windows 系统代理只修改默认（局域网）连接；VPN / 拨号连接有各自的代理设置。
- 凭据使用 Windows DPAPI（当前用户）加密：其他 Windows 账户和离线拷贝无法解密，但与所有用户级密钥存储一样，**以同一用户身份运行的恶意程序可以请求解密**。
- IP 类型（住宅 / 数据中心）与地理位置来自第三方数据库，仅供参考；CCNest 不提供任何“账号风险评分”。
- 显示语言切换需要已安装对应语言包，且应用和恢复都需要注销后才生效；CCNest 不修改系统区域设置、字体或驱动。
- “自动设置时区”开启时，Windows 可能会按位置把时区改回。
- 用户名中带有会话 / 地区参数的住宅代理通常不是固定 IP（CCNest 会给出推测性提示），会话到期换 IP 时 CCNest 会按设计阻断流量。
- 出口 IP 按配置的间隔定期检查；IP 变化到被发现之间存在时间间隔，不能视为实时检测。

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

内置的 mihomo 内核二进制不在仓库中，由 [scripts/fetch-mihomo.ps1](scripts/fetch-mihomo.ps1) 从官方发布页下载并校验。

## 参与项目

如果 CCNest 帮你省去了配置和排障的麻烦，欢迎点一个 **Star**，也方便以后回来查看更新。

- 遇到连接或恢复问题，欢迎[提交 Issue](https://github.com/BroderQi/CCNest/issues)，附上 Windows 版本、CCNest 版本、保护模式和脱敏后的日志；请勿公开代理密码、密钥或浏览器资料。
- 想改善兼容性、诊断体验或文档，欢迎提交 PR。涉及系统设置与网络保护的改动，请一并说明验证场景。
- 准备参与开发，可以先读[技术架构](docs/ARCHITECTURE.md)和[测试说明](docs/TESTING.md)。

# CCNest 第三方组件许可声明 / Third-Party Notices

CCNest 自身代码以 GNU General Public License v3.0 发布（见仓库根目录 LICENSE）。
CCNest 随附或使用了以下第三方组件。各组件版权归其各自作者所有，并按其许可证分发。

## 随应用分发的组件

### mihomo（Clash Meta 内核）v1.19.33
- 许可证：**GNU General Public License v3.0**（完整文本见安装目录 `core\LICENSE-mihomo.txt`）
- 版权：MetaCubeX 及贡献者
- 源代码：https://github.com/MetaCubeX/mihomo/tree/v1.19.33
- 说明：CCNest 以**未修改**的官方发行版二进制文件形式（`mihomo-windows-amd64-v1-v1.19.33.zip`，
  发布页 SHA-256 摘要 `fb0b960eff568459502d4ebe53e9cca203053d86ec9024fe935f19615d74d9aa`，
  解压后 `mihomo.exe` 的 SHA-256 为 `a4fbf2b8bc2b5416ce2d0d392ac59f2e0a234ea4fcf635b66bd21e62ea9641b9`）
  作为独立进程运行，并通过其公开的 RESTful API 与标准输入通信。CCNest 不链接 mihomo 代码。
  按 GPL-3.0 要求，你可以从上方链接获取对应的完整源代码。

### WPF-UI 4.3.0
- 许可证：MIT
- 版权：Copyright (c) Leszek Pomianowski and WPF UI Contributors
- https://github.com/lepoco/wpfui
- 包含 Fluent System Icons（MIT，Copyright (c) Microsoft Corporation）

### CommunityToolkit.Mvvm 8.4.2
- 许可证：MIT
- 版权：Copyright (c) .NET Foundation and Contributors
- https://github.com/CommunityToolkit/dotnet

### YamlDotNet 18.1.0
- 许可证：MIT
- 版权：Copyright (c) Antoine Aubry and contributors
- https://github.com/aaubry/YamlDotNet

### .NET 运行时（自包含发布时随附）
- 许可证：MIT
- 版权：Copyright (c) .NET Foundation and Contributors
- https://github.com/dotnet/runtime

## 构建工具（不随应用分发）

### Inno Setup 6.7.3（用于生成安装程序）
- 许可证：Inno Setup License — https://jrsoftware.org/files/is/license.txt
- 版权：Copyright (C) 1997-2026 Jordan Russell, Martijn Laan
- 安装界面简体中文语言文件来自 Inno Setup 官方仓库的非官方翻译（Files/Languages/Unofficial/ChineseSimplified.isl）。

## 设计参考（未复制代码）

### check-cc
- 许可证：MIT，Copyright (c) 2026 yacuo / CheckCC
- https://github.com/yacuo/check-cc
- CCNest 参考了其浏览器环境检测的思路（语言、时区、区域格式等信号的采集方式），检测页面为独立实现。
  CCNest 有意**没有**实现其中针对受限地区的“风险评分”及中文字体/国产设备识别，因为 CCNest 不提供、
  也不协助任何规避地区资格或平台风控的功能。

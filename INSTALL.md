# VPNTray · 安装与使用说明

Windows 10/11 系统托盘小工具，封装 `openconnect` 连接 Cisco AnyConnect 兼容的 VPN，自动生成 TOTP 一次性口令。基于 .NET 8（WPF）开发，是 macOS 版 [VPN MenuBar](https://github.com/CoderZCC/VPNMenuBar) 的 Windows 移植版。

> ⚠️ **这是一个通用 OpenConnect 前端**。网关地址、服务器证书指纹、密码前缀、TOTP 密钥都是**占位符 / 空值**，没有内置任何真实默认值。你需要在 Onboarding 向导（或 Settings 设置窗口）里填上你**自己 VPN** 的值，这些一般由你的 VPN 管理员提供。

## 系统要求

- **Windows 10 或 11，x64**
- **.NET 8 桌面运行时**（运行依赖框架的构建时需要），或自行打包 self-contained 版本
- 本机管理员权限（app 启动时会请求 UAC 提权）

原生 VPN 二进制（openconnect + Wintun + 脚本）**已经随仓库一起带了**（见下面[原生二进制文件](#原生二进制文件)），不用额外装东西。

## 原生二进制文件

VPNTray 运行时需要的原生文件**已提交在 `src/VPNTray/Resources/`**，构建时会自动拷到 `VPNTray.exe` 同级的 `Resources/` 里 —— 开箱即用：

| 文件 | 是什么 |
| ---- | ---- |
| `openconnect.exe` + 它的 `lib*.dll` / `zlib1.dll` 一套依赖 | OpenConnect **v8.10**（anyconnect/DTLS/ESP），来自 [horar/openconnect](https://github.com/horar/openconnect) 预编译 MinGW64 构建，已校验 SHA-512。 |
| `wintun.dll` | Wintun **0.14.1**（amd64），[wintun.net](https://www.wintun.net/) 官方构建。 |
| `vpnc-script-win.js` | VPN 配置脚本，与 openconnect 构建版本匹配。 |
| `vpnc-script-win--no-dns.js` | 同一脚本，但**禁用了 DNS 配置那一段**。开启"跳过 DNS 修改"（默认）时用这个，让 VPNTray 不去动你系统的 DNS 设置。 |

> 💡 所有 `lib*.dll` / `zlib1.dll` 是 `openconnect.exe` 的运行时依赖，必须和它放在同一个 `Resources/` 文件夹里，否则 openconnect 起不来。详见 [`src/VPNTray/Resources/README.md`](src/VPNTray/Resources/README.md)（含来源、版本、许可证，以及怎么换更新的 openconnect）。

## 安装（从源码构建）

跟 macOS 版不一样，**Windows 版不需要 Homebrew，openconnect 已经放在 `Resources/` 里随 app 一起带了**，构建即可用。

```powershell
git clone <仓库地址>
cd VPNTray
dotnet build -c Release
```

构建产物在 `src/VPNTray/bin/Release/net8.0-windows/`，原生二进制已自动拷到它旁边的 `Resources/` 子文件夹。

## 首次启动（UAC 提权）

双击 `VPNTray.exe`。因为整个 app 标记为 `requireAdministrator`，Windows 每次启动都会弹 **UAC 提权框 —— 点"是"放行**。

> 💡 **关于提权**：这是 Windows 版相对 macOS 版的关键区别。mac 版需要配置 `sudo NOPASSWD` 免密规则；Windows 版则是整个 app 以管理员身份运行 —— 启动时确认**一次** UAC，之后连接 / 断开 / 改路由都不再需要二次提权。

放行后托盘区会出现 VPNTray 图标，并自动弹出配置窗口（Credentials）。

## 配置（一个 Credentials 窗口）

首次启动 app 自动弹出配置窗口；之后随时可以**单击托盘图标**（左键单击），或**右键**菜单点 **Open Settings…** 再打开。整个 app 只有这一个窗口，填你**自己 VPN** 的信息：

- **Username**：VPN 登录用户名（例如 `zhangsan.li`）
- **Password prefix**：固定密码前缀（VPN 管理员给的，很多公司部署里所有人共用同一个前缀）。实际连接时密码 = 这个前缀 + 当前 6 位 TOTP 动态码，app 自动拼好通过 stdin 喂给 openconnect，你不用手输动态码。
- **TOTP secret (Base32)**：**你自己的** TOTP 密钥，**推荐直接导入 QR 码**（见下面[如何导入 TOTP 密钥](#如何导入-totp-密钥)），也可以手动粘贴 Base32 字符串（大写字母 + 数字 2–7，通常 16–32 位）。**绝对不要用别人的**。
- **Gateway**：你的 VPN 服务器地址（占位符，必须换成真实值）。
- **Server cert pin**：服务器证书 SHA-256 指纹（占位符，必须换成真实值）。

五个字段都填好点 **Save**（保存后窗口不关闭）。窗口顶部有连接状态和 **Connect** 按钮：点 Connect 显示 **Connecting…**，连上后变 **Disconnect**，点它断开——**在主界面就能连接 / 断开，不用再去托盘**。托盘右键菜单里的 Connect / Disconnect / Reconnect 依然可用。

> 💡 设置没填完时，托盘菜单只显示 **Open Settings…** 和 **Quit** 两项。

窗口底部还有一个 **Check tools** 按钮：点它检查随包附带的原生工具（openconnect / wintun / vpnc 脚本）是否齐全，绿色对勾表示正常。原生工具已随 app 打包，正常无需操作，仅在连接异常时用来排查。

#### 如何导入 TOTP 密钥

VPN 管理员一般给你一张 `otpauth://` QR 码图片。TOTP 密钥输入框下方有两个按钮，**全程本地解析，不经过任何在线服务**：

- **Import QR image…**：选择保存在本地的 QR 码图片文件，自动识别并填入密钥。
- **Scan screen for QR**：QR 码显示在屏幕上（邮件、聊天窗口、网页里）时点这个，窗口会暂时隐藏并扫描全部屏幕，识别后自动填入。

两种方式都支持完整的 `otpauth://totp/...?secret=...` QR 码，也兼容内容只是一串裸 Base32 密钥的 QR 码。

> ⚠️ **Google Authenticator / Microsoft Authenticator 看不到密钥明文** —— 它们出于安全故意禁用了导出。如果密钥只在这两个 app 里没有备份，请管理员重新签发一张 QR 码再导入。

## 日常使用

**左键单击**托盘图标打开主界面（Credentials 配置窗口）；**右键单击**弹出菜单：

| 菜单项 | 作用 |
| ------ | ---- |
| **Connect** | 生成 TOTP 并建立 VPN 隧道 |
| **Disconnect** | 断开隧道（同时清除自动重连意图）|
| **Reconnect** | 先断开再连 |
| **Open Settings…** | 修改所有字段，包括 Advanced（路径、跳过 DNS 修改、开机自启）|
| **Quit** | 退出 app（连着的话先断开）|

托盘图标上的状态小圆点：
- 🟢 **绿色**：已连接
- 🟠 **橙色**：连接中
- 🔴 **红色**：连接失败
- ⚫ **灰色**：未连接

### 修改配置
左键单击托盘图标，或右键菜单点 **Open Settings…**，打开主界面，可以改全部五个字段（用户名、密码前缀、TOTP 密钥、网关、证书指纹），底部 **Check tools** 按钮用来排查随包原生工具。

### 退出
菜单点 **Quit**。如果当前连着 VPN，会先断开再退出，不会留下僵尸 `openconnect.exe` 进程。

## 自动化行为

- **开机自启**：通过注册一个**以最高权限运行的计划任务（Scheduled Task）**实现，登录时静默、已提权地启动 app（**不弹 UAC**）。不想要的话 Settings → Advanced 里关掉 `Launch at login`。
- **断网自动断开**：连接中如果网络断了（切网、断网），会主动断开 VPN 避免卡死。
- **联网自动重连**：如果之前是你**主动**连的，网络恢复后会自动重连；之前主动 Disconnect 过、或上次是连接失败，则**不会**自动重连（避免对着错误配置死循环重试）。
- **跳过 DNS 修改**：默认开启，VPN 不会改你系统的 DNS 设置（用的是 `vpnc-script-win--no-dns.js`）。想让 VPN 接管 DNS 的话 Settings → Advanced 里关掉 `Skip DNS modification`。

## 常见问题

### Q1. 一连就 Failed？
确认 `Resources/` 里的原生二进制文件齐全（已随仓库附带；见[原生二进制文件](#原生二进制文件)）。文件缺失或损坏的话重装 / 重新构建一次即可。

### Q2. 提示认证失败？
去 Settings 重新核对 **TOTP secret** 和 **Password prefix**。6 位动态码是从密钥推算的，密钥错了码就错。

### Q3. 提示服务器证书校验失败？
多半是运维换了证书。去 Settings 更新 **Server cert pin**。

### Q4. 每次启动都要点 UAC 吗？
手动启动时要点一次。通过"开机自启"的计划任务启动时是静默已提权的，**不弹 UAC**。

### Q5. 能同时连两个 VPN 吗？
不能。VPNTray 只管理一个 `openconnect` 连接。

## 配置文件与隐私

- **位置**：`%APPDATA%\VPNTray\config.json`
- **权限**：ACL 锁定到当前用户（相当于 macOS 的 `chmod 0600`，只有你自己能读写）
- **内容**：明文 JSON，包含用户名、密码前缀、TOTP 密钥、网关、证书指纹、openconnect / vpnc-script 路径
- **不会上传到任何地方** —— 完全本地存储

> ⚠️ **安全提醒**：**TOTP 密钥是你本人账号独有的**，等同一次性密码的种子。**绝对不要把 `config.json` 发给别人，也不要提交到 git 仓库**。每个同事都应该各自从运维拿自己的 TOTP 密钥走 Onboarding，而不是拷别人的配置。

## 卸载

1. 托盘菜单点 **Quit** 退出（会先断开当前 VPN）。
2. 如果开了开机自启，关掉 Settings → Advanced 的 `Launch at login`（或在"任务计划程序"里删掉对应计划任务）。
3. 删掉运行所在的 app 目录（`bin\Release\net8.0-windows`）。
4. 删掉配置和密钥：删除 `%APPDATA%\VPNTray` 整个目录。

## 暂未支持（v1 范围外）

- 自动更新（auto-update）

## 反馈

这是 [VPN MenuBar](https://github.com/CoderZCC/VPNMenuBar) 的 Windows 移植版。欢迎提 issue 或 PR。

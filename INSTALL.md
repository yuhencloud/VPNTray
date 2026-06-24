# VPNTray · 安装与使用说明

Windows 10/11 系统托盘小工具，封装 `openconnect` 连接 Cisco AnyConnect 兼容的 VPN，自动生成 TOTP 一次性口令。基于 .NET 8（WPF）开发，是 macOS 版 [VPN MenuBar](https://github.com/CoderZCC/VPNMenuBar) 的 Windows 移植版。

> ⚠️ **这是一个通用 OpenConnect 前端**。网关地址、服务器证书指纹、密码前缀、TOTP 密钥都是**占位符 / 空值**，没有内置任何真实默认值。你需要在 Onboarding 向导（或 Settings 设置窗口）里填上你**自己 VPN** 的值，这些一般由你的 VPN 管理员提供。

## 系统要求

- **Windows 10 或 11，x64**
- **.NET 8 桌面运行时**（运行依赖框架的构建时需要），或自行打包 self-contained 版本
- 四个**原生二进制文件**（见下面[原生二进制文件](#原生二进制文件)），放进 `Resources/` 文件夹 —— **本仓库不附带这些文件**
- 本机管理员权限（app 启动时会请求 UAC 提权）

## 原生二进制文件

VPNTray 会从 `VPNTray.exe` 同级的 `Resources/` 文件夹加载四个原生文件：

| 文件 | 来源 |
| ---- | ---- |
| `openconnect.exe` | 官方 **OpenConnect Windows 构建，v9.x 或更新版本**。v9.x 自带 Wintun 驱动，不需要单独装驱动。 |
| `wintun.dll` | 跟 `openconnect.exe` 在同一个 v9.x 构建里一起提供。 |
| `vpnc-script-win.js` | 该 OpenConnect 构建自带的默认 VPN 配置脚本。 |
| `vpnc-script-win--no-dns.js` | `vpnc-script-win.js` 的副本，但**删掉了 DNS 配置那一段**。开启"跳过 DNS 修改"时用这个，让 VPNTray 不去动你系统的 DNS 设置。 |

> ⚠️ **这四个文件故意不提交到仓库里。** 四个文件没全部放到 `Resources/` 之前，app 内的依赖检查会一直红叉，也连不上 VPN。

**怎么获取：**

1. 下载官方 OpenConnect Windows 构建（v9.x 或更新版本）。
2. 把 `openconnect.exe`、`wintun.dll`、`vpnc-script-win.js` 拷进 `Resources/`。
3. 把 `vpnc-script-win.js` 复制一份命名为 `vpnc-script-win--no-dns.js`，打开它，把 DNS 配置那一段删掉（或注释掉）。

详见 [`src/VPNTray/Resources/README.md`](src/VPNTray/Resources/README.md)。缺这些文件不影响编译 —— 只影响运行时能不能真正连上。

## 安装（从源码构建）

跟 macOS 版不一样，**Windows 版不需要 Homebrew，openconnect 是放在 `Resources/` 里随 app 一起带的**（你自己放进去）。

```powershell
git clone <仓库地址>
cd VPNTray
dotnet build -c Release
```

构建产物在 `src/VPNTray/bin/Release/net8.0-windows/`。把四个原生二进制文件放进 `VPNTray.exe` 旁边的 `Resources/` 子文件夹（`.csproj` 会在构建时把 `src/VPNTray/Resources/` 里的内容拷到输出目录，所以也可以在构建前就放进去）。

## 首次启动（UAC 提权）

双击 `VPNTray.exe`。因为整个 app 标记为 `requireAdministrator`，Windows 每次启动都会弹 **UAC 提权框 —— 点"是"放行**。

> 💡 **关于提权**：这是 Windows 版相对 macOS 版的关键区别。mac 版需要配置 `sudo NOPASSWD` 免密规则；Windows 版则是整个 app 以管理员身份运行 —— 启动时确认**一次** UAC，之后连接 / 断开 / 改路由都不再需要二次提权。

放行后托盘区会出现 VPNTray 图标，并自动弹出 Onboarding 向导。

## 首次启动引导（Onboarding 三步）

### Step 1 · Welcome
欢迎页，介绍必备条件，点 **Next** 继续。

### Step 2 · Dependency Check
检查 `Resources/` 里四个原生二进制文件是否齐全。任一项红叉说明对应文件缺失 —— 按上面[原生二进制文件](#原生二进制文件)补齐后点重新检查，全绿才能继续。

### Step 3 · Credentials
填你**自己 VPN** 的信息：

- **Username**：VPN 登录用户名（例如 `zhangsan.li`）
- **Password prefix**：固定密码前缀（VPN 管理员给的，很多公司部署里所有人共用同一个前缀）。实际连接时密码 = 这个前缀 + 当前 6 位 TOTP 动态码，app 自动拼好通过 stdin 喂给 openconnect，你不用手输动态码。
- **TOTP secret (Base32)**：**你自己的** TOTP 密钥（从管理员发的 QR 码里提取，见下面[如何获取 TOTP 密钥](#如何获取-totp-密钥)）。一串大写字母 + 数字 2–7 的 Base32 字符串，通常 16–32 位，**绝对不要用别人的**。
- **Gateway**：你的 VPN 服务器地址（占位符，必须换成真实值）。
- **Server cert pin**：服务器证书 SHA-256 指纹（占位符，必须换成真实值）。

填完点保存进入 Step 4。

### Step 4 · Done
完成，回到托盘。设置完整后托盘完整菜单才会出现。

> 💡 设置没填完时，托盘菜单只显示 **Check Dependencies…** 和 **Quit** 两项。

#### 如何获取 TOTP 密钥

VPN 管理员一般给你一张 QR 码图片。**VPNTray 要的是 QR 码里的 Base32 密钥文本本身**（不是用手机扫码注册 Authenticator）。请在本地提取，**不要用在线 QR 解码网站**（等于把 VPN 凭证上传给陌生服务）。

**方法 A · zbar 命令行（推荐，本地处理）**
1. 把 QR 码图片存到本地，比如 `vpn-qr.png`
2. 装 [zbar](https://github.com/mchehab/zbar)，执行：
   ```
   zbarimg vpn-qr.png
   ```
3. 输出形如 `otpauth://totp/example:your.name?secret=ABCDEFGHIJKLMNOP2345&issuer=example`
4. **`secret=` 后面、`&` 前面那段就是你要的 TOTP 密钥**，复制粘贴进去

**方法 B · 手机相机**
用手机相机对准 QR 码，会弹出完整的 `otpauth://...` URL，截取 `secret=` 后面的部分发回电脑。

**方法 C · 密码管理器**
如果你已经把这个 TOTP 存在密码管理器里：
- **1Password**：对应条目 → "一次性密码"字段 → 查看 `otpauth://` URI，里面有 `secret=`
- **Bitwarden**："Authenticator key" 字段直接就是 Base32 密钥

> ⚠️ **Google Authenticator / Microsoft Authenticator 看不到密钥明文** —— 它们出于安全故意禁用了导出。如果密钥只在这两个 app 里没有备份，只能请管理员重新签发一张 QR 码，用方法 A 重新解码。

## 日常使用

点击托盘图标弹出菜单：

| 菜单项 | 作用 |
| ------ | ---- |
| **Connect** | 生成 TOTP 并建立 VPN 隧道 |
| **Disconnect** | 断开隧道（同时清除自动重连意图）|
| **Reconnect** | 先断开再连 |
| **Open Settings…** | 修改所有字段，包括 Advanced（路径、跳过 DNS 修改、开机自启）|
| **Check Dependencies…** | 重新检查原生二进制依赖 |
| **Quit** | 退出 app（连着的话先断开）|

托盘图标上的状态小圆点：
- 🟢 **绿色**：已连接
- 🟠 **橙色**：连接中
- 🔴 **红色**：连接失败
- ⚫ **灰色**：未连接

### 修改配置
菜单点 **Open Settings…** 打开设置窗口，可以改所有字段（用户名、密码前缀、TOTP 密钥、网关、证书指纹），Advanced 区还有 openconnect / vpnc-script 路径、**Skip DNS modification（跳过 DNS 修改）** 开关、**Launch at login（开机自启）** 开关。

### 退出
菜单点 **Quit**。如果当前连着 VPN，会先断开再退出，不会留下僵尸 `openconnect.exe` 进程。

## 自动化行为

- **开机自启**：通过注册一个**以最高权限运行的计划任务（Scheduled Task）**实现，登录时静默、已提权地启动 app（**不弹 UAC**）。不想要的话 Settings → Advanced 里关掉 `Launch at login`。
- **断网自动断开**：连接中如果网络断了（切网、断网），会主动断开 VPN 避免卡死。
- **联网自动重连**：如果之前是你**主动**连的，网络恢复后会自动重连；之前主动 Disconnect 过、或上次是连接失败，则**不会**自动重连（避免对着错误配置死循环重试）。
- **跳过 DNS 修改**：默认开启，VPN 不会改你系统的 DNS 设置（用的是 `vpnc-script-win--no-dns.js`）。想让 VPN 接管 DNS 的话 Settings → Advanced 里关掉 `Skip DNS modification`。

## 常见问题

### Q1. 一连就 Failed / 依赖检查红叉？
确认 `Resources/` 里四个原生二进制文件都齐了（见[原生二进制文件](#原生二进制文件)），然后菜单点 **Check Dependencies…** 重新检查。

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
- TOTP 密钥的 **QR 码图片导入**（目前请用 zbar 等提取 Base32 文本再粘贴）

## 反馈

这是 [VPN MenuBar](https://github.com/CoderZCC/VPNMenuBar) 的 Windows 移植版。欢迎提 issue 或 PR。

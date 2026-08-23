# DeepSeek Harness 桌面启动器

把 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（dsh）包装成 Windows 桌面软件的一键启动器。dsh 本身需要安装 Node.js、pnpm、下载源码、手动启动，门槛较高；本启动器把这些步骤全部自动化，双击即可使用。

## 功能特性

* 全自动安装：首次运行只需一个exe文件（大小仅1M多），自动从 GitHub 下载dsh 源码（可选国内镜像加速）、解压并全自动安装所有的依赖环境。或手动选择已有目录。
* 内置 WebView2 浏览器，打开即桌面软件外观；Runtime 缺失时自动下载静默安装，操作栏可手动「修复浏览器」
* 一键启动/停止服务，自动打开 dsh 界面
* 端口占用自动检测、加载失败自动重试
* 任务提醒：原版dsh在任务完成时没有消息通知，本启动器添加了选项，可在对话任务完成、失败、取消时、弹出窗口等待用户选择时，在系统右下角弹出气泡通知，及时提醒用户查看。可选「后台才提醒」，在窗口未激活时才发消息提醒。
* 关闭窗口自动缩到系统托盘，服务持续后台运行（右键托盘可操作/退出）
* 自动更新：启动时自动检查 dsh 官方新版本并更新。更新采用文件级合并覆盖，自行添加或修改的文件会保留，用户聊天记录与数据不受影响。
* 用户数据独立存放（`userdata` 目录），更新/重装不丢失，支持一键备份。
* 全部路径以启动器所在文件夹为根，放任意盘符/目录均可使用，无需安装。

## 快速使用

- **首次使用**：双击 exe → 自动下载DSH安装（或选已有目录）→ 自动启动 → 开始对话
- **日常使用**：双击 exe → 自动检查DSH更新 → 自动启动服务 → 打开即用
- **结束后台**：关窗口进托盘，服务继续运行；托盘右键「退出」才真正停止 

---

## 下载与安装

**方式一：下载现成的编译产物（推荐，无需编译环境）**

前往本仓库的 **Releases** 页面下载最新版：

```
https://github.com/xxm-huijiong/deepseek-harness-launcher/releases
```

下载 `dsh-launcher.exe`（框架依赖版，约 1.6MB；需目标机装有 .NET 7 Desktop Runtime）或 `dsh-launcher-standalone.exe`（自包含版，约 150MB，免装 .NET）。
运行前先确认已满足「环境要求」（.NET 7 Desktop Runtime / WebView2 / Node.js / pnpm；WebView2 缺失时启动器会自动下载安装）。

**方式二：从源码自行编译**

见下方「编译启动器」章节（需要 .NET 7 SDK，Windows 不自带）。

---

## 快速开始

**第一步：安装运行环境**（Windows 不自带，需手动安装一次）

| 依赖                     | 作用        | 下载                                                        |
| ---------------------- | --------- | --------------------------------------------------------- |
| .NET 7 Desktop Runtime | 运行启动器本体   | https://dotnet.microsoft.com/download/dotnet/7.0          |
| WebView2 Runtime       | 内置浏览器内核   | Win11 自带；缺失时启动器会**自动下载并静默安装**（约 2MB，无需管理员），也可点「修复浏览器」手动安装 |
| Node.js ≥ 22.19        | 运行 dsh 服务 | https://nodejs.org                                        |
| pnpm                   | 安装 dsh 依赖 | 装完 Node 后执行 `npm install -g pnpm`                         |

**第二步：打开启动器**

双击 `dsh-launcher.exe`。若本机还没有 dsh 源码，会弹出引导：

1. **【是】自动下载安装** —— 从 GitHub 下载 dsh 源码，自动解压并安装依赖（会询问使用**国内镜像**还是直连）
2. **【否】手动选择目录** —— 如果你已手动下载/解压了 dsh 源码，选择那个目录即可
3. **【取消】** —— 退出

完成后启动器会记住源码位置（`config.json` 的 `workDir`），下次直接启动。

> dsh 源码默认存放在启动器目录下的 `dsh-src\`；也可以放其他位置后手动指定。

---

## dsh 本体的安装方式（背景说明）

官方支持两种运行方式（详见 dsh 官方 [README](https://github.com/deepseek-ai/deepseek-harness)）：

**方式一：npm 直装（只需 Node.js）**

```sh
npx @deepseek-ai/dsh web
```

启动后 Web UI 默认在 `http://127.0.0.1:3080`。

**方式二：源码运行（完整功能，可二次开发）**

```sh
git clone https://github.com/deepseek-ai/deepseek-harness.git
cd deepseek-harness
pnpm install
pnpm run build
pnpm dsh web
```

> 本启动器采用**源码方式**管理 dsh 本体：自动下载后执行 `pnpm install`，用 `pnpm dsh web` 启动服务（源码模式下由 tsx 直接运行，无需手动 build；需要正式构建时执行 `pnpm run build`）。

---

## 编译启动器（开发者）

**注意：.NET SDK 不是 Windows 自带的**，需要先安装：

| 项          | 说明                                                             |
| ---------- | -------------------------------------------------------------- |
| .NET 7 SDK | 下载：https://dotnet.microsoft.com/download/dotnet/7.0 （含编译与运行所需） |
| 验证         | 命令行执行 `dotnet --version`，能输出版本号即可                              |

**一键编译**：源码目录下有 `build.bat`，支持两种发布模式：

| 命令               | 产物                                     | 说明                                  |
| ---------------- | -------------------------------------- | ----------------------------------- |
| `build.bat`      | `dsh-launcher.exe`（约 1.6MB）            | 框架依赖版：目标机需已装 .NET 7 Desktop Runtime |
| `build.bat self` | `dsh-launcher-standalone.exe`（约 150MB） | 自包含版：内置 .NET 运行时，免装即可运行（适合全新虚拟机）    |

> **运行入口永远是源码根目录下的 `dsh-launcher.exe`**；
> `bin\Release\net7.0-windows\win-x64\DshLauncher.exe`（约 350KB）只是中间构建产物，不能直接运行。
> 若复制报"文件被占用"，先关闭正在运行的启动器再编译。

**手动命令**（等价于 build.bat 默认模式）：

```bash
cd <启动器目录>
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# 产物：bin\Release\net7.0-windows\win-x64\publish\DshLauncher.exe，复制为根目录 dsh-launcher.exe
```

> 源码文件：`Program.cs`、`InstallForm.cs`、`UpdateForm.cs`、`DshLauncher.csproj`（另加 NuGet 依赖 `Microsoft.Web.WebView2`，首次编译自动还原）。
> 所有运行时路径以 exe 所在目录为根（`Environment.ProcessPath`），无需修改任何配置即可换盘符/换目录运行。

---

## 目录说明（<启动器目录> = exe 所在文件夹）

```
<启动器目录>\                    ← 启动器所在文件夹（可放任意盘）
├─ dsh-launcher.exe          ← 启动器主程序（框架依赖版）
├─ dsh-launcher-standalone.exe ← 自包含版（`build.bat self` 产物，可选）
├─ config.json                   ← 配置（workDir = dsh 源码目录）
├─ userdata\                     ← ★ 用户数据（最重要）
│  ├─ sessions\                  ← 聊天记录
│  ├─ storages\                  ← 存储数据
│  ├─ settings.yaml              ← 设置
│  ├─ .credentials.yaml          ← API 凭据（敏感，勿公开）
│  └─ profiles\                  ← dsh 自动生成，可重建
├─ pics\                         ← 启动画面图库（随机展示，可自行增删）
├─ webview-data-v3\              ← 内置浏览器缓存（可重建）
├─ launcher.log                  ← 运行日志
├─ backups\                      ← 「备份数据」按钮的输出目录（backup-<时间戳>\userdata\...）
├─ update-dsh.bat                ← 一键更新脚本（需 dsh 源码为 git 仓库）
├─ build.bat                     ← 一键编译脚本（开发者）
└─ dsh-src\                      ← dsh 源码（默认位置，也可在别处并手动指定）
```

---

## 使用说明

- **启动服务**：打开启动器后自动启动（可取消「启动时自动运行」）；也可在 `≡ 操作` 或托盘右键菜单中手动操作
- **停止服务**：`≡ 操作` → 停止服务；或托盘右键 → 停止服务
- **缩到托盘**：点窗口「✕」会隐藏到系统托盘（右下角），**服务继续后台运行**；双击托盘图标恢复窗口，**右键托盘菜单可完成所有操作**（启动/停止/刷新/浏览器/备份/检查更新/退出）
- **刷新 / 外部浏览器**：内置浏览器异常时可刷新，或用「外部浏览器」在 **Edge/Chrome/Firefox（自动探测，不依赖系统默认浏览器）** 打开 `http://127.0.0.1:3080/web/`
- **修复浏览器**：内置浏览器不可用（缺 WebView2 Runtime）时，点「安装/修复内置浏览器」自动下载并静默安装（约 2MB，无需管理员），装好自动重试
- **备份数据**：一键把 `userdata` 用户数据复制到 `backups\backup-<时间戳>\userdata\`（排除自动生成的 profiles）；恢复时把内容复制回 `userdata` 并删除其中的 `profiles` 即可
- **检查更新**：启动时自动对比本地 dsh 版本与 GitHub 官方版本；发现新版弹出**前置更新窗口**，一键自动更新（详见下方「更新 dsh」）
- **任务提醒**：任务完成/失败/取消时气泡提醒（按"回合结束"判定，子代理会话不打扰）；勾选「后台才提醒」（默认开启）则**仅当启动器窗口不在前端时**才提醒
- **选项行**：`≡ 操作` 面板分两行——第一行按钮，第二行四个选项（启动时自动运行 / 启动时检查更新 / 任务提醒 / 后台才提醒）
- **启动画面**：`pics\` 下的图片（jpg/png/gif/bmp）会在启动等待期随机展示，gif 可播放动画

---

## 更新 dsh

**推荐：启动器内置一键更新** —— 启动时（或点「检查更新」）发现新版后，在更新窗口点「立即更新」：

1. 自动停止占用源码目录的进程（3080 端口监听 + 所有命令行指向源码目录的 node/git 进程）
2. git 仓库 → `git fetch + reset`；普通目录 → 下载官方源码 zip 后**文件级合并覆盖**
3. 合并前二次确认：将覆盖同名文件；你自行添加的文件（新包里没有的，如 `_launcher_build*`、`.agents` 等）自动保留，建议先手动备份
4. 自动 `pnpm install` + `pnpm run build`；文件被占用时自动重试并提示具体路径，失败可整流程重试

> 更新只动 dsh 源码目录，`userdata` 用户数据不受影响。
> 若源码是 git 仓库，也可双击 `update-dsh.bat` 手动更新（git pull + 安装依赖 + 构建）。

---

## 常见问题

**Q：内置浏览器空白 / 不可用？**
点 `≡ 操作` → 刷新；若提示缺少 WebView2 Runtime，点「修复浏览器」自动下载安装（约 2MB）。

**Q：更新时提示"文件被另一进程使用"？**
新版更新已改为文件级合并覆盖 + 占用进程自动清理 + 自动重试；若仍提示，说明存在未识别进程占用，按提示的具体文件路径关闭对应程序后重试。

**Q：通知太频繁？**
「后台才提醒」默认开启：只有启动器窗口不在前端时才弹通知；也可取消「任务提醒」彻底关闭。

**Q：端口 3080 被占用？**
启动器会检测并提示；若为其他程序占用，请先释放。

**Q：自动下载很慢 / 失败？**
改用国内镜像重试，或手动从 GitHub 下载后选「手动选择目录」。

**Q：node / pnpm 未找到？**
按引导安装 Node.js ≥ 22.19（nodejs.org），然后 `npm install -g pnpm`。

**Q：编译时报 .NET SDK 错误？**
Windows 不自带 .NET SDK，从 https://dotnet.microsoft.com/download/dotnet/7.0 安装后重试。

**Q：关闭窗口后服务还在跑？**
这是预期行为——点「✕」只缩到托盘，服务继续后台运行；托盘右键「退出」才会真正停止并退出。

---

## 开源与许可

- 本启动器：MIT（发布时请移除 `userdata`、`launcher.log` 等包含个人数据的目录与文件）
- dsh 本体：DeepSeek 官方开源（MIT），见 [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)
- `deepseek.ico` 为 DeepSeek 官方品牌图标，仅供学习使用，分发需注意品牌规范

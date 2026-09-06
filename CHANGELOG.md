# 更新记录

## v1.5.6（2026-09-06）

- 适配 dsh v0.1.2+ 令牌鉴权：鉴权采用 Cookie 机制（带 `?token=` 首访后下发），HTTP 与 WebSocket 共享 CookieContainer，自动从服务输出捕获带令牌的访问地址
- 适配 dsh v0.1.2 事件协议重构：旧 `/api/events.mux`、`/api/events.host` 已被官方移除，事件监听重写为 `/api/remote.mux` 的 `$events` 流（open/item 帧），并按新事件白名单分发
- 任务提醒基于新事件语义：`api-session/activity`（用户输入）识别主会话、`api-session/status`（agent 运行结束）触发完成提醒、`api-session/error` 失败提醒；`approval/request`、`user-questions/request` 确认提醒
- 就绪判定改为 HTTP 就绪（事件通道由监听自行重连，不再阻塞页面加载）
- 新增「内置浏览器打开」选项（默认勾选）：取消勾选后 dsh 启动时自动打开系统默认浏览器，服务就绪后启动器自动最小化到托盘
- 版本号升级至 1.5.6

## v1.5.5（2026-08-31）

- 清理硬编码本机路径：node/pnpm 查找改为优先 PATH 环境变量与自管理目录，不再依赖本机 C:/D: 绝对路径
- 旧版数据迁移目录改为从用户主目录环境变量推导（`~/.dsh`），兼容任意机器
- 安装向导（InstallForm）全面改为 npm 方式：自动 `npm install -g @deepseek-ai/dsh`，移除源码下载/构建流程
- 首次使用判断改为检测 dsh 全局包，不再依赖源码目录
- 删除过时的 `update-dsh.bat` 与旧版源码更新描述，README 更新为 npm 方式说明
- 版本号升级至 1.5.5

## v1.5.4（2026-08-30）

- 任务提醒优化：只提醒「主会话」（用户正在交互的会话）的任务完成/失败/取消；子代理完成不再打扰，不依赖 dsh 会话列表，兼容未来格式变化
- 启动 dsh 时增加 `--no-open`：不再自动打开系统默认浏览器，避免与启动器内置浏览器重复打开
- 版本号升级至 1.5.4

# claude-code-notify

给 [Claude Code](https://docs.claude.com/claude-code) 用的原生 Windows 弹窗通知。

当 Claude Code **需要你确认**（授权某个操作）或**任务完成**时，在屏幕中央弹出一个漂亮的通知卡片并播放提示音。点击卡片可以一键把运行 Claude Code 的终端窗口拉回前台。

> English README below · [English](#english)

![两种场景：等待确认（琥珀色）与任务完成（绿色）](docs/preview.png)

## 特性

- **两种场景，颜色区分**
  - 🟠 等待确认（Notification hook）—— 琥珀色 + 闹钟音，Claude 需要你授权时
  - 🟢 任务完成（Stop hook）—— 绿色 + 铃声，Claude 干完活时
- **原生 exe，几乎零延迟** —— 用 C# 编译成单个 `.exe`，声音异步播放，弹窗立即出现
- **点击回到终端** —— 点卡片自动聚焦运行 Claude Code 的终端窗口
- **多窗口定位** —— 通过 `CLAUDE_PID` 进程树匹配，多开时尽量路由到正确的会话窗口（详见[已知限制](#已知限制)）
- **DPI 自适应** —— 高分屏下不模糊
- **零依赖** —— 只用 .NET Framework 自带程序集（Windows 10/11 预装）

## 安装

需要 Windows 10/11 和 Claude Code。C# 编译器（`csc.exe`，随 .NET Framework 预装）通常已经在系统里。

```powershell
git clone https://github.com/harmony520/claude-code-notify.git
cd claude-code-notify
powershell -ExecutionPolicy Bypass -File install.ps1
```

安装脚本会：

1. 用系统自带的 `csc.exe` 把 `claude-notify.cs` 编译成 `claude-notify.exe`
2. 把 Notification 和 Stop 两个 hook **合并**进你的 `~/.claude/settings.json`（保留你已有的配置，不覆盖）

装完新开一个 Claude Code 会话即可生效。

### 卸载

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
```

会从 `settings.json` 里移除本工具写入的 hook（不动你的其它配置）。

## 自定义

### 换提示音

`claude-notify.exe` 接受 `--sound` 参数，可以是内置名或任意 `.wav` 绝对路径：

```
--sound Reminder          # 闹钟音（Alarm05）
--sound Default           # 铃声（Ring01）
--sound "C:\path\to.wav"  # 自定义 wav
```

Windows 自带的声音在 `%SystemRoot%\Media\` 下，可以挑喜欢的换。改完 `settings.json` 里对应 hook 命令的 `--sound` 值即可。

### 换文案

用 `--title` 和 `--message` 覆盖默认文字：

```
claude-notify.exe --scenario done --title "搞定" --message "任务跑完啦"
```

### 手动配置 hook

如果不想用安装脚本，也可以自己往 `~/.claude/settings.json` 加：

```json
{
  "hooks": {
    "Notification": [
      {
        "matcher": "",
        "hooks": [
          { "type": "command", "command": "\"%USERPROFILE%\\.claude\\hooks\\claude-notify.exe\" --scenario confirm" }
        ]
      }
    ],
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          { "type": "command", "command": "\"%USERPROFILE%\\.claude\\hooks\\claude-notify.exe\" --scenario done" }
        ]
      }
    ]
  }
}
```

## 工作原理

Claude Code 在特定事件会执行 [hooks](https://docs.claude.com/en/docs/claude-code/hooks)。本工具挂在两个事件上：

- **Notification** —— Claude 需要用户输入/授权时触发 → 弹「等待确认」
- **Stop** —— Claude 结束一轮响应时触发 → 弹「任务完成」

exe 分两阶段运行：前台进程立即拉起一个分离的后台进程然后退出（这样 hook 不会阻塞 Claude），后台进程负责画弹窗、放声音、处理点击。

点击卡片时，它枚举所有可见窗口，找标题含 "Claude Code" 的终端窗口并拉到前台。如果拿得到 `CLAUDE_PID` 环境变量，会先用进程树匹配来定位到触发通知的那个具体会话。

## 已知限制

- **仅 Windows。** 用了 WinForms 和 Win32 API。
- **Windows Terminal 多开无法精准定位。** WT 的 ConPTY 架构下所有标签/窗口共用一个 `WindowsTerminal.exe` 进程，且中间的 OpenConsole 宿主会退出、切断进程链，所以多个 Claude 会话无法靠进程树区分——会降级到「第一个标题匹配的窗口」。单窗口场景 100% 准确。若需多开精准定位，可让每个会话跑在独立进程的终端里（conhost，或 WT 设为每窗口独立进程）。

## 许可

MIT

---

## English

Native Windows toast notifications for [Claude Code](https://docs.claude.com/claude-code).

Pops a centered notification card with sound when Claude Code **needs your confirmation** (amber) or **finishes a task** (green). Click the card to bring the terminal running Claude Code back to the foreground.

### Install

```powershell
git clone https://github.com/harmony520/claude-code-notify.git
cd claude-code-notify
powershell -ExecutionPolicy Bypass -File install.ps1
```

The installer compiles `claude-notify.cs` with the system `csc.exe` and merges the Notification + Stop hooks into your `~/.claude/settings.json` (existing config is preserved). Start a new Claude Code session to activate.

Uninstall: `powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall`

### Customize

- `--sound Reminder|Default|<path.wav>` — notification sound
- `--title "..."` / `--message "..."` — override default text (defaults are in Chinese)

### Known limitations

Windows only. Under stock Windows Terminal, multiple Claude sessions can't be told apart (shared `WindowsTerminal.exe` process), so clicks fall back to the first title-matching window; exact for the single-window case.

### License

MIT

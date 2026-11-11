# audio-output-switcher

一个极轻量的 Windows 托盘小工具：按自定义全局快捷键，在「内置扬声器」和「耳机」之间一键切换系统默认声音输出设备。

**零依赖 · 免安装 · 单文件 · 无需管理员权限**

## 为什么做它

Windows 没有提供切换默认输出设备的快捷键，每次插拔耳机都要进「声音设置」点好几下。这个工具把这件事压缩成一个组合键。

## 功能

- **全局热键切换**，默认 `Ctrl + Alt + O`，可在设置里随时改成任意 Ctrl / Alt / Shift / Win 组合
- **系统托盘常驻**：双击切换，右键可单独切到设备 A / B、打开设置、开机自启、退出
- 首次运行自动按名称识别「扬声器 / 耳机」，并弹出设置窗口让你确认
- 托盘图标颜色区分当前设备（蓝色 = 扬声器，绿色 = 耳机），切换后气泡提示（可关闭）
- 同时切换 `eConsole` / `eMultimedia` / `eCommunications` 三个角色，微信、会议软件也跟着换（可关闭）
- 设备未连接时给出提示；设备 ID 变化（重新插拔、驱动重装）时自动按名称回退匹配并更新配置
- 提供命令行接口，方便接 AutoHotkey、Stream Deck 等工具

## 快速开始

### 直接用（推荐）

1. 从 [Releases 最新版本](https://github.com/gaoyuke/audio-output-switcher/releases/latest) 下载 `AudioSwitch.exe`（仓库根目录也保留了同一份文件）
2. 双击运行 → 任务栏托盘出现小喇叭图标
3. 首次启动会弹出设置窗口：确认设备 A / B，点「全局快捷键」输入框后直接按下想要的组合键，点「保存」

> 未签名的 exe 可能触发 Windows SmartScreen 提示，点「更多信息 → 仍要运行」即可；也可以自己编译，见下。

### 自己编译

只需要 Windows 自带的 .NET Framework 4.x（Windows 10 / 11 已内置），**不需要 Visual Studio、不需要 dotnet SDK**：

双击 `build.bat`，或在命令行执行：

```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /out:AudioSwitch.exe ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Core.dll ^
  AudioSwitch.cs
```

## 命令行参数

| 参数 | 作用 |
| --- | --- |
| （无参数） | 启动托盘常驻 |
| `/toggle` | 切换一次后退出 |
| `/a`、`/b` | 切换到设备 A / B 后退出 |
| `/settings` | 只打开设置窗口 |
| `/list` | 列出活动播放设备及其设备 ID |
| `/listout <路径>` | 把设备列表写入文件 |
| `/check` | 自检：设备、配置、快捷键是否被占用、热键消息是否正常 |

例如用 AutoHotkey 绑定自己的按键：

```ahk
^!h::Run "D:\path\to\AudioSwitch.exe" /toggle
```

## 配置

配置保存在 `%APPDATA%\AudioSwitch\config.xml`，包含两个设备的 ID 与名称、快捷键修饰键 / 虚拟键码以及各开关。删掉该文件即可恢复首次运行状态。

「开机自动启动」写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，只对当前用户生效；取消勾选即可移除，不写系统目录、不装服务。

## 实现原理

- **设备枚举与默认设备读取**：`IMMDeviceEnumerator` / `IMMDeviceCollection`，设备名称取 `PKEY_Device_FriendlyName`。
- **设置默认设备**：Windows 未公开的 `IPolicyConfig::SetDefaultEndpoint`（IID `F8679F50-850A-41CF-9C72-430F290290C8`，CLSID `870AF99C-171D-4F9E-AF0D-E63DF40C2BC9`）。该接口自 Vista 起一直稳定，系统声音设置、EarTrumpet、SoundSwitch、nircmd 都走它；本工具同时写入 `eConsole`、`eMultimedia`、`eCommunications` 三个角色。
- **全局热键**：`RegisterHotKey` + message-only window（`HWND_MESSAGE`）接收 `WM_HOTKEY`，带 `MOD_NOREPEAT` 防止长按重复触发。
- **界面与持久化**：WinForms `ApplicationContext` + `NotifyIcon`，配置用 `XmlSerializer` 读写。

> 因为用了未公开的 COM 接口，未来 Windows 大版本更新理论上可能使其失效。届时 `/check` 与托盘气泡会给出错误提示，直接调整源码里的接口定义即可。

## 系统要求

- Windows 7 及以上（已在 Windows 10 / 11 + Realtek 声卡上实测）
- .NET Framework 4.x（Windows 10 / 11 自带）
- 至少两个可用的播放输出设备

## 目录

```
AudioSwitch.cs    完整源码
AudioSwitch.exe   编译好的可执行文件
build.bat         重新编译脚本
```

## 常见问题

**Q：耳机拔掉后按快捷键会怎样？**
A：目标设备不在活动列表时会提示「设备当前不可用」；`/toggle` 会自动尝试切回另一个仍可用的设备。

**Q：快捷键没反应？**
A：运行 `AudioSwitch.exe /check`。若显示「全局热键注册失败」，说明组合键被其它程序占用，换一个即可。

**Q：切换后某个正在播放的软件没跟着换？**
A：部分程序在启动时锁定音频设备，需要重启该程序。这是 Windows 音频会话的普遍行为。

**Q：会不会修改系统文件？**
A：不会。只调用系统音频 API、写当前用户的配置文件，以及可选的开机自启注册表项。

## 许可证

[MIT](LICENSE) © 2026 audio-output-switcher contributors

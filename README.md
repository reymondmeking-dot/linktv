# LinkTV 双向投屏

在同一个局域网里，把手机、电视或 Mac 的画面投到 Windows，也可以把 Windows 画面投过去。两台设备互相确认即可连接，配对数字只用于核对。

[下载最新版](https://github.com/reymondmeking-dot/linktv/releases/latest) · [Apple 构建记录](https://github.com/reymondmeking-dot/linktv/actions/workflows/apple.yml)

## 设备支持

当前每次连接一台 Windows 电脑和一台接收或发送设备。

| 平台 | 安装要求 | 主要操作 |
| --- | --- | --- |
| Windows | Windows 10 或 11、.NET Framework 4.8 | 分享主屏、接收画面、全屏观看 |
| 安卓手机 | Android 5.0 或更新版本 | 发送和接收，适配触摸、横竖屏与系统返回 |
| Android TV | 使用与手机相同的 APK | 遥控器操作，收到画面时自动全屏 |
| iPhone | iOS 16 或更新版本 | 接收画面，通过系统屏幕广播发送 |
| Mac | macOS 13 或更新版本 | 接收画面，经系统授权后分享主屏，支持 Apple 芯片和 Intel 构建 |

Windows 和安卓提供可下载的发布包，Mac 提供 Apple 芯片和 Intel 通用试用包。iPhone 真机安装需要给应用和广播扩展签名并设置同一个 App Group。Apple 的项目源码与构建步骤见 [Apple 构建指南](apple/README.md)。

## 开始投屏

1. 两台设备连接同一个局域网，并打开 LinkTV。
2. 在「附近设备」选中对方。两边显示相同的核对数字后，各点一次「确认」。
3. 在发送端选「投屏到设备」。手机、电视和 Mac 会显示系统共享提示，按提示授权。

电视接收时自动全屏。按遥控器唤出「退出全屏」和「停止观看」，连续 3 秒无操作会隐藏。Windows 接收时可点「全屏」，按 Esc 或 F11 返回窗口。选择「停止观看」结束当前投屏。连接可以保留，iPhone 需要重新发起系统屏幕广播。

## 声音

- **Windows 发送**。打开「同步本机声音」，再勾选「本机静音」，可以让接收端继续播放。投屏结束后程序会恢复本机扬声器的原状态。 发送声音需要 Windows 11 或至少 20348 版系统的进程环回支持。 [Microsoft 音频采集说明](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)
- **安卓发送**。Android 10 及以上可以打开「发送应用声音」。系统会请求录音权限，用来采集允许共享的应用播放声音。旧版安卓设备发送画面，仍可接收和播放对方发来的声音。
- **iPhone 和 Mac 发送**。可以在开始共享前打开「同步应用声音」，由系统屏幕广播或录屏接口提供音频。受保护的内容由系统和播放应用决定是否允许共享。
- **接收端静音**。接收端静音只关闭本设备对投屏声音的播放。安卓或 Apple 设备作为发送端时，本机扬声器由系统控制。

## 画质与帧率

支持 H.264 的设备自动协商画质，兼容模式使用 JPEG。Windows 使用 FFmpeg 编解码。把 `ffmpeg.exe` 放在 LinkTV 同一目录，加入 `PATH`，或用 `LINKTV_FFMPEG` 指定路径。Windows 以 H.264 发送高帧率画面需要支持 NVENC 的 NVIDIA 硬件。

H.264 模式最高 **1080p、60 帧/秒**。索尼 KD‑49X8000C 的接收端用局域网动态测试画面实测约 59 帧/秒。实际帧率会随发送和接收设备、内容及网络变动。电视发送画面时，分辨率与帧率由电视的编码能力决定。

## 从源码构建

- **Windows**。在 Windows PowerShell 运行 `./build-windows.ps1`，生成 `release/LinkTV.exe`。
- **安卓手机和 Android TV**。使用 JDK 17、Android SDK 36 和 Build Tools 36.0.0，在 Windows PowerShell 运行 `./android/build.ps1`。脚本接受 `-Jdk` 和 `-Sdk` 参数。
- **iPhone 和 Mac**。按 [Apple 构建指南](apple/README.md) 安装 Xcode 和 XcodeGen，再构建 `LinkTV-iPhone` 或 `LinkTV-Mac`。

# LinkTV 双向投屏

把手机、电视和电脑的画面投到同一个局域网里的设备。打开两端的 LinkTV，选中「附近设备」，核对数字后各点一次确认即可连接。支持 H.264 的设备可用最高 **1080p、60 帧/秒** 的模式，兼容模式使用 JPEG。

[下载 Windows 和安卓版本](https://github.com/reymondmeking-dot/linktv/releases/latest)

## 设备支持

Windows 是当前的配对主机。安卓手机、Android TV、iPhone 和 Mac 可以各自与一台 Windows 电脑建立双向投屏连接。每次连接一对设备。

| 设备 | 接收和发送 | 安装与验证 |
| --- | --- | --- |
| Windows | 共享主屏、接收画面。支持时可同步本机声音，也可独立静音 | 提供 EXE，Windows 构建与本机测试通过 |
| 安卓手机、Android TV | 可接收画面，也可经系统授权共享本机屏幕。分辨率与帧率按设备支持的解码和编码能力选择 | 共用一个 APK，电视端有遥控器操作入口 |
| iPhone | 接收画面，通过系统屏幕广播发送画面 | 提供 Xcode 项目源码，签名构建和真机互投待验证 |
| macOS | 接收画面，经系统授权后共享主屏 | 提供 Xcode 项目源码，构建和真机互投待验证 |

## 使用

1. 让两台设备连接同一个局域网，并打开 LinkTV。
2. 在「附近设备」选中对方。两台设备显示相同的核对数字时，两边各点一次「确认」。
3. 在发送端选「投屏到设备」。手机、电视和 Mac 分享全屏时，由系统询问授权。

电视收到画面会自动全屏。按遥控器唤出「退出全屏」和「停止观看」，连续 3 秒无操作就隐藏。选择「停止观看」结束当前投屏，下一次可以直接开始。

Windows 发送时可打开「同步本机声音」，并勾选「本机静音」让声音仅在接收端播放。停止投屏后会恢复本机扬声器之前的状态。旧版 Android 设备的内部声音采集能力受系统限制。 [Android 播放音频采集说明](https://developer.android.com/media/platform/av-capture)

## 高帧率模式

Windows 端使用可用的 FFmpeg 进行 H.264 编解码。把 `ffmpeg.exe` 放到 LinkTV 同一目录，加入 `PATH`，或用 `LINKTV_FFMPEG` 指定文件位置。使用高帧率桌面发送需要兼容的 NVIDIA 硬件编码器。设备配对后会自动协商可用的视频格式，必要时使用 JPEG。

帧率会随发送设备、接收设备和网络变化。索尼 KD‑49X8000C 的正式版接收端通过局域网接收 1080p60 H.264 动态样片，实机呈现约 59 帧/秒。此前的独立原型测试用真实 Windows 桌面跑了两轮，实际呈现约 44 和 55 帧/秒。电视向另一台设备发送时，分辨率和帧率由本机编码器能力决定。

## 从源码构建

- **Windows**。在 Windows PowerShell 运行 `./build-windows.ps1`，需要 .NET Framework 4.8。生成的文件是 `release/LinkTV.exe`。
- **安卓手机和 Android TV**。在 Windows PowerShell 运行 `./android/build.ps1`，需要 JDK、Android SDK 和 Build Tools。脚本接受 `-Jdk`、`-Sdk` 参数，也支持 `JAVA_HOME` 和 `ANDROID_HOME`。
- **iPhone 和 Mac**。在装有 Xcode 和 XcodeGen 的 Mac 上进入 `apple`，运行 `xcodegen generate`，再构建 `LinkTV-iPhone` 或 `LinkTV-Mac` scheme。iPhone 真机需要给应用和广播扩展设置同一开发团队并开通相同的 App Group。更改组 ID 时同步修改 `BroadcastExchange.swift` 和 `project.yml`。

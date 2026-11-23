# iPhone、iPad 和 Mac 使用说明

## 连接与投屏

1. 让 Apple 设备和 Windows 电脑连到同一个局域网，打开 Windows 上的「双向投屏」。在 Apple 端允许「本地网络」权限。
2. 在「附近设备」选中 Windows 电脑。iPhone 或 iPad 可选择「接收投屏」或「投出屏幕」。Mac 先点「连接电脑」，配对后选择投出画面，或等待电脑投过来。
3. 两台设备上显示相同的六位数字时，分别点「确认」。接收端可以切换全屏、暂停播放投屏声音，或停止观看并保留连接。

iPhone 和 iPad 发送时，通过系统屏幕广播按钮选择「开始直播」，再回到应用确认配对数字。Mac 初次发送时，请到「系统设置 → 隐私与安全性」给应用开通「屏幕录制」或「屏幕与系统音频录制」。

发送前打开「同步应用声音」可把系统允许共享的应用声音一并发送。受保护的画面或声音受系统及原应用限制。接收端的声音开关只控制本机播放，发送设备的扬声器音量由系统控制。

搜不到设备时，先确认两端处在同一局域网，避开隔离访客网络，并检查本地网络权限。回到应用点「重新搜索」。[Apple 本地网络权限说明](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)

## 构建与安装

支持 iOS / iPadOS 16 及以上、macOS 13 及以上。需要装有 Xcode 的 Mac，以及 XcodeGen 2.42 或更新版本。从仓库根目录执行以下命令。

~~~sh
brew install xcodegen
cd apple
xcodegen generate
open LinkTV.xcodeproj
~~~

- **iPhone / iPad 真机**。在 Xcode 的「Signing & Capabilities」为 `LinkTV-iPhone` 和 `LinkTVBroadcast` 选择同一个支持 App Groups 的开发团队，并把两个目标关联到同一个 App Group。若仓库的标识已被占用，修改 `project.yml` 的主 App、扩展 Bundle ID 和 App Group，同时修改 `Sources/BroadcastExchange.swift` 里的对应常量，再重新生成项目。扩展 Bundle ID 必须以主 App Bundle ID 加英文句点开头。选 `LinkTV-iPhone` 方案和已连接的设备，再点运行。App Group 用来在屏幕广播扩展和应用之间传递选中的接收设备与配对状态。[Apple App Groups 配置说明](https://developer.apple.com/documentation/xcode/configuring-app-groups)、[开发团队可用能力](https://developer.apple.com/help/account/reference/supported-capabilities-ios)
- **Mac**。选 `LinkTV-Mac` 和 `My Mac`，再点运行。若要把 app 发给其他 Mac 用户，配置 Developer ID 签名与公证。[Apple Mac 应用分发说明](https://developer.apple.com/developer-id/)

## 验证

GitHub Actions 的 Apple builds 使用 Xcode 编译 Mac 通用架构、iOS 真机及 iOS 模拟器目标，并检查 iOS App 内含屏幕广播扩展。这些构建无需个人证书。发布页的 Mac 通用试用包带临时签名，尚未取得 Developer ID 签名和 Apple 公证。iOS 安装、屏幕广播、系统权限、局域网与声音要用 Apple 设备验证。

Mac 上可用合成音频检查转换，无需录音权限。先进入本目录并运行以下命令。

~~~sh
test_bin="$(mktemp -d)/pcm-conversion"
xcrun swiftc Sources/AppleCapturedPCM.swift Tests/PCMConversionSmoke.swift -o "$test_bin"
"$test_bin"
~~~

真机检查时在同一个局域网打开 Windows App，分别测试 Apple 接收和发送。在 iPhone 上切换横竖屏，用两端都听得到的普通音源检查声音，再关闭接收声音、退出全屏并停止观看，确认两台设备仍处于连接状态。iPhone 广播停止后，可以回到附近设备重新发起投屏。

[GitHub Actions 构建记录](https://github.com/reymondmeking-dot/linktv/actions/workflows/apple.yml)

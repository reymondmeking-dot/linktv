import SwiftUI
import AVFoundation
#if os(iOS)
import ReplayKit
import UIKit
#else
import AppKit
#endif

private enum LinkPalette {
    static let cream = Color(red: 1.00, green: 0.97, blue: 0.95)
    static let blush = Color(red: 0.99, green: 0.88, blue: 0.93)
    static let lavender = Color(red: 0.93, green: 0.89, blue: 0.98)
    static let ink = Color(red: 0.26, green: 0.17, blue: 0.30)
    static let raspberry = Color(red: 0.64, green: 0.23, blue: 0.44)
    static let muted = Color(red: 0.43, green: 0.32, blue: 0.42)
}

@main
@MainActor
struct LinkTVApp: App {
    @StateObject private var model = LinkTVModel()

    var body: some Scene {
        WindowGroup {
            #if os(macOS)
            LinkTVHome(model: model).frame(minWidth: 440, minHeight: 440)
            #else
            LinkTVHome(model: model)
            #endif
        }
    }
}

@MainActor
private struct LinkTVHome: View {
    @ObservedObject var model: LinkTVModel
    @Environment(\.scenePhase) private var scenePhase
    @State private var expanded = false

    private var pairingSheet: Binding<PairingPrompt?> {
        Binding(get: { model.prompt }, set: { _ in })
    }

    var body: some View {
        Group {
            if expanded && model.phase == .receiving {
                expandedViewer
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        header
                        if model.connectionIsBusy {
                            sessionCard
                        } else {
                            nearbyCard
                        }
                        if let message = model.message {
                            Text(message)
                                .font(.callout)
                                .foregroundStyle(LinkPalette.muted)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                    .frame(maxWidth: 700)
                    .padding(18)
                    .frame(maxWidth: .infinity)
                }
                .background {
                    LinearGradient(colors: [LinkPalette.cream, LinkPalette.lavender],
                                   startPoint: .topLeading, endPoint: .bottomTrailing)
                    .ignoresSafeArea()
                }
            }
        }
        .tint(LinkPalette.raspberry)
        .task {
            model.setAudioVisible(scenePhase == .active)
            model.startDiscovery()
        }
        .onChange(of: scenePhase) { next in
            model.setAudioVisible(next == .active)
        }
        .onChange(of: model.phase) { next in
            if next != .receiving { expanded = false }
        }
        .sheet(item: pairingSheet) { prompt in
            #if os(iOS)
            pairingCard(prompt).presentationDetents([.medium])
            #else
            pairingCard(prompt).frame(minWidth: 350)
            #endif
        }
    }

    private var header: some View {
        HStack(spacing: 14) {
            Image("Mascot")
                .resizable()
                .scaledToFit()
                .frame(width: 68, height: 68)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                Text("双向投屏")
                    .font(.system(.largeTitle, design: .rounded).weight(.bold))
                    .foregroundStyle(LinkPalette.ink)
                    .accessibilityAddTraits(.isHeader)
                Text("在附近设备间共享屏幕")
                    .font(.subheadline)
                    .foregroundStyle(LinkPalette.muted)
            }
            Spacer(minLength: 0)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(LinkPalette.blush, in: RoundedRectangle(cornerRadius: 24))
    }

    private var nearbyCard: some View {
        panel {
            HStack {
                Text("附近设备")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(LinkPalette.ink)
                    .accessibilityAddTraits(.isHeader)
                Spacer(minLength: 10)
                Button { model.rescan() } label: {
                    Label("重新搜索", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.bordered)
            }
            Text(model.discoveryMessage)
                .font(.callout)
                .foregroundStyle(LinkPalette.muted)
            ForEach(model.computers) { computer in
                computerRow(computer)
            }
        }
    }

    private func computerRow(_ computer: NearbyComputer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(computer.name, systemImage: "desktopcomputer")
                .font(.headline)
                .foregroundStyle(LinkPalette.ink)
                .textSelection(.disabled)
            HStack(spacing: 8) {
                #if os(iOS)
                Button("接收投屏") { model.connect(computer) }
                    .buttonStyle(.borderedProminent)
                Button("投出屏幕") { model.prepareBroadcast(computer) }
                    .buttonStyle(.bordered)
                #else
                Button("连接电脑") { model.connect(computer) }
                    .buttonStyle(.borderedProminent)
                #endif
            }
            .controlSize(.large)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(15)
        .background(LinkPalette.lavender, in: RoundedRectangle(cornerRadius: 18))
    }

    private var sessionCard: some View {
        panel {
            Text(model.peerName.isEmpty ? "连接电脑" : model.peerName)
                .font(.title3.weight(.semibold))
                .foregroundStyle(LinkPalette.ink)
                .accessibilityAddTraits(.isHeader)
            Text(phaseDescription)
                .font(.callout)
                .foregroundStyle(LinkPalette.muted)

            if model.phase == .receiving {
                picture(showVideo: !expanded)
                    .frame(minHeight: 180, maxHeight: 390)
                    .clipShape(RoundedRectangle(cornerRadius: 16))
                Button("全屏观看") { expanded = true }
                    .buttonStyle(.borderedProminent)
            }
            #if os(iOS)
            if model.phase == .broadcastReady {
                BroadcastPicker()
                    .frame(width: 56, height: 54)
                    .background(LinkPalette.blush, in: RoundedRectangle(cornerRadius: 15))
                    .accessibilityLabel("打开系统屏幕广播")
            }
            #else
            if model.phase == .connected {
                Button("投屏到设备") { model.startMacSharing() }
                    .buttonStyle(.borderedProminent)
            } else if model.phase == .sending || model.phase == .startingCapture {
                Button("停止本机投屏") { model.stopMacSharing() }
                    .buttonStyle(.borderedProminent)
            }
            #endif

            Button(disconnectLabel, role: .cancel) { model.disconnect() }
                .buttonStyle(.bordered)
                .disabled(model.phase == .cancelling || model.phase == .broadcastStopping)
        }
    }

    private var phaseDescription: String {
        switch model.phase {
        case .idle: return ""
        case .connecting, .broadcastConnecting: return "正在连接电脑…"
        case .pairing: return "请比较两台设备显示的数字。"
        case .confirming: return "本机已确认，等待对方设备确认。"
        case .cancelling: return "正在取消配对…"
        case .connected: return "已连接。可在电脑上选择「投屏到设备」。"
        case .receiving: return "正在接收对方的画面。"
        case .startingCapture: return "正在准备屏幕共享…"
        case .sending: return "正在分享本机画面。"
        case .broadcastReady: return "点下方系统按钮开始屏幕广播，然后回到这里核对配对数字。"
        case .broadcasting: return "正在分享本机画面。"
        case .broadcastStopping: return "正在停止屏幕广播…"
        }
    }

    private var disconnectLabel: String {
        switch model.phase {
        case .broadcasting, .broadcastStopping: return "停止屏幕广播"
        case .connecting, .pairing, .confirming, .cancelling,
             .broadcastReady, .broadcastConnecting: return "取消"
        default: return "断开连接"
        }
    }

    private func picture(showVideo: Bool) -> some View {
        ZStack {
            Color.black
            if let video = model.h264Layer {
                if showVideo { AppleVideoView(video: video).accessibilityLabel("设备投屏画面") }
            } else if let frame = model.frame {
                Image(decorative: frame, scale: 1, orientation: .up)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
                    .accessibilityLabel("设备投屏画面")
            } else {
                ProgressView("等待画面…").foregroundStyle(.white).tint(.white)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var expandedViewer: some View {
        ZStack(alignment: .topTrailing) {
            Color.black.ignoresSafeArea()
            picture(showVideo: true)
            Button { expanded = false } label: {
                Label("退出全屏", systemImage: "arrow.down.right.and.arrow.up.left")
            }
            .buttonStyle(.borderedProminent)
            .padding(12)
            #if os(macOS)
            .keyboardShortcut(.escape, modifiers: [])
            #endif
        }
    }

    private func pairingCard(_ prompt: PairingPrompt) -> some View {
        VStack(spacing: 15) {
            Text("连接到 " + prompt.name)
                .font(.title2.weight(.semibold))
                .foregroundStyle(LinkPalette.ink)
            Text(prompt.number)
                .font(.system(.largeTitle, design: .monospaced).weight(.bold))
                .tracking(5)
                .foregroundStyle(LinkPalette.raspberry)
                .accessibilityLabel("配对数字 " + prompt.number)
            Text("两台设备显示相同数字时，请在两边选择「确认」。")
                .foregroundStyle(LinkPalette.muted)
                .multilineTextAlignment(.center)
            HStack(spacing: 16) {
                Button("取消", role: .cancel) { model.respond(approved: false) }
                    .buttonStyle(.bordered)
                Button("确认") { model.respond(approved: true) }
                    .buttonStyle(.borderedProminent)
            }
            .controlSize(.large)
        }
        .padding(22)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(LinkPalette.cream.ignoresSafeArea())
        .interactiveDismissDisabled()
    }

    private func panel<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 13, content: content)
            .padding(18)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.white, in: RoundedRectangle(cornerRadius: 24))
    }
}

#if os(iOS)
private struct BroadcastPicker: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 56, height: 54))
        picker.preferredExtension = BroadcastExchange.extensionIdentifier
        picker.showsMicrophoneButton = false
        picker.tintColor = UIColor(red: 0.64, green: 0.23, blue: 0.44, alpha: 1)
        return picker
    }

    func updateUIView(_ picker: RPSystemBroadcastPickerView, context: Context) {}
}
#endif

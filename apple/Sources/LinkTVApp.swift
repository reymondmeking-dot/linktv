import SwiftUI
import AVFoundation
#if os(iOS)
import ReplayKit
import UIKit
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
        #if os(macOS)
        Window("双向投屏", id: "main") {
            LinkTVHome(model: model)
                .frame(minWidth: 360, minHeight: 360)
        }
        .defaultSize(width: 680, height: 620)
        #else
        WindowGroup {
            LinkTVHome(model: model)
        }
        #endif
    }
}

@MainActor
private struct LinkTVHome: View {
    @ObservedObject var model: LinkTVModel
    @Environment(\.scenePhase) private var scenePhase
    @State private var expanded = false
    #if os(iOS)
    @Environment(\.openURL) private var openURL
    #endif

    private var pairingSheet: Binding<PairingPrompt?> {
        Binding(get: { model.prompt }, set: { _ in })
    }

    var body: some View {
        Group {
            if expanded && model.phase == .receiving {
                expandedViewer
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        header
                        if model.connectionIsBusy { sessionCard } else { nearbyCard }
                        if let message = model.message {
                            Text(message)
                                .font(.callout)
                                .foregroundStyle(LinkPalette.ink)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(12)
                                .background(LinkPalette.blush, in: RoundedRectangle(cornerRadius: 12))
                        }
                    }
                    .frame(maxWidth: 700)
                    .padding(14)
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
        .preferredColorScheme(.light)
        .task { sceneChanged(scenePhase) }
        .onChange(of: scenePhase, perform: sceneChanged)
        .onChange(of: model.phase) { next in
            if next != .receiving { expanded = false }
        }
        .sheet(item: pairingSheet) { prompt in
            #if os(iOS)
            pairingCard(prompt).presentationDetents([.medium, .large])
            #else
            pairingCard(prompt).frame(minWidth: 300, idealWidth: 380, minHeight: 240)
            #endif
        }
    }

    private func sceneChanged(_ phase: ScenePhase) {
        model.setAudioVisible(phase == .active)
        if phase == .active { model.startDiscovery() }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image("Mascot")
                .resizable()
                .scaledToFit()
                .frame(width: 54, height: 54)
                .accessibilityHidden(true)
            Text("双向投屏")
                .font(.system(.largeTitle, design: .rounded).weight(.bold))
                .foregroundStyle(LinkPalette.ink)
                .accessibilityAddTraits(.isHeader)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(LinkPalette.blush, in: RoundedRectangle(cornerRadius: 22))
    }

    private var nearbyCard: some View {
        panel {
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 12) {
                    nearbyTitle.fixedSize(horizontal: true, vertical: false)
                    Spacer(minLength: 0)
                    rescanButton.fixedSize(horizontal: true, vertical: false)
                }
                VStack(alignment: .leading, spacing: 8) {
                    nearbyTitle
                    rescanButton
                }
            }
            Text(model.discoveryMessage)
                .font(.callout)
                .foregroundStyle(LinkPalette.muted)
            #if os(iOS)
            if model.discoveryNeedsSettings {
                Button("打开本地网络设置") {
                    if let url = URL(string: UIApplication.openSettingsURLString) { openURL(url) }
                }
                .buttonStyle(.bordered)
            }
            #endif
            ForEach(model.computers) { computer in
                computerRow(computer)
            }
        }
    }

    private var nearbyTitle: some View {
        Text("附近设备")
            .font(.title3.weight(.semibold))
            .foregroundStyle(LinkPalette.ink)
            .accessibilityAddTraits(.isHeader)
    }

    private var rescanButton: some View {
        Button { model.rescan() } label: {
            Label("重新搜索", systemImage: "arrow.clockwise")
        }
        .buttonStyle(.bordered)
    }

    private func computerRow(_ computer: NearbyComputer) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Label(computer.name, systemImage: "desktopcomputer")
                .font(.headline)
                .foregroundStyle(LinkPalette.ink)
                .fixedSize(horizontal: false, vertical: true)
            #if os(iOS)
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) {
                    receiveButton(computer).fixedSize(horizontal: true, vertical: false)
                    broadcastButton(computer).fixedSize(horizontal: true, vertical: false)
                }
                VStack(alignment: .leading, spacing: 8) {
                    receiveButton(computer)
                    broadcastButton(computer)
                }
            }
            .controlSize(.large)
            #else
            Button("连接电脑") { model.connect(computer) }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
            #endif
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(13)
        .background(LinkPalette.lavender, in: RoundedRectangle(cornerRadius: 16))
    }

    #if os(iOS)
    private func receiveButton(_ computer: NearbyComputer) -> some View {
        Button("接收投屏") { model.connect(computer) }
            .buttonStyle(.borderedProminent)
    }

    private func broadcastButton(_ computer: NearbyComputer) -> some View {
        Button("投出屏幕") { model.prepareBroadcast(computer) }
            .buttonStyle(.bordered)
    }
    #endif

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
                picture
                    .aspectRatio(model.pictureAspectRatio, contentMode: .fit)
                    .frame(maxWidth: .infinity, maxHeight: 360)
                    .clipShape(RoundedRectangle(cornerRadius: 14))
                Toggle("播放投屏声音", isOn: $model.receiveSoundEnabled)
                    .toggleStyle(.switch)
                    .foregroundStyle(LinkPalette.ink)
                    .disabled(model.stopViewingPending)
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 8) {
                        expandButton.fixedSize(horizontal: true, vertical: false)
                        stopViewingButton.fixedSize(horizontal: true, vertical: false)
                    }
                    VStack(alignment: .leading, spacing: 8) {
                        expandButton
                        stopViewingButton
                    }
                }
            }
            #if os(iOS)
            if model.phase == .broadcastReady {
                HStack(spacing: 12) {
                    BroadcastPicker()
                        .frame(width: 56, height: 54)
                        .background(LinkPalette.blush, in: RoundedRectangle(cornerRadius: 15))
                    Text("开始屏幕广播")
                        .font(.headline)
                        .foregroundStyle(LinkPalette.ink)
                        .accessibilityHidden(true)
                }
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

    private var expandButton: some View {
        Button("全屏观看") { expanded = true }
            .buttonStyle(.borderedProminent)
            .disabled(model.stopViewingPending)
    }

    private var stopViewingButton: some View {
        Button(model.stopViewingPending ? "正在停止…" : "停止观看") { model.stopViewing() }
            .buttonStyle(.bordered)
            .disabled(model.stopViewingPending)
    }

    private var phaseDescription: String {
        switch model.phase {
        case .idle: return ""
        case .connecting, .broadcastConnecting: return "正在连接电脑…"
        case .pairing: return "请比较两台设备显示的数字。"
        case .confirming: return "本机已确认，等待对方设备确认。"
        case .cancelling: return "正在取消配对…"
        case .connected: return "已连接。可在电脑上选择「投屏到设备」。"
        case .receiving: return model.stopViewingPending ? "正在等待对方停止投屏…" : "正在接收对方的画面。"
        case .startingCapture: return "正在准备屏幕共享…"
        case .sending, .broadcasting: return "正在分享本机画面。"
        case .broadcastReady: return "点下面的系统按钮，选择「开始直播」，然后回到这里核对配对数字。"
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

    private var picture: some View {
        ZStack {
            Color.black
            if let video = model.h264Layer {
                AppleVideoView(video: video).accessibilityLabel("设备投屏画面")
            } else if let frame = model.frame {
                Image(decorative: frame, scale: 1, orientation: .up)
                    .resizable()
                    .interpolation(.high)
                    .scaledToFit()
                    .accessibilityLabel("设备投屏画面")
            } else {
                ProgressView("等待画面…")
                    .foregroundStyle(.white)
                    .tint(.white)
                    .environment(\.colorScheme, .dark)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var expandedViewer: some View {
        ZStack(alignment: .topTrailing) {
            picture.ignoresSafeArea()
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) {
                    stopViewingButton.fixedSize(horizontal: true, vertical: false)
                    exitFullScreenButton.fixedSize(horizontal: true, vertical: false)
                }
                VStack(alignment: .trailing, spacing: 8) {
                    exitFullScreenButton
                    stopViewingButton
                }
            }
            .foregroundStyle(.white)
            .environment(\.colorScheme, .dark)
            .padding(10)
            .background(.black.opacity(0.68), in: RoundedRectangle(cornerRadius: 14))
            .padding(12)
        }
    }

    private var exitFullScreenButton: some View {
        Button { expanded = false } label: {
            Label("退出全屏", systemImage: "arrow.down.right.and.arrow.up.left")
        }
        .buttonStyle(.borderedProminent)
        #if os(macOS)
        .keyboardShortcut(.escape, modifiers: [])
        #endif
    }

    private func pairingCard(_ prompt: PairingPrompt) -> some View {
        ScrollView {
            VStack(spacing: 14) {
                Text("连接到 " + prompt.name)
                    .font(.title2.weight(.semibold))
                    .foregroundStyle(LinkPalette.ink)
                    .accessibilityAddTraits(.isHeader)
                Text(prompt.number)
                    .font(.system(.largeTitle, design: .monospaced).weight(.bold))
                    .tracking(3)
                    .foregroundStyle(LinkPalette.raspberry)
                    .lineLimit(1)
                    .minimumScaleFactor(0.5)
                    .accessibilityLabel("配对数字 " + prompt.number.map { String($0) }.joined(separator: " "))
                Text("两台设备显示相同数字时，请在两边选择「确认」。")
                    .foregroundStyle(LinkPalette.muted)
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 12) {
                        rejectPairing.fixedSize(horizontal: true, vertical: false)
                        approvePairing.fixedSize(horizontal: true, vertical: false)
                    }
                    VStack(spacing: 8) {
                        approvePairing
                        rejectPairing
                    }
                }
                .controlSize(.large)
            }
            .multilineTextAlignment(.center)
            .padding(20)
            .frame(maxWidth: 500)
            .frame(maxWidth: .infinity)
        }
        .background(LinkPalette.cream.ignoresSafeArea())
        .interactiveDismissDisabled()
    }

    private var rejectPairing: some View {
        Button("取消", role: .cancel) { model.respond(approved: false) }
            .buttonStyle(.bordered)
    }

    private var approvePairing: some View {
        Button("确认") { model.respond(approved: true) }
            .buttonStyle(.borderedProminent)
    }

    private func panel<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12, content: content)
            .padding(15)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.white, in: RoundedRectangle(cornerRadius: 22))
    }
}

#if os(iOS)
private struct BroadcastPicker: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 56, height: 54))
        picker.preferredExtension = BroadcastExchange.extensionIdentifier
        picker.showsMicrophoneButton = false
        picker.tintColor = UIColor(red: 0.64, green: 0.23, blue: 0.44, alpha: 1)
        picker.accessibilityLabel = "开始屏幕广播"
        return picker
    }

    func updateUIView(_ picker: RPSystemBroadcastPickerView, context: Context) {}
}
#endif

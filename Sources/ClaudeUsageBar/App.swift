import ServiceManagement
import SwiftUI
import UsageCore

@main
struct ClaudeUsageBarApp: App {
    @StateObject private var model = UsageModel()

    init() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--snapshot"), i + 1 < args.count {
            exit(renderSnapshot(to: args[i + 1]) ? 0 : 1)
        }
    }

    var body: some Scene {
        MenuBarExtra {
            PanelView(model: model)
        } label: {
            Image(nsImage: menuBarImage(model.menuBarText, level: model.alertLevel))
                .accessibilityLabel(model.menuBarText)
        }
        .menuBarExtraStyle(.window)
    }
}

struct PanelView: View {
    @ObservedObject var model: UsageModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(Array((model.usage?.rows ?? []).enumerated()), id: \.offset) { _, row in
                VStack(alignment: .leading, spacing: 4) {
                    HStack {
                        Text(row.title).font(.headline)
                        Spacer()
                        Text("\(Int(row.percent.rounded()))%").monospacedDigit()
                    }
                    UsageBar(percent: row.percent)
                    if let resetsAt = row.resetsAt {
                        Text("\(resetDescription(resetsAt)) · \(resetsAt.formatted(.dateTime.weekday().day().month().hour().minute()))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    switch projection(for: row) {
                    case .lasts(let percent)?:
                        Text("At this pace: lasts, about \(Int(percent.rounded()))% at reset")
                            .font(.caption).foregroundStyle(.green)
                    case .runsOut(let date)?:
                        Text("At this pace: runs out \(date.formatted(.dateTime.weekday().day().month().hour().minute()))")
                            .font(.caption).foregroundStyle(.orange)
                    case nil:
                        EmptyView()
                    }
                }
            }
            if let error = model.error { Text(error).foregroundStyle(.red) }
            Divider()
            HStack {
                if let updated = model.updatedAt {
                    Text("Updated \(updated.formatted(date: .omitted, time: .shortened))")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Refresh") { Task { await model.refresh() } }.keyboardShortcut("r")
                Button("") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q").hidden().frame(width: 0)
                Menu {
                    Toggle("Open at login", isOn: Binding(get: { model.opensAtLogin }, set: { model.setOpensAtLogin($0) }))
                    Divider()
                    Button("Quit") { NSApplication.shared.terminate(nil) }
                } label: {
                    Image(systemName: "gearshape")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                .accessibilityLabel("Options")
            }
        }
        .padding()
        .frame(width: 280)
    }
}

@MainActor
final class UsageModel: ObservableObject {
    @Published var usage: Usage?
    @Published var error: String?
    @Published var updatedAt: Date?
    @Published var opensAtLogin = SMAppService.mainApp.status == .enabled
    private var timer: Timer?

    /// Static model with fixed data, for the README screenshot.
    init(preview usage: Usage) {
        self.usage = usage
        updatedAt = Date()
        opensAtLogin = true
    }

    init() {
        // Opt in to launch at login on first run; afterwards the toggle decides.
        if !UserDefaults.standard.bool(forKey: "didSetUpLoginItem") {
            UserDefaults.standard.set(true, forKey: "didSetUpLoginItem")
            setOpensAtLogin(true)
        }
        Task { await refresh() }
        timer = Timer.scheduledTimer(withTimeInterval: 300, repeats: true) { [weak self] _ in
            Task { await self?.refresh() }
        }
    }

    func setOpensAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            self.error = "Open at login: \(error.localizedDescription)"
        }
        opensAtLogin = SMAppService.mainApp.status == .enabled
    }

    var alertLevel: AlertLevel {
        (usage?.rows ?? []).map { UsageCore.alertLevel(for: $0) }.max() ?? .none
    }

    var menuBarText: String {
        guard let usage else { return error == nil ? "✳︎ …" : "⚠︎" }
        return (alertLevel == .none ? "✳︎ " : "⚠︎ ") + usage.menuBarTitle
    }

    func refresh() async {
        do {
            usage = try await fetchUsage()
            error = nil
            updatedAt = Date()
            notify(alerts(for: usage?.rows ?? []))
        } catch {
            self.error = error.localizedDescription
            // The usage endpoint rate-limits per account; retry when it says so instead of waiting the full 5 min.
            if let wait = (error as? UsageError)?.retryAfter {
                Task { [weak self] in
                    try? await Task.sleep(for: .seconds(wait + 1))
                    await self?.refresh()
                }
            }
        }
    }
}

struct UsageError: LocalizedError {
    let errorDescription: String?
    var retryAfter: Double?
    init(_ message: String, retryAfter: Double? = nil) {
        errorDescription = message
        self.retryAfter = retryAfter
    }
}

func fetchUsage() async throws -> Usage {
    guard let token = readToken() else { throw UsageError("Not logged in: run claude and /login") }
    var request = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
    request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
    request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
    let (data, response) = try await URLSession.shared.data(for: request)
    let status = (response as? HTTPURLResponse)?.statusCode ?? 0
    // shortcut: no token refresh of our own; Claude Code refreshes it on its next run. Upgrade if expired tokens become common.
    if status == 401 { throw UsageError("Token expired: open Claude Code once to refresh it") }
    if status == 429 {
        let wait = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Retry-After").flatMap(Double.init) ?? 60
        throw UsageError("Rate limited, retrying in \(Int(wait))s", retryAfter: wait)
    }
    guard status == 200 else { throw UsageError("API returned HTTP \(status)") }
    return try Usage.decode(data)
}

/// Claude Code keeps its OAuth credentials in the login Keychain on macOS. Reading through
/// /usr/bin/security reuses the item's existing ACL, so there is no extra Keychain prompt.
func readToken() -> String? {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
    process.arguments = ["find-generic-password", "-s", "Claude Code-credentials", "-w"]
    let pipe = Pipe()
    process.standardOutput = pipe
    process.standardError = FileHandle.nullDevice
    if (try? process.run()) != nil {
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        if process.terminationStatus == 0, let token = accessToken(fromCredentials: data) { return token }
    }
    let file = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude/.credentials.json")
    return (try? Data(contentsOf: file)).flatMap(accessToken(fromCredentials:))
}

/// Drawn by hand so the color shows even when the panel is not the key window.
struct UsageBar: View {
    let percent: Double

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(.quaternary)
                Capsule().fill(percent >= 90 ? Color.red : percent >= 70 ? .orange : .blue)
                    .frame(width: geo.size.width * min(max(percent, 0), 100) / 100)
            }
        }
        .frame(height: 6)
        .accessibilityLabel("\(Int(percent.rounded())) percent used")
    }
}

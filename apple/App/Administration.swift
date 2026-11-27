import SwiftUI

struct MCPStatus: Decodable, Sendable, Identifiable {
    struct Running: Decodable, Sendable, Identifiable {
        let agentId: String
        let agentName: String?
        let pid: Int
        var id: String { "\(agentId)#\(pid)" }
    }
    let name: String
    let disabled: Bool
    let tools: [String?]
    let readAt: String?
    let running: [Running]
    let failed: Bool
    var id: String { name }

    enum CodingKeys: String, CodingKey { case name, disabled, tools, readAt, running, error }
    init(from decoder: Decoder) throws {
        let data = try decoder.container(keyedBy: CodingKeys.self)
        name = try data.decode(String.self, forKey: .name)
        disabled = try data.decode(Bool.self, forKey: .disabled)
        tools = try data.decode([String?].self, forKey: .tools)
        readAt = try data.decodeIfPresent(String.self, forKey: .readAt)
        running = try data.decode([Running].self, forKey: .running)
        // Launch configuration and subprocess errors may contain credentials; do not retain them.
        failed = (try data.decodeIfPresent(String.self, forKey: .error))?.isEmpty == false
    }
}

struct OutsideClient: Decodable, Sendable, Identifiable {
    let name: String
    let created: String
    let tokenPath: String
    let unread: Int
    var id: String { name }
    static func normalized(_ input: String) -> String { input.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }
    static func isValid(_ input: String) -> Bool { input.wholeMatch(of: /[a-z0-9][a-z0-9-]{0,39}/) != nil }
}

struct BackupInfo: Decodable, Sendable {
    struct Snapshot: Decodable, Sendable {
        let name: String
        let taken: String
        let bytes: Int64
        let encrypted: Bool
    }
    let directory: String
    let encrypting: Bool
    let intervalMinutes: Int
    let snapshots: [Snapshot]
}

enum AdministrationPage: String, Hashable {
    case mcp = "MCP Servers", clients = "Outside Clients", backups = "Backups"
}

struct AdministrationView: View {
    let page: AdministrationPage
    let api: API
    let route: UUID
    let active: Bool
    let hint: UUID
    let agents: [Agent]
    @State private var mcp = OperationList<MCPStatus>()
    @State private var clients = OperationList<OutsideClient>()
    @State private var backups = OperationList<BackupInfo>()
    @State private var name = ""
    @State private var editingPassphrase = false
    @State private var passphrase = ""
    @State private var pending: Confirmation?
    @State private var confirming = false

    private enum Confirmation {
        case reload, remove(OutsideClient), stopEncrypting
        var isDestructive: Bool { if case .reload = self { return false }; return true }
        var title: String {
            switch self {
            case .reload: "Reload MCP Servers?"
            case .remove(let client): "Remove \(client.name)?"
            case .stopEncrypting: "Stop Encrypting Backups?"
            }
        }
        var detail: String {
            switch self {
            case .reload: "Marten will reread the MCP section of the server configuration and refresh server tool lists."
            case .remove: "This revokes the outside client’s access and deletes its token file on the Marten server. It does not affect Apple device pairing."
            case .stopEncrypting: "Future snapshots will not be encrypted. Existing snapshots are unchanged and still require their original passphrase."
            }
        }
    }

    var body: some View {
        List {
            switch page {
            case .mcp: mcpRows
            case .clients: clientRows
            case .backups: backupRows
            }
        }
        .navigationTitle(page.rawValue)
        .toolbar { Button("Refresh Status", systemImage: "arrow.clockwise") { Task { await refresh() } }.disabled(!active) }
        .refreshable { await refresh() }
        .task(id: OperationsRefresh(route: route, active: active)) {
            guard active else { return }
            while !Task.isCancelled { await refresh(); try? await Task.sleep(for: .seconds(8)) }
        }
        .task(id: hint) { await refresh() }
        .onChange(of: OperationsRefresh(route: route, active: active)) { clearSensitiveInputs() }
        .onDisappear { clearSensitiveInputs() }
        .alert(pending?.title ?? "Confirm Change", isPresented: $confirming, presenting: pending) { action in
            Button("Confirm", role: action.isDestructive ? .destructive : nil) {
                clearConfirmation()
                guard active else { return }
                switch action {
                case .reload:
                    guard ready(mcp) else { return }
                    Task { await mcp.change({ try await api.reloadMCP() }, read: api.mcpStatus) }
                case .remove(let selected):
                    guard ready(clients) else { return }
                    Task {
                        guard clients.items?.contains(where: { $0.name == selected.name && $0.created == selected.created }) == true else { return }
                        await clients.change({ try await api.removeOutsideClient(selected.name) }, read: api.outsideClients)
                    }
                case .stopEncrypting:
                    guard ready(backups), backups.items?.first?.encrypting == true else { return }
                    savePassphrase(nil)
                }
            }
            Button("Cancel", role: .cancel, action: clearConfirmation)
        } message: { action in Text(action.detail) }
    }

    @ViewBuilder private var mcpRows: some View {
        Section {
            Button("Reload Server Configuration…") { stage(.reload) }.disabled(!ready(mcp))
            Text("Edit the MCP section of config.json on the Marten server, then reload. Launch commands, arguments, and raw errors are hidden because they may contain credentials.")
                .font(.footnote).foregroundStyle(.secondary)
            stateRows(mcp, empty: "No MCP servers configured.")
        }
        ForEach(mcp.items ?? []) { server in
            Section(server.name) {
                Text(server.disabled ? "Disabled" : server.failed ? "Tool discovery failed" : server.readAt == nil ? "Tool list not read yet" : "Enabled")
                if server.failed { Label("Check the server-local configuration and logs for details.", systemImage: "exclamationmark.triangle") }
                if let at = server.readAt { LabeledContent("Tools Last Read") { InboxTime(stamp: at) } }
                let names = server.tools.compactMap { $0 }
                LabeledContent("Tools", value: "\(names.count)")
                if !names.isEmpty { Text(names.joined(separator: ", ")).textSelection(.enabled) }
                ForEach(server.running) { run in
                    let label = "\(run.agentName ?? run.agentId) · PID \(run.pid)"
                    if agents.contains(where: { $0.id == run.agentId }) {
                        NavigationLink(label, value: WorkspaceDestination.agent(run.agentId))
                    } else { Text(label) }
                }
            }
        }
    }

    @ViewBuilder private var clientRows: some View {
        Section {
            Text("Outside agents use scripts/marten-ext or /api/ext. The token file path is on the Marten server, not on this device. This app neither reads nor shows the token.")
                .font(.footnote).foregroundStyle(.secondary)
            TextField("New client name", text: $name)
                .autocorrectionDisabled()
                #if os(iOS)
                .textInputAutocapitalization(.never)
                #endif
            Button("Add Outside Client") {
                let submitted = OutsideClient.normalized(name)
                guard ready(clients), OutsideClient.isValid(submitted) else { return }
                name = ""
                Task { await clients.change({ try await api.addOutsideClient(submitted) }, read: api.outsideClients) }
            }.disabled(!ready(clients) || !OutsideClient.isValid(OutsideClient.normalized(name)))
            Text("Up to 40 letters, digits or dashes, starting with a letter or digit. Names are saved in lowercase.")
                .font(.footnote).foregroundStyle(.secondary)
            stateRows(clients, empty: "No outside clients.")
        }
        ForEach(clients.items ?? []) { client in
            Section(client.name) {
                LabeledContent("Unread by Client", value: "\(client.unread)")
                Text("Server token file").font(.caption)
                Text(client.tokenPath).font(.footnote.monospaced()).textSelection(.enabled)
                Button("Remove Client…", role: .destructive) { stage(.remove(client)) }.disabled(!ready(clients))
            }
        }
    }

    @ViewBuilder private var backupRows: some View {
        Section {
            stateRows(backups, empty: "Backup status unavailable.")
            Button("Back Up Now") {
                guard ready(backups) else { return }
                Task { await backups.change({ try await api.backupNow() }, read: readBackups) }
            }.disabled(!ready(backups))
        }
        if let info = backups.items?.first {
            Section("Snapshots") {
                if let last = info.snapshots.first {
                    LabeledContent("Latest Snapshot") { InboxTime(stamp: last.taken) }
                    LabeledContent("Size", value: ByteCountFormatter.string(fromByteCount: last.bytes, countStyle: .file))
                    LabeledContent("Latest Snapshot Encryption", value: last.encrypted ? "Encrypted" : "Not encrypted")
                } else { Text("No snapshots yet.") }
                LabeledContent("Snapshots Reported", value: "\(info.snapshots.count)")
                Text(info.directory).font(.footnote.monospaced()).textSelection(.enabled)
                Text(info.intervalMinutes > 0 ? "Automatically every \(info.intervalMinutes) min when something changed." : "Automatic snapshots off.")
            }
            Section("Backup Encryption") {
                Text(info.encrypting ? "Future snapshots use a server-stored passphrase." : "Future snapshots are not encrypted. Secrets are excluded from snapshots.")
                if editingPassphrase {
                    SecureField("New passphrase", text: $passphrase)
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textContentType(nil)
                        .textInputAutocapitalization(.never)
                        #endif
                    Text("Keep a copy outside Marten. Changing the passphrase does not re-encrypt existing snapshots.")
                        .font(.footnote).foregroundStyle(.secondary)
                    Button("Save Passphrase") {
                        guard ready(backups), !passphrase.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
                        savePassphrase(passphrase)
                        clearPassphrase()
                    }.disabled(!ready(backups) || passphrase.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    Button("Cancel", action: clearPassphrase)
                } else {
                    Button(info.encrypting ? "Change Passphrase…" : "Encrypt Future Backups…") { editingPassphrase = true }.disabled(!ready(backups))
                    if info.encrypting {
                        Button("Stop Encrypting…", role: .destructive) { stage(.stopEncrypting) }.disabled(!ready(backups))
                    }
                }
            }
        }
    }

    private func savePassphrase(_ input: String?) {
        var value = input
        Task {
            await backups.change({
                let submitted = value
                value = nil
                try await api.setBackupPassphrase(submitted)
            }, read: readBackups)
            value = nil
        }
    }

    @ViewBuilder private func stateRows<Item>(_ state: OperationList<Item>, empty: String) -> some View {
        if let error = state.error { Label(error, systemImage: "exclamationmark.circle") }
        else if state.items == nil { ProgressView("Loading…") }
        else if state.items?.isEmpty == true { Text(empty).foregroundStyle(.secondary) }
        if state.working { ProgressView("Updating server…") }
        if let error = state.actionError { Label(error, systemImage: "exclamationmark.circle") }
    }

    private func ready<Item>(_ state: OperationList<Item>) -> Bool {
        active && state.items != nil && state.error == nil && !state.working && !state.needsRead
    }
    private func refresh() async {
        guard active else { return }
        switch page {
        case .mcp: await mcp.refresh(api.mcpStatus)
        case .clients: await clients.refresh(api.outsideClients)
        case .backups: await backups.refresh(readBackups)
        }
    }
    private func readBackups() async throws -> [BackupInfo] { [try await api.backupStatus()] }
    private func stage(_ action: Confirmation) { clearPassphrase(); pending = action; confirming = true }
    private func clearPassphrase() { passphrase = ""; editingPassphrase = false }
    private func clearConfirmation() { pending = nil; confirming = false }
    private func clearSensitiveInputs() { clearPassphrase(); clearConfirmation() }
}

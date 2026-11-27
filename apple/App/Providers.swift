import SwiftUI

struct ProviderAccount: Decodable, Sendable {
    let connected: Bool
    let email: String?
    let signingIn: Bool
    let signInFailed: Bool

    enum CodingKeys: String, CodingKey { case connected, email, signingIn, lastError }
    init(from decoder: Decoder) throws {
        let body = try decoder.container(keyedBy: CodingKeys.self)
        connected = try body.decode(Bool.self, forKey: .connected)
        email = try body.decodeIfPresent(String.self, forKey: .email)
        signingIn = try body.decode(Bool.self, forKey: .signingIn)
        // Auth failures may contain upstream details. Retain only whether one was reported.
        signInFailed = (try body.decodeIfPresent(String.self, forKey: .lastError))?.isEmpty == false
    }
}

struct ProviderBackend: Decodable, Sendable, Identifiable {
    let provider: String
    let label: String
    let connected: Bool
    let keySource: String?
    let envVar: String?
    let spentToday: Decimal
    let budget: Decimal?
    var id: String { provider }
    var hasSavedKey: Bool { keySource == "saved" }
    var exhaustedBudget: Bool { budget.map { spentToday >= $0 } ?? false }

    var connectionLabel: String {
        if hasSavedKey { return "Saved key on server" }
        if !connected { return "No key" }
        if let envVar, keySource == envVar,
           envVar.wholeMatch(of: /[A-Za-z_][A-Za-z0-9_]*/) != nil {
            return "Server environment (\(envVar))"
        }
        return "Connected (server-managed key)"
    }
}

struct ProvidersView: View {
    let status: ServerStatus?
    let active: Bool
    let ready: Bool
    let busy: Bool
    let route: UUID
    let statusError: String?
    let refresh: () async -> Void
    let logout: () -> Void
    let update: (String, String?) -> Void
    @State private var editing: String?
    @State private var key = ""
    @State private var pending: Confirmation?
    @State private var confirming = false

    private enum Confirmation {
        case logout, remove(String, String)
        var title: String {
            switch self { case .logout: "Sign out of ChatGPT?"; case .remove(_, let name): "Remove the saved \(name) key?" }
        }
        var detail: String {
            switch self {
            case .logout: "This signs out the account on the Marten server and can interrupt ChatGPT-backed work. It does not disconnect this device from Marten."
            case .remove: "Marten will delete its saved key on the server. If a key is configured in the server environment, Marten will use it instead."
            }
        }
    }

    var body: some View {
        Form {
            if let statusError { Label(statusError, systemImage: "exclamationmark.circle") }
            if status == nil && statusError == nil { ProgressView("Loading providers…") }
            Section("ChatGPT") {
                if let account = status?.auth {
                    Text(account.connected ? "Connected" : account.signingIn ? "Waiting for sign-in on the server…" : "Not connected")
                    if account.connected, let email = account.email { Text(email).textSelection(.enabled) }
                    if account.connected && account.signingIn { Text("A sign-in is also in progress on the server.") }
                    if account.signInFailed {
                        Label("The last sign-in failed. Check the server-local Marten web UI for details.", systemImage: "exclamationmark.circle")
                    }
                    if account.connected {
                        Button("Sign Out…", role: .destructive) { stage(.logout) }.disabled(!ready)
                    }
                }
                Text("To sign in or reconnect, open the Marten web UI in a browser on the machine running Marten. ChatGPT returns to a loopback callback on that machine; this app does not start sign-in.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            if let backends = status?.backends {
                if backends.isEmpty { Section("API-key Providers") { Text("No API-key providers configured.") } }
                ForEach(backends) { backend in
                    Section(backend.label) {
                        Text(backend.connectionLabel)
                        if backend.spentToday > 0 || backend.budget != nil {
                            LabeledContent("Spent Today", value: backend.spentToday.formatted(.currency(code: "USD")))
                            if let limit = backend.budget { LabeledContent("Daily Budget", value: limit.formatted(.currency(code: "USD"))) }
                            if backend.exhaustedBudget { Label("Daily budget reached", systemImage: "exclamationmark.triangle") }
                        }
                        if editing == backend.id {
                            SecureField("New API key", text: $key)
                                .autocorrectionDisabled()
                                #if os(iOS)
                                .textContentType(nil)
                                .textInputAutocapitalization(.never)
                                #endif
                            Button("Save Key") {
                                guard active, ready, !key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
                                let value = key
                                clearDraft()
                                update(backend.id, value)
                            }.disabled(!ready || key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                            Button("Cancel", action: clearDraft)
                        } else {
                            Button(backend.connected ? "Change Key…" : "Add Key…") {
                                clearDraft()
                                editing = backend.id
                            }.disabled(!ready)
                            if backend.hasSavedKey {
                                Button("Remove Saved Key…", role: .destructive) {
                                    stage(.remove(backend.id, backend.label))
                                }.disabled(!ready)
                            }
                        }
                    }
                }
            }
            if busy { ProgressView("Updating server…") }
        }
        .navigationTitle("Providers & Account")
        .toolbar { Button("Refresh", systemImage: "arrow.clockwise") { Task { await refresh() } }.disabled(!active) }
        .refreshable { await refresh() }
        .alert(pending?.title ?? "Confirm Change", isPresented: $confirming, presenting: pending) { action in
            Button("Confirm", role: .destructive) {
                clearConfirmation()
                guard active, ready else { return }
                switch action {
                case .logout: logout()
                case .remove(let provider, _): update(provider, nil)
                }
            }
            Button("Cancel", role: .cancel, action: clearConfirmation)
        } message: { action in Text(action.detail) }
        .onChange(of: route) { clear() }
        .onChange(of: active) { if !active { clear() } }
        .onChange(of: status?.backends?.map(\.id)) {
            if let editing, status?.backends?.contains(where: { $0.id == editing }) != true { clearDraft() }
        }
        .onDisappear { clear() }
    }

    private func stage(_ action: Confirmation) { clearDraft(); pending = action; confirming = true }
    private func clearDraft() { key = ""; editing = nil }
    private func clearConfirmation() { confirming = false; pending = nil }
    private func clear() { clearDraft(); clearConfirmation() }
}

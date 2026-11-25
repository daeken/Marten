import SwiftUI
import CryptoKit

struct ReviewedTool: Decodable, Identifiable, Sendable {
    let name: String
    let scope: String
    let path: String
    let script: String
    let description: String?
    let error: String?
    let modified: String?
    let pendingReview: Bool
    var id: String { path }

    func reviewCard(in messages: [InboxMessage]) -> InboxMessage? {
        guard pendingReview else { return nil }
        let hash = SHA256.hash(data: Data(script.utf8)).map { String(format: "%02X", $0) }.joined()
        return messages.first { $0.isOpen && $0.kind == "tool_review" && $0.reference == "\(path)\n\(hash)" }
    }
}

struct AgentSettings: Encodable, Equatable, Sendable {
    var model: String?
    var effort: String?

    enum CodingKeys: String, CodingKey { case model, effort }
    func encode(to encoder: Encoder) throws {
        var body = encoder.container(keyedBy: CodingKeys.self)
        try body.encode(model, forKey: .model)
        try body.encode(effort, forKey: .effort)
    }
}

extension Agent {
    var settings: AgentSettings { .init(model: model, effort: effort) }
}

struct AgentSettingsView: View {
    let api: API
    let agent: Agent
    let config: ServerStatus.Config?
    let agents: [Agent]
    let route: UUID
    let active: Bool
    let ready: Bool
    let save: (AgentSettings, AgentSettings) -> Void
    @State private var draft = AgentSettings()
    @State private var original: AgentSettings?
    @State private var catalog: [String] = []
    @State private var catalogError: String?
    @State private var loading = false

    var body: some View {
        Form {
            Section("Model Settings") {
                Picker("Model", selection: $draft.model) {
                    Text(defaultLabel(config?.model ?? config?.defaultModel ?? catalog.first)).tag(nil as String?)
                    ForEach(choices(catalog, draft.model, agent.model), id: \.self) { Text($0).tag(Optional($0)) }
                }
                Picker("Reasoning Effort", selection: $draft.effort) {
                    Text(defaultLabel(config?.effort)).tag(nil as String?)
                    ForEach(choices(["low", "medium", "high", "xhigh"], draft.effort, agent.effort), id: \.self) { Text($0).tag(Optional($0)) }
                }
                if loading { ProgressView("Loading models…") }
                if let catalogError {
                    Label(catalogError, systemImage: "exclamationmark.circle")
                    Button("Reload Models") { Task { await loadModels() } }.disabled(!active)
                }
                if let original, original != agent.settings && draft != agent.settings {
                    Text("The settings changed on the server. Reload them before saving.").foregroundStyle(.secondary)
                    Button("Reload Current Settings") { resetDraft() }
                }
                Button("Save Settings") {
                    if let original { save(draft, original) }
                }.disabled(!active || !ready || original != agent.settings || draft == agent.settings)
            }
            Section("Agent Diagnostics") {
                LabeledContent("Status", value: agent.status.replacingOccurrences(of: "_", with: " ").capitalized)
                if agent.closed == true { Label("Closed", systemImage: "archivebox") }
                if agent.pausedEffective { Label(agent.paused ? "Paused by agent setting" : "Paused by project or workspace", systemImage: "pause") }
                if let detail = agent.statusDetail { Text(detail).textSelection(.enabled) }
                if let retry = agent.retryAt { HStack { Text("Next Retry"); Spacer(); InboxTime(stamp: retry) } }
                if let pending = agent.pending { LabeledContent("Pending Input", value: "\(pending)") }
                if let count = agent.openTasks {
                    NavigationLink(value: WorkspaceDestination.operations(agent.id, task: nil)) { LabeledContent("Open Tasks", value: "\(count)") }
                }
                if let parent = agent.parentId {
                    if let match = agents.first(where: { $0.id == parent }) {
                        NavigationLink("Reports to \(match.name)", value: WorkspaceDestination.agent(match.id))
                    } else { LabeledContent("Parent", value: parent) }
                }
                if let directory = agent.workingDirectory { LabeledContent("Working Directory", value: directory).textSelection(.enabled) }
                if let worktree = agent.worktree { LabeledContent("Worktree", value: worktree).textSelection(.enabled) }
            }
            if let call = agent.modelCall {
                Section(call.kind == "compaction" ? "Context Compaction" : "Model Reply") {
                    LabeledContent("Model", value: call.model)
                    HStack { Text("Started"); Spacer(); InboxTime(stamp: call.startedAt) }
                    if let end = call.endedAt {
                        HStack { Text(call.outcome?.capitalized ?? "Ended"); Spacer(); InboxTime(stamp: end) }
                    } else { Text("In flight") }
                }
            }
            if agent.nudgesStopped == true || agent.abandonedRunspaces?.isEmpty == false {
                Section("Warnings") {
                    if agent.nudgesStopped == true { Label("Automatic nudges stopped.", systemImage: "exclamationmark.triangle") }
                    if let abandoned = agent.abandonedRunspaces, let last = abandoned.last {
                        Text("\(abandoned.count) PowerShell runspace(s) abandoned after a timeout. Marten must be restarted to reclaim them.")
                        InboxTime(stamp: last.at)
                        TranscriptCode(text: last.command)
                    }
                }
            }
        }
        .navigationTitle("\(agent.name) · Settings")
        .task(id: OperationsRefresh(route: route, active: active)) { if active { await loadModels() } }
        .onChange(of: agent.settings, initial: true) { _, settings in
            if original == nil || draft == original || draft == settings { resetDraft() }
        }
    }

    private func resetDraft() { original = agent.settings; draft = agent.settings }
    private func defaultLabel(_ value: String?) -> String { value.map { "Default (\($0))" } ?? "Default" }
    private func choices(_ list: [String], _ draft: String?, _ saved: String?) -> [String] {
        ([saved, draft].compactMap { $0 } + list).reduce(into: []) { if !$0.contains($1) { $0.append($1) } }
    }
    private func loadModels() async {
        guard active, !loading else { return }
        loading = true
        defer { loading = false }
        do {
            let fresh = try await api.models()
            try Task.checkCancellation()
            catalog = fresh
            catalogError = nil
        } catch is CancellationError {
        } catch { if !Task.isCancelled { catalogError = safeMessage(error) } }
    }
}

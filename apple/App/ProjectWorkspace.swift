import SwiftUI

struct ProjectMemory: Decodable, Sendable {
    struct Edit: Decodable, Sendable {
        let at: String
        let agentId: String
        let reason: String?
        let path: String?
        let added: [String]
        let removed: [String]
    }
    let path: String
    let text: String
    let prompt: String
    let log: [Edit]
}

struct ProjectCreation: Encodable, Sendable {
    let name: String
    let bindings: [String]
    let prompt: String?
    let worktreesByDefault: Bool
    let brief: String?

    init(name: String, folders: String, prompt: String, worktrees: Bool, brief: String) {
        self.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        self.bindings = folders.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        self.prompt = Self.optional(prompt)
        self.worktreesByDefault = worktrees
        self.brief = Self.optional(brief)
    }

    var isValid: Bool { !name.isEmpty && !bindings.isEmpty }

    private static func optional(_ value: String) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    enum CodingKeys: String, CodingKey { case name, bindings, prompt, worktreesByDefault, brief }
    func encode(to encoder: Encoder) throws {
        var payload = encoder.container(keyedBy: CodingKeys.self)
        try payload.encode(name, forKey: .name)
        try payload.encode(bindings, forKey: .bindings)
        try payload.encode(prompt, forKey: .prompt)
        try payload.encode(worktreesByDefault, forKey: .worktreesByDefault)
        try payload.encode(brief, forKey: .brief)
    }
}

enum LifecycleAction: Equatable, Sendable {
    case pauseAll(Bool), pauseProject(String, Bool), pauseAgent(String, Bool)
    case close(String, Bool), freshContext(String)

    var destructive: Bool {
        switch self { case .close(_, true), .freshContext: true; default: false }
    }

    var title: String {
        switch self {
        case .pauseAll(let pause): pause ? "Pause All" : "Resume All"
        case .pauseProject(_, let pause): pause ? "Pause Project" : "Resume Project"
        case .pauseAgent(_, let pause): pause ? "Pause Agent" : "Resume Agent"
        case .close(_, let close): close ? "Close Agent" : "Reopen Agent"
        case .freshContext: "Start Fresh Context"
        }
    }

    func confirmation(for name: String) -> String {
        switch self {
        case .close(_, true): "Closing \(name) cancels its current turn, stops its processes, and suspends schedules. The transcript is kept. Reopening or sending a message can wake it again."
        case .freshContext: "\(name) will no longer see earlier messages, tool results, or compaction summaries. The transcript stays browsable. Current instructions, memory, and queued input are kept."
        default: ""
        }
    }

    func applies(to status: ServerStatus) -> Bool {
        switch self {
        case .pauseAll(let value): status.paused.map { $0 != value } ?? false
        case .pauseProject(let id, let value): status.projects.first { $0.id == id }?.paused.map { $0 != value } ?? false
        case .pauseAgent(let id, let value): status.agents.first { $0.id == id }.map { $0.paused != value } ?? false
        case .close(let id, let value): status.agents.first { $0.id == id }.map { ($0.closed == true) != value } ?? false
        case .freshContext(let id): status.agents.first { $0.id == id }?.canStartFreshContext == true
        }
    }
}

@MainActor @Observable
final class WorkspaceControls {
    struct Target: Equatable {
        let route: UUID
        let page: WorkspaceDestination?
    }
    struct Confirmation {
        let id = UUID()
        let action: LifecycleAction
        let name: String
        let target: Target
    }
    private(set) var status: ServerStatus?
    private(set) var statusError: String?
    private(set) var authorizationRejected = false
    private(set) var notice: String?
    private(set) var loadedRoute: UUID?
    private(set) var pending: Confirmation?
    private(set) var busy = false
    private var needsRead = false
    private var generation = UUID()
    private var reading: UUID?
    private var desiredRoute: UUID?

    func refresh(using api: API, route: UUID) async {
        guard !Task.isCancelled, reading != route else { return }
        desiredRoute = route
        reading = route
        let request = UUID()
        generation = request
        defer { if generation == request { reading = nil } }
        do {
            let latest = try await api.status()
            guard !Task.isCancelled, generation == request else { return }
            status = latest
            loadedRoute = route
            statusError = nil
            authorizationRejected = false
            needsRead = false
        } catch is CancellationError {
        } catch {
            if !Task.isCancelled && generation == request {
                statusError = safeMessage(error)
                authorizationRejected = (error as? ClientError) == .unauthorized
            }
        }
    }

    func ready(_ route: UUID) -> Bool { loadedRoute == route && statusError == nil && !busy && !needsRead }

    func stage(_ action: LifecycleAction, name: String, target: Target) {
        guard action.destructive, ready(target.route), status.map(action.applies) == true else { return }
        pending = Confirmation(action: action, name: name, target: target)
    }

    func cancel() { pending = nil }
    func dismissNotice() { notice = nil }
    func showNotice(_ text: String) { notice = text }

    @discardableResult
    func apply(_ action: LifecycleAction, confirmation: UUID? = nil, target: Target, using api: API) -> Task<Void, Never>? {
        if action.destructive {
            guard let pending, pending.id == confirmation, pending.action == action else { return nil }
            cancel()
            guard pending.target == target else { return nil }
        }
        guard ready(target.route), status.map(action.applies) == true else { return nil }
        return run(using: api, route: target.route, label: action.title, request: {
            try await api.applyLifecycle(action)
        }) { _ in }
    }

    @discardableResult
    func saveSettings(_ draft: AgentSettings, original: AgentSettings, agent: String, using api: API, route: UUID) -> Task<Void, Never>? {
        guard ready(route), draft != original, status?.agents.first(where: { $0.id == agent })?.settings == original else { return nil }
        return run(using: api, route: route, label: "Save Agent Settings", request: {
            try await api.updateSettings(agent, settings: draft)
        }) { _ in }
    }

    @discardableResult
    func signOutProvider(using api: API, route: UUID) -> Task<Void, Never>? {
        guard ready(route), status?.auth?.connected == true else { return nil }
        return run(using: api, route: route, label: "Sign Out of ChatGPT", request: {
            try await api.signOutProviderAccount()
        }) { _ in }
    }

    @discardableResult
    func updateProvider(_ id: String, key: String?, using api: API, route: UUID) -> Task<Void, Never>? {
        guard ready(route), let provider = status?.backends?.first(where: { $0.id == id }), key != nil || provider.hasSavedKey else { return nil }
        var input = key
        return run(using: api, route: route, label: key == nil ? "Remove Provider Key" : "Save Provider Key", request: {
            let value = input
            input = nil
            try await api.setProviderKey(id, key: value)
        }) { _ in }
    }

    func create(_ draft: ProjectCreation, using api: API, route: UUID, completion: @escaping (String?) -> Void) {
        guard draft.isValid, ready(route) else { return }
        run(using: api, route: route, label: "Create Project", request: {
            try await api.createProject(draft).orchestratorId
        }, completion: completion)
    }

    func createChat(_ name: String, using api: API, route: UUID, completion: @escaping (String?) -> Void) {
        guard ready(route) else { return }
        run(using: api, route: route, label: "Create Chat", request: {
            try await api.createChat(name).id
        }, completion: completion)
    }

    @discardableResult
    private func run<Result: Sendable>(using api: API, route: UUID, label: String, request: @escaping () async throws -> Result,
                                      completion: @escaping (Result) -> Void) -> Task<Void, Never> {
        busy = true
        notice = nil
        return Task {
            defer { busy = false }
            do {
                let result = try await request()
                await reconcile(using: api, route: route)
                completion(result)
            } catch {
                if (error as? ClientError) == .response(409), label == "Start Fresh Context" {
                    notice = "Could not start fresh context. An active turn, pause, or server shutdown can prevent this action."
                } else if (error as? ClientError) == .response(400), label == "Create Project" {
                    notice = "Could not create the project. Check its name and that the bound folders exist on the server."
                } else {
                    let reason = error is CancellationError ? "The request was cancelled." : safeMessage(error)
                    notice = "\(label): \(reason) Check server state before trying again."
                }
                await reconcile(using: api, route: route)
            }
        }
    }

    private func reconcile(using api: API, route: UUID) async {
        guard desiredRoute == route else { return }
        // Do not allow reads started before the outcome to clear the reconciliation requirement.
        needsRead = true
        generation = UUID()
        reading = nil
        await refresh(using: api, route: route)
    }
}

struct ProjectMemoryView: View {
    let api: API
    let project: Project
    let agents: [Agent]
    let route: UUID
    let active: Bool
    let hint: UUID
    let controlsReady: Bool
    let pause: () -> Void
    let inbox: InboxState
    let answer: (InboxMessage, Int?) -> Void
    @State private var tools = OperationList<ReviewedTool>()
    @State private var memory: ProjectMemory?
    @State private var error: String?
    @State private var reading = false

    var body: some View {
        List {
            Section("Project") {
                if let paused = project.paused {
                    Button(paused ? "Resume Project" : "Pause Project", systemImage: paused ? "play" : "pause", action: pause)
                        .disabled(!active || !controlsReady)
                }
                ForEach(Array((project.bindings ?? []).enumerated()), id: \.offset) { _, binding in
                    Text(binding).font(.footnote.monospaced()).textSelection(.enabled)
                }
            }
            Section("Agent-Written Tools") {
                if let error = tools.error { Label(error, systemImage: "exclamationmark.circle") }
                else if tools.items == nil { ProgressView("Loading tools…") }
                else if tools.items?.isEmpty == true { Text("No tools yet.").foregroundStyle(.secondary) }
                ForEach(tools.items ?? []) { tool in
                    DisclosureGroup(tool.name) {
                        Text(tool.scope == "global" ? "Global · available across projects" : "Project · \(project.name)").font(.subheadline)
                        Text(tool.path).font(.footnote.monospaced()).textSelection(.enabled)
                        if let modified = tool.modified { InboxTime(stamp: modified) }
                        if let error = tool.error { Label(error, systemImage: "exclamationmark.triangle") }
                        else if let description = tool.description { Text(description).textSelection(.enabled) }
                        TranscriptCode(text: tool.script)
                        if tool.pendingReview {
                            if let card = tool.reviewCard(in: inbox.messages ?? []) {
                                QuestionCard(message: card, inbox: inbox, active: active, answer: answer)
                            } else {
                                Text("Needs review. No pending review card matches this version. Native approval requires a version-bound card.")
                                    .foregroundStyle(.secondary)
                            }
                        } else { Text("No review required").foregroundStyle(.secondary) }
                    }
                }
            }
            if let error { Label(error, systemImage: "exclamationmark.circle") }
            if let memory {
                Section("Memory") {
                    Text(memory.path).font(.footnote.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                    if memory.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text("Empty so far.").foregroundStyle(.secondary)
                    } else { RichTranscript(text: memory.text) }
                }
                if !memory.prompt.isEmpty { Section("Project Prompt") { RichTranscript(text: memory.prompt) } }
                Section("History") {
                    if memory.log.isEmpty { Text("No edits yet.").foregroundStyle(.secondary) }
                    ForEach(Array(memory.log.enumerated().reversed()), id: \.offset) { _, edit in
                        VStack(alignment: .leading, spacing: 5) {
                            if let author = agents.first(where: { $0.id == edit.agentId }) {
                                NavigationLink(author.name, value: WorkspaceDestination.agent(author.id)).font(.headline)
                            } else { Text(edit.agentId).font(.headline) }
                            InboxTime(stamp: edit.at)
                            Text(edit.path ?? "MEMORY.md").font(.footnote.monospaced()).textSelection(.enabled)
                            if let reason = edit.reason { Text(reason).font(.footnote).textSelection(.enabled) }
                            if !edit.removed.isEmpty { TranscriptCode(text: edit.removed.map { "− \($0)" }.joined(separator: "\n")).foregroundStyle(.red) }
                            if !edit.added.isEmpty { TranscriptCode(text: edit.added.map { "+ \($0)" }.joined(separator: "\n")).foregroundStyle(.green) }
                        }
                    }
                }
            } else if error == nil { ProgressView("Loading memory…") }
        }
        .navigationTitle("\(project.name) · Memory")
        .toolbar { Button("Refresh", systemImage: "arrow.clockwise") { Task { await refresh() } }.disabled(!active) }
        .refreshable { await refresh() }
        .task(id: OperationsRefresh(route: route, active: active)) {
            guard active else { return }
            while !Task.isCancelled { await refresh(); try? await Task.sleep(for: .seconds(8)) }
        }
        .task(id: hint) { await refresh() }
    }

    private func refresh() async {
        guard active, !Task.isCancelled, !reading else { return }
        reading = true
        defer { reading = false }
        async let refreshTools: Void = tools.refresh { try await api.scriptTools(project: project.id) }
        do {
            let fresh = try await api.projectMemory(project.id)
            try Task.checkCancellation()
            memory = fresh
            error = nil
        } catch is CancellationError {
        } catch { if !Task.isCancelled { self.error = safeMessage(error) } }
        await refreshTools
    }
}

struct CreationView: View {
    let project: Bool
    let ready: Bool
    let busy: Bool
    let statusError: String?
    let submitChat: (String) -> Void
    let submitProject: (ProjectCreation) -> Void
    @State private var name = ""
    @State private var bindings = ""
    @State private var prompt = ""
    @State private var worktrees = false
    @State private var brief = ""

    var body: some View {
        let draft = ProjectCreation(name: name, folders: bindings, prompt: prompt, worktrees: worktrees, brief: brief)
        Form {
            Section { TextField("Name", text: $name, prompt: Text(project ? "Project name" : "Chat")) }
            if project {
                Section("Bound Folders or Repositories") {
                    TextEditor(text: $bindings).frame(minHeight: 75).font(.body.monospaced()).accessibilityLabel("Bound folders or repositories")
                    Text("Enter one existing server path per line. The first is the orchestrator’s working directory. AGENTS.md is read at each root.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                Section("Project Prompt (Optional)") {
                    TextEditor(text: $prompt).frame(minHeight: 90).accessibilityLabel("Project prompt")
                }
                Section {
                    Toggle("Give task agents their own Git worktree by default", isOn: $worktrees)
                }
                Section("First Message (Optional)") {
                    TextEditor(text: $brief).frame(minHeight: 75).accessibilityLabel("First message to the orchestrator")
                    Text("If provided, this message is queued for the new orchestrator when the project is created.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
            if let statusError { Label(statusError, systemImage: "exclamationmark.circle") }
            if busy { ProgressView() }
            Button(project ? "Create Project" : "Create Chat") {
                if project { submitProject(draft) } else { submitChat(name) }
            }.disabled(!ready || (project && !draft.isValid))
        }
        .navigationTitle(project ? "New Project" : "New Chat")
    }
}

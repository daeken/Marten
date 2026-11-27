import SwiftUI
#if os(macOS)
import AppKit
#endif

struct WorkspaceView: View {
    let api: API
    let showServers: () -> Void
    let endPairedSession: (() -> Void)?
    let routeID: UUID
    let networkActive: Bool
    @State private var controls = WorkspaceControls()
    @State private var showClosedChats = false
    @State private var confirmLifecycle = false
    @AppStorage("workspace.collapsedProjects.v1") private var projectPreferences = Data()

    private var collapsedByOrigin: [String: Set<String>] {
        (try? JSONDecoder().decode([String: Set<String>].self, from: projectPreferences)) ?? [:]
    }

    private func projectExpansion(_ id: String) -> Binding<Bool> {
        Binding(get: { collapsedByOrigin[api.endpoint.origin]?.contains(id) != true }, set: { expanded in
            var preferences = collapsedByOrigin
            let origin = api.endpoint.origin
            if expanded { preferences[origin]?.remove(id) }
            else { preferences[origin, default: []].insert(id) }
            if preferences[origin]?.isEmpty == true { preferences.removeValue(forKey: origin) }
            if let encoded = try? JSONEncoder().encode(preferences) { projectPreferences = encoded }
        })
    }
    @State private var eventHint = UUID()
    @State private var activity: [String: LiveActivity] = [:]
    @State private var socketID = UUID()
    @State private var path: [WorkspaceDestination] = []
    @State private var inbox = InboxState()
    @State private var messages = MessagesState()

    private var status: ServerStatus? { controls.status }
    private var statusError: String? { controls.statusError }
    private var controlsReady: Bool { networkActive && controls.ready(routeID) }
    private var target: WorkspaceControls.Target { .init(route: routeID, page: path.last) }

    private var showsMessages: Bool { path.last == .messages }
    private var showsInboxData: Bool {
        switch path.last {
        case .inbox, .agent, .memory: true
        default: false
        }
    }

    private var directory: WorkspaceDirectory {
        WorkspaceDirectory(projects: status?.projects ?? [], agents: status?.agents ?? [],
                           showClosed: showClosedChats, selected: path.first)
    }

    @ViewBuilder private var navigation: some View {
        #if os(macOS)
        NavigationSplitView {
            List(selection: Binding(get: { path.first }, set: selectRoot)) { sidebar }
                .navigationSplitViewColumnWidth(min: 220, ideal: 275, max: 360)
                .navigationTitle("Marten")
        } detail: {
            if let root = path.first {
                NavigationStack(path: Binding(get: { path.first == root ? Array(path.dropFirst()) : [] }, set: {
                    guard path.first == root else { return }
                    path = [root] + $0
                })) {
                    detail(root)
                        .navigationDestination(for: WorkspaceDestination.self, destination: detail)
                }.id(root)
            } else {
                ContentUnavailableView("Select a Conversation", systemImage: "bubble.left.and.bubble.right",
                                       description: Text("Choose a chat or a project from the sidebar."))
            }
        }
        #else
        NavigationStack(path: $path) {
            List { sidebar }
                .navigationTitle("Marten")
                .toolbar {
                    if path.isEmpty {
                        ToolbarItem(placement: .primaryAction) {
                            Button("Servers", systemImage: "server.rack", action: showServers)
                        }
                    }
                }
                .navigationDestination(for: WorkspaceDestination.self, destination: detail)
        }
        #endif
    }

    private func selectRoot(_ destination: WorkspaceDestination?) {
        guard destination != path.first else { return }
        path = destination.map { [$0] } ?? []
    }

    @ViewBuilder private func sidebarLink<LabelContent: View>(_ destination: WorkspaceDestination,
                                                             @ViewBuilder label: () -> LabelContent) -> some View {
        #if os(macOS)
        label().tag(destination)
        #else
        NavigationLink(value: destination, label: label)
        #endif
    }

    @ViewBuilder private var sidebar: some View {
        if let statusError {
            Section("Connection Problem") {
                Text(statusError)
                Button("Retry") { Task { await refresh() } }
            }
        }
        if let paused = status?.paused {
            Section {
                Button(paused ? "Resume All" : "Pause All", systemImage: paused ? "play" : "pause") {
                    lifecycle(.pauseAll(!paused))
                }.disabled(!controlsReady)
                if paused {
                    Text("Everything is paused. Agents finish their current step and hold; nudges and schedules stop.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
        }
        Section("Workspace") {
        if endPairedSession != nil {
            sidebarLink(.devices) { Label("Paired Devices", systemImage: "qrcode") }
        }
        sidebarLink(.providers) {
            Label("Providers & Account", systemImage: "person.crop.circle")
        }
        sidebarLink(.inbox) {
            HStack {
                Label("Inbox", systemImage: "tray")
                Spacer()
                if let waiting = status?.openQuestions, waiting > 0 {
                    Text("\(waiting) waiting").font(.subheadline.bold())
                } else if let count = status?.inbox, count > 0 {
                    Text("\(count)").foregroundStyle(.secondary).accessibilityLabel("\(count) messages")
                }
            }
        }
        sidebarLink(.messages) {
            Label("Messages Between Agents", systemImage: "arrow.left.arrow.right")
        }
        sidebarLink(.processes(nil)) {
            Label("Background Processes", systemImage: "terminal")
        }
        if directory.hasClosedChats {
            Toggle("Show Closed Conversations", isOn: $showClosedChats)
        }
        }
        Section("Projects") {
            sidebarLink(.createProject) { Label("New Project", systemImage: "folder.badge.plus") }
            ForEach(directory.projects) { project in
                DisclosureGroup(isExpanded: projectExpansion(project.id)) {
                    sidebarLink(.memory(project.id)) { Label("Memory", systemImage: "doc.text") }
                    ForEach(directory.members[project.id] ?? []) { agentRow($0) }
                } label: {
                    Label(project.paused == true ? "\(project.name) · Paused" : project.name, systemImage: "folder")
                }
            }
        }
        Section("Standalone") {
            sidebarLink(.createChat) { Label("New Chat", systemImage: "plus.bubble") }
            if status == nil && statusError == nil { ProgressView("Loading conversations…") }
            if status != nil && directory.standalone.isEmpty { Text("No standalone conversations.").foregroundStyle(.secondary) }
            ForEach(directory.standalone) { agentRow($0) }
        }
    }

    private func agentRow(_ agent: Agent) -> some View {
        sidebarLink(.agent(agent.id)) {
            VStack(alignment: .leading, spacing: 2) {
                Label(agent.name, systemImage: agent.kind == "chat" ? "bubble.left" : "person.crop.circle")
                Text("\(agent.kind.capitalized) · \(agentStatus(agent))")
                    .font(.caption).foregroundStyle(.secondary)
                if let count = agent.openTasks, count > 0 { Text("\(count) open tasks").font(.caption) }
                if agent.nudgesStopped == true || agent.abandonedRunspaces?.isEmpty == false {
                    Label("Agent warnings", systemImage: "exclamationmark.triangle").font(.caption)
                }
            }.padding(.vertical, 2)
        }
    }

    @ViewBuilder private func detail(_ destination: WorkspaceDestination) -> some View {
        switch destination {
        case .inbox:
            InboxView(inbox: inbox, projects: status?.projects ?? [], agents: status?.agents ?? [],
                      active: networkActive, refresh: refreshInbox, answer: submitAnswer)
        case .messages:
            MessagesView(state: messages, projects: status?.projects ?? [], agents: status?.agents ?? [],
                         active: networkActive, refresh: refreshMessages)
        case .devices:
            if let endPairedSession {
                DeviceManagementView(api: api, active: networkActive && path.last == .devices,
                                     route: routeID, endSession: endPairedSession).id(routeID)
            }
        case .providers:
            ProvidersView(status: status, active: networkActive && path.last == .providers,
                          ready: controlsReady && path.last == .providers, busy: controls.busy, route: routeID,
                          statusError: statusError, refresh: refresh, logout: {
                guard networkActive, path.last == .providers else { return }
                controls.signOutProvider(using: api, route: routeID)
            }, update: { provider, key in
                guard networkActive, path.last == .providers else { return }
                controls.updateProvider(provider, key: key, using: api, route: routeID)
            }).id(routeID)
        case .memory(let id):
            if let project = status?.projects.first(where: { $0.id == id }) {
                ProjectMemoryView(api: api, project: project, agents: status?.agents ?? [], route: routeID,
                                  active: networkActive && path.last == destination, hint: eventHint,
                                  controlsReady: controlsReady, pause: {
                    if let paused = project.paused, path.last == destination { lifecycle(.pauseProject(id, !paused)) }
                }, inbox: inbox, answer: submitAnswer).id(routeID)
            } else { ContentUnavailableView("Project Unavailable", systemImage: "folder") }
        case .createChat, .createProject:
            CreationView(project: destination == .createProject, ready: controlsReady && path.last == destination, busy: controls.busy,
                         statusError: statusError, submitChat: { name in
                guard controlsReady, path.last == destination else { return }
                controls.createChat(name, using: api, route: routeID) { finishCreation($0, from: destination) }
            }, submitProject: { draft in
                guard controlsReady, path.last == destination else { return }
                controls.create(draft, using: api, route: routeID) { finishCreation($0, from: destination) }
            })
        case .administration(let page):
            AdministrationView(page: page, api: api, route: routeID, active: networkActive && path.last == destination,
                               hint: eventHint, agents: status?.agents ?? []).id(routeID)
        case .settings(let id):
            if let agent = status?.agents.first(where: { $0.id == id }) {
                AgentSettingsView(api: api, agent: agent, config: status?.config, agents: status?.agents ?? [],
                                  route: routeID, active: networkActive && path.last == destination,
                                  ready: controlsReady) { draft, original in
                    guard controlsReady, path.last == destination else { return }
                    controls.saveSettings(draft, original: original, agent: id, using: api, route: routeID)
                }.id(routeID)
            } else { ContentUnavailableView("Agent Unavailable", systemImage: "gearshape") }
        case .notebook(let id):
            if let agent = status?.agents.first(where: { $0.id == id }) {
                NotebookView(api: api, agent: agent, route: routeID, active: networkActive && path.last == destination, hint: eventHint)
                    .id(id)
            } else { ContentUnavailableView("Agent Unavailable", systemImage: "book.closed") }
        case .operations(let id, let task):
            if let agent = status?.agents.first(where: { $0.id == id }) {
                AgentOperationsView(api: api, agent: agent, agents: status?.agents ?? [], targetTask: task,
                                    route: routeID, active: networkActive && path.last == destination, hint: eventHint)
                    .id(routeID)
            } else { ContentUnavailableView("Agent Unavailable", systemImage: "checklist") }
        case .processes(let id):
            ProcessesView(api: api, agentID: id, route: routeID, active: networkActive && path.last == destination, hint: eventHint)
                .id(routeID)
        case .processLog(let key):
            ProcessLogView(api: api, key: key, route: routeID, active: networkActive && path.last == destination)
                .id(routeID)
        case .agent(let id):
            if let agent = status?.agents.first(where: { $0.id == id }) {
                ConversationView(api: api, agent: agent, refreshStatus: refresh, routeID: routeID,
                                 networkActive: networkActive && path.last == destination, eventHint: eventHint,
                                 inbox: inbox, answer: submitAnswer,
                                 activity: networkActive ? activity[id] : nil,
                                 controlsReady: controlsReady, lifecycle: { action in
                    guard path.last == destination else { return }
                    lifecycle(action, name: agent.name)
                }).id(id)
            } else {
                ContentUnavailableView("Agent Unavailable", systemImage: "bubble.left")
            }
        }
    }

    var body: some View {
        navigation
        #if os(macOS)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Servers", systemImage: "server.rack", action: showServers)
            }
        }
        #endif
        .refreshable { await refresh() }
        .task(id: RefreshRoute(id: routeID, active: networkActive)) {
            guard networkActive else { return }
            while !Task.isCancelled {
                await refresh()
                try? await Task.sleep(for: .seconds(8))
            }
        }
        .task(id: RefreshRoute(id: routeID, active: networkActive)) {
            let id = UUID()
            socketID = id
            activity = [:]
            guard networkActive else { return }
            await listenForEvents(id: id)
        }
        .task(id: RefreshRoute(id: routeID, active: networkActive && showsInboxData)) {
            guard networkActive, showsInboxData else { return }
            while !Task.isCancelled {
                await refreshInbox()
                try? await Task.sleep(for: .seconds(8))
            }
        }
        .task(id: eventHint) {
            if showsInboxData { await refreshInbox() }
            if showsMessages { await refreshMessages() }
        }
        .task(id: MessagesRefresh(route: routeID, active: networkActive && showsMessages, project: messages.projectID)) {
            guard networkActive, showsMessages else { return }
            while !Task.isCancelled {
                await refreshMessages()
                try? await Task.sleep(for: .seconds(8))
            }
        }
        .alert(controls.pending?.action.title ?? "Confirm Action", isPresented: $confirmLifecycle, presenting: controls.pending) { confirmation in
            Button(confirmation.action.title, role: .destructive) {
                guard controlsReady else { controls.cancel(); return }
                controls.apply(confirmation.action, confirmation: confirmation.id, target: target, using: api)
            }
            Button("Cancel", role: .cancel) { controls.cancel() }
        } message: { confirmation in
            Text(confirmation.action.confirmation(for: confirmation.name))
        }
        .safeAreaInset(edge: .bottom) {
            if let notice = controls.notice {
                HStack {
                    Label(notice, systemImage: "exclamationmark.circle").font(.footnote)
                    Spacer()
                    Button("Dismiss", systemImage: "xmark", action: controls.dismissNotice).labelStyle(.iconOnly)
                }.padding(8).background(.regularMaterial)
            }
        }
        .onChange(of: target) { cancelLifecycle() }
        .onChange(of: networkActive) { if !networkActive { cancelLifecycle() } }
        .onDisappear { cancelLifecycle() }
        .onChange(of: status?.projects.map(\.id)) { _, ids in
            if let ids, let project = messages.projectID, !ids.contains(project) { messages.select(nil) }
        }
    }

    @MainActor private func submitAnswer(_ message: InboxMessage, choice: Int?) {
        guard networkActive else { return }
        Task {
            await inbox.answer(message.id, choice: choice, using: api, confirmed: message)
            await refresh()
        }
    }

    @MainActor private func refreshInbox() async {
        guard networkActive else { return }
        await inbox.refresh(api)
    }

    @MainActor private func refreshMessages() async {
        guard networkActive else { return }
        await messages.refresh(api)
    }

    @MainActor private func consumeActivity(_ stream: AsyncStream<[String: LiveActivity]>?, id: UUID) async {
        guard let stream else { return }
        for await update in stream {
            guard !Task.isCancelled, socketID == id else { return }
            activity = update
        }
        if socketID == id { activity = [:] }
    }

    @MainActor private func listenForEvents(id: UUID) async {
        var delay = 1
        let clock = ContinuousClock()
        while !Task.isCancelled {
            let start = clock.now
            do {
                guard let stream = try api.events() else { return }
                defer { stream.cancel() }
                try await withTaskCancellationHandler {
                    async let live: Void = consumeActivity(stream.activity, id: id)
                    defer { stream.cancel() }
                    for try await _ in stream.hints {
                        try Task.checkCancellation()
                        eventHint = UUID()
                        await refresh()
                        try await Task.sleep(for: .seconds(1))
                    }
                    stream.cancel()
                    await live
                } onCancel: { stream.cancel() }
            } catch {
                // Polling remains on the same route; only the socket is retried.
            }
            if clock.now - start >= .seconds(30) { delay = 1 }
            do { try await Task.sleep(for: .seconds(delay)) }
            catch { return }
            delay = min(delay * 2, 16)
        }
    }

    @MainActor private func refresh() async {
        guard networkActive else { return }
        await controls.refresh(using: api, route: routeID)
        if controls.authorizationRejected { endPairedSession?() }
    }

    private func lifecycle(_ action: LifecycleAction, name: String = "") {
        guard controlsReady else { return }
        if action.destructive {
            controls.stage(action, name: name, target: target)
            confirmLifecycle = controls.pending != nil
        } else { controls.apply(action, target: target, using: api) }
    }

    private func cancelLifecycle() {
        confirmLifecycle = false
        controls.cancel()
    }

    private func finishCreation(_ id: String?, from destination: WorkspaceDestination) {
        guard path.last == destination, controls.loadedRoute == routeID else { return }
        if let id, status?.agents.contains(where: { $0.id == id }) == true {
            path = [.agent(id)]
        } else {
            path = []
            controls.showNotice("Created successfully. Refresh the agent list to open the new conversation.")
        }
    }
}

enum WorkspaceDestination: Hashable {
    case inbox, messages, providers, devices, agent(String)
    case createChat, createProject, memory(String)
    case settings(String)
    case administration(AdministrationPage)
    case notebook(String), operations(String, task: Int?), processes(String?), processLog(AgentProcess.Key)
}

private struct MessagesRefresh: Equatable {
    let route: UUID
    let active: Bool
    let project: String?
}

private struct RefreshRoute: Equatable {
    let id: UUID
    let active: Bool
}

struct WorkspaceDirectory {
    let projects: [Project]
    let standalone: [Agent]
    let members: [String: [Agent]]
    let hasClosedChats: Bool

    init(projects: [Project], agents: [Agent], showClosed: Bool, selected: WorkspaceDestination?) {
        self.projects = projects.sorted { Self.inOrder($0.name, $0.id, $1.name, $1.id) }
        let projectIDs = Set(projects.map(\.id))
        hasClosedChats = agents.contains { $0.closed == true }
        let visible = agents.filter {
            showClosed || $0.closed != true || selected == .agent($0.id)
        }.sorted { Self.inOrder($0.name, $0.id, $1.name, $1.id) }
        var members: [String: [Agent]] = [:]
        var standalone: [Agent] = []
        for agent in visible {
            if let id = agent.projectId, projectIDs.contains(id) { members[id, default: []].append(agent) }
            else { standalone.append(agent) }
        }
        for project in projects {
            guard let id = project.orchestratorId, var rows = members[project.id],
                  let index = rows.firstIndex(where: { $0.id == id }), index > 0 else { continue }
            let orchestrator = rows.remove(at: index)
            rows.insert(orchestrator, at: 0)
            members[project.id] = rows
        }
        self.members = members
        self.standalone = standalone
    }

    private static func inOrder(_ lhs: String, _ lhsID: String, _ rhs: String, _ rhsID: String) -> Bool {
        let order = lhs.localizedStandardCompare(rhs)
        return order == .orderedSame ? lhsID < rhsID : order == .orderedAscending
    }
}

private func agentStatus(_ agent: Agent) -> String {
    if agent.closed == true { return "Closed" }
    return agent.pausedEffective ? "Paused" : agent.status.replacingOccurrences(of: "_", with: " ").capitalized
}

// The route task owns the automatic GET. An event wakes the loop, not a new
// SwiftUI task that would cancel the in-flight response when its id changes.
@MainActor final class TranscriptRefreshLoop {
    private var current: (id: UUID, wake: AsyncStream<Void>.Continuation)?

    func hint() { current?.wake.yield(()) }

    func run(waitForPoll: @escaping @Sendable () async throws -> Void = { try await Task.sleep(for: .seconds(4)) },
             refresh: @MainActor () async -> Void) async {
        guard !Task.isCancelled else { return }
        let id = UUID()
        let (signals, wake) = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        current = (id, wake)
        defer {
            wake.finish()
            if current?.id == id { current = nil }
        }
        while !Task.isCancelled && current?.id == id {
            await refresh()
            guard !Task.isCancelled, current?.id == id else { return }
            guard await Self.waitForHintOrPoll(signals, wake: wake, waitForPoll: waitForPoll) else { return }
        }
    }

    private nonisolated static func waitForHintOrPoll(_ signals: AsyncStream<Void>,
                                                     wake: AsyncStream<Void>.Continuation,
                                                     waitForPoll: @escaping @Sendable () async throws -> Void) async -> Bool {
        await withTaskGroup(of: Void.self) { group in
            group.addTask {
                do {
                    try await waitForPoll()
                    try Task.checkCancellation()
                    wake.yield(())
                } catch { }
            }
            defer { group.cancelAll() }
            var iterator = signals.makeAsyncIterator()
            return await iterator.next() != nil
        }
    }
}

private struct ConversationView: View {
    let api: API
    let agent: Agent
    let refreshStatus: () async -> Void
    let routeID: UUID
    let networkActive: Bool
    let eventHint: UUID
    @State private var timeline = TimelinePages()
    let inbox: InboxState
    let answer: (InboxMessage, Int?) -> Void
    let activity: LiveActivity?
    let controlsReady: Bool
    let lifecycle: (LifecycleAction) -> Void
    @State private var refreshLoop = TranscriptRefreshLoop()
    @State private var latestRequest: (id: UUID, route: UUID)?
    @State private var pageRequest: UUID?
    @State private var pageError: String?
    @State private var mutating = false
    @State private var draft = ""
    #if os(iOS)
    @FocusState private var composerFocused: Bool
    #endif
    @State private var mode: SendMode = .queued
    @State private var refreshError: String?
    @State private var actionError: String?
    @State private var showInterrupt = false
    @State private var readingWindow = UUID()
    @State private var follow = LiveFollow()

    private var rows: [TimelineRow] { timeline.rows(questions: inbox.questions(for: agent.id)) }
    private var live: LiveActivity? { timeline.archive == nil ? activity : nil }

    var body: some View {
        Group {
            if timeline.page == nil { initialTranscriptState }
            else { transcript }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) { composer }
        .navigationTitle(agent.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu("Agent Actions", systemImage: "ellipsis.circle") {
                    NavigationLink(value: WorkspaceDestination.settings(agent.id)) {
                        Label("Settings & Diagnostics", systemImage: "gearshape")
                    }
                    NavigationLink(value: WorkspaceDestination.notebook(agent.id)) {
                        Label("PowerShell Notebook", systemImage: "book.closed")
                    }
                    NavigationLink(value: WorkspaceDestination.operations(agent.id, task: nil)) {
                        Label("Tasks & Schedules", systemImage: "checklist")
                    }
                    NavigationLink(value: WorkspaceDestination.processes(agent.id)) {
                        Label("Background Processes", systemImage: "terminal")
                    }
                    Button("Refresh", systemImage: "arrow.clockwise") {
                        Task { await refresh(); await refreshStatus() }
                    }
                    Button(agent.paused ? "Resume Agent" : "Pause Agent", systemImage: agent.paused ? "play" : "pause") {
                        lifecycle(.pauseAgent(agent.id, !agent.paused))
                    }.disabled(!networkActive || !controlsReady || mutating)
                    Button(agent.closed == true ? "Reopen Agent" : "Close Agent…", systemImage: agent.closed == true ? "arrow.uturn.backward" : "archivebox") {
                        lifecycle(.close(agent.id, agent.closed != true))
                    }.disabled(!networkActive || !controlsReady || mutating)
                    Button("Start Fresh Context…", systemImage: "arrow.counterclockwise", role: .destructive) {
                        lifecycle(.freshContext(agent.id))
                    }.disabled(!networkActive || !controlsReady || mutating || !agent.canStartFreshContext)
                    Button("Interrupt…", systemImage: "stop.circle", role: .destructive) { showInterrupt = true }
                        .disabled(!networkActive || mutating)
                }
            }
        }
        .confirmationDialog("Interrupt the current agent turn?", isPresented: $showInterrupt, titleVisibility: .visible) {
            Button("Interrupt", role: .destructive) {
                Task { await perform { try await api.interrupt(agent.id) } }
            }
        }
        .task(id: RefreshRoute(id: routeID, active: networkActive)) {
            guard networkActive else { return }
            await refreshLoop.run { await refresh() }
        }
        .onChange(of: eventHint) { if networkActive { refreshLoop.hint() } }
        .onChange(of: routeID) {
            if latestRequest?.route != routeID { latestRequest = nil }
            pageRequest = nil
            showInterrupt = false
        }
        .onChange(of: networkActive) {
            if !networkActive { latestRequest = nil; pageRequest = nil; showInterrupt = false }
        }
    }

    @ViewBuilder private var initialTranscriptState: some View {
        if let refreshError {
            ContentUnavailableView {
                Label("Transcript Unavailable", systemImage: "exclamationmark.circle")
            } description: {
                Text(refreshError)
            } actions: {
                Button("Retry Transcript") { Task { await refresh() } }.disabled(!networkActive || latestRequest?.route == routeID)
            }
        } else {
            ProgressView("Loading transcript…").frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var transcript: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    LazyVStack(alignment: .leading, spacing: 12) {
                        #if os(macOS)
                        NavigationLink(value: WorkspaceDestination.operations(agent.id, task: nil)) {
                            Label("Tasks & Schedules", systemImage: "checklist")
                        }.buttonStyle(.borderless)
                        NavigationLink(value: WorkspaceDestination.processes(agent.id)) {
                            Label("Background Processes", systemImage: "terminal")
                        }.buttonStyle(.borderless)
                        NavigationLink(value: WorkspaceDestination.notebook(agent.id)) {
                            Label("PowerShell Notebook", systemImage: "book.closed")
                        }.buttonStyle(.borderless)
                        Text(agentStatus(agent)).font(.footnote).foregroundStyle(.secondary)
                        if let directory = agent.worktree ?? agent.workingDirectory {
                            Text(directory).font(.footnote.monospaced()).foregroundStyle(.secondary).textSelection(.enabled)
                        }
                        #endif
                        if agent.pausedEffective && !agent.paused {
                            Text("Paused by the server or project.").font(.footnote).foregroundStyle(.secondary)
                        }
                        if let refreshError {
                            Label(refreshError, systemImage: "exclamationmark.circle").font(.footnote)
                        }
                        if timeline.page != nil {
                            if rows.isEmpty { Text("No transcript entries yet.").foregroundStyle(.secondary) }
                            ForEach(rows) { row in
                                Group {
                                    switch row.content {
                                    case .question(let question):
                                        QuestionCard(message: question, inbox: inbox, active: networkActive, answer: answer)
                                    case .entry(let entry, let result):
                                        EntryRow(entry: entry, result: result, api: api, agent: agent.id, active: networkActive, route: routeID)
                                    }
                                }.id(row.id)
                            }
                        }
                        if let live, timeline.latest != nil {
                            if let tool = live.tool { Label("Running \(tool)…", systemImage: "hammer").font(.footnote) }
                            if !live.text.isEmpty { RichTranscript(text: live.text) }
                            else if let thinking = live.thinking { ProgressView(thinking).font(.footnote) }
                        }
                    }
                    Color.clear.frame(height: 1).id(TimelineRow.ID.end).accessibilityHidden(true)
                        .onGeometryChange(for: CGFloat.self) { geometry in
                            guard let viewport = geometry.bounds(of: .scrollView(axis: .vertical)) else { return .infinity }
                            return -viewport.maxY
                        } action: { distance in follow.measure(distance: distance) }
                        .onDisappear { follow.measure(distance: .infinity) }
                }
                .padding()
                .frame(maxWidth: 860)
                .frame(maxWidth: .infinity)
                #if os(iOS)
                .background { keyboardDismissSurface }
                #endif
            }
            #if os(iOS)
            .scrollDismissesKeyboard(.interactively)
            .background { keyboardDismissSurface }
            #endif
            .refreshable { await refresh() }
            .modifier(TranscriptAnchor(archived: timeline.archive != nil))
            .id(readingWindow)
            .onChange(of: rows.map(\.id)) { followLatest(using: proxy) }
            .onChange(of: live) { followLatest(using: proxy) }
        }
    }

    #if os(iOS)
    // Behind the transcript, not an ancestor gesture: rows keep their links, buttons,
    // and text selection. This covers its gutters and unused viewport space.
    private var keyboardDismissSurface: some View {
        Color.clear.contentShape(Rectangle())
            .onTapGesture { composerFocused = false }
            .accessibilityHidden(true)
    }
    #endif

    @ViewBuilder private var pagingControls: some View {
        if timeline.page?.hasOlder == true || timeline.archive != nil || pageError != nil {
            HStack {
                Button("Older") { Task { await move(.older) } }
                    .disabled(!networkActive || pageRequest != nil || timeline.target(.older) == nil)
                    .accessibilityLabel("Older transcript page")
                if timeline.archive != nil {
                    Button("Newer") { Task { await move(.newer) } }
                        .disabled(!networkActive || pageRequest != nil)
                        .accessibilityLabel("Newer transcript page")
                }
                Text(pageRequest != nil ? "Loading page…" : timeline.archive != nil ? "Older entries" : "Latest entries")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            if let pageError { Label(pageError, systemImage: "exclamationmark.circle").font(.footnote) }
        }
    }

    private var composer: some View {
        VStack(alignment: .leading, spacing: 4) {
            pagingControls
            if let actionError {
                HStack {
                    Text(actionError).font(.footnote)
                    Spacer(minLength: 4)
                    Button("Dismiss error", systemImage: "xmark") { self.actionError = nil }.labelStyle(.iconOnly)
                }
                .foregroundStyle(.red)
            }
            HStack(alignment: .bottom, spacing: 8) {
                #if os(macOS)
                MessageComposer(draft: $draft) { Task { await send() } }
                    .background(.background, in: RoundedRectangle(cornerRadius: 6))
                    .overlay { RoundedRectangle(cornerRadius: 6).strokeBorder(.quaternary).allowsHitTesting(false) }
                #else
                TextField("Message", text: $draft, axis: .vertical)
                    .focused($composerFocused)
                    .lineLimit(1...5)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityIdentifier("messageDraft")
                #endif
                Button {
                    Task { await send() }
                } label: {
                    Image(systemName: "arrow.up.circle.fill").font(.system(size: 29))
                        .frame(minWidth: 44, minHeight: 44)
                }
                .buttonStyle(.plain)
                .foregroundStyle(Color.accentColor)
                .accessibilityLabel("Send Message")
                .disabled(!networkActive || mutating || draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            HStack {
                Menu {
                    Picker("Delivery Mode", selection: $mode) {
                        Text("Queued").tag(SendMode.queued)
                        Text("Immediate").tag(SendMode.immediate)
                    }
                } label: {
                    Label(mode == .queued ? "Queued" : "Immediate", systemImage: "chevron.up.chevron.down")
                        .font(.footnote).padding(.vertical, 8)
                }
                .accessibilityLabel("Delivery Mode: \(mode.rawValue)")
                .disabled(mutating)
                Spacer()
                Button("Jump to Latest", systemImage: "arrow.down.to.line") {
                    returnToLatest()
                }
                .font(.footnote)
                .accessibilityLabel("Jump to Latest")
                .disabled(timeline.latest == nil)
            }
        }
        .padding(.horizontal)
        .padding(.top, 8)
        .background(.bar)
    }

    private func followLatest(using proxy: ScrollViewProxy) {
        guard follow.shouldFollow(archived: timeline.archive != nil) else { return }
        proxy.scrollTo(TimelineRow.ID.end, anchor: .bottom)
    }

    private func returnToLatest() {
        pageRequest = nil
        pageError = nil
        timeline.returnToLatest()
        follow.resume()
        readingWindow = UUID()
    }

    @MainActor private func refresh() async {
        guard networkActive, !Task.isCancelled, latestRequest?.route != routeID else { return }
        let id = UUID()
        latestRequest = (id, routeID)
        defer { if latestRequest?.id == id { latestRequest = nil } }
        do {
            let page = try await api.transcript(agent.id)
            guard !Task.isCancelled, latestRequest?.id == id else { return }
            timeline.updateLatest(page)
            refreshError = nil
        } catch is CancellationError {
        } catch {
            guard !Task.isCancelled, latestRequest?.id == id else { return }
            refreshError = safeMessage(error)
        }
    }

    @MainActor private func move(_ direction: TimelinePages.Move) async {
        guard networkActive, !Task.isCancelled, pageRequest == nil, let target = timeline.target(direction) else { return }
        guard let cursor = target.last else { returnToLatest(); return }
        let id = UUID()
        pageRequest = id
        pageError = nil
        defer { if pageRequest == id { pageRequest = nil } }
        do {
            let page = try await api.transcript(agent.id, before: cursor)
            guard pageRequest == id, !Task.isCancelled else { return }
            timeline.show(page, at: target)
            readingWindow = UUID()
        } catch is CancellationError {
        } catch {
            if pageRequest == id { pageError = safeMessage(error) }
        }
    }

    @MainActor @discardableResult private func perform(_ work: () async throws -> Void) async -> Bool {
        guard networkActive, !mutating else { return false }
        mutating = true
        actionError = nil
        defer { mutating = false }
        do { try await work() }
        catch is CancellationError { return false }
        catch { actionError = safeMessage(error); return false }
        await refresh()
        await refreshStatus()
        return true
    }

    @MainActor private func send() async {
        let message = draft
        guard !message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if await perform({
            try await api.send(agent.id, text: message, mode: mode)
            if draft == message { draft = "" }
        }) { returnToLatest() }
    }
}

// Handle Return at the focused text control, before platform text bindings consume it.
#if os(macOS)
private struct MessageComposer: NSViewRepresentable {
    @Binding var draft: String
    var send: () -> Void

    final class Editor: NSTextView, NSTextViewDelegate {
        var change: (String) -> Void = { _ in }
        var send: () -> Void = {}

        override func keyDown(with event: NSEvent) {
            // Enter on the numeric keypad follows Return. Leave IME confirmation and
            // system shortcuts to the responder rather than submitting marked text.
            let modifiers = event.modifierFlags.intersection([.shift, .command, .control, .option])
            guard (event.keyCode == 36 || event.keyCode == 76), !hasMarkedText(),
                  modifiers.isEmpty || modifiers == .shift else {
                super.keyDown(with: event)
                return
            }
            if modifiers == .shift { insertNewline(nil) }
            else if !event.isARepeat { send() }
        }

        func textDidChange(_ notification: Notification) { change(string) }
    }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        let editor = Editor(frame: .zero)
        editor.delegate = editor
        editor.isRichText = false
        editor.isAutomaticQuoteSubstitutionEnabled = false
        editor.isAutomaticDashSubstitutionEnabled = false
        editor.drawsBackground = false
        editor.font = .systemFont(ofSize: NSFont.systemFontSize)
        editor.textContainerInset = NSSize(width: 4, height: 5)
        editor.isVerticallyResizable = true
        editor.autoresizingMask = [.width]
        editor.textContainer?.widthTracksTextView = true
        editor.setAccessibilityLabel("Message")
        editor.setAccessibilityIdentifier("messageDraft")
        scroll.documentView = editor
        scroll.drawsBackground = false
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let editor = scroll.documentView as? Editor else { return }
        editor.change = { draft = $0 }
        editor.send = send
        if editor.string != draft, !editor.hasMarkedText() { editor.string = draft }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView scroll: NSScrollView, context: Context) -> CGSize? {
        guard let width = proposal.width, let editor = scroll.documentView as? Editor,
              let font = editor.font else { return nil }
        let height = (draft + " ").boundingRect(with: NSSize(width: max(1, width - 18), height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading], attributes: [.font: font]).height
        return CGSize(width: width, height: min(90, max(34, ceil(height) + 10)))
    }
}
#endif

private struct TranscriptAnchor: ViewModifier {
    let archived: Bool

    func body(content: Content) -> some View {
        if #available(macOS 15, iOS 18, *) {
            content.defaultScrollAnchor(archived ? .top : .bottom, for: .initialOffset)
        } else {
            content.defaultScrollAnchor(archived ? .top : .bottom)
        }
    }
}

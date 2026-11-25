import SwiftUI

struct WorkspaceView: View {
    let api: API
    let showServers: () -> Void
    let routeID: UUID
    let networkActive: Bool
    @State private var status: ServerStatus?
    @State private var statusError: String?
    @State private var checking = false
    @State private var scope: AgentScope = .all
    @State private var eventHint = UUID()
    @State private var activity: [String: LiveActivity] = [:]
    @State private var socketID = UUID()
    @State private var path: [WorkspaceDestination] = []
    @State private var inbox = InboxState()
    @State private var messages = MessagesState()

    private var showsMessages: Bool { path.last == .messages }
    private var showsInboxData: Bool {
        switch path.last {
        case .inbox, .agent: true
        default: false
        }
    }

    private var agents: [Agent] {
        (status?.agents ?? []).filter { agent in
            guard agent.closed != true else { return false }
            switch scope {
            case .all: return true
            case .standalone: return agent.projectId == nil
            case .project(let id): return agent.projectId == id
            }
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    var body: some View {
        NavigationStack(path: $path) {
            List {
                Section {
                    Picker("Project", selection: $scope) {
                        Text("All Projects & Agents").tag(AgentScope.all)
                        Text("Standalone Agents").tag(AgentScope.standalone)
                        ForEach(status?.projects ?? []) { project in
                            Text(project.name).tag(AgentScope.project(project.id))
                        }
                    }
                } footer: {
                    Text(api.endpoint.origin).font(.footnote)
                }
                if let statusError {
                    Section("Connection Problem") {
                        Text(statusError)
                        Button("Retry") { Task { await refresh() } }
                    }
                }
                NavigationLink(value: WorkspaceDestination.inbox) {
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
                NavigationLink(value: WorkspaceDestination.messages) {
                    Label("Messages Between Agents", systemImage: "arrow.left.arrow.right")
                }
                NavigationLink(value: WorkspaceDestination.processes(nil)) {
                    Label("Background Processes", systemImage: "terminal")
                }
                Section("Agents") {
                    if status == nil && statusError == nil { ProgressView("Loading agents…") }
                    if status != nil && agents.isEmpty { Text("No active agents here.").foregroundStyle(.secondary) }
                    ForEach(agents) { agent in
                        NavigationLink(value: WorkspaceDestination.agent(agent.id)) {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(agent.name).font(.headline)
                                Text("\(agent.kind.capitalized) · \(agentStatus(agent))")
                                    .font(.subheadline).foregroundStyle(.secondary)
                            }
                            .padding(.vertical, 4)
                        }
                    }
                }
            }
            .navigationTitle("Marten")
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button("Servers", systemImage: "server.rack", action: showServers)
                }
            }
            .refreshable { await refresh() }
            .navigationDestination(for: WorkspaceDestination.self) { destination in
                switch destination {
                case .inbox:
                    InboxView(inbox: inbox, projects: status?.projects ?? [], agents: status?.agents ?? [],
                              active: networkActive, refresh: refreshInbox, answer: submitAnswer)
                case .messages:
                    MessagesView(state: messages, projects: status?.projects ?? [], agents: status?.agents ?? [],
                                 active: networkActive, refresh: refreshMessages)
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
                                         activity: networkActive ? activity[id] : nil).id(id)
                    } else {
                        ContentUnavailableView("Agent Unavailable", systemImage: "bubble.left")
                    }
                }
            }
        }
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
        .onChange(of: status?.projects.map(\.id)) { _, ids in
            if let ids, let project = messages.projectID, !ids.contains(project) { messages.select(nil) }
        }
    }

    @MainActor private func submitAnswer(_ id: String, choice: Int?) {
        guard networkActive else { return }
        Task {
            await inbox.answer(id, choice: choice, using: api)
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
        guard networkActive, !Task.isCancelled, !checking else { return }
        checking = true
        defer { checking = false }
        do {
            let result = try await api.status()
            try Task.checkCancellation()
            status = result
            statusError = nil
            if case .project(let id) = scope, !result.projects.contains(where: { $0.id == id }) { scope = .all }
        } catch is CancellationError {
        } catch {
            statusError = safeMessage(error)
        }
    }
}

enum WorkspaceDestination: Hashable {
    case inbox, messages, agent(String)
    case operations(String, task: Int?), processes(String?), processLog(AgentProcess.Key)
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

private enum AgentScope: Hashable {
    case all, standalone, project(String)
}

private func agentStatus(_ agent: Agent) -> String {
    agent.pausedEffective ? "Paused" : agent.status.replacingOccurrences(of: "_", with: " ").capitalized
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
    let answer: (String, Int?) -> Void
    let activity: LiveActivity?
    @State private var loaded = false
    @State private var checking = false
    @State private var pageRequest: UUID?
    @State private var pageError: String?
    @State private var mutating = false
    @State private var draft = ""
    @State private var mode: SendMode = .queued
    @State private var refreshError: String?
    @State private var actionError: String?
    @State private var showInterrupt = false
    @State private var scrollTarget: TimelineRow.ID = .end
    @State private var scrollGeneration = 0
    @State private var follow = LiveFollow()

    private var rows: [TimelineRow] { timeline.rows(questions: inbox.questions(for: agent.id)) }
    private var live: LiveActivity? { timeline.archive == nil ? activity : nil }

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 12) {
                    NavigationLink(value: WorkspaceDestination.operations(agent.id, task: nil)) {
                        Label("Tasks, Schedules & Processes", systemImage: "checklist")
                    }.buttonStyle(.borderless)
                    Text(agentStatus(agent)).font(.footnote).foregroundStyle(.secondary)
                    if agent.pausedEffective && !agent.paused {
                        Text("Paused by the server or project.").font(.footnote).foregroundStyle(.secondary)
                    }
                    if let refreshError {
                        Label(refreshError, systemImage: "exclamationmark.circle").font(.footnote)
                    } else if !loaded {
                        ProgressView("Loading transcript…")
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
                    if let live {
                        if let tool = live.tool { Label("Running \(tool)…", systemImage: "hammer").font(.footnote) }
                        if !live.text.isEmpty { RichTranscript(text: live.text) }
                        else if let thinking = live.thinking { ProgressView(thinking).font(.footnote) }
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
            }
            #if os(iOS)
            .scrollDismissesKeyboard(.interactively)
            #endif
            .refreshable { await refresh() }
            .onChange(of: scrollGeneration) {
                proxy.scrollTo(scrollTarget, anchor: scrollTarget == .end ? .bottom : .top)
            }
            .onChange(of: rows.map(\.id)) { followLatest(using: proxy) }
            .onChange(of: live) { followLatest(using: proxy) }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) { composer }
        .navigationTitle(agent.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu("Agent Actions", systemImage: "ellipsis.circle") {
                    NavigationLink(value: WorkspaceDestination.operations(agent.id, task: nil)) {
                        Label("Tasks, Schedules & Processes", systemImage: "checklist")
                    }
                    Button("Refresh", systemImage: "arrow.clockwise") {
                        Task { await refresh(); await refreshStatus() }
                    }
                    Button(agent.paused ? "Resume Agent" : "Pause Agent", systemImage: agent.paused ? "play" : "pause") {
                        Task { await perform { try await api.pause(agent.id, paused: !agent.paused) } }
                    }.disabled(!networkActive || mutating)
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
            while !Task.isCancelled {
                await refresh()
                try? await Task.sleep(for: .seconds(4))
            }
        }
        .task(id: eventHint) { await refresh() }
        .onChange(of: routeID) { pageRequest = nil }
        .onChange(of: networkActive) { if !networkActive { pageRequest = nil } }
    }

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
                Text(pageRequest != nil ? "Loading page…" : timeline.archive != nil ? "Archived page" : "Latest entries")
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
                TextField("Message", text: $draft, axis: .vertical)
                    .lineLimit(1...5)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityIdentifier("messageDraft")
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
                Button("Latest", systemImage: "arrow.down.to.line") {
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

    private func jump(to target: TimelineRow.ID = .end) {
        scrollTarget = target
        scrollGeneration += 1
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
        jump()
    }

    @MainActor private func refresh() async {
        guard networkActive, !Task.isCancelled, !checking else { return }
        checking = true
        defer { checking = false }
        do {
            let page = try await api.transcript(agent.id)
            try Task.checkCancellation()
            timeline.updateLatest(page)
            if !loaded { jump() }
            loaded = true
            refreshError = nil
        } catch is CancellationError {
        } catch {
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
            jump()
        } catch is CancellationError {
        } catch {
            if pageRequest == id { pageError = safeMessage(error) }
        }
    }

    @MainActor private func perform(_ work: () async throws -> Void) async {
        guard networkActive, !mutating else { return }
        mutating = true
        actionError = nil
        defer { mutating = false }
        do { try await work() }
        catch is CancellationError { return }
        catch { actionError = safeMessage(error); return }
        await refresh()
        await refreshStatus()
    }

    @MainActor private func send() async {
        let message = draft
        guard !message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        await perform {
            try await api.send(agent.id, text: message, mode: mode)
            if draft == message { draft = "" }
        }
        if actionError == nil { returnToLatest() }
    }
}

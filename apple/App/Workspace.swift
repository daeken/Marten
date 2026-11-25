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
        NavigationStack {
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
                Section("Agents") {
                    if status == nil && statusError == nil { ProgressView("Loading agents…") }
                    if status != nil && agents.isEmpty { Text("No active agents here.").foregroundStyle(.secondary) }
                    ForEach(agents) { agent in
                        NavigationLink(value: agent.id) {
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
            .navigationDestination(for: String.self) { id in
                if let agent = status?.agents.first(where: { $0.id == id }), agent.closed != true {
                    ConversationView(api: api, agent: agent, refreshStatus: refresh, routeID: routeID,
                                     networkActive: networkActive, eventHint: eventHint).id(id)
                } else {
                    ContentUnavailableView("Agent Unavailable", systemImage: "bubble.left")
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
            guard networkActive else { return }
            await listenForEvents()
        }
    }

    @MainActor private func listenForEvents() async {
        var delay = 1
        let clock = ContinuousClock()
        while !Task.isCancelled {
            let start = clock.now
            do {
                guard let stream = try api.events() else { return }
                defer { stream.cancel() }
                try await withTaskCancellationHandler {
                    for try await _ in stream.hints {
                        try Task.checkCancellation()
                        eventHint = UUID()
                        await refresh()
                        try await Task.sleep(for: .seconds(1))
                    }
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

struct TranscriptBuffer {
    private(set) var entries: [Entry] = []
    private(set) var hasOlder = false

    // Returns true when the latest page is disconnected from the loaded window.
    // Reset to a contiguous window so an exclusive `before` cursor never skips a gap.
    @discardableResult mutating func applyLatest(_ page: TranscriptPage) -> Bool {
        if entries.isEmpty || page.entries.isEmpty {
            entries = []
            merge(page.entries)
            hasOlder = page.hasOlder
            return false
        }
        let known = Set(entries.map(\.seq))
        if !page.entries.contains(where: { known.contains($0.seq) }) {
            entries = []
            merge(page.entries)
            hasOlder = page.hasOlder
            return true
        }
        merge(page.entries)
        return false
    }

    mutating func prepend(_ page: TranscriptPage, before cursor: Int64) {
        // Stop on a non-advancing page even if a server incorrectly reports hasOlder.
        let older = page.entries.filter { $0.seq < cursor }
        merge(older)
        hasOlder = page.hasOlder && !older.isEmpty
    }

    private mutating func merge(_ added: [Entry]) {
        var bySequence: [Int64: Entry] = [:]
        for entry in entries { bySequence[entry.seq] = entry }
        for entry in added { bySequence[entry.seq] = entry }
        entries = bySequence.values.sorted { $0.seq < $1.seq }
    }
}

private struct ConversationView: View {
    let api: API
    let agent: Agent
    let refreshStatus: () async -> Void
    let routeID: UUID
    let networkActive: Bool
    let eventHint: UUID
    @State private var transcript = TranscriptBuffer()
    @State private var loaded = false
    @State private var checking = false
    @State private var loadingOlder = false
    @State private var mutating = false
    @State private var draft = ""
    @State private var mode: SendMode = .queued
    @State private var refreshError: String?
    @State private var actionError: String?
    @State private var showInterrupt = false
    @State private var scrollTarget: Int64?
    @State private var scrollGeneration = 0
    @State private var scrollToEnd = false
    @State private var jumpedWindow = false

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 12) {
                    Text(agentStatus(agent)).font(.footnote).foregroundStyle(.secondary)
                    if agent.pausedEffective && !agent.paused {
                        Text("Paused by the server or project.").font(.footnote).foregroundStyle(.secondary)
                    }
                    if let refreshError {
                        Label(refreshError, systemImage: "exclamationmark.circle").font(.footnote)
                    } else if !loaded {
                        ProgressView("Loading transcript…")
                    } else if transcript.entries.isEmpty {
                        Text("No transcript entries yet.").foregroundStyle(.secondary)
                    }
                    if jumpedWindow { Text("Showing the newest page. Load older entries to catch up.").font(.footnote) }
                    if transcript.hasOlder {
                        Button(loadingOlder ? "Loading…" : "Load Older Entries") { Task { await older() } }
                            .disabled(loadingOlder || checking)
                            .frame(minHeight: 44)
                    }
                    ForEach(transcript.entries) { entry in
                        EntryRow(entry: entry).id(entry.seq)
                    }
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
                if let scrollTarget { proxy.scrollTo(scrollTarget, anchor: scrollToEnd ? .bottom : .top) }
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) { composer }
        .navigationTitle(agent.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu("Agent Actions", systemImage: "ellipsis.circle") {
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
    }

    private var composer: some View {
        VStack(alignment: .leading, spacing: 4) {
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
                    jump(to: transcript.entries.last?.seq, end: true)
                }
                .font(.footnote)
                .accessibilityLabel("Jump to Latest")
                .disabled(transcript.entries.isEmpty)
            }
        }
        .padding(.horizontal)
        .padding(.top, 8)
        .background(.bar)
    }

    private func jump(to seq: Int64?, end: Bool) {
        scrollTarget = seq
        scrollToEnd = end
        scrollGeneration += 1
    }

    @MainActor private func refresh() async {
        guard networkActive, !Task.isCancelled, !checking, !loadingOlder else { return }
        checking = true
        defer { checking = false }
        do {
            let page = try await api.transcript(agent.id)
            try Task.checkCancellation()
            let reset = transcript.applyLatest(page)
            if reset { jumpedWindow = true }
            if !loaded || reset { jump(to: transcript.entries.last?.seq, end: true) }
            loaded = true
            refreshError = nil
        } catch is CancellationError {
        } catch {
            refreshError = safeMessage(error)
        }
    }

    @MainActor private func older() async {
        guard networkActive, !Task.isCancelled, !checking, !loadingOlder, let cursor = transcript.entries.first?.seq else { return }
        loadingOlder = true
        defer { loadingOlder = false }
        do {
            let page = try await api.transcript(agent.id, before: cursor)
            transcript.prepend(page, before: cursor)
            refreshError = nil
            jumpedWindow = false
            jump(to: cursor, end: false)
        } catch is CancellationError {
        } catch {
            refreshError = safeMessage(error)
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
        if actionError == nil { jump(to: transcript.entries.last?.seq, end: true) }
    }
}

private struct EntryRow: View {
    let entry: Entry

    private var title: String {
        if entry.kind == "message" { return entry.from ?? "Message" }
        if entry.kind == "assistant" { return "Assistant" }
        return entry.kind.replacingOccurrences(of: "_", with: " ").capitalized
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if entry.kind == "assistant" || entry.kind == "message" || entry.kind == "error" {
                Text(title).font(.subheadline.bold()).foregroundStyle(.secondary)
                if let text = entry.text { Text(text).textSelection(.enabled) }
            } else if let detail = entry.text ?? entry.reason {
                DisclosureGroup(entry.name.map { "\(title) · \($0)" } ?? title) {
                    Text(detail).font(.callout).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                }
                .font(.subheadline)
            } else {
                Text(entry.name.map { "\(title) · \($0)" } ?? title).font(.subheadline).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
    }
}

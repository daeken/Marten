import SwiftUI

struct AgentTask: Decodable, Identifiable, Sendable {
    struct Dependency: Decodable, Sendable { let agentId: String; let taskId: Int }
    struct Check: Decodable, Sendable { let at: String; let passed: Bool; let exitCode: Int?; let output: String }
    let id: Int
    let title: String
    let state: String
    let waitingOn: Dependency?
    let reason: String?
    let assignedBy: String
    let updated: String
    let check: String?
    let lastCheck: Check?
}

struct AgentSchedule: Decodable, Identifiable, Sendable {
    let created: String
    let id: Int
    let command: String
    let description: String?
    let everySeconds: Int?
    let nextRun: String
    let lastRun: String?
    let lastResult: String?
    let inProgress: Bool?

    var timing: String {
        guard let seconds = everySeconds, seconds > 0 else { return "Once" }
        for (unit, size) in [("d", 86_400), ("h", 3_600), ("m", 60)] where seconds % size == 0 {
            return "Every \(seconds / size)\(unit)"
        }
        return "Every \(seconds)s"
    }
}

struct AgentProcess: Decodable, Identifiable, Sendable {
    struct Key: Hashable, Sendable { let agent: String; let number: Int }
    private let number: Int
    let agentId: String
    let agentName: String
    let name: String
    let command: String
    let workingDirectory: String
    let pid: Int
    let started: String
    let state: String
    var id: Key { .init(agent: agentId, number: number) }
    var canStop: Bool { state == "running" || state == "exited" }

    enum CodingKeys: String, CodingKey {
        case number = "id"
        case agentId, agentName, name, command, workingDirectory, pid, started, state
    }
}

struct TaskGroups {
    let active: [AgentTask]
    let done: [AgentTask]

    init(_ items: [AgentTask]) {
        active = items.filter { ["open", "waiting", "blocked"].contains($0.state) }
        done = items.filter { $0.state == "done" }
    }
}

struct ProcessGroups {
    let running: [AgentProcess]
    let history: [AgentProcess]

    init(_ items: [AgentProcess]) {
        running = items.filter { $0.state == "running" }
        history = items.filter { $0.state == "exited" || $0.state == "stopped" }
    }
}

@MainActor @Observable
final class OperationList<Item: Sendable> {
    private(set) var items: [Item]?
    private(set) var error: String?
    private(set) var actionError: String?
    private(set) var working = false
    private(set) var needsRead = false
    private var revision = UUID()

    func refresh(_ read: () async throws -> [Item]) async {
        guard !working, !Task.isCancelled else { return }
        let id = UUID()
        revision = id
        do {
            let fresh = try await read()
            guard !Task.isCancelled, revision == id else { return }
            items = fresh
            error = nil
            needsRead = false
        } catch {
            guard !Task.isCancelled, !(error is CancellationError), revision == id else { return }
            self.error = safeMessage(error)
        }
    }

    // Only invoked by explicit confirmation. Neither reload nor reconnect calls this.
    func change(_ action: () async throws -> Void, read: () async throws -> [Item]) async {
        guard !working, !needsRead, !Task.isCancelled else { return }
        working = true
        revision = UUID()
        actionError = nil
        do { try await action() }
        catch { actionError = "Could not confirm the change. Check the refreshed list before trying again. \(safeMessage(error))" }
        needsRead = true
        working = false
        await refresh(read)
    }
}

struct OperationsRefresh: Equatable {
    let route: UUID
    let active: Bool
}

struct AgentOperationsView: View {
    let api: API
    let agent: Agent
    let agents: [Agent]
    let targetTask: Int?
    let route: UUID
    let active: Bool
    let hint: UUID
    @State private var tasks = OperationList<AgentTask>()
    @State private var schedules = OperationList<AgentSchedule>()
    @State private var removing: AgentSchedule?

    @State private var tasksExpanded = false
    @State private var doneExpanded = false
    @State private var revealedTask: Int?

    private var targetIsDone: Bool? {
        guard let targetTask, let item = tasks.items?.first(where: { $0.id == targetTask }) else { return nil }
        return item.state == "done"
    }

    var body: some View {
        ScrollViewReader { proxy in
            List {
                Section {
                    let groups = tasks.items.map(TaskGroups.init)
                    if let error = tasks.error { Label(error, systemImage: "exclamationmark.circle") }
                    if let targetTask, tasks.items != nil, targetIsDone == nil {
                        Text("Task #\(targetTask) is no longer listed.").foregroundStyle(.secondary)
                    }
                    if let groups {
                        if !groups.active.isEmpty || !groups.done.isEmpty {
                            DisclosureGroup("Agent Tasks (\(groups.active.count) active, \(groups.done.count) done)", isExpanded: $tasksExpanded) {
                                if groups.active.isEmpty { Text("No active tasks.").foregroundStyle(.secondary) }
                                ForEach(groups.active, content: taskRow)
                                DisclosureGroup("Done (\(groups.done.count))", isExpanded: $doneExpanded) {
                                    if groups.done.isEmpty { Text("No completed tasks.").foregroundStyle(.secondary) }
                                    ForEach(groups.done, content: taskRow)
                                }
                            }
                        } else if tasks.error == nil {
                            Text("No tasks.").foregroundStyle(.secondary)
                        }
                    } else if tasks.error == nil {
                        ProgressView("Loading tasks…")
                    }
                }
                Section("Schedules") {
                    LoadState(items: schedules.items, error: schedules.error, empty: "No upcoming schedules.")
                    if let error = schedules.actionError { Label(error, systemImage: "exclamationmark.circle") }
                    ForEach(schedules.items ?? []) { item in
                        VStack(alignment: .leading, spacing: 6) {
                            Text("#\(item.id) · \(item.description ?? item.timing)").font(.headline)
                            Text(item.inProgress == true ? "Running · \(item.timing)" : item.timing)
                            LabeledContent("Next run") { InboxTime(stamp: item.nextRun) }
                            DisclosureGroup("Command and Last Run") {
                                TranscriptCode(text: item.command)
                                if let last = item.lastRun { InboxTime(stamp: last) }
                                if let result = item.lastResult { Text(result).textSelection(.enabled) }
                            }
                            Button("Remove Schedule…", role: .destructive) { removing = item }
                                .buttonStyle(.borderless).disabled(!active || schedules.working || schedules.needsRead)
                        }.padding(.vertical, 4)
                    }
                }
                NavigationLink("Background Processes", value: WorkspaceDestination.processes(agent.id))
            }
            .onChange(of: targetIsDone, initial: true) { _, isDone in
                guard let isDone, revealedTask == nil else { return }
                tasksExpanded = true
                doneExpanded = isDone
                revealedTask = targetTask
            }
            .task(id: revealedTask) {
                guard let revealedTask else { return }
                // The disclosure rows need to be in the List before scrolling to one.
                await Task.yield()
                guard !Task.isCancelled else { return }
                proxy.scrollTo(revealedTask, anchor: .top)
            }
        }
        .navigationTitle("\(agent.name) · Operations")
        .refreshable { await refresh() }
        .task(id: OperationsRefresh(route: route, active: active)) {
            guard active else { return }
            while !Task.isCancelled { await refresh(); try? await Task.sleep(for: .seconds(8)) }
        }
        .task(id: hint) { await refresh() }
        .onChange(of: OperationsRefresh(route: route, active: active)) { removing = nil }
        .confirmationDialog("Remove this schedule?", isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } }), presenting: removing) { item in
            Button("Remove Schedule", role: .destructive) {
                guard active else { return }
                Task {
                    guard schedules.items?.contains(where: { $0.id == item.id && $0.created == item.created }) == true else { return }
                    await schedules.change({ try await api.removeSchedule(agent.id, item.id) }, read: { try await api.schedules(agent.id) })
                }
            }
        } message: { item in Text("Schedule #\(item.id) will no longer run.") }
    }

    private func taskRow(_ task: AgentTask) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("#\(task.id) \(task.title)").font(.headline)
            Text(task.state.capitalized).font(.subheadline)
            if let dependency = task.waitingOn {
                let name = agents.first { $0.id == dependency.agentId }?.name ?? dependency.agentId
                NavigationLink("Waiting for \(name) #\(dependency.taskId)", value: WorkspaceDestination.operations(dependency.agentId, task: dependency.taskId))
            }
            if let reason = task.reason { Text(reason) }
            if let command = task.check {
                DisclosureGroup("Completion Check") {
                    TranscriptCode(text: command)
                    if let result = task.lastCheck {
                        Text(result.passed ? "Passed" : "Failed").font(.headline)
                        if let exit = result.exitCode { Text("Exit code: \(exit)") }
                        InboxTime(stamp: result.at)
                        if !result.output.isEmpty { TranscriptCode(text: result.output) }
                    } else { Text("Not run yet.") }
                }
            }
            InboxTime(stamp: task.updated)
        }.padding(.vertical, 4).id(task.id)
    }

    private func refresh() async {
        guard active else { return }
        async let taskRead: Void = tasks.refresh { try await api.tasks(agent.id) }
        async let scheduleRead: Void = schedules.refresh { try await api.schedules(agent.id) }
        _ = await (taskRead, scheduleRead)
    }
}

struct ProcessesView: View {
    let api: API
    let agentID: String?
    let route: UUID
    let active: Bool
    let hint: UUID
    @State private var processes = OperationList<AgentProcess>()
    @State private var runningExpanded = false
    @State private var historyExpanded = false
    @State private var stopping: AgentProcess?

    var body: some View {
        let groups = processes.items.map(ProcessGroups.init)
        List {
            if agentID == nil {
                Section("Server Administration") {
                    NavigationLink("MCP Servers", value: WorkspaceDestination.administration(.mcp))
                    NavigationLink("Outside Clients", value: WorkspaceDestination.administration(.clients))
                    NavigationLink("Backups", value: WorkspaceDestination.administration(.backups))
                }
            }
            if let error = processes.error { Label(error, systemImage: "exclamationmark.circle") }
            if let error = processes.actionError { Label(error, systemImage: "exclamationmark.circle") }
            if agentID != nil, let groups, groups.running.isEmpty && groups.history.isEmpty {
                if processes.error == nil { Text("No background processes to show.").foregroundStyle(.secondary) }
            } else {
                processDisclosure("Running processes", rows: groups?.running, empty: "No running processes.", expanded: $runningExpanded)
                processDisclosure("Process history", rows: groups?.history, empty: "No exited or stopped processes.", expanded: $historyExpanded)
            }
        }
        .navigationTitle("Background Processes")
        .refreshable { await refresh() }
        .task(id: OperationsRefresh(route: route, active: active)) {
            guard active else { return }
            while !Task.isCancelled { await refresh(); try? await Task.sleep(for: .seconds(5)) }
        }
        .task(id: hint) { await refresh() }
        .onChange(of: OperationsRefresh(route: route, active: active)) { stopping = nil }
        .confirmationDialog("Stop this process?", isPresented: Binding(get: { stopping != nil }, set: { if !$0 { stopping = nil } }), presenting: stopping) { process in
            Button("Stop Process", role: .destructive) {
                guard active else { return }
                Task {
                    guard processes.items?.contains(where: { $0.id == process.id && $0.started == process.started && $0.canStop }) == true else { return }
                    await processes.change({ try await api.stopProcess(process.id) }, read: { try await api.processes(agentID, includeStopped: true) })
                }
            }
        } message: { process in Text("Marten will stop \(process.name) (PID \(process.pid)) and its child processes.") }
    }

    private func processDisclosure(_ title: String, rows: [AgentProcess]?, empty: String, expanded: Binding<Bool>) -> some View {
        DisclosureGroup(rows.map { "\(title) (\($0.count))" } ?? title, isExpanded: expanded) {
            if let rows {
                if rows.isEmpty && processes.error == nil { Text(empty).foregroundStyle(.secondary) }
                ForEach(rows, content: processRow)
            } else if processes.error == nil {
                ProgressView("Loading processes…")
            }
        }
    }

    private func processRow(_ process: AgentProcess) -> some View {
        // Give each action its own List row so Log cannot also target Stop.
        Group {
            VStack(alignment: .leading, spacing: 6) {
                Text(process.name).font(.headline)
                Text("PID \(process.pid) · \(process.state.capitalized)")
                LabeledContent("Started") { InboxTime(stamp: process.started) }
            }.padding(.vertical, 4)
            NavigationLink(process.agentName, value: WorkspaceDestination.agent(process.agentId))
            DisclosureGroup("Command and Working Directory") {
                TranscriptCode(text: process.command)
                Text(process.workingDirectory).textSelection(.enabled)
            }
            NavigationLink("Process Log", value: WorkspaceDestination.processLog(process.id))
                .accessibilityIdentifier("process-log-\(process.id.agent)-\(process.id.number)")
            if process.canStop {
                Button("Stop Process…", role: .destructive) { stopping = process }
                    .disabled(!active || processes.working || processes.needsRead)
            }
        }
    }

    private func refresh() async {
        guard active else { return }
        await processes.refresh { try await api.processes(agentID, includeStopped: true) }
    }
}

struct ProcessLogView: View {
    let api: API
    let key: AgentProcess.Key
    let route: UUID
    let active: Bool
    @State private var log: String?
    @State private var error: String?
    @State private var request = UUID()

    var body: some View {
        ScrollView {
            VStack(alignment: .leading) {
                if let error { Label(error, systemImage: "exclamationmark.circle") }
                if let log { TranscriptCode(text: log.isEmpty ? "(empty)" : log) }
                else if error == nil { ProgressView("Reading process log…") }
            }.padding().frame(maxWidth: .infinity, alignment: .leading)
        }
        .navigationTitle("Process #\(key.number) Log")
        .refreshable { await refresh() }
        .task(id: OperationsRefresh(route: route, active: active)) {
            guard active else { return }
            while !Task.isCancelled { await refresh(); try? await Task.sleep(for: .seconds(2)) }
        }
    }

    private func refresh() async {
        guard active, !Task.isCancelled else { return }
        let id = UUID()
        request = id
        do {
            let fresh = try await api.processLog(key)
            guard request == id, !Task.isCancelled else { return }
            log = fresh
            error = nil
        } catch {
            guard request == id, !Task.isCancelled, !(error is CancellationError) else { return }
            self.error = safeMessage(error)
        }
    }
}

private struct LoadState<Item>: View {
    let items: [Item]?
    let error: String?
    let empty: String
    var body: some View {
        if let error { Label(error, systemImage: "exclamationmark.circle") }
        else if items == nil { ProgressView("Loading…") }
        else if items?.isEmpty == true { Text(empty).foregroundStyle(.secondary) }
    }
}

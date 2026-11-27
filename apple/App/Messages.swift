import SwiftUI

@MainActor @Observable
final class MessagesState {
    private(set) var projectID: String?
    private(set) var rows: [Row]?
    private(set) var error: String?
    private var request = UUID()

    struct Row: Identifiable {
        struct ID: Hashable { let message: AgentMessage; let occurrence: Int }
        let id: ID
        var message: AgentMessage { id.message }
    }

    func select(_ project: String?) {
        guard project != projectID else { return }
        projectID = project
        rows = nil
        error = nil
        request = UUID()
    }

    func refresh(_ api: API) async {
        guard !Task.isCancelled else { return }
        let id = UUID()
        request = id
        do {
            let messages = try await api.agentMessages(projectID: projectID)
            guard request == id, !Task.isCancelled else { return }
            var seen: [AgentMessage: Int] = [:]
            rows = messages.map { message in
                let index = seen[message, default: 0]
                seen[message] = index + 1
                return Row(id: .init(message: message, occurrence: index))
            }
            error = nil
        } catch {
            guard request == id, !Task.isCancelled, !(error is CancellationError) else { return }
            self.error = safeMessage(error)
        }
    }
}

struct MessagesView: View {
    let state: MessagesState
    let projects: [Project]
    let agents: [Agent]
    let active: Bool
    let refresh: () async -> Void

    var body: some View {
        List {
            Picker("Project", selection: Binding(get: { state.projectID }, set: state.select)) {
                Text("All Projects and Chats").tag(nil as String?)
                ForEach(projects) { project in Text(project.name).tag(Optional(project.id)) }
            }
            if let error = state.error { Label(error, systemImage: "exclamationmark.circle") }
            if state.rows == nil, state.error == nil { ProgressView("Loading messages…") }
            if state.rows?.isEmpty == true { Text("No messages yet.").foregroundStyle(.secondary) }
            ForEach(state.rows ?? []) { row in
                let message = row.message
                VStack(alignment: .leading, spacing: 7) {
                    HStack {
                        agentLink(message.fromName, id: message.fromId)
                        Image(systemName: "arrow.right").accessibilityLabel("to")
                        agentLink(message.toName, id: message.toId)
                    }.font(.headline)
                    if let project = projects.first(where: { $0.id == message.projectId }) {
                        Button(project.name) { state.select(project.id) }.buttonStyle(.borderless)
                            .disabled(state.projectID == project.id)
                    }
                    Text([message.mode.capitalized, message.reason].compactMap { $0 }.joined(separator: " · "))
                        .font(.caption).foregroundStyle(.secondary)
                    InboxTime(stamp: message.at)
                    Text(message.text).textSelection(.enabled)
                }.padding(.vertical, 4)
            }
        }
        .navigationTitle("Messages Between Agents")
        .refreshable { await refresh() }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Refresh Messages", systemImage: "arrow.clockwise") { Task { await refresh() } }.disabled(!active)
            }
        }
    }

    @ViewBuilder private func agentLink(_ name: String, id: String) -> some View {
        if agents.contains(where: { $0.id == id }) {
            NavigationLink(name, value: WorkspaceDestination.agent(id)).buttonStyle(.borderless)
        } else { Text(name) }
    }
}

import SwiftUI

@MainActor @Observable
final class InboxState {
    private(set) var messages: [InboxMessage]?
    private(set) var loadError: String?
    private(set) var submitting: Set<String> = []
    private(set) var needsRefresh: Set<String> = []
    private(set) var answerErrors: [String: String] = [:]
    var drafts: [String: String] = [:]
    private var readID = UUID()
    private var revision = UUID()

    func questions(for agent: String) -> [InboxMessage] {
        (messages ?? []).filter { $0.question == true && $0.from == agent }
    }

    func refresh(_ api: API) async {
        guard !Task.isCancelled else { return }
        let request = UUID()
        readID = request
        let before = revision
        do {
            let fresh = try await api.inbox()
            guard !Task.isCancelled, readID == request, revision == before else { return }
            let recorded = Dictionary((messages ?? []).filter { $0.answer != nil }.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
            messages = fresh.map { item in item.answer == nil ? recorded[item.id] ?? item : item }
            loadError = nil
            needsRefresh.removeAll()
            for item in messages ?? [] where item.answer != nil {
                drafts[item.id] = nil
                answerErrors[item.id] = nil
            }
        } catch {
            guard !Task.isCancelled, !(error is CancellationError), readID == request, revision == before else { return }
            loadError = safeMessage(error)
        }
    }

    func answer(_ id: String, choice: Int?, using api: API) async {
        guard !Task.isCancelled, let question = messages?.first(where: { $0.id == id }), question.isOpen,
              !submitting.contains(id), !needsRefresh.contains(id) else { return }
        let words = drafts[id, default: ""].trimmingCharacters(in: .whitespacesAndNewlines)
        guard choice != nil || !words.isEmpty else { return }
        submitting.insert(id)
        answerErrors[id] = nil
        revision = UUID()
        defer { submitting.remove(id); revision = UUID() }
        do {
            let saved = try await api.answer(question, choice: choice, text: words)
            if let index = messages?.firstIndex(where: { $0.id == id }) { messages?[index] = saved }
            drafts[id] = nil
        } catch {
            // A failed response doesn't tell us whether the server saved it.
            // Only a fresh GET permits a new, explicitly initiated submission.
            needsRefresh.insert(id)
            switch error {
            case ClientError.response(409): answerErrors[id] = "The server rejected this answer. Check the question’s current state."
            case ClientError.response(404): answerErrors[id] = "This question is no longer available."
            case is CancellationError: answerErrors[id] = "Could not confirm the answer. Check the saved state before sending again."
            default: answerErrors[id] = safeMessage(error)
            }
        }
    }
}

struct InboxView: View {
    let inbox: InboxState
    let projects: [Project]
    let agents: [Agent]
    let active: Bool
    let refresh: () async -> Void
    let answer: (String, Int?) -> Void

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 16) {
                if let error = inbox.loadError {
                    Label(error, systemImage: "exclamationmark.circle")
                    Button("Reload Inbox") { Task { await refresh() } }.disabled(!active)
                } else if inbox.messages == nil {
                    ProgressView("Loading inbox…")
                } else if inbox.messages?.isEmpty == true {
                    Text("Nothing from your agents yet.").foregroundStyle(.secondary)
                }
                ForEach(inbox.messages ?? []) { message in
                    VStack(alignment: .leading, spacing: 8) {
                        let project = projects.first { $0.id == message.projectId }
                        let source = [project?.name, message.fromName].compactMap { $0 }.joined(separator: " / ")
                        if agents.contains(where: { $0.id == message.from && $0.closed != true }) {
                            NavigationLink(source, value: WorkspaceDestination.agent(message.from))
                        } else { Text(source).font(.headline) }
                        InboxTime(stamp: message.at)
                        if message.question == true {
                            QuestionCard(message: message, inbox: inbox, active: active, answer: answer)
                        } else { Text(message.text).textSelection(.enabled) }
                    }
                    Divider()
                }
            }
            .padding()
            .frame(maxWidth: 860)
            .frame(maxWidth: .infinity)
        }
        .navigationTitle("Inbox")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Refresh Inbox", systemImage: "arrow.clockwise") { Task { await refresh() } }
                    .disabled(!active)
            }
        }
        .refreshable { await refresh() }
    }
}

struct QuestionCard: View {
    let message: InboxMessage
    let inbox: InboxState
    let active: Bool
    let answer: (String, Int?) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(message.text).font(.headline).textSelection(.enabled)
            if let context = message.context, !context.isEmpty {
                Text(context).font(.subheadline).foregroundStyle(.secondary).textSelection(.enabled)
            }
            if let saved = message.answer {
                Text("Saved answer").font(.subheadline.bold())
                if let at = message.answeredAt { InboxTime(stamp: at) }
                Text(saved).textSelection(.enabled)
            } else {
                let options = message.options ?? []
                let words = inbox.drafts[message.id, default: ""].trimmingCharacters(in: .whitespacesAndNewlines)
                let blocked = !active || inbox.submitting.contains(message.id) || inbox.needsRefresh.contains(message.id)
                TextField(options.isEmpty ? "Your answer" : "Optional note, or your own answer", text: Binding(
                    get: { inbox.drafts[message.id, default: ""] },
                    set: { inbox.drafts[message.id] = $0 }
                ), axis: .vertical)
                .textFieldStyle(.roundedBorder)
                .disabled(inbox.submitting.contains(message.id))
                ForEach(options.indices, id: \.self) { index in
                    Button { answer(message.id, index) } label: { Text(options[index]) }
                        .buttonStyle(.bordered)
                        .disabled(blocked)
                }
                Button("Send Written Answer") { answer(message.id, nil) }
                    .disabled(blocked || words.isEmpty)
                if inbox.submitting.contains(message.id) { ProgressView("Saving answer…") }
                if let error = inbox.answerErrors[message.id] { Label(error, systemImage: "exclamationmark.circle") }
                if inbox.needsRefresh.contains(message.id) {
                    Text("Waiting for a fresh inbox check. Your draft is kept.").font(.footnote).foregroundStyle(.secondary)
                }
            }
        }
    }
}

struct InboxTime: View {
    let stamp: String
    var body: some View {
        Group {
            if let date = InboxMessage.date(stamp) { Text(date.formatted(date: .abbreviated, time: .shortened)) }
            else { Text(stamp) }
        }.font(.caption).foregroundStyle(.secondary)
    }
}

import CryptoKit
import Foundation
import Observation

struct PendingOperatorMessage: Decodable, Identifiable, Equatable, Sendable {
    let id: String
    let text: String
    let mode: String

    var recallable: Bool { mode == "queued" }
    var visible: Bool { recallable || mode == "immediate" }
}

// Keep drafts/reservations across navigation and transport replacement, but not
// across credentials. Nothing here is persisted or sent until the operator sends.
@MainActor final class ConversationComposers {
    private struct Key: Hashable {
        let endpoint: Endpoint
        let credential: SHA256.Digest?
        let agent: String
    }
    private var states: [Key: PendingComposer] = [:]

    func state(_ agent: String, using api: API) -> PendingComposer {
        let key = Key(endpoint: api.endpoint, credential: api.token.map { SHA256.hash(data: Data($0.utf8)) }, agent: agent)
        if let existing = states[key] { return existing }
        let state = PendingComposer(agent: agent)
        states[key] = state
        return state
    }

    func forget(_ endpoint: Endpoint) {
        states = states.filter { $0.key.endpoint != endpoint }
    }
}

@MainActor @Observable final class PendingComposer {
    private let agent: String
    private var snapshotRoute: UUID?
    private var messages: [PendingOperatorMessage] = []
    private var revision = UUID()
    private(set) var changeHint = UUID()
    private(set) var draft = ""
    var mode: SendMode = .queued
    private(set) var recalling = false
    private(set) var sending = false
    private(set) var recallNotice: String?

    init(agent: String) { self.agent = agent }

    func rows(on route: UUID) -> [PendingOperatorMessage] {
        snapshotRoute == route ? messages : []
    }

    func setDraft(_ text: String) {
        guard !recalling else { return }
        draft = text
    }

    var canRecall: Bool { !sending && !recalling && draft.isEmpty }

    func refresh(using api: API, route: UUID) async {
        guard !Task.isCancelled, !recalling else { return }
        let read = UUID()
        revision = read
        let list = (try? await api.pendingMessages(agent)) ?? []
        guard !Task.isCancelled, read == revision else { return }
        snapshotRoute = route
        messages = list
    }

    // Reserve synchronously, before launching the request. This task survives
    // view disappearance and always applies to this original agent's composer.
    func recall(_ row: PendingOperatorMessage, using api: API, route: UUID) -> Task<Void, Never>? {
        guard canRecall, row.recallable, rows(on: route).contains(row) else { return nil }
        recalling = true
        recallNotice = nil
        revision = UUID()
        return Task {
            defer { recalling = false; changeHint = UUID() }
            do {
                if let removed = try await api.recallPendingMessage(agent, id: row.id) {
                    draft = removed.text
                    mode = .queued
                } else {
                    recallNotice = "That message is no longer pending."
                }
            } catch {
                recallNotice = "Could not confirm recall. Check the pending messages and transcript before sending again."
            }
            // A previously started GET must never resurrect this row. Refetch
            // through the currently visible route, not the pinned DELETE route.
            messages.removeAll { $0.id == row.id }
        }
    }

    func send(using api: API) async throws -> Bool {
        let message = draft
        guard !sending, !recalling, !message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        sending = true
        defer { sending = false; changeHint = UUID() }
        try await api.send(agent, text: message, mode: mode)
        if draft == message { draft = "" }
        return true
    }
}

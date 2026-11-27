import Foundation

struct LiveActivity: Sendable, Equatable {
    var text = ""
    var tool: String?
    var thinking: String?
}

// Socket-local, bounded state. Coalescing snapshots does not discard text deltas.
struct LiveEvents {
    private struct Frame: Decodable {
        let type: String
        let agentId: String?
        let text: String?
        let name: String?
        let entry: Entry?
        let modelCall: Call?
        struct Call: Decodable { let kind: String?; let outcome: String? }
    }

    private(set) var agents: [String: LiveActivity] = [:]
    private(set) var pendingAgent: String?

    mutating func receive(_ data: Data) -> (updated: Bool, invalidate: Bool) {
        pendingAgent = nil
        guard let frame = try? JSONDecoder().decode(Frame.self, from: data),
              let id = frame.agentId, !id.isEmpty, id.utf8.count <= 128 else { return (false, true) }
        if frame.type == "inbox" { pendingAgent = id }
        var next = agents[id] ?? LiveActivity()
        switch frame.type {
        case "delta":
            guard let text = frame.text else { return (false, false) }
            next.text = String(decoding: (next.text + text).utf8.suffix(32_768), as: UTF8.self)
        case "entry":
            switch frame.entry?.kind {
            case "assistant", "tool_call", "idle", "error", "context_reset": next.text = ""
            default: break
            }
            if ["tool_result", "idle", "error", "context_reset"].contains(frame.entry?.kind ?? "") { next.tool = nil }
            if ["idle", "error", "context_reset"].contains(frame.entry?.kind ?? "") { next.thinking = nil }
        case "tool_start": next.tool = String((frame.name ?? "Tool").prefix(128))
        case "model_call_start":
            next.text = ""
            next.thinking = frame.modelCall?.kind == "compaction" ? "Compacting context" : "Thinking"
        case "model_call_end":
            next.thinking = nil
            if frame.modelCall?.outcome != "succeeded" { next.text = "" }
        default: return (false, true)
        }
        let prior = agents[id] ?? LiveActivity()
        if next == LiveActivity() { agents[id] = nil }
        else {
            if agents.count >= 16, agents[id] == nil, let evicted = agents.keys.first { agents.removeValue(forKey: evicted) }
            agents[id] = next
        }
        return (next != prior, frame.type != "delta")
    }
}

struct LiveFollow {
    private(set) var nearEnd = true
    mutating func measure(distance: CGFloat) { nearEnd = distance <= 80 }
    mutating func resume() { nearEnd = true }
    func shouldFollow(archived: Bool) -> Bool { nearEnd && !archived }
}

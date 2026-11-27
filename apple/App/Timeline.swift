import SwiftUI

struct TimelinePages {
    enum Move { case older, newer }
    private(set) var latest: TranscriptPage?
    private(set) var archive: TranscriptPage?
    private var cursors: [Int64] = []
    var page: TranscriptPage? { archive ?? latest }

    private var knownDeliveries: [String: Date] = [:]

    mutating func updateLatest(_ page: TranscriptPage) {
        latest = page
        recordDeliveries(page.entries)
    }

    private mutating func recordDeliveries(_ entries: [Entry]) {
        // A delivery already observed on a different page should not reappear
        // as a card when the latest window advances beyond that message.
        for entry in entries where entry.kind == "message" && entry.from == "Sera" {
            guard let text = entry.text, text.hasPrefix("Sera answered your question \""),
                  let date = entry.ts.flatMap(InboxMessage.date) else { continue }
            knownDeliveries[text] = max(date, knownDeliveries[text] ?? .distantPast)
        }
    }

    func rows(questions: [InboxMessage]) -> [TimelineRow] {
        guard let page else { return [] }
        return TimelineRow.build(page: page, archived: archive != nil, questions: questions, knownDeliveries: knownDeliveries)
    }

    func target(_ move: Move) -> [Int64]? {
        switch move {
        case .older:
            guard let page, page.hasOlder, let first = page.entries.first else { return nil }
            if let cursor = cursors.last, first.seq >= cursor { return nil }
            return cursors + [first.seq]
        case .newer:
            return cursors.isEmpty ? nil : Array(cursors.dropLast())
        }
    }

    mutating func show(_ page: TranscriptPage, at target: [Int64]) {
        guard let before = target.last else { return }
        let entries = page.entries.filter { $0.seq < before }
        recordDeliveries(entries)
        archive = TranscriptPage(entries: entries, hasOlder: page.hasOlder && !entries.isEmpty)
        cursors = target
    }

    mutating func returnToLatest() { archive = nil; cursors = [] }
}

struct TimelineRow: Identifiable {
    enum ID: Hashable { case entry(Int64), question(String), end }
    let id: ID
    let content: Content
    enum Content {
        case entry(Entry, result: Entry?)
        case question(InboxMessage)
    }

    fileprivate static func build(page: TranscriptPage, archived: Bool, questions: [InboxMessage], knownDeliveries: [String: Date]) -> [Self] {
        let entries = page.entries
        let dates = entries.map { $0.ts.flatMap(InboxMessage.date) }
        let delivered = Set(entries.filter { $0.kind == "message" && $0.from == "Sera" }.compactMap(\.text))
        var seen: Set<String> = []
        let unique = questions.filter { seen.insert($0.id).inserted }
        let saved = unique.compactMap { question -> (InboxMessage, Date)? in
            guard let answer = question.answer else { return nil }
            let delivery = "Sera answered your question \"\(question.text)\":\n\n\(answer)"
            let stamp = InboxMessage.date(question.answeredAt ?? question.at)
            if let received = knownDeliveries[delivery], received >= stamp ?? .distantPast { return nil }
            if stamp == nil, delivered.contains(delivery) { return nil }
            if page.hasOlder, let lower = dates.first ?? nil, (stamp ?? .distantPast) < lower { return nil }
            if archived {
                guard let upper = dates.last ?? nil, let stamp, stamp <= upper else { return nil }
            }
            return (question, stamp ?? .distantFuture)
        }.sorted { $0.1 == $1.1 ? $0.0.id < $1.0.id : $0.1 < $1.1 }
        let calls = Set(entries.filter { $0.kind == "tool_call" }.compactMap(\.callId))
        var results: [String: Entry] = [:]
        for entry in entries where entry.kind == "tool_result" {
            if let id = entry.callId { results[id] = entry }
        }
        var rows: [Self] = []
        var next = 0
        for (entry, date) in zip(entries, dates) {
            while next < saved.count, let date, saved[next].1 <= date {
                let item = saved[next].0
                rows.append(.init(id: .question(item.id), content: .question(item)))
                next += 1
            }
            if entry.kind == "tool_result", let id = entry.callId, calls.contains(id) { continue }
            if ["assistant", "reasoning"].contains(entry.kind), entry.text?.isEmpty != false { continue }
            if entry.kind == "idle", entry.reason != "interrupted" { continue }
            if entry.kind == "wake", !["restart", "project_idle", "nudge"].contains(entry.reason ?? "") { continue }
            rows.append(.init(id: .entry(entry.seq), content: .entry(entry, result: entry.kind == "tool_call" ? entry.callId.flatMap { results[$0] } : nil)))
        }
        for (item, _) in saved.dropFirst(next) { rows.append(.init(id: .question(item.id), content: .question(item))) }
        if !archived {
            for item in unique where item.isOpen { rows.append(.init(id: .question(item.id), content: .question(item))) }
        }
        return rows
    }
}

struct EntryRow: View {
    let entry: Entry
    let result: Entry?
    let api: API
    let agent: String
    let active: Bool
    let route: UUID
    let artifactRouteIsCurrent: @MainActor () -> Bool

    @ViewBuilder var body: some View {
        switch entry.kind {
        case "idle": marker("Interrupted")
        case "wake":
            marker(["restart": "Resumed after restart", "project_idle": "Project idle with waiting tasks",
                    "nudge": "Nudged: open tasks"][entry.reason ?? ""] ?? "Resumed")
        case "elide": marker("Elided \(entry.seqs?.count ?? 0) old tool results from context")
        case "context_reset": marker("Fresh context started")
        case "reload": marker("System prompt reloaded")
        default:
            VStack(alignment: .leading, spacing: 6) {
                switch entry.kind {
                case "tool_call":
                    DisclosureGroup(toolLabel) {
                        if let arguments = entry.arguments { TranscriptCode(text: pretty(arguments)) }
                        if let result { output(result) }
                    }
                case "tool_result":
                    DisclosureGroup(entry.isError == true ? "Tool result (error) · call on another page" : "Tool result · call on another page") {
                        output(entry)
                    }
                case "reasoning", "compaction":
                    DisclosureGroup(entry.kind == "reasoning" ? "Reasoning" : entry.reason == "whole" ? "Context compacted · summary" : "Older context rolled over · summary") {
                        if let text = entry.text { RichTranscript(text: text) }
                    }
                default:
                    Text(title).font(.subheadline.bold()).foregroundStyle(entry.kind == "error" ? Color.red : .secondary)
                    if let text = entry.text {
                        if entry.kind == "message" && entry.from == "Sera" { Text(text).textSelection(.enabled) }
                        else { RichTranscript(text: text) }
                    }
                }
                if api.supportsArtifacts {
                    ForEach(result?.artifacts ?? entry.artifacts, id: \.id) { ref in
                        ArtifactCard(ref: ref, api: api, agent: agent, active: active, route: route,
                                     isCurrent: artifactRouteIsCurrent).id(ref)
                    }
                }
                if let stamp = entry.ts { InboxTime(stamp: stamp) }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
        }
    }

    private var title: String {
        if entry.kind == "message" { return entry.from ?? "Message" }
        if entry.kind == "assistant", let model = entry.model { return "Assistant · \(model)" }
        return entry.kind.replacingOccurrences(of: "_", with: " ").capitalized
    }

    private var toolLabel: String {
        let name = entry.name ?? "Tool call"
        guard let result else { return name }
        return name + (result.isError == true ? " · Error" : " · Completed")
    }

    private func marker(_ text: String) -> some View {
        Text(text).font(.footnote).foregroundStyle(.secondary).frame(maxWidth: .infinity)
    }

    private func pretty(_ arguments: String) -> String {
        guard let json = try? JSONSerialization.jsonObject(with: Data(arguments.utf8)) else { return arguments }
        if entry.name == "powershell", let command = (json as? [String: Any])?["command"] as? String { return command }
        guard let formatted = try? JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys]) else { return arguments }
        return String(decoding: formatted, as: UTF8.self)
    }

    @ViewBuilder private func output(_ entry: Entry) -> some View {
        if let text = entry.text { TranscriptCode(text: text) }
        ForEach(Array((entry.images ?? []).enumerated()), id: \.offset) { _, ref in
            AttachmentPreview(ref: ref, api: api, agent: agent, active: active, route: route).id(ref)
        }
    }
}

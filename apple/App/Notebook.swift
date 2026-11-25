import SwiftUI
import CoreTransferable
import UniformTypeIdentifiers

struct NotebookCell: Decodable, Identifiable, Sendable {
    let id: Int
    let at: String
    let command: String
    let workingDirectory: String
    let stateIn: NotebookJSON?
    let stateOut: NotebookJSON?
    let output: String
    let isError: Bool
    let durationMs: Int64
}

struct NotebookReplay: Decodable, Sendable {
    let run: NotebookCell
    let sameOutput: Bool
    let sameState: Bool
}

struct NotebookFileHeader: Decodable {
    enum CodingKeys: String, CodingKey { case nbformat, cells, metadata }
    init(from decoder: Decoder) throws {
        let file = try decoder.container(keyedBy: CodingKeys.self)
        guard try file.decode(Int.self, forKey: .nbformat) == 4 else { throw ClientError.malformed }
        _ = try file.nestedUnkeyedContainer(forKey: .cells)
        _ = try file.nestedContainer(keyedBy: CodingKeys.self, forKey: .metadata)
    }
}

// $State can be any JSON value, not only an object. Render for inspection only;
// the recorded input (not this representation) is restored by the server.
indirect enum NotebookJSON: Codable, Sendable {
    case null, bool(Bool), integer(Int64), unsigned(UInt64), decimal(Decimal), number(Double)
    case string(String), array([Self]), object([String: Self])

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() { self = .null }
        else if let v = try? c.decode(Bool.self) { self = .bool(v) }
        else if let v = try? c.decode(Int64.self) { self = .integer(v) }
        else if let v = try? c.decode(UInt64.self) { self = .unsigned(v) }
        else if let v = try? c.decode(Decimal.self) { self = .decimal(v) }
        else if let v = try? c.decode(Double.self) { self = .number(v) }
        else if let v = try? c.decode(String.self) { self = .string(v) }
        else if let v = try? c.decode([Self].self) { self = .array(v) }
        else { self = .object(try c.decode([String: Self].self)) }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let v): try c.encode(v)
        case .integer(let v): try c.encode(v)
        case .unsigned(let v): try c.encode(v)
        case .decimal(let v): try c.encode(v)
        case .number(let v): try c.encode(v)
        case .string(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .object(let v): try c.encode(v)
        }
    }

    var display: String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        guard let bytes = try? encoder.encode(self) else { return "(Could not display JSON state)" }
        return String(decoding: bytes, as: UTF8.self)
    }
}

@MainActor @Observable
final class NotebookState {
    enum Outcome {
        case result(NotebookReplay)
        case unconfirmed(String)
    }

    private(set) var cells: [NotebookCell]?
    private(set) var readError: String?
    private(set) var pending: NotebookCell?
    private(set) var running: Int?
    private(set) var outcomes: [Int: Outcome] = [:]
    private var loadedRoute: UUID?
    private var pendingRoute: UUID?
    private var readInFlight: (route: UUID, id: UUID)?
    private var needsRead = false
    private var revision = UUID()

    func refresh(using api: API, agent: String, route: UUID) async {
        guard !Task.isCancelled, readInFlight?.route != route else { return }
        let request = UUID()
        readInFlight = (route, request)
        revision = request
        defer { if readInFlight?.id == request { readInFlight = nil } }
        do {
            let fresh = try await api.notebook(agent)
            guard !Task.isCancelled, revision == request else { return }
            cells = fresh
            readError = nil
            loadedRoute = route
            needsRead = false
            let ids = Set(fresh.map(\.id))
            outcomes = outcomes.filter { ids.contains($0.key) }
        } catch is CancellationError {
        } catch {
            guard !Task.isCancelled, revision == request else { return }
            readError = safeMessage(error)
        }
    }

    func canPrepare(route: UUID) -> Bool {
        loadedRoute == route && readError == nil && !needsRead && running == nil
    }

    func prepare(_ id: Int, route: UUID) {
        guard canPrepare(route: route) else { return }
        pending = cells?.first { $0.id == id }
        pendingRoute = pending == nil ? nil : route
    }

    func cancelConfirmation() {
        pending = nil
        pendingRoute = nil
    }

    // This is the only entry into a POST; synchronously consume confirmation before
    // launching it. Neither refresh nor route replacement calls this method.
    @discardableResult
    func confirm(_ id: Int, using api: API, agent: String, route: UUID) -> Task<Void, Never>? {
        guard pending?.id == id else { return nil }
        let stagedRoute = pendingRoute
        cancelConfirmation()
        guard stagedRoute == route, canPrepare(route: route), cells?.contains(where: { $0.id == id }) == true else { return nil }
        running = id
        outcomes[id] = nil
        return Task {
            defer { running = nil }
            do { outcomes[id] = .result(try await api.rerunCell(agent, id)) }
            catch {
                // A lost response or cancelled connection is not proof the command didn't run.
                let message = error is CancellationError ? "The request was cancelled." : safeMessage(error)
                outcomes[id] = .unconfirmed("\(message) Marten could not confirm the result. Check for side effects before rerunning again.")
                needsRead = true
                revision = UUID()
            }
        }
    }
}

private struct NotebookShare: Transferable {
    static let contentType = UTType(importedAs: "dev.marten.jupyter-notebook", conformingTo: .json)
    let data: Data

    static var transferRepresentation: some TransferRepresentation {
        DataRepresentation(exportedContentType: contentType) { $0.data }
    }
}

struct NotebookView: View {
    let api: API
    let agent: Agent
    let route: UUID
    let active: Bool
    let hint: UUID
    @State private var state = NotebookState()
    @State private var confirming = false
    @State private var exportRequest: UUID?
    @State private var exportItem: NotebookShare?
    @State private var saving = false
    @State private var exportError: String?

    var body: some View {
        List {
            Section {
                Text("Shows up to 300 recorded PowerShell runs. Export includes the full notebook, which may contain sensitive commands, state, or output.")
                    .foregroundStyle(.secondary)
                Button("Export .ipynb…", systemImage: "square.and.arrow.up") {
                    exportError = nil
                    exportRequest = UUID()
                }.disabled(!active || state.cells?.isEmpty != false || exportRequest != nil || saving)
                if exportRequest != nil { ProgressView("Downloading notebook…") }
                if let exportError { Label(exportError, systemImage: "exclamationmark.circle") }
            }
            if let error = state.readError { Label(error, systemImage: "exclamationmark.circle") }
            else if state.cells == nil { ProgressView("Loading notebook…") }
            else if state.cells?.isEmpty == true { Text("No PowerShell runs yet.").foregroundStyle(.secondary) }
            ForEach((state.cells ?? []).reversed()) { cell in
                VStack(alignment: .leading, spacing: 8) {
                    Text("Cell #\(cell.id)").font(.headline)
                    HStack {
                        InboxTime(stamp: cell.at)
                        Text(cell.isError ? "Failed · \(cell.durationMs) ms" : "Succeeded · \(cell.durationMs) ms")
                    }.font(.footnote)
                    Text(cell.workingDirectory).font(.callout.monospaced()).textSelection(.enabled)
                        .accessibilityLabel("Working directory: \(cell.workingDirectory)")
                    TranscriptCode(text: cell.command)
                    DisclosureGroup(cell.isError ? "Output (error)" : "Output") { output(cell.output) }
                    DisclosureGroup("$State In / Out") {
                        NotebookStateComparison(first: "Before", before: cell.stateIn, second: "After", after: cell.stateOut)
                    }
                    rerunStatus(for: cell)
                    Button("Rerun…") {
                        state.prepare(cell.id, route: route)
                        confirming = state.pending != nil
                    }.buttonStyle(.borderless).disabled(!active || !state.canPrepare(route: route))
                }.padding(.vertical, 4)
            }
        }
        .navigationTitle("\(agent.name) · Notebook")
        .toolbar {
            Button("Refresh", systemImage: "arrow.clockwise") { Task { await refresh() } }.disabled(!active)
        }
        .refreshable { await refresh() }
        .task(id: OperationsRefresh(route: route, active: active)) {
            guard active else { return }
            while !Task.isCancelled { await refresh(); try? await Task.sleep(for: .seconds(8)) }
        }
        .task(id: hint) { await refresh() }
        .onChange(of: OperationsRefresh(route: route, active: active)) { dismissPending() }
        .onChange(of: route) { _, _ in saving = false; exportItem = nil }
        .onDisappear { dismissPending() }
        .alert("Rerun PowerShell cell?", isPresented: $confirming, presenting: state.pending) { cell in
            Button("Run on Server", role: .destructive) {
                guard active else { state.cancelConfirmation(); return }
                state.confirm(cell.id, using: api, agent: agent.id, route: route)
            }
            Button("Cancel", role: .cancel) { state.cancelConfirmation() }
        } message: { cell in
            Text("Cell #\(cell.id) will execute on the server with its recorded input $State and working directory if available. It may write or delete files or cause other side effects again. It does not replace the agent’s live $State.")
        }
        .task(id: exportRequest) {
            guard active, let request = exportRequest else { return }
            defer { if exportRequest == request { exportRequest = nil } }
            do {
                let data = try await api.notebookFile(agent.id)
                guard exportRequest == request, !Task.isCancelled else { return }
                exportItem = NotebookShare(data: data)
                saving = true
            } catch is CancellationError {
            } catch {
                guard exportRequest == request, !Task.isCancelled else { return }
                exportError = safeMessage(error)
            }
        }
        .fileExporter(isPresented: $saving, item: exportItem, contentTypes: [NotebookShare.contentType],
                      defaultFilename: "\(agent.id)-notebook.ipynb") { result in
            exportItem = nil
            if case .failure = result { exportError = "Could not save the notebook to the selected location." }
        } onCancellation: {
            exportItem = nil
        }
    }

    @ViewBuilder private func rerunStatus(for cell: NotebookCell) -> some View {
        if state.running == cell.id { ProgressView("Running on server…") }
        switch state.outcomes[cell.id] {
        case .unconfirmed(let message): Label(message, systemImage: "exclamationmark.triangle")
        case .result(let replay):
            DisclosureGroup("Rerun: \(replay.run.isError ? "Failed" : "Succeeded") · \(replay.run.durationMs) ms") {
                Text(replay.sameOutput ? "Same output" : "Different output")
                if !replay.sameOutput {
                    Text("Recorded output").font(.caption.bold())
                    output(cell.output)
                    Text("Rerun output").font(.caption.bold())
                    output(replay.run.output)
                }
                Text(replay.sameState ? "Same $State" : "Different $State")
                if !replay.sameState {
                    NotebookStateComparison(first: "Recorded output $State", before: cell.stateOut,
                                            second: "Rerun output $State", after: replay.run.stateOut)
                }
            }
        case nil: EmptyView()
        }
    }

    private func output(_ text: String) -> some View { TranscriptCode(text: text.isEmpty ? "(empty)" : text) }

    private func refresh() async {
        guard active else { return }
        await state.refresh(using: api, agent: agent.id, route: route)
    }

    private func dismissPending() {
        confirming = false
        state.cancelConfirmation()
        exportRequest = nil
    }
}

private struct NotebookStateComparison: View {
    let first: String
    let before: NotebookJSON?
    let second: String
    let after: NotebookJSON?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(first).font(.caption.bold())
            TranscriptCode(text: before?.display ?? "null")
            Text(second).font(.caption.bold())
            TranscriptCode(text: after?.display ?? "null")
        }
    }
}

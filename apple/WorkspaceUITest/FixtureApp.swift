import SwiftUI

@main struct FixtureApp: App {
    var body: some Scene {
        WindowGroup { content }
    }

    private var content: some View {
        WorkspaceView(api: API(endpoint: try! Endpoint("https://sidebar-fixture.example.ts.net"),
                               token: String(repeating: "a", count: 64), transport: fixture),
                      showServers: {}, endPairedSession: nil, routeID: route, networkActive: true,
                      composers: composers)
            #if os(macOS)
            .frame(minWidth: 1000, minHeight: 650)
            #endif
    }
    let route = UUID()
    let fixture = Fixture()
    @State private var composers = ConversationComposers()

    init() {
        // This app has its own bundle and preferences. Reset only on the
        // first launch of a test, not the relaunch that checks persistence.
        if ProcessInfo.processInfo.environment["MARTEN_FIXTURE_RESET"] == "1" {
            UserDefaults.standard.removeObject(forKey: "workspace.collapsedProjects.v1")
        }
    }
}

private struct FixtureMessage: Decodable {
    let text: String
    let mode: String
}

actor Fixture: Transport {
    private var latestReads: [String: Int] = [:]
    private var queuedPending = true
    private var stalePending = true

    private func record(_ line: String) throws {
        guard let trace = ProcessInfo.processInfo.environment["MARTEN_FIXTURE_TRACE"] else { return }
        let file = try FileHandle(forWritingTo: URL(fileURLWithPath: trace))
        defer { try? file.close() }
        try file.seekToEnd()
        try file.write(contentsOf: Data("\(line)\n".utf8))
    }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let url = request.url!
        let path = url.path
        if path == "/api/inbox" || path == "/api/messages" { try record(path) }
        let compactChrome = ProcessInfo.processInfo.environment["MARTEN_FIXTURE_COMPACT_CHROME"] == "1"
        var body = "[]"
        var code = 200
        if path == "/api/status" {
            #if os(macOS)
            let leadID = #", "orchestratorId":"z""#
            let lead = #"{"id":"z","name":"Zulu Lead","kind":"orchestrator","projectId":"p","status":"idle","paused":false,"pausedEffective":false},"#
            #else
            let leadID = ""
            let lead = ""
            #endif
            let alphaDirectory = compactChrome ? #", "workingDirectory":"/fixture/alpha""# : ""
            body = #"""
            {"paused":false,"inbox":5,"openQuestions":2,
            "projects":[{"id":"p","name":"Fixture Project"\#(leadID)},{"id":"q","name":"Second Project"}],"agents":[
              {"id":"a","name":"Standalone Alpha","kind":"chat","status":"idle","paused":false,"pausedEffective":false\#(alphaDirectory)},
              {"id":"b","name":"Project Beta","kind":"task","projectId":"p","status":"idle","paused":false,"pausedEffective":false},
              \#(lead)
              {"id":"c","name":"Closed Gamma","kind":"chat","closed":true,"status":"idle","paused":false,"pausedEffective":false},
              {"id":"d","name":"Closed Project Task","kind":"task","projectId":"q","closed":true,"status":"idle","paused":false,"pausedEffective":false},
              {"id":"e","name":"Active Project Task","kind":"task","projectId":"q","status":"idle","paused":false,"pausedEffective":false}
            ]}
            """#
        } else if request.httpMethod == "POST", path == "/api/agents/a/messages", let data = request.httpBody {
            let message = try JSONDecoder().decode(FixtureMessage.self, from: data)
            try record("send \(message.mode) \(message.text.debugDescription)")
            body = "{}"
        } else if path.hasSuffix("/pending-messages") || path.contains("/pending-messages/") {
            if ProcessInfo.processInfo.environment["MARTEN_FIXTURE_PENDING"] != "1" {
                code = 404
            } else if path == "/api/agents/a/pending-messages", request.httpMethod == "GET" {
                var rows = [#"{"id":"s","text":"Fixture steering","mode":"immediate"}"#]
                if stalePending { rows.append(#"{"id":"stale","text":"Fixture stale","mode":"queued"}"#) }
                if queuedPending { rows.append(#"{"id":"q","text":"Fixture queued","mode":"queued"}"#) }
                body = "[\(rows.joined(separator: ","))]"
            } else if path == "/api/agents/a/pending-messages/q", request.httpMethod == "DELETE", queuedPending {
                try record("recall q")
                queuedPending = false
                body = #"{"id":"q","text":"Fixture recalled text","mode":"queued"}"#
            } else if path == "/api/agents/a/pending-messages/stale", request.httpMethod == "DELETE" {
                try record("recall stale")
                stalePending = false
                code = 404
            } else {
                code = 404
            }
        } else if path.hasSuffix("/transcript") {
            let call = url.lastPathComponent
            let id = url.deletingLastPathComponent().lastPathComponent
            let archived = url.query?.contains("before=") == true
            if id == "a", !archived,
               let gate = ProcessInfo.processInfo.environment["MARTEN_FIXTURE_TRANSCRIPT_GATE"],
               !FileManager.default.fileExists(atPath: gate) {
                if latestReads[id] == nil {
                    latestReads[id] = 0
                    try await Task.sleep(for: .seconds(12))
                }
                return (Data(), HTTPURLResponse(url: url, statusCode: 503, httpVersion: nil, headerFields: nil)!)
            }
            let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            try record("\(id) \(call) \(archived ? "archive" : "latest") \(query.first { $0.name == "limit" }?.value ?? "missing-limit")")
            if ProcessInfo.processInfo.environment["MARTEN_FIXTURE_POSTURE_TRACE"] == "1" {
                try record("transcript-time \(ProcessInfo.processInfo.systemUptime) \(id) \(archived ? "archive" : "latest")")
            }
            if !archived { latestReads[id, default: 0] += 1 }
            let upper = archived ? 80 : 159 + (latestReads[id] ?? 1)
            let entries = ((upper-79)...upper).map { i in
                #"{"kind":"assistant","seq":\#(i),"text":"\#(id.uppercased()) fixture entry \#(i)"}"#
            }.joined(separator: ",")
            body = #"{"entries":[\#(entries)],"hasOlder":\#(!archived)}"#
        } else if path == "/api/agents/a/tasks" {
            body = #"""
            [{"id":1,"title":"Fixture active task","state":"open","assignedBy":"fixture","updated":"2026-01-01T10:00:00Z"},
             {"id":2,"title":"Fixture waiting task","state":"waiting","waitingOn":{"agentId":"b","taskId":9},"assignedBy":"fixture","updated":"2026-01-01T10:00:00Z"},
             {"id":3,"title":"Fixture completed task","state":"done","assignedBy":"fixture","updated":"2026-01-01T10:00:00Z"}]
            """#
        } else if path == "/api/agents/b/tasks" {
            body = #"""
            [{"id":9,"title":"Fixture dependency","state":"done","assignedBy":"fixture","updated":"2026-01-01T10:00:00Z","check":"fixture check","lastCheck":{"at":"2026-01-01T10:00:00Z","passed":true,"exitCode":0,"output":"fixture check output"}}]
            """#
        } else if path == "/api/agents/a/schedules" {
            body = #"[{"id":1,"description":"Fixture schedule","created":"2026-01-01T10:00:00Z","nextRun":"2026-01-01T11:00:00Z","command":"fixture schedule"}]"#
        } else if path == "/api/processes" || path == "/api/agents/a/processes" {
            func process(_ id: Int, _ name: String, _ state: String) -> String {
                #"{"id":\#(id),"agentId":"a","agentName":"Standalone Alpha","name":"\#(name)","state":"\#(state)","pid":100,"started":"2026-01-01T10:00:00Z","command":"fixture worker","workingDirectory":"/fixture"}"#
            }
            let marker = ProcessInfo.processInfo.environment["MARTEN_FIXTURE_EXIT_MARKER"]
            let exited = marker.map { FileManager.default.fileExists(atPath: $0) } ?? false
            var rows = [process(1, "Fixture transitioning worker", exited ? "exited" : "running"),
                        process(2, "Fixture exited worker", "exited")]
            if path == "/api/processes" || url.query == "includeStopped=true" {
                rows.append(process(3, "Fixture stopped worker", "stopped"))
            }
            if path == "/api/processes" { rows.append(process(4, "Fixture global worker", "running")) }
            body = "[\(rows.joined(separator: ","))]"
        } else if path == "/api/agents/a/processes/1/log" {
            return (Data("fixture retained process log".utf8), HTTPURLResponse(url: url, statusCode: 200,
                    httpVersion: nil, headerFields: ["Content-Type": "text/plain"])!)
        }
        return (Data(body.utf8), HTTPURLResponse(url: url, statusCode: code, httpVersion: nil, headerFields: nil)!)
    }
}
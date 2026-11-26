import SwiftUI

@main struct FixtureApp: App {
    var body: some Scene {
        WindowGroup { content }
    }

    private var content: some View {
        WorkspaceView(api: API(endpoint: try! Endpoint("https://sidebar-fixture.example.ts.net"),
                               token: String(repeating: "a", count: 64), transport: fixture),
                      showServers: {}, endPairedSession: nil, routeID: route, networkActive: true)
            #if os(macOS)
            .frame(minWidth: 1000, minHeight: 650)
            #endif
    }
    let route = UUID()
    let fixture = Fixture()

    init() {
        // This app has its own bundle and preferences. Reset only on the
        // first launch of a test, not the relaunch that checks persistence.
        if ProcessInfo.processInfo.environment["MARTEN_FIXTURE_RESET"] == "1" {
            UserDefaults.standard.removeObject(forKey: "workspace.collapsedProjects.v1")
        }
    }
}

actor Fixture: Transport {
    private var latestReads: [String: Int] = [:]

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
        var body = "[]"
        if path == "/api/status" {
            body = #"""
            {"paused":false,"inbox":5,"openQuestions":2,
            "projects":[{"id":"p","name":"Fixture Project"},{"id":"q","name":"Second Project"}],"agents":[
              {"id":"a","name":"Standalone Alpha","kind":"chat","status":"idle","paused":false,"pausedEffective":false},
              {"id":"b","name":"Project Beta","kind":"task","projectId":"p","status":"idle","paused":false,"pausedEffective":false},
              {"id":"c","name":"Closed Gamma","kind":"chat","closed":true,"status":"idle","paused":false,"pausedEffective":false},
              {"id":"d","name":"Closed Project Task","kind":"task","projectId":"q","closed":true,"status":"idle","paused":false,"pausedEffective":false},
              {"id":"e","name":"Active Project Task","kind":"task","projectId":"q","status":"idle","paused":false,"pausedEffective":false}
            ]}
            """#
        } else if path.hasSuffix("/transcript") {
            let call = url.lastPathComponent
            let id = url.deletingLastPathComponent().lastPathComponent
            let archived = url.query?.contains("before=") == true
            let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
            try record("\(id) \(call) \(archived ? "archive" : "latest") \(query.first { $0.name == "limit" }?.value ?? "missing-limit")")
            if !archived { latestReads[id, default: 0] += 1 }
            let upper = archived ? 80 : 159 + (latestReads[id] ?? 1)
            let entries = ((upper-79)...upper).map { i in
                #"{"kind":"assistant","seq":\#(i),"text":"\#(id.uppercased()) fixture entry \#(i)"}"#
            }.joined(separator: ",")
            body = #"{"entries":[\#(entries)],"hasOlder":\#(!archived)}"#
        }
        return (Data(body.utf8), HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!)
    }
}
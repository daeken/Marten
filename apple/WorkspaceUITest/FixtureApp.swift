import SwiftUI

@main struct FixtureApp: App {
    var body: some Scene {
        WindowGroup {
            WorkspaceView(api: API(endpoint: try! Endpoint("https://sidebar-fixture.example.ts.net"), transport: Fixture()),
                          showServers: {}, endPairedSession: nil, routeID: route, networkActive: true)
                .frame(minWidth: 1000, minHeight: 650)
        }
    }
    let route = UUID()
}

actor Fixture: Transport {
    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let url = request.url!
        let path = url.path
        var body = "[]"
        if path == "/api/status" {
            body = #"{"paused":false,"projects":[{"id":"p","name":"Fixture Project"}],"agents":[{"id":"a","name":"Standalone Alpha","kind":"chat","status":"idle","paused":false,"pausedEffective":false},{"id":"b","name":"Project Beta","kind":"task","projectId":"p","status":"idle","paused":false,"pausedEffective":false},{"id":"c","name":"Closed Gamma","kind":"chat","closed":true,"status":"idle","paused":false,"pausedEffective":false}]}"#
        } else if path.hasSuffix("/transcript") {
            let call = url.lastPathComponent
            let id = url.deletingLastPathComponent().lastPathComponent
            let archived = url.query?.contains("before=") == true
            if let trace = ProcessInfo.processInfo.environment["MARTEN_FIXTURE_TRACE"] {
                let file = try FileHandle(forWritingTo: URL(fileURLWithPath: trace))
                defer { try? file.close() }
                try file.seekToEnd()
                try file.write(contentsOf: Data("\(id) \(call) \(archived ? "archive" : "latest")\n".utf8))
            }
            let upper = archived ? 40 : 80
            let entries = ((upper-39)...upper).map { i in
                #"{"kind":"assistant","seq":\#(i),"text":"\#(id.uppercased()) fixture entry \#(i)"}"#
            }.joined(separator: ",")
            body = #"{"entries":[\#(entries)],"hasOlder":\#(!archived)}"#
        }
        return (Data(body.utf8), HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!)
    }
}
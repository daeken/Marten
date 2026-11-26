#if os(iOS)
import XCTest

final class CompactWorkspaceTests: XCTestCase {
    @MainActor func testCompactWorkspace() throws {
        continueAfterFailure = false
        let app = XCUIApplication()
        let trace = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        try Data().write(to: trace)
        defer { try? FileManager.default.removeItem(at: trace) }
        app.launchEnvironment["MARTEN_FIXTURE_TRACE"] = trace.path
        app.launchEnvironment["MARTEN_FIXTURE_RESET"] = "1"
        app.launch()
        defer { app.terminate() }

        func reads() throws -> [String] {
            try String(contentsOf: trace, encoding: .utf8).split(separator: "\n").map(String.init)
        }
        func visible(_ item: XCUIElement) -> Bool { item.exists && item.isHittable }
        func seek(_ item: XCUIElement, down: Bool = true) {
            for _ in 0..<4 {
                if visible(item) { return }
                if down { app.swipeUp() } else { app.swipeDown() }
            }
        }
        func back() {
            app.navigationBars.buttons.firstMatch.tap()
            XCTAssertTrue(app.navigationBars["Marten"].waitForExistence(timeout: 5))
        }
        func entry(_ text: String) -> XCUIElement {
            app.staticTexts.matching(NSPredicate(format: "label == %@ OR value == %@", text, text)).firstMatch
        }
        func assertVisible(_ item: XCUIElement, file: StaticString = #filePath, line: UInt = #line) {
            let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in visible(item) }, object: nil)
            XCTAssertEqual(XCTWaiter.wait(for: [ready], timeout: 10), .completed,
                           app.windows.firstMatch.debugDescription, file: file, line: line)
        }

        XCTAssertTrue(app.navigationBars["Marten"].waitForExistence(timeout: 10))
        let firstProject = app.staticTexts["Fixture Project"].firstMatch
        let beta = app.staticTexts["Project Beta"].firstMatch
        let second = app.staticTexts["Active Project Task"].firstMatch
        seek(firstProject)
        assertVisible(firstProject)
        seek(beta)
        assertVisible(beta)
        seek(firstProject, down: false)
        firstProject.tap()
        XCTAssertTrue(beta.waitForNonExistence(timeout: 4))
        seek(second)
        assertVisible(second)
        seek(firstProject, down: false)
        firstProject.tap()
        seek(beta)
        assertVisible(beta)

        let filter = app.switches["Show Closed Conversations"].firstMatch
        func setFilter(_ on: Bool) {
            filter.coordinate(withNormalizedOffset: CGVector(dx: 0.92, dy: 0.5)).tap()
            XCTAssertEqual(filter.value as? String, on ? "1" : "0")
        }
        let closed = app.staticTexts["Closed Gamma"].firstMatch
        let closedTask = app.staticTexts["Closed Project Task"].firstMatch
        seek(closed)
        XCTAssertFalse(closed.exists)
        XCTAssertFalse(closedTask.exists)
        seek(filter, down: false)
        assertVisible(filter)
        setFilter(true)
        seek(closedTask)
        assertVisible(closedTask)
        seek(closed)
        assertVisible(closed)
        seek(filter, down: false)
        setFilter(false)
        seek(app.staticTexts["Standalone Alpha"].firstMatch)
        XCTAssertFalse(closed.exists)
        XCTAssertFalse(closedTask.exists)
        XCTAssertEqual(try reads(), [])

        let inbox = app.staticTexts["Inbox"].firstMatch
        seek(inbox, down: false)
        assertVisible(app.staticTexts["2 waiting"])
        inbox.tap()
        assertVisible(app.staticTexts["Nothing from your agents yet."])
        XCTAssertEqual(Set(try reads()), ["/api/inbox"])
        back()
        let messages = app.staticTexts["Messages Between Agents"].firstMatch
        seek(messages, down: false)
        messages.tap()
        assertVisible(app.staticTexts["No messages yet."])
        XCTAssertEqual(Set(try reads()), ["/api/inbox", "/api/messages"])
        back()

        let alpha = app.staticTexts["Standalone Alpha"].firstMatch
        seek(alpha)
        alpha.tap()
        assertVisible(entry("A fixture entry 160"))
        let newest = "a transcript latest 80"
        let older = "a transcript archive 80"
        func transcripts() -> [String] { ((try? reads()) ?? []).filter { $0.contains(" transcript ") } }
        XCTAssertEqual(Set(transcripts()), [newest])
        attach(app, name: "Compact latest on first open")
        app.buttons["Older transcript page"].tap()
        let firstEntry = entry("A fixture entry 1")
        assertVisible(firstEntry)
        let count = transcripts().filter { $0 == newest }.count
        let poll = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            transcripts().filter { $0 == newest }.count > count
        }, object: nil)
        XCTAssertEqual(XCTWaiter.wait(for: [poll], timeout: 10), .completed)
        XCTAssertTrue(visible(firstEntry))
        XCTAssertEqual(transcripts().filter { $0 == older }.count, 1)
        attach(app, name: "Compact archive after latest poll")
        let latestEntry = 159 + transcripts().filter { $0 == newest }.count
        app.buttons["Jump to Latest"].tap()
        assertVisible(entry("A fixture entry \(latestEntry)"))
        back()
        seek(beta, down: false)
        beta.tap()
        assertVisible(entry("B fixture entry 160"))
        back()
        let before = transcripts().filter { $0 == newest }.count
        seek(alpha)
        alpha.tap()
        assertVisible(entry("A fixture entry \(160 + before)"))
        XCTAssertEqual(Set(transcripts()), [newest, older, "b transcript latest 80"])
        attach(app, name: "Compact latest on re-entry")
    }

    @MainActor private func attach(_ app: XCUIApplication, name: String) {
        let evidence = XCTAttachment(screenshot: app.screenshot())
        evidence.name = name
        evidence.lifetime = .keepAlways
        add(evidence)
    }
}
#endif

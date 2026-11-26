import XCTest

final class SidebarFixtureTests: XCTestCase {
    @MainActor func testSidebarAndTimeline() throws {
        continueAfterFailure = false
        let app = XCUIApplication()
        let trace = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        try Data().write(to: trace)
        defer { try? FileManager.default.removeItem(at: trace) }
        func reads() -> [String] {
            ((try? String(contentsOf: trace, encoding: .utf8)) ?? "").split(separator: "\n").map(String.init)
        }
        func entry(_ text: String) -> XCUIElement {
            app.staticTexts.matching(NSPredicate(format: "value == %@", text)).firstMatch
        }
        func assertVisible(_ item: XCUIElement, file: StaticString = #filePath, line: UInt = #line) {
            let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in item.exists && item.isHittable }, object: nil)
            XCTAssertEqual(XCTWaiter.wait(for: [ready], timeout: 8), .completed, app.windows.firstMatch.debugDescription, file: file, line: line)
        }
        app.launchEnvironment["MARTEN_FIXTURE_TRACE"] = trace.path
        app.launchEnvironment["MARTEN_FIXTURE_RESET"] = "1"
        app.launch()
        defer { app.terminate() }
        XCTAssertTrue(app.staticTexts["Select a Conversation"].waitForExistence(timeout: 12))
        let project = app.disclosureTriangles.firstMatch
        let beta = app.staticTexts["Project Beta"]
        let second = app.staticTexts["Active Project Task"]
        assertVisible(beta)
        assertVisible(second)
        project.click()
        XCTAssertTrue(beta.waitForNonExistence(timeout: 3))
        assertVisible(second)
        app.terminate()
        app.launchEnvironment.removeValue(forKey: "MARTEN_FIXTURE_RESET")
        app.launch()
        assertVisible(second)
        XCTAssertFalse(beta.exists)
        project.click()
        assertVisible(beta)
        let closed = app.staticTexts["Closed Gamma"]
        let closedTask = app.staticTexts["Closed Project Task"]
        XCTAssertFalse(closed.exists)
        XCTAssertFalse(closedTask.exists)
        app.checkBoxes["Show Closed Conversations"].click()
        assertVisible(closed)
        assertVisible(closedTask)
        app.checkBoxes["Show Closed Conversations"].click()
        XCTAssertTrue(closed.waitForNonExistence(timeout: 3))
        XCTAssertTrue(closedTask.waitForNonExistence(timeout: 3))
        XCTAssertEqual(reads(), [])
        assertVisible(app.staticTexts["2 waiting"])
        app.staticTexts["Inbox"].firstMatch.click()
        assertVisible(app.staticTexts["Nothing from your agents yet."])
        XCTAssertEqual(Set(reads()), ["/api/inbox"])
        app.staticTexts["Messages Between Agents"].firstMatch.click()
        assertVisible(app.staticTexts["No messages yet."])
        XCTAssertEqual(Set(reads()), ["/api/inbox", "/api/messages"])
        let alpha = app.staticTexts["Standalone Alpha"].firstMatch
        alpha.click()
        assertVisible(entry("A fixture entry 160"))
        let latest = "a transcript latest 80"
        let archive = "a transcript archive 80"
        func transcripts() -> [String] { reads().filter { $0.contains(" transcript ") } }
        XCTAssertEqual(Set(transcripts()), [latest])
        app.buttons["Older transcript page"].click()
        let first = entry("A fixture entry 1")
        assertVisible(first)
        let count = transcripts().filter { $0 == latest }.count
        let polled = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            transcripts().filter { $0 == latest }.count > count
        }, object: nil)
        XCTAssertEqual(XCTWaiter.wait(for: [polled], timeout: 8), .completed)
        XCTAssertTrue(first.isHittable, "A new latest page must not move the archive")
        XCTAssertEqual(transcripts().filter { $0 == archive }.count, 1)
        let newest = 159 + transcripts().filter { $0 == latest }.count
        app.buttons["Jump to Latest"].click()
        assertVisible(entry("A fixture entry \(newest)"))
        beta.click()
        assertVisible(entry("B fixture entry 160"))
        let before = transcripts().filter { $0 == latest }.count
        alpha.click()
        assertVisible(entry("A fixture entry \(160 + before)"))
        XCTAssertEqual(Set(transcripts()), [latest, archive, "b transcript latest 80"])
    }
}

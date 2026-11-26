import XCTest

final class SidebarFixtureTests: XCTestCase {
    @MainActor func testSidebarAndTimeline() throws {
        let app = XCUIApplication()
        let trace = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        try Data().write(to: trace)
        defer { try? FileManager.default.removeItem(at: trace) }
        app.launchEnvironment["MARTEN_FIXTURE_TRACE"] = trace.path
        app.launch()
        defer { app.terminate() }
        XCTAssertTrue(app.staticTexts["Select a Conversation"].waitForExistence(timeout: 8))
        let project = app.disclosureTriangles.firstMatch
        let beta = app.staticTexts["Project Beta"]
        XCTAssertTrue(beta.waitForExistence(timeout: 8))
        project.click()
        XCTAssertTrue(beta.waitForNonExistence(timeout: 3))
        project.click()
        XCTAssertTrue(beta.waitForExistence(timeout: 3))
        let closed = app.staticTexts["Closed Gamma"]
        XCTAssertFalse(closed.exists)
        app.checkBoxes["Show Closed Chats"].click()
        XCTAssertTrue(closed.waitForExistence(timeout: 3))
        app.checkBoxes["Show Closed Chats"].click()
        XCTAssertTrue(closed.waitForNonExistence(timeout: 3))
        XCTAssertEqual(try String(contentsOf: trace, encoding: .utf8), "")
        let alpha = app.staticTexts["Standalone Alpha"].firstMatch
        alpha.click()
        let latest = app.staticTexts.matching(NSPredicate(format: "value == %@", "A fixture entry 80")).firstMatch
        XCTAssertTrue(latest.waitForExistence(timeout: 5))
        XCTAssertTrue(latest.isHittable)
        let firstRead = try String(contentsOf: trace, encoding: .utf8).split(separator: "\n")
        XCTAssertFalse(firstRead.isEmpty)
        XCTAssertTrue(firstRead.allSatisfy { $0 == "a transcript latest" })
        app.buttons["Older transcript page"].click()
        let first = app.staticTexts.matching(NSPredicate(format: "value == %@", "A fixture entry 1")).firstMatch
        XCTAssertTrue(first.waitForExistence(timeout: 5))
        XCTAssertTrue(first.isHittable)
        app.buttons["Jump to Latest"].click()
        XCTAssertTrue(latest.waitForExistence(timeout: 5))
        XCTAssertTrue(latest.isHittable)
        beta.click()
        let other = app.staticTexts.matching(NSPredicate(format: "value == %@", "B fixture entry 80")).firstMatch
        XCTAssertTrue(other.waitForExistence(timeout: 5))
        XCTAssertTrue(other.isHittable)
        let reads = try String(contentsOf: trace, encoding: .utf8).split(separator: "\n")
        XCTAssertEqual(reads.filter { $0 == "a transcript archive" }.count, 1)
        XCTAssertTrue(reads.contains("b transcript latest"))
        XCTAssertTrue(reads.allSatisfy { ["a transcript latest", "a transcript archive", "b transcript latest"].contains($0) })
    }
}
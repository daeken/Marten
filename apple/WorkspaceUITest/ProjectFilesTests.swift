#if os(iOS)
import XCTest

final class ProjectFilesTests: XCTestCase {
    @MainActor func testBrowsePreviewAndExplicitLocalShare() throws {
        continueAfterFailure = false
        let app = XCUIApplication()
        let trace = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        try Data().write(to: trace)
        defer { try? FileManager.default.removeItem(at: trace) }
        app.launchEnvironment["MARTEN_FIXTURE_TRACE"] = trace.path
        app.launchEnvironment["MARTEN_FIXTURE_RESET"] = "1"
        app.launchEnvironment["MARTEN_FIXTURE_FILES"] = "1"
        app.launch()
        defer { app.terminate() }
        func entry(_ text: String) -> XCUIElement { app.staticTexts[text].firstMatch }
        func tap(_ text: String) {
            let item = entry(text)
            XCTAssertTrue(item.waitForExistence(timeout: 8), app.debugDescription)
            item.tap()
        }
        func calls() -> [String] {
            ((try? String(contentsOf: trace, encoding: .utf8)) ?? "").split(separator: "\n").map(String.init).filter { $0.hasPrefix("files ") }
        }
        XCTAssertTrue(app.navigationBars["Marten"].waitForExistence(timeout: 10))
        let files = entry("Files")
        for _ in 0..<5 where !files.exists || !files.isHittable { app.swipeUp() }
        XCTAssertTrue(calls().isEmpty)
        tap("Files")
        tap("Demo checkout")
        tap("docs")
        tap("README.md")
        XCTAssertTrue(app.staticTexts["project-file-preview"].firstMatch.waitForExistence(timeout: 8))
        XCTAssertEqual(calls(), ["files roots", "files entries/demo-root", "files entries/docs", "files bytes/readme/text"])
        app.buttons["Share a copy…"].firstMatch.tap()
        let hasShare = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in calls().contains("files bytes/readme/share") }, object: nil)
        XCTAssertEqual(XCTWaiter.wait(for: [hasShare], timeout: 8), .completed)
        let close = app.buttons["Close"].firstMatch
        XCTAssertTrue(close.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertTrue(app.staticTexts["Copy"].firstMatch.exists)
        close.tap()
        XCTAssertTrue(close.waitForNonExistence(timeout: 8))
    }
}
#endif

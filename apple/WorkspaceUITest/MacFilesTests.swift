#if os(iOS)
import XCTest

final class MacFilesTests: XCTestCase {
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
        let files = entry("Mac Files")
        for _ in 0..<5 where !files.exists || !files.isHittable { app.swipeUp() }
        XCTAssertTrue(calls().isEmpty)
        tap("Mac Files")
        XCTAssertTrue(entry("External Volume").waitForExistence(timeout: 8))
        tap("Home")
        let more = app.buttons["Load more"].firstMatch
        XCTAssertTrue(more.waitForExistence(timeout: 8))
        more.tap()
        XCTAssertTrue(entry("sample.bin").waitForExistence(timeout: 8))
        tap("docs")
        tap("README.md")
        let preview = app.buttons["Preview Text"].firstMatch
        XCTAssertTrue(preview.waitForExistence(timeout: 8))
        XCTAssertEqual(calls(), ["files roots", "files entries/demo-root", "files entries/demo-next", "files entries/docs"])
        preview.tap()
        XCTAssertTrue(app.staticTexts["mac-file-preview"].firstMatch.waitForExistence(timeout: 8))
        XCTAssertEqual(calls(), ["files roots", "files entries/demo-root", "files entries/demo-next", "files entries/docs", "files bytes/readme/text"])
        app.buttons["Share a copy…"].firstMatch.tap()
        let hasShare = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in calls().contains("files bytes/readme/share") }, object: nil)
        XCTAssertEqual(XCTWaiter.wait(for: [hasShare], timeout: 8), .completed)
        let close = app.buttons["Close"].firstMatch
        XCTAssertTrue(close.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertTrue(app.staticTexts["Copy"].firstMatch.exists)
        close.tap()
        XCTAssertTrue(close.waitForNonExistence(timeout: 8))
    }

    @MainActor func testFeatureHiddenWithoutCapability() {
        let app = XCUIApplication()
        app.launchEnvironment["MARTEN_FIXTURE_FILES"] = "0"
        app.launch()
        defer { app.terminate() }
        XCTAssertTrue(app.staticTexts["Fixture Project"].firstMatch.waitForExistence(timeout: 8))
        XCTAssertFalse(app.staticTexts["Mac Files"].firstMatch.exists)
    }
}
#endif

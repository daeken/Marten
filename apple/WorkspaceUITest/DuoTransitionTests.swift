import XCTest

/// Opt-in protocol: the operator drives the official Device Hub posture control;
/// XCUI never synthesizes posture or accepts simulator geometry overrides.
final class DuoTransitionTests: XCTestCase {
    @MainActor func testCoverToInnerAndBackMidChat() throws {
        continueAfterFailure = false
        let app = XCUIApplication()
        let trace = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
        try Data().write(to: trace)
        defer { try? FileManager.default.removeItem(at: trace) }
        app.launchEnvironment["MARTEN_FIXTURE_TRACE"] = trace.path
        app.launchEnvironment["MARTEN_FIXTURE_POSTURE_TRACE"] = "1"
        app.launchEnvironment["MARTEN_FIXTURE_RESET"] = "1"
        app.launch()
        defer { app.terminate() }

        let title = app.navigationBars["Standalone Alpha"]
        let window = app.windows.firstMatch
        let draft = app.descendants(matching: .any)["messageDraft"].firstMatch
        let text = "unsent Duo fixture draft"
        let anchor = app.staticTexts["A fixture entry 1"].firstMatch
        func lines() -> [String] {
            ((try? String(contentsOf: trace, encoding: .utf8)) ?? "").split(separator: "\n").map(String.init)
        }
        func times() -> [TimeInterval] {
            lines().compactMap { line in
                let parts = line.split(separator: " ")
                guard parts.count == 4, parts[0] == "transcript-time", parts[2] == "a", parts[3] == "latest" else { return nil }
                return Double(parts[1])
            }
        }
        func wait(_ label: String, timeout: TimeInterval = 12, until ready: () -> Bool,
                  file: StaticString = #filePath, line: UInt = #line) {
            let deadline = ProcessInfo.processInfo.systemUptime + timeout
            repeat {
                if ready() { return }
                RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.25))
            } while ProcessInfo.processInfo.systemUptime < deadline
            XCTFail("Timed out: \(label)\n\(app.debugDescription)", file: file, line: line)
        }
        func checkpoint(_ label: String) {
            wait("\(label): same chat, draft and archived viewport") {
                title.exists && (draft.value as? String) == text && anchor.isHittable
                    && app.staticTexts["Older entries"].exists
            }
            XCTAssertTrue(app.buttons["Agent Actions"].firstMatch.isHittable)
            for label in ["Servers", "Tasks & Schedules", "Background Processes", "PowerShell Notebook"] {
                XCTAssertFalse(app.buttons[label].firstMatch.exists, "Inline duplicate: \(label)")
            }
            let attachment = XCTAttachment(screenshot: app.screenshot())
            attachment.name = label
            attachment.lifetime = .keepAlways
            add(attachment)
        }

        XCTAssertTrue(app.navigationBars["Marten"].waitForExistence(timeout: 10))
        // This runner pins the dedicated Duo. During the display handoff, XCUI
        // can briefly report 155.3 × 226 instead of the 466 × 678 cover.
        func onCover() -> Bool {
            let size = window.frame.size
            return abs(size.width - 466) <= 2 && abs(size.height - 678) <= 2
        }
        if !onCover() {
            XCTContext.runActivity(named: "SETUP ACTION: use official Device Hub to FOLD to the starting cover now") { _ in
                wait("starting portrait cover", timeout: 120, until: onCover)
            }
        }
        let agent = app.staticTexts["Standalone Alpha"].firstMatch
        for _ in 0..<4 where !agent.exists || !agent.isHittable { app.swipeUp() }
        XCTAssertTrue(agent.isHittable)
        agent.tap()
        XCTAssertTrue(title.waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["A fixture entry 160"].waitForExistence(timeout: 10))
        draft.tap()
        draft.typeText(text)
        app.buttons["Older transcript page"].tap()
        checkpoint("01 Cover · archived viewport and unsent draft")
        wait("baseline transcript poll") { times().count >= 2 }
        let baseline = try XCTUnwrap(times().last)

        XCTContext.runActivity(named: "ACTION: use official Device Hub to UNFOLD the dedicated Duo now") { _ in
            // The Duo's default inner display is wider than tall even
            // when the device itself has not rotated.
            wait("wider inner display", timeout: 120) {
                let size = window.frame.size
                return abs(size.width - 951) <= 2 && abs(size.height - 669) <= 2
            }
            checkpoint("02 Inner · same conversation, archive and draft")
        }
        XCTContext.runActivity(named: "ACTION: use official Device Hub to FOLD back to cover now") { _ in
            wait("return to original portrait cover width", timeout: 120) {
                onCover()
            }
            checkpoint("03 Returned cover · same conversation, archive and draft")
        }
        let atReturn = ProcessInfo.processInfo.systemUptime
        wait("normal poll after returning to cover") { (times().last ?? 0) > atReturn }
        let records = lines()
        let traceAttachment = XCTAttachment(string: records.joined(separator: "\n"))
        traceAttachment.name = "Synthetic transcript request trace"
        traceAttachment.lifetime = .keepAlways
        add(traceAttachment)
        let requests = records.filter { $0.contains(" transcript ") }
        XCTAssertEqual(requests.filter { $0 == "a transcript archive 80" }.count, 1)
        XCTAssertTrue(requests.allSatisfy { $0 == "a transcript archive 80" || $0 == "a transcript latest 80" }, records.joined(separator: "\n"))
        XCTAssertFalse(records.contains { $0.hasPrefix("send ") })
        let latest = times().filter { $0 >= baseline }
        for (previous, next) in zip(latest, latest.dropFirst()) {
            // The regular latest poll sleeps 4 s after each request. Allow timing
            // tolerance, but reject a route remount that duplicates the next poll.
            XCTAssertGreaterThanOrEqual(next - previous, 3.5, "Unexpected latest fetch burst around posture change")
        }

        // Confirm the relocated actions are actually available from the menu
        // after returning to cover. CompactWorkspaceTests opens each route.
        app.buttons["Agent Actions"].firstMatch.tap()
        for label in ["Tasks & Schedules", "Background Processes", "PowerShell Notebook", "Settings & Diagnostics"] {
            XCTAssertTrue(app.buttons[label].firstMatch.waitForExistence(timeout: 5), "Missing Agent Actions item: \(label)")
        }
        let menu = XCTAttachment(screenshot: app.screenshot())
        menu.name = "04 Agent Actions after return to cover"
        menu.lifetime = .keepAlways
        add(menu)
    }
}

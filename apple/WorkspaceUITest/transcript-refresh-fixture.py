"""Headless Swift regression; no simulator or real transport is required.

Extract the exact refresh methods and drive the legal SwiftUI task ordering
explicitly. The synthetic GET can return after cancellation, as an in-flight
response might. Run from anywhere with python3 on an Xcode host.
"""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2]
path = 'apple/App/Workspace.swift'
baseline = subprocess.check_output(['git', 'show', f'4c58f1b:{path}'], cwd=root, text=True)
candidate = (root / path).read_text()
assert '.task(id: eventHint) { await refresh() }' in baseline
assert '.onChange(of: eventHint) { if networkActive { refreshLoop.hint() } }' in candidate
assert 'await refreshLoop.run { await refresh() }' in candidate


def method(source):
    start = source.index('    @MainActor private func refresh() async {', source.index('private struct ConversationView'))
    end = source.index('    @MainActor private func move(', start)
    return source[start:end].replace('@MainActor private func', '@MainActor func', 1)


stubs = '''import Foundation
@MainActor final class Gate {
    var waiting: CheckedContinuation<Int, Never>?
    var started: CheckedContinuation<Void, Never>?
    private(set) var requests = 0
    private(set) var cancelledOnReturn = false
    func transcript(_ id: String) async throws -> Int {
        requests += 1
        let response = await withCheckedContinuation {
            waiting = $0
            started?.resume(); started = nil
        }
        cancelledOnReturn = Task.isCancelled
        return response
    }
    func waitForGET() async { if waiting == nil { await withCheckedContinuation { started = $0 } } }
    func finish() { let c = waiting; waiting = nil; c?.resume(returning: requests) }
}
struct Timeline {
    var latest: Int?
    var archive = 90
    mutating func updateLatest(_ page: Int) { latest = page }
}
struct Agent { var id = "fixture" }
func safeMessage(_ error: Error) -> String { "fixture error" }
'''
loop = candidate[candidate.index('@MainActor final class TranscriptRefreshLoop'):candidate.index('private struct ConversationView')]
legacy = '''@MainActor final class LegacyView {
    let api = Gate(), agent = Agent()
    var timeline = Timeline()
    var refreshError: String?
    var networkActive = true, checking = false
''' + method(baseline) + '}\n'
patched = '''@MainActor final class PatchedView {
    let api = Gate(), agent = Agent(), refreshLoop = TranscriptRefreshLoop()
    var timeline = Timeline()
    var refreshError: String?
    var networkActive = true
    var routeID = UUID()
    var latestRequest: (id: UUID, route: UUID)?
''' + method(candidate) + '}\n'

driver = '''@main struct Fixture {
    @MainActor static func main() async {
        let old = LegacyView()
        let first = Task { await old.refresh() }
        await old.api.waitForGET()
        await old.refresh() // Initial poll skips while the initial event task owns checking.
        first.cancel() // Hint changes the task id; transport has not yet returned.
        await old.refresh() // Replacement skips before the cancelled task releases checking.
        old.api.finish()
        await first.value
        precondition(old.api.requests == 1 && old.api.cancelledOnReturn && old.timeline.latest == nil)
        let nextPoll = Task { await old.refresh() }
        await old.api.waitForGET(); old.api.finish(); await nextPoll.value
        precondition(old.timeline.latest == 2)
        print("Baseline: hint cancels first GET; no latest page until next poll")

        let view = PatchedView()
        // Disable the real-time tick, so the only possible second GET is the hint.
        let noTick: @Sendable () async throws -> Void = { try await Task.sleep(for: .seconds(600)) }
        let route = Task { await view.refreshLoop.run(waitForPoll: noTick) { await view.refresh() } }
        await view.api.waitForGET()
        for _ in 0..<5 { view.refreshLoop.hint() }
        precondition(view.api.requests == 1 && view.timeline.latest == nil)
        view.api.finish()
        await view.api.waitForGET()
        precondition(view.timeline.latest == 1 && view.api.requests == 2 && !view.api.cancelledOnReturn)
        precondition(view.timeline.archive == 90)
        route.cancel() // Models disappearance/route-id change/backgrounding.
        view.api.finish()
        await route.value
        precondition(view.api.cancelledOnReturn && view.timeline.latest == 1 && view.api.requests == 2)
        print("Candidate: first page publishes; hint burst queues one GET; route cancellation suppresses late response")

        let replacement = Task { await view.refreshLoop.run(waitForPoll: noTick) { await view.refresh() } }
        await view.api.waitForGET()
        replacement.cancel()
        view.api.finish()
        await replacement.value
        precondition(view.api.requests == 3 && view.timeline.latest == 1)
        print("Candidate: replacement route starts immediately and also cancels its first delayed GET")

        let fallback = Task { await view.refreshLoop.run(waitForPoll: {}) { await view.refresh() } }
        await view.api.waitForGET()
        view.api.finish()
        await view.api.waitForGET()
        precondition(view.api.requests == 5 && view.timeline.latest == 4)
        fallback.cancel(); view.api.finish(); await fallback.value
        print("Candidate: fallback poll fetches when no hints arrive")
    }
}
'''
with tempfile.TemporaryDirectory(prefix='.transcript-refresh-', dir=root) as tmp:
    swift = Path(tmp) / 'Fixture.swift'
    swift.write_text(stubs + loop + legacy + patched + driver)
    binary = swift.with_name('fixture')
    subprocess.run(['xcrun', 'swiftc', '-swift-version', '6', '-parse-as-library', str(swift), '-o', str(binary)], check=True)
    subprocess.run([str(binary)], check=True, timeout=10)

import Foundation
import XCTest
@testable import Marten

final class WorkspaceDirectoryTests: XCTestCase {
    func testOrchestratorLeadsOnlyItsOwnVisibleProjectMembers() throws {
        let status = try JSONDecoder().decode(ServerStatus.self, from: Data(#"""
        {"projects":[{"id":"p","name":"Project","orchestratorId":"o"},{"id":"q","name":"Other","orchestratorId":"o"}],"agents":[
          {"id":"m","name":"Middle","kind":"task","status":"idle","projectId":"p","paused":false,"pausedEffective":false},
          {"id":"o","name":"Zulu","kind":"orchestrator","status":"idle","projectId":"p","closed":true,"paused":false,"pausedEffective":false},
          {"id":"a","name":"Alpha","kind":"task","status":"idle","projectId":"p","paused":false,"pausedEffective":false},
          {"id":"q","name":"Other member","kind":"task","status":"idle","projectId":"q","paused":false,"pausedEffective":false}
        ]}
        """#.utf8))

        func members(_ project: String, showClosed: Bool = false, selected: WorkspaceDestination? = nil) -> [String]? {
            WorkspaceDirectory(projects: status.projects, agents: status.agents, showClosed: showClosed, selected: selected).members[project]?.map(\.id)
        }
        XCTAssertEqual(members("p"), ["a", "m"])
        XCTAssertEqual(members("p", showClosed: true), ["o", "a", "m"])
        XCTAssertEqual(members("p", selected: .agent("o")), ["o", "a", "m"])
        XCTAssertEqual(members("q", showClosed: true), ["q"])
    }

    func testSidebarGroupsAndKeepsSelectedClosedChatUntilLeaving() throws {
        let status = try JSONDecoder().decode(ServerStatus.self, from: Data(#"""
        {"projects":[{"id":"b","name":"Beta"},{"id":"a","name":"Alpha"}],"agents":[
          {"id":"t","name":"Child","kind":"task","status":"idle","projectId":"a","paused":false,"pausedEffective":false},
          {"id":"d","name":"Closed Child","kind":"task","status":"idle","projectId":"a","closed":true,"paused":false,"pausedEffective":false},
          {"id":"s","name":"Standalone","kind":"chat","status":"idle","paused":false,"pausedEffective":false},
          {"id":"c","name":"Closed","kind":"chat","status":"idle","closed":true,"paused":false,"pausedEffective":false},
          {"id":"m","name":"Missing Project","kind":"task","status":"idle","projectId":"missing","paused":false,"pausedEffective":false}
        ]}
        """#.utf8))

        func organize(showClosed: Bool = false, selected: WorkspaceDestination? = nil) -> WorkspaceDirectory {
            WorkspaceDirectory(projects: status.projects, agents: status.agents, showClosed: showClosed, selected: selected)
        }

        let active = organize()
        XCTAssertEqual(active.projects.map(\.id), ["a", "b"])
        XCTAssertEqual(active.members["a"]?.map(\.id), ["t"])
        XCTAssertEqual(active.standalone.map(\.id), ["m", "s"])
        XCTAssertTrue(active.hasClosedChats)
        XCTAssertEqual(organize(selected: .agent("c")).standalone.map(\.id), ["c", "m", "s"])
        XCTAssertEqual(organize(showClosed: true).standalone.map(\.id), ["c", "m", "s"])
        XCTAssertEqual(organize(selected: .agent("d")).members["a"]?.map(\.id), ["t", "d"])
        XCTAssertEqual(organize(showClosed: true).members["a"]?.map(\.id), ["t", "d"])
    }
}

#!/bin/bash
# Default: unlocked Mac desktop. With a UDID, target only a dedicated test simulator.
set -euo pipefail
umask 077
here=$(cd "$(dirname "$0")" && pwd -P)
scratch=$(mktemp -d "${TMPDIR:-/tmp}/marten-workspace-ui.XXXXXX")
trap 'rm -rf "$scratch"' EXIT
mkdir "$scratch/App" "$scratch/UITests"
cp "$here"/../App/*.swift "$scratch/App/"
cp "$here/FixtureApp.swift" "$scratch/App/MartenApp.swift"
if [[ $# == 0 ]]; then
    destination=platform=macOS
    cp "$here/SidebarFixtureTests.swift" "$scratch/UITests/"
    cp "$here/project.yml" "$scratch/"
elif [[ $# == 1 && $1 =~ ^[[:xdigit:]-]{36}$ ]]; then
    destination="platform=iOS Simulator,id=$1"
    cp "$here/CompactWorkspaceTests.swift" "$scratch/UITests/"
    cp "$here/project-ios.yml" "$scratch/project.yml"
else
    echo "Usage: $0 [dedicated-simulator-UDID]" >&2
    exit 2
fi
action=test
if [[ ${MARTEN_FIXTURE_BUILD_ONLY:-0} == 1 ]]; then action=build-for-testing; fi
selection=()
if [[ ${MARTEN_FIXTURE_DUO_TRANSITION:-0} == 1 ]]; then
    if [[ $# != 1 || $1 != 5F2956AD-A279-428F-B7DB-2DBF7E026AD7 ]]; then
        echo 'Transition fixture requires the dedicated Marten SwiftUI Duo UDID.' >&2
        exit 2
    fi
    cp "$here/DuoTransitionTests.swift" "$scratch/UITests/"
    selection=(-only-testing:SidebarFixtureUITests/DuoTransitionTests)
fi
result=()
if [[ -n ${MARTEN_FIXTURE_RESULT:-} ]]; then
    result=(-resultBundlePath "$MARTEN_FIXTURE_RESULT")
fi
xcodegen generate --spec "$scratch/project.yml"
xcodebuild "$action" -project "$scratch/SidebarFixture.xcodeproj" -scheme SidebarFixture \
    -destination "$destination" -derivedDataPath "$scratch/build" \
    ${result[@]+"${result[@]}"} ${selection[@]+"${selection[@]}"} -parallel-testing-enabled NO CODE_SIGNING_ALLOWED=NO

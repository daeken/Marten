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
result=()
if [[ -n ${MARTEN_FIXTURE_RESULT:-} ]]; then
    result=(-resultBundlePath "$MARTEN_FIXTURE_RESULT")
fi
xcodegen generate --spec "$scratch/project.yml"
xcodebuild test -project "$scratch/SidebarFixture.xcodeproj" -scheme SidebarFixture \
    -destination "$destination" -derivedDataPath "$scratch/build" \
    ${result[@]+"${result[@]}"} -parallel-testing-enabled NO CODE_SIGNING_ALLOWED=NO

#!/bin/bash
# Run only in an unlocked macOS desktop session; XCUI drives a disposable window.
set -euo pipefail
umask 077
here=$(cd "$(dirname "$0")" && pwd -P)
scratch=$(mktemp -d "${TMPDIR:-/tmp}/marten-workspace-ui.XXXXXX")
trap 'rm -rf "$scratch"' EXIT
mkdir "$scratch/App" "$scratch/UITests"
cp "$here"/../App/*.swift "$scratch/App/"
cp "$here/FixtureApp.swift" "$scratch/App/MartenApp.swift"
cp "$here/SidebarFixtureTests.swift" "$scratch/UITests/"
cp "$here/project.yml" "$scratch/"
xcodegen generate --spec "$scratch/project.yml"
xcodebuild test -project "$scratch/SidebarFixture.xcodeproj" -scheme SidebarFixture \
    -destination 'platform=macOS' -derivedDataPath "$scratch/build" \
    -parallel-testing-enabled NO CODE_SIGNING_ALLOWED=NO

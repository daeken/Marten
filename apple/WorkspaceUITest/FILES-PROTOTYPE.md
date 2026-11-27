# Project Files: iOS fixture only

This prototype is based on SwiftUI #51 but does not modify its branch or production Xcode target. `MARTEN_FILES_PROTOTYPE` is set only for the iOS workspace fixture. That fixture shows Files under the first project only when its launch environment has `MARTEN_FIXTURE_FILES=1`. Its transport uses deterministic fixture bytes; no server, physical device, pair credential or live workspace is needed.

```sh
MARTEN_FIXTURE_FILES_ONLY=1 bash apple/WorkspaceUITest/run.sh <dedicated-iOS-simulator-UDID>
```

The fixture test covers project → root → subfolder → plain-text preview, then explicit fetch, local system share sheet and cancel. Listing uses opaque cursor/entry IDs; the view also supports PNG/JPEG preview with input, dimension and pixel limits. Unsupported types are share-only, within the download cap. Share uses a protected private temp file; no network URL is exported.

Before production wiring: add a real bounded stream-to-file transport behind the existing no-redirect, no-cache, exact canonical authority proxy; keep the fixture/data adapter out of the production target. Exercise late route-switch responses, raster/unsupported inputs, short/oversize replies, and confirm local temp cleanup and Duo posture changes on scratch builds. The production backend is separate and not required for this prototype.

# SensorFlex Player WebSocket Backend Task

## Abstract

Implement WebSocket replay input as a first-class backend alongside ZIP without
changing the upper-level player architecture. The target outcome is that
`FrameLoader`, camera replay, and future depth/occlusion consumers can work
from either transport with the same logical loader state and without adding
source-specific branching in upper layers.

This is primarily a backend parity and product-surface task. It includes the
network protocol, buffering behavior, loader-state population, and the editor
configuration path a developer uses to select ZIP or WebSocket input. ZIP
should remain the semantic reference model, while WebSocket should become a
transport that streams equivalent scene metadata and per-frame payloads.

Key action items:

- define and document the target WebSocket protocol around ZIP-equivalent scene
  metadata and frame payloads
- refactor the WebSocket backend so it populates the same `FrameLoader` state
  fields ZIP already uses, including timing, pose, intrinsics, and optional
  depth
- preserve a warm-up and bounded streaming-buffer model so WebSocket playback
  behaves like a real streaming peer of ZIP instead of a one-shot image preload
- keep configuration inside the existing `ARSensorFlexSession` inspector flow,
  including choosing ZIP vs WebSocket and editing the WebSocket server URL from
  the GUI
- provide a development test path, using a Node.js WebSocket server that reads
  a ZIP replay and streams the refactored protocol for backend validation

## Engineering Notes

- do the work on `task/websocket-backend`, not on `main`
- make incremental commits as meaningful parts of the task are completed
- do not wait until the very end to create one large mixed commit
- prefer commits that isolate protocol definition, backend refactor steps,
  editor GUI updates, and documentation updates into reviewable units
- keep upper-layer architecture unchanged unless a concrete blocker is proven

## Purpose

This document is a handoff for implementing proper WebSocket backend support in
the SensorFlex player package.

The product goal is:

1. keep the current upper-level runtime architecture intact
2. keep `FrameLoader` as the integration seam
3. support replay input from either:
   - ZIP archive backend
   - WebSocket backend
4. make those two backends deliver the same logical data streams to the rest of
   the runtime

File-system input exists and should be left alone for this task.

This is not a rendering task. It is an IO/backend parity task.

Additional explicit requirement:

- the WebSocket backend should preserve a warm-up / streaming-buffer mechanism
  comparable to the current ZIP backend behavior
- editor GUI configuration must work cleanly for both ZIP and WebSocket modes
- a user must be able to choose ZIP or WebSocket from the inspector/editor UI
- when WebSocket is selected, the server URL must be configurable from the GUI
- this editor flow is part of the product surface, not a nice-to-have for
  internal testing

The backend should not regress into a naive image-only or decode-directly-in-
callbacks path.

Branch requirement:

- this work should be done on a `task/websocket-backend` branch
- do not implement it directly on `main`

---

## Core Requirement

The WebSocket backend must become a true peer of the ZIP backend.

That means:

- upper layers should not care whether replay data came from ZIP or WebSocket
- the camera subsystem should continue reading from `FrameLoader`
- the occlusion path should continue reading from loader state / subsystem APIs
- no app-level architectural changes should be required

The correct implementation direction is to improve backend capability while
preserving the current abstraction boundary:

- `IFrameLoaderBackend`
- `IFrameLoaderState`
- `FrameLoader`

and not to add source-specific branching throughout upper layers.

---

## Repositories And Files

Primary repo:

- `<repo-root>/Packages/com.sensorflex.player.unity`

Most relevant code:

- `Runtime/Library/FrameLoading.cs`
- `Runtime/Subsystem/Camera.cs`
- `Runtime/Subsystem/Depth.cs`
- `Runtime/ARSensorFlexSession.cs`
- `Docs/Architecture.md`
- `Docs/ZipFormat.md`
- `Editor/SensorFlexSettingsEditor.cs`

Current testbed README still documents the old WebSocket contract:

- `<repo-root>/README.md`

Configuration files already in scope:

- `Runtime/ARSensorFlexSession.cs`
- `Editor/SensorFlexSettingsEditor.cs`

---

## Current Architecture Summary

The current runtime is already structured in the right general shape.

`FrameLoader` owns a shared state object and delegates source-specific work to
an `IFrameLoaderBackend`.

The shared state can already carry:

- `Frames`
- `DepthBins`
- `Poses`
- `Intrinsics`
- ring-buffer readiness
- global frame index mapping
- coordinate conversion information
- frame interval

That is enough to support both ZIP and WebSocket without changing the upper
architecture, provided the WebSocket backend fills the same state correctly.

This is important:

- the architecture is not the blocker
- backend completeness is the blocker
- warm-up / bounded buffering behavior is part of that completeness
- editor configurability is part of product completeness

---

## Current State Of ZIP

The ZIP backend is currently the most complete backend.

It already does all of the following:

- reads scene-level `meta.json`
- sets `TotalFrames`
- sets `FrameInterval`
- sets `CoordConvMatrix`
- sets `UseNegativeZForwardOpticalAxis`
- streams RGB frames on a background thread
- uploads textures on the main thread
- stores `DepthBins`
- stores `Poses`
- stores `Intrinsics`
- uses ring-buffer semantics with `SlotReady` and `SlotGlobalIdx`

In other words, ZIP already fills the loader state with nearly all data the
upper layers care about.

This should be treated as the reference backend behavior.

---

## Current State Of WebSocket

The current WebSocket backend is intentionally much thinner.

Today it does:

- connect to `session.WebSocketUrl`
- send `GET_FRAMES <count>` on open
- receive binary messages
- parse the first 4 bytes as frame index
- decode the remaining bytes as PNG/JPG
- write textures directly into `state.Frames`
- mark the loader ready once enough RGB frames are received

Today it does **not** do:

- scene-level metadata handshake
- frame count discovery beyond requested preload count
- frame interval / FPS negotiation
- pose delivery
- per-frame intrinsics delivery
- depth delivery
- coordinate-system metadata delivery
- ring-buffer style streaming parity with ZIP
- a protocol version contract
- warm-up refill behavior comparable to ZIP

This is why upper layers currently have much better behavior with ZIP than with
WebSocket.

On the editor side, the current code already has the right basic shape:

- `ARSensorFlexSession` serializes `m_FrameSourceMode`, `m_WebSocketUrl`, and
  `m_ZipFilePath`
- `SensorFlexSettingsEditor` already switches visible fields by source mode

That should be preserved and tightened during this task, not bypassed.

---

## Explicit Constraint

Do not solve this by changing upper-level architecture.

Avoid:

- adding source-specific if/else logic in camera / occlusion subsystems
- teaching subsystems to talk to WebSocket directly
- adding separate data paths that bypass `FrameLoader`
- changing app-facing APIs just to fit WebSocket

Prefer:

- keeping `FrameLoader` and shared state as the only backend integration seam
- making WebSocket fill the same shared state fields as ZIP
- reusing ZIP parsing assumptions and data contracts where possible
- keeping source-mode selection and WebSocket URL configuration exposed through
  the editor GUI

---

## Product Goal Restated As A Backend Rule

For the rest of the runtime, the following should be true:

- ZIP and WebSocket are different transport layers
- they are not different data models

The upper-level runtime should consume:

- replay frames
- replay pose
- replay intrinsics
- replay depth
- replay timing

without needing to know whether the bytes arrived from:

- an archive reader
- a streaming server

At authoring time, the user should also be able to choose the source cleanly in
the inspector without hand-editing assets or code.

---

## Recommended Design Direction

Use ZIP as the semantic reference and make WebSocket deliver the same logical
payloads.

That implies the WebSocket protocol should expose:

- scene/session metadata
- frame payloads
- per-frame metadata
- optional depth payloads

The easiest long-term model is:

- same logical archive fields
- different transport

Meaning:

- the ZIP archive layout remains the source-of-truth schema
- the WebSocket protocol serializes the same concepts over the network

This will minimize special cases and documentation drift.

The same principle should apply to editor configuration:

- source selection should stay one coherent UI flow
- ZIP path and WebSocket URL should remain source-specific fields in the same
  existing configuration surface
- this should not require custom temporary debug scripts or manual asset edits

---

## Proposed WebSocket Capability Target

The WebSocket backend should eventually be able to populate all of:

- `state.FrameInterval`
- `state.TotalFrames` when known, or a streaming equivalent when not fixed
- `state.CoordConvMatrix`
- `state.UseNegativeZForwardOpticalAxis`
- `state.Frames`
- `state.DepthBins`
- `state.Poses`
- `state.Intrinsics`
- `state.SlotReady`
- `state.SlotGlobalIdx`

That is the minimum needed for parity with ZIP-driven upper-layer behavior.

---

## Main Gaps To Solve

### 1. Missing protocol contract

There is currently no real protocol beyond:

- `GET_FRAMES <count>`
- raw image responses with frame index prefix

This is not sufficient for parity.

Need to define:

- handshake message(s)
- protocol versioning
- scene metadata message format
- frame metadata message format
- RGB payload encoding
- depth payload encoding
- ordering / sequencing behavior
- end-of-stream / looping semantics
- error signaling

Recommendation:

- keep the control plane lightweight and human-readable if useful
- encode heavy frame payloads as binary packets
- avoid base64 for RGB/depth transport because it adds avoidable client decode
  overhead

### 2. Missing metadata path

The ZIP backend derives critical camera behavior from scene and frame metadata.

The WebSocket backend currently provides none of that.

Need to deliver at least:

- FPS / frame interval
- pose matrix
- intrinsics
- coordinate-system metadata
- optional total frame count

### 3. Missing depth path

`DepthBins` are already part of loader state, but WebSocket never fills them.

Need to decide:

- whether depth is sent inline with frame messages
- or as separate typed messages

Recommendation:

- use separate typed payloads or structured frame packets, not ad hoc image-only
  responses
- prefer binary frame packets over JSON+base64 payloads

### 4. Mismatch in buffering model

ZIP uses:

- background enqueue
- main-thread upload
- ring-buffer slots
- global frame index mapping

WebSocket currently uses:

- direct texture creation during message handling
- linear preload semantics only

Need to bring WebSocket closer to ZIP's buffering model so upper-layer playback
behavior stays consistent.

This should explicitly include:

- initial warm-up before playback becomes ready
- bounded future-frame buffering
- a refill strategy as playhead advances
- no unbounded queue growth
- no requirement that the entire stream be preloaded before playback can begin

### 5. No streaming story beyond initial preload

The current WebSocket backend behaves like "request N frames up front" and stop.

Need to decide whether WebSocket support means:

- bounded preload only, or
- real continuous streaming backend

Given the architecture and ZIP parity goal, the correct target is:

- real backend support for ongoing frame progression
- with an explicit warm-up / refill buffer model

---

## Warm-Up Buffer Requirement

This requirement is important enough to call out separately.

The WebSocket backend should still behave like a streaming backend with a
warm-up threshold, not like a one-shot blob loader.

Desired behavior:

- receive enough future frames to satisfy `framesToWait`
- mark the loader ready only after warm-up threshold is reached
- continue refilling as playback advances
- keep memory bounded through the same slot/global-index style model already
  used by ZIP

This matters because the rest of the runtime already assumes a backend can:

- become ready after warm-up
- play from buffered future frames
- avoid direct dependency on transport jitter

If WebSocket mode cannot maintain this, upper-level behavior will diverge from
ZIP in ways that violate the interchangeable-backend goal.

---

## Editor Configuration Requirement

This backend work is not complete unless the editor configuration path also
works cleanly.

Required behavior:

- user can choose frame source mode from the existing editor/inspector UI
- user can choose ZIP mode and configure ZIP path in the GUI
- user can choose WebSocket mode and configure server URL in the GUI
- source-specific fields should appear or hide appropriately based on mode
- no manual asset text editing should be required for normal use

Relevant files:

- `Runtime/ARSensorFlexSession.cs`
- `Editor/SensorFlexSettingsEditor.cs`

Implementation expectation:

- keep source selection on `ARSensorFlexSession` as the single configuration
  surface used by runtime startup
- keep the inspector as the normal way a developer selects ZIP vs WebSocket
- do not require hidden debug components, hard-coded URLs, or hand-edited scene
  YAML to switch backend
- if backend refactoring changes configuration needs, extend the existing
  inspector flow instead of introducing a parallel configuration path

Validation:

- in the inspector, switching between ZIP and WebSocket updates the visible
  configuration fields correctly
- the WebSocket URL is editable and persisted
- the ZIP path remains editable and persisted
- entering Play mode uses the currently selected source mode and configured URL
  / ZIP path without additional code changes

---

## Suggested Protocol Direction

Do not treat this section as final law, but it is the recommended path.

### High-level principle

The WebSocket protocol should mirror ZIP semantics:

- one scene/session metadata payload
- repeated per-frame payloads
- each frame can carry:
  - RGB bytes
  - frame metadata
  - optional depth bytes

### Message categories

At minimum, define messages for:

- protocol / hello
- scene metadata
- frame packet
- end-of-stream
- error

Recommended transport split:

- control / handshake messages may be text or JSON
- frame packets should be binary

### Scene metadata should cover

- scene id
- total frame count if known
- fps
- coordinate system handedness / forward axis
- any information needed to reconstruct the same `FrameLoaderState` values ZIP
  currently sets from archive metadata

### Frame metadata should cover

- global frame index
- timestamp if available
- pose
- intrinsics
- optional depth presence flag

Recommendation:

- keep frame metadata inside the binary frame packet header or a compact UTF-8
  metadata segment inside that packet

Suggested development packet layout for frame messages:

```text
4 bytes  magic      "SFWP"
2 bytes  version    UInt16 LE
2 bytes  type       UInt16 LE
4 bytes  frameIndex UInt32 LE
4 bytes  rgbLength  UInt32 LE
4 bytes  metaLength UInt32 LE
4 bytes  depthLength UInt32 LE
N bytes  rgb payload
M bytes  UTF-8 meta.json payload
K bytes  depth.bin payload
```

This is only a suggested first refactor target, but it is intentionally close
to the ZIP data model and easy to decode in Unity.

### Depth payload should prefer ZIP-compatible semantics

Prefer:

- raw float32 little-endian metric depth

If depth is lower resolution than RGB, that is acceptable so long as metadata
and loader behavior are consistent with the existing ZIP interpretation.

---

## Implementation Rule: Preserve Upper-Level Interfaces

If new backend capability is needed, add it behind backend/state boundaries.

Allowed kinds of change:

- enrich `WebSocketFrameLoaderBackend`
- add helper parsing / message DTOs
- add loader-state fields only if both ZIP and WebSocket can benefit
- add backend-local worker queues if needed

Avoid changing:

- how `CameraSubsystem` decides which backend to use
- how upper-level systems consume `FrameLoader`
- how scene components configure the source mode at a high level

If a change forces app-facing or subsystem-facing API changes, the design should
be challenged first.

---

## Development Test Server

Below is a development-only Node.js test server that can be used while
implementing and debugging the WebSocket backend.

Purpose:

- load a SensorFlex ZIP replay
- expose it through WebSocket for backend testing
- stream the target refactored data model directly
- provide a concrete backend for protocol investigation and client refactoring

This script is not intended as a production backend. It is a developer utility
for testing and evolving the WebSocket loader.

### Requirements

- Node.js 18+
- `npm install ws jszip`

### Example Usage

```bash
npm install ws jszip
node ws_zip_test_server.js --zip <absolute-path-to-scene.zip> --port 3000
```

### Commands Supported By The Script

- `GET_ZIP_INFO`
- `STREAM_ZIP_FRAMES <start> <count> [intervalMs]`

These commands use:

- JSON/text for lightweight control replies
- binary packets for frame payloads

That matches the intended refactor direction more closely than JSON+base64 frame
payloads.

### Why The Script Exists

The repo previously only documented a minimal image-only WebSocket assumption.

This script gives the next developer:

- a concrete backend they can run immediately
- a way to prototype ZIP-equivalent WebSocket messages
- a way to refactor the Unity client toward the intended protocol instead of the
  legacy image-only contract

### Development Script

```js
#!/usr/bin/env node

/*
 * Development-only WebSocket test server for SensorFlex Player backend work.
 *
 * Requirements:
 * - Node.js 18+
 * - npm install ws jszip
 *
 * Supported commands:
 *
 * - GET_ZIP_INFO
 *   Sends one JSON message describing the ZIP scene and frame count.
 *
 * - STREAM_ZIP_FRAMES <start> <count> [intervalMs]
 *   Sends binary frame packets.
 */

const fs = require("fs");
const path = require("path");
const WebSocket = require("ws");
const JSZip = require("jszip");

function usage() {
  console.error(
    "Usage: node ws_zip_test_server.js --zip <path> [--port 3000] [--host 127.0.0.1]"
  );
  process.exit(1);
}

function parseArgs(argv) {
  const args = {
    zip: "",
    port: 3000,
    host: "127.0.0.1",
  };

  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--zip") {
      args.zip = argv[++i] || "";
    } else if (arg === "--port") {
      args.port = Number(argv[++i] || "3000");
    } else if (arg === "--host") {
      args.host = argv[++i] || "127.0.0.1";
    } else {
      usage();
    }
  }

  if (!args.zip) {
    usage();
  }

  args.zip = path.resolve(args.zip);
  if (!fs.existsSync(args.zip)) {
    throw new Error(`ZIP file not found: ${args.zip}`);
  }

  if (!Number.isFinite(args.port) || args.port <= 0) {
    throw new Error(`Invalid port: ${args.port}`);
  }

  return args;
}

async function loadZip(zipPath) {
  const bytes = fs.readFileSync(zipPath);
  return JSZip.loadAsync(bytes);
}

async function zipEntries(zip) {
  return Object.keys(zip.files).filter((entry) => !zip.files[entry].dir);
}

async function zipRead(zip, entryPath, asText = false) {
  const entry = zip.file(entryPath);
  if (!entry) {
    throw new Error(`ZIP entry not found: ${entryPath}`);
  }

  return asText ? entry.async("string") : entry.async("nodebuffer");
}

async function buildZipIndex(zipPath) {
  const zip = await loadZip(zipPath);
  const entries = await zipEntries(zip);
  const sceneMetaEntry = entries.find(
    (entry) => entry.endsWith("/meta.json") && !entry.includes("/frames/")
  );

  if (!sceneMetaEntry) {
    throw new Error("Could not find scene meta.json in ZIP.");
  }

  const sceneMeta = JSON.parse(await zipRead(zip, sceneMetaEntry, true));
  const sceneId = sceneMeta.scene_id || sceneMetaEntry.split("/")[0];
  const rgbEntries = entries
    .filter((entry) => new RegExp(`^${sceneId}/frames/\\d{6}/rgb\\.(jpg|jpeg|png)$`, "i").test(entry))
    .sort();

  if (rgbEntries.length === 0) {
    throw new Error(`No RGB frame entries found for scene '${sceneId}'.`);
  }

  const frames = rgbEntries.map((rgbEntry) => {
    const prefix = rgbEntry.replace(/rgb\.(jpg|jpeg|png)$/i, "");
    const frameIndex = Number(prefix.match(/frames\/(\d{6})\//)?.[1] || "-1");
    return {
      frameIndex,
      prefix,
      rgbEntry,
      metaEntry: `${prefix}meta.json`,
      depthEntry: `${prefix}depth.bin`,
      hasMeta: entries.includes(`${prefix}meta.json`),
      hasDepth: entries.includes(`${prefix}depth.bin`),
    };
  });

  return {
    zipPath,
    zip,
    sceneId,
    sceneMetaEntry,
    sceneMeta,
    frames,
  };
}

async function readFrameBundle(zipIndex, frameIndex) {
  const frame = zipIndex.frames.find((item) => item.frameIndex === frameIndex);
  if (!frame) {
    return null;
  }

  const rgb = await zipRead(zipIndex.zip, frame.rgbEntry);
  const meta = frame.hasMeta ? JSON.parse(await zipRead(zipIndex.zip, frame.metaEntry, true)) : null;
  const depth = frame.hasDepth ? await zipRead(zipIndex.zip, frame.depthEntry) : null;

  return {
    frameIndex: frame.frameIndex,
    rgb,
    meta,
    depth,
  };
}

function buildFramePacket(bundle) {
  const magic = Buffer.from("SFWP", "ascii");
  const version = 1;
  const typeFrame = 1;
  const metaBytes = bundle.meta
    ? Buffer.from(JSON.stringify(bundle.meta), "utf8")
    : Buffer.alloc(0);
  const depthBytes = bundle.depth || Buffer.alloc(0);
  const rgbBytes = bundle.rgb || Buffer.alloc(0);

  const header = Buffer.allocUnsafe(4 + 2 + 2 + 4 + 4 + 4 + 4);
  let offset = 0;
  magic.copy(header, offset);
  offset += 4;
  header.writeUInt16LE(version, offset);
  offset += 2;
  header.writeUInt16LE(typeFrame, offset);
  offset += 2;
  header.writeUInt32LE(bundle.frameIndex >>> 0, offset);
  offset += 4;
  header.writeUInt32LE(rgbBytes.length, offset);
  offset += 4;
  header.writeUInt32LE(metaBytes.length, offset);
  offset += 4;
  header.writeUInt32LE(depthBytes.length, offset);

  return Buffer.concat([header, rgbBytes, metaBytes, depthBytes]);
}

function streamZipFrames(ws, zipIndex, start, count, intervalMs) {
  const effectiveStart = Math.max(0, start);
  const effectiveCount = Math.max(0, count);
  const interval = Math.max(0, intervalMs);

  let sent = 0;
  const matchingFrames = zipIndex.frames
    .filter((frame) => frame.frameIndex >= effectiveStart)
    .slice(0, effectiveCount);

  const sendNext = async () => {
    if (ws.readyState !== WebSocket.OPEN || sent >= matchingFrames.length) {
      return;
    }

    const bundle = await readFrameBundle(zipIndex, matchingFrames[sent].frameIndex);
    if (bundle) {
      ws.send(buildFramePacket(bundle));
    }

    sent += 1;
    if (sent < matchingFrames.length) {
      setTimeout(sendNext, interval);
    }
  };

  sendNext();
}

async function handleCommand(ws, zipIndex, text) {
  const trimmed = text.trim();
  if (!trimmed) {
    return;
  }

  const parts = trimmed.split(/\s+/);
  const cmd = parts[0];

  if (cmd === "GET_ZIP_INFO") {
    ws.send(
      JSON.stringify({
        type: "zipInfo",
        sceneId: zipIndex.sceneId,
        totalFrames: zipIndex.frames.length,
        sceneMeta: zipIndex.sceneMeta,
      })
    );
    return;
  }

  if (cmd === "STREAM_ZIP_FRAMES") {
    const start = Number(parts[1] || "0");
    const count = Number(parts[2] || "0");
    const intervalMs = Number(parts[3] || "0");
    streamZipFrames(ws, zipIndex, start, count, intervalMs);
    return;
  }

  ws.send(
    JSON.stringify({
      type: "error",
      message: `Unsupported command: ${trimmed}`,
    })
  );
}

async function main() {
  const args = parseArgs(process.argv);
  const zipIndex = await buildZipIndex(args.zip);

  const server = new WebSocket.Server({
    host: args.host,
    port: args.port,
  });

  server.on("connection", (ws) => {
    console.log("[ws-zip-test] client connected");

    ws.send(
      JSON.stringify({
        type: "hello",
        protocolVersion: "0.1-dev",
        sceneId: zipIndex.sceneId,
        totalFrames: zipIndex.frames.length,
        commands: ["GET_ZIP_INFO", "STREAM_ZIP_FRAMES <start> <count> [intervalMs]"],
      })
    );

    ws.on("message", async (data, isBinary) => {
      if (isBinary) {
        ws.send(JSON.stringify({ type: "error", message: "Binary commands are not supported." }));
        return;
      }

      await handleCommand(ws, zipIndex, data.toString("utf8"));
    });

    ws.on("close", () => {
      console.log("[ws-zip-test] client disconnected");
    });
  });

  console.log(
    `[ws-zip-test] serving '${args.zip}' on ws://${args.host}:${args.port} ` +
      `(scene=${zipIndex.sceneId}, frames=${zipIndex.frames.length})`
  );
}

main().catch((error) => {
  console.error("[ws-zip-test] fatal error:", error);
  process.exit(1);
});
```

---

## Recommended Plan

### Phase 1. Document the protocol and parity target

Before implementation:

- define the WebSocket message schema
- define required metadata fields
- define depth encoding
- define lifecycle / sequencing behavior
- define which ZIP semantics are intentionally mirrored
- define warm-up / refill buffering behavior

Deliverable:

- a protocol definition that another engineer can implement server-side

### Phase 2. Refactor WebSocket backend toward ZIP-like state population

Make the backend populate the same loader-state fields ZIP already uses.

Tasks:

- parse scene metadata
- set `FrameInterval`
- set coordinate conversion data
- deliver per-frame pose / intrinsics
- deliver optional depth bytes
- adopt slot/global-index semantics
- preserve warm-up threshold behavior instead of one-shot preload logic
- verify the backend still reads its source selection and WebSocket URL from the
  existing session configuration path

Deliverable:

- `CameraSubsystem` can use WebSocket-fed pose/intrinsics without backend
  special cases

### Phase 3. Move WebSocket to a buffered streaming model

Tasks:

- stop treating WebSocket as image-only preload
- queue incoming frame payloads
- upload textures on the main thread
- keep buffering bounded
- support continued playback beyond initial preload
- support refill as the playhead advances
- keep warm-up / ready behavior explicit

Deliverable:

- playback behavior is structurally closer to ZIP

### Phase 4. Hook WebSocket depth into the occlusion path

Tasks:

- populate `DepthBins`
- convert or upload depth data consistently with the chosen depth path
- make `Depth.cs` able to consume WebSocket-provided depth without violating the
  current subsystem boundary

Deliverable:

- WebSocket mode can drive environment depth, not just RGB replay

### Phase 5. Validate parity and tighten docs

Tasks:

- compare ZIP and WebSocket behavior in the same replay sequence
- validate pose, intrinsics, and timing consistency
- validate editor GUI source-mode configuration
- update `Architecture.md` and README as needed

Deliverable:

- WebSocket is documented as a supported input transport with defined limits

---

## Suggested Engineering Checklist

- [ ] Define a WebSocket protocol version and handshake.
- [ ] Define scene metadata payload fields.
- [ ] Define per-frame metadata payload fields.
- [ ] Define depth payload format and units.
- [ ] Define binary frame-packet layout.
- [ ] Define warm-up / refill semantics in the backend contract or protocol.
- [ ] Ensure WebSocket can populate `FrameInterval`.
- [ ] Ensure WebSocket can populate coordinate conversion information.
- [ ] Ensure WebSocket can populate `Poses`.
- [ ] Ensure WebSocket can populate `Intrinsics`.
- [ ] Ensure WebSocket can populate `DepthBins`.
- [ ] Ensure WebSocket preserves bounded warm-up buffering.
- [ ] Ensure the editor GUI lets users select ZIP or WebSocket cleanly.
- [ ] Ensure the editor GUI exposes editable WebSocket server URL configuration.
- [ ] Ensure Play mode actually uses the inspector-selected source mode and
      configured WebSocket URL.
- [ ] Move texture upload work to the main-thread upload path.
- [ ] Add bounded buffering semantics comparable to ZIP.
- [ ] Validate camera playback from WebSocket without source-specific hacks.
- [ ] Validate occlusion can eventually consume WebSocket depth.
- [ ] Validate the Node.js ZIP test server against the target refactored
      protocol.
- [ ] Update docs to reflect the new protocol and backend capabilities.

---

## Validation Requirements

Use ZIP behavior as the comparison baseline.

Validation scenarios:

- same scene played from ZIP and WebSocket
- same pose path
- same intrinsics over time
- same depth availability when applicable
- same timing / FPS behavior within acceptable tolerance
- same upper-layer camera behavior
- no backend-specific branching added in camera / occlusion subsystems
- editor UI allows switching source mode and configuring WebSocket URL / ZIP path
- entering Play mode after changing the inspector uses the new source selection
  immediately and predictably

Success criteria:

- the runtime behaves the same from the point of view of upper layers
- differences are transport-related, not architecture-related
- a developer can configure ZIP or WebSocket entirely from the normal editor UI

---

## Non-Goals

This task should not:

- redesign the whole loader architecture
- replace `FrameLoader`
- change app-facing setup patterns
- touch the FileSystem backend except where shared abstractions require it
- solve all occlusion work by itself

It is acceptable for rendering behavior to remain incomplete while backend
parity is being implemented, as long as the loader state and transport design
become correct.

---

## Final Deliverable

Deliver a WebSocket backend that is a real peer of the ZIP backend:

- same logical data model
- same shared loader-state contract
- same upper-layer consumption path
- transport-specific implementation only inside the backend layer

The end state should be:

- ZIP and WebSocket are interchangeable replay inputs
- FileSystem remains separate and can stay simpler for now

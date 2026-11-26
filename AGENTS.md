# Agent guidance

This file must be kept up to date. When a rule here stops matching reality, or a new rule emerges from
work in this repository, update this file as part of that change rather than leaving it to drift.

This repository is a **Macro Deck 3 out-of-process plugin** that shows IP cameras on a deck widget. It
offers a camera widget type, a video stream provider and a config flow, one config entry per camera.
The user-facing guide is [README.md](README.md); this file is the rule set for changing the code.

## Orientation

```
src/IpCamera/
  Program.cs                 builder chain - a few lines and RunAsync
  Composition.cs             DI wiring for the camera services
  manifest.json              identity, icon, per-platform entrypoints, host:video-streams permission
  macrodeck-build.json       one publish target per declared entrypoint
  PluginIntegration.cs       lifecycle and every capability opt-in
  Cameras/                   camera model, config parsing, per-camera frame pipelines
  Streaming/                 JPEG framing, snapshot and MJPEG sources, ffmpeg, loopback relay
  VideoStreams/              the video stream provider and its state notifications
  Widgets/                   camera widget view, configuration, UI session adapter
  ConfigFlow/                the camera setup flow
  Onvif/                     WS-Discovery and ONVIF stream resolution
  Actions/                   the full-screen camera action
  Localization/Strings.resx  every user-facing string
store/                     Store screenshots, listing copy and their build scripts
  Assets/icon.svg            the icon the manifest declares
tests/IpCamera.Tests/        unit tests, the harness integration tests and one local end-to-end test
```

The project is a normal Macro Deck 3 plugin; the SDK, CLI and template facts live in the
[official documentation](https://docs.macro-deck.app). Read the relevant feature page before changing a
capability, and the sample plugins repository for worked examples.

## Store gate

A plugin is published through the Macro Deck Store, and the Creator Portal refuses a build that breaks
its policy. The policy changes over time, so never rely on a copy, on memory or on an earlier fetch in
the same session. **Fetch every document below again before you start a change and again before you call
it done.** If one cannot be fetched, say so and stop rather than assuming it still says what it said last
time.

| What | Where |
| --- | --- |
| Creator Guidelines (Markdown) | <https://api.macro-deck.app/api/v1/public/creator-guidelines> |
| Blocked packages (JSON) | <https://api.macro-deck.app/api/v1/public/dependency-policy/blocked-packages> |
| Minimum SDK version and allowed Macro Deck packages (JSON) | <https://api.macro-deck.app/api/v1/public/dependency-policy/sdk> |

A change is not done until all six hold:

1. The plugin follows the Creator Guidelines, in full.
2. No blocked package is in the dependency graph (`dotnet list package --include-transitive`).
3. The Macro Deck SDK is at least `minimumSdkVersion`.
4. Every package whose id starts with `MacroDeck.` is in `allowedMacroDeckPackages`.
5. The manifest names its author, licence and repository. `publisher.name` must match the Creator
   Portal owner, `license` is an SPDX identifier and `repository` is the GitHub repository the plugin
   is released from. The repository is deliberately absent until the project has one.
6. `macrodeck-plugin test` passes on every platform in `entrypoints`, with no Required failure.

Report which version of the guidelines was checked (the `X-Creator-Guidelines-Version` header) and
whether each point holds.

## Versions and dependencies

- The Macro Deck packages are pinned to one version in `Directory.Packages.props`
  (`MacroDeckSdkVersion`). The CLI in `README.md` and the workflows must be the same version. Bump them
  together, and only to a stable release unless no stable exists.
- The plugin uses no third-party NuGet package beyond the Macro Deck SDK and the test packages. Adding
  one needs a dependency review and the user's approval.
- ffmpeg is an external program, not a dependency: it is never bundled or redistributed, and every
  feature that needs it must degrade to a clear integration issue when it is missing.

## Identity and contracts

- Identity is `manifest.json` only. The integration carries no id, name or version.
- Ids written in source are local ids (`^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$`): the widget type id
  `camera` and the video stream provider id `cameras` are stored in user data. **Never rename them.**
- The widget's stored data keys `camera` and `fit` are persisted too; changing them strands configured
  widgets.
- Camera ids are config entry GUIDs, so a widget keeps pointing at the same camera when its settings
  change.

## Camera and streaming rules

- **Loopback only.** The MJPEG relay binds `127.0.0.1` on a random port. Never expose it on a LAN
  interface, and never set the plugin's own listener URL (`UseUrls`, `ASPNETCORE_URLS`, `urls`): the
  supervisor owns that.
- The camera password never goes into a log line, an exception message, a widget's stored data or a
  URL that leaves the process. HTTP credentials are sent as an `Authorization` header. RTSP credentials
  go into the ffmpeg input URL because RTSP has no separate credential option; that is a documented
  limitation, not a licence to add more.
- A frame source must stop when its cancellation token fires, and ffmpeg must be killed as a process
  tree. One producer runs per camera while a client watches; the last client leaving stops it.
- Frames are dropped for a slow client, never buffered without bound.
- A source failure is a state, not a crash: set `Disconnected`, log the reason once per episode (not
  once per retry) and retry with backoff.
- Keep MJPEG as the only transport. Every Macro Deck client can play it; HLS would add a second path
  with no benefit on a deck tile.

## Lifecycle

`InitializeAsync` runs after the session is established, again after a non-resume reconnect and on
every config change (after `ShutdownAsync`). The MJPEG server may stay bound across those cycles; the
video stream provider is created per initialization and disposed in `ShutdownAsync`. Everything else
must be safe to run repeatedly, and a camera list replacement must keep the pipelines whose settings
did not change.

Capability order matters: integration `InitializeAsync` reads the entries and starts the relay, then
the widget type registers, then the video provider registers. Do not assume a provider hook ran before
another.

## Localization and logging

- Every user-facing string is a key in `Localization/Strings.resx`. Keys are dotted and named after
  where they are used. Log messages and exception messages stay English literals.
- The manifest `languages` array is generated by `build`/`pack`; never hand-edit it.
- Log through Serilog (`ILogger`) with short messages. Do not log frame data, credentials or full URLs
  that carry credentials.

## Async and concurrency

- Forward the cancellation token into everything you await.
- No blocking calls (`.Result`, `.Wait()`, `Thread.Sleep`) in SDK contract types or anything reachable
  from them.
- No `async void` except the `(object? sender, EventArgs e)` handler shape.
- Registry and stream state is shared across sessions: lock it, and raise events outside the lock.
- Host callbacks such as `NotifyStreamsChangedAsync` can throw on rate limiting or a dropped
  connection. Catch, log at debug, and carry on.

## Code quality

- C# in `src/` is tab-indented. `Directory.Build.props` sets nullable, implicit usings,
  `latest-recommended` analyzers and `CS8602` as an error. Build warning-free; do not relax a rule to
  make a build pass.
- Comments explain a non-obvious constraint, never what the code says. English, no em dashes, no
  banners.
- Prefer the SDK-native feature over custom code: `IIntegrationConfig` for entries, the
  `IVideoStreamProviderContext` for state changes, the UI DSL for views.
- Dispose what owns a socket, process, timer, channel or subscription.

## Verifying a change

Run, in this order:

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release
macrodeck-plugin test --project src/IpCamera
```

Before packing a release:

```bash
macrodeck-plugin build --source src/IpCamera --output ./artifacts
macrodeck-plugin inspect --artifact ./artifacts/com.itaimarom.ip-camera-1.5.2.macroDeckPlugin
macrodeck-plugin validate --level publication --artifact ./artifacts/com.itaimarom.ip-camera-1.5.2.macroDeckPlugin
```

The tests need no camera and no ffmpeg: the end-to-end test runs a local HTTP snapshot server. Do not
claim a real camera or a real Macro Deck session was verified unless it was.

## Workflow

- Work on a branch, not directly on `main`: `feature/`, `fix/`, `refactor/`, `chore/`, `docs/` or
  `ci/`, with a short kebab-case description.
- Keep changes focused; no unrelated reformatting.
- Do not push, tag or create a GitHub release unless asked. Creating a release starts a build upload
  to the Creator Portal.
- Do not add AI attribution or co-author trailers.
- Update `README.md` when behaviour, setup or the streaming story changes. Update this file when a rule
  stops being true.

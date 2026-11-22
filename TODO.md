# Roadmap

Planned work for the IP Camera plugin. Items are ordered by value; the first five are the intended 1.1.0
scope, the last three are larger and can wait.

## 1.1.0

- [x] **Connection test in the camera setup**
  Fetch one frame when the camera entry is submitted and explain a failure precisely: unreachable host,
  401/403, a response that is not JPEG or multipart, or a go2rtc stream that still offers H.265 only.
  Show the reason on the URL field instead of saving a camera that can never produce a picture.
  Cover the check with a fake `HttpMessageHandler` for the reachable and each failure case.
  Decide whether a timeout blocks saving or only warns, since a camera can be temporarily offline.

- [x] **Tap to enlarge**
  A short press opens a modal with the same stream, filling Macro Deck's modal box, which the client
  fixes at about 34rem by 36rem. A plugin cannot make that box true full screen. Add `SupportsFlows` so
  a user's own Short Press flow wins over the default, and allow `flows` in the widget data schema.
  Test the modal session and the flow priority.

- [x] **Offline and state feedback**
  When a producer fails, call `UpdateSessionAsync` with `Reconnecting` and a localized message so the
  widget reads "Camera offline, retrying" instead of showing a frozen frame. After repeated failures,
  report an integration issue per camera with a retry action. Clear both when frames return.

- [x] **Pre-warm on open**
  Start the source in `OpenAsync` and return once the first frame exists, with a timeout below the
  capability invoke timeout. Removes the empty first response from go2rtc and the few seconds of
  "Connecting" after a reconnect. On timeout, refuse with `StreamUnavailable` and a logged reason.

- [x] **Standard widget appearance**
  Declare `AppearanceProperties` for background, label, label colour, font and accent, read them with
  `UiWidgetAppearance.Read`, and draw a label overlay over the video. Add the standard appearance
  section to the widget configuration and allow the keys in the data schema.

## Later

- [ ] **Crop panning inside a filled frame** (blocked on Macro Deck)
  `UiVideoStream` crops inside its own element for `Cover`, so a `UiTransform` around it can only move
  the already-cropped picture. Panning cannot reveal the part of the frame the video component hid. A
  movable crop rectangle needs `Zoom` and `OffsetX`/`OffsetY` on the video component itself, the way
  `UiButton` already frames artwork. Track that client capability, then restore the rectangle editor
  and its live preview.

- [ ] **Full-screen camera folder view**
  Register an `IFolderViewProvider` that draws one camera, or a grid of every camera, across the whole
  folder. That is the closest a deck gets to full screen and it avoids the fixed modal box entirely.
  Offer a per-folder view with a camera picker, Fill or Fit, and an optional label; reuse the existing
  video stream provider and the widget's appearance handling. Add the folder-view configuration tree,
  the `folder` surface declaration, tests and the conformance run.

- [ ] **ONVIF discovery**
  A setup step that runs WS-Discovery, lists cameras, and fills the snapshot or RTSP URL from the media
  profiles. Needs credentials handling per camera and a decision on how much ONVIF to support.

- [ ] **HLS transport**
  Serve HLS alongside MJPEG, offering it first on clients that play it natively. Requires an ffmpeg HLS
  packager, a loopback segment server, cleanup, and the same session lifecycle as the MJPEG relay.
  Only worth it for smoother playback on iPhones; MJPEG works everywhere.

- [ ] **Store identity and branding**
  Keep "Macro Deck" out of the plugin name, package id, namespace, publisher and icon. The Creator
  Guidelines treat those as identity, and a platform name there implies an official product. Keep
  `IP Camera`, `com.itaim.ip-camera` and publisher `itaim`, with a generic camera icon. Use "Macro
  Deck" only in the description and README, as a statement of compatibility, and check the Store
  title, screenshots and icon against the naming and branding rules before submitting.

- [ ] **Publishing**
  Add the GitHub repository and matching publisher to `manifest.json`, add the Store badge to the
  README, push the repository, run the CI and release workflows, and submit through the Creator Portal.
  Until then the plugin is installed from a local `.macroDeckPlugin` file.

## Future

- [ ] **Camera controls**
  Add optional per-camera control, starting with pan and tilt:

  - **Xiaomi PTZ.** The Mi 360 moves with the encrypted motor command (`cmdMotorReq` 0x112, operations
    1 left, 2 right, 3 up, 4 down) over the P2P session go2rtc already holds. go2rtc has an open pull
    request (#2531) exposing `POST /api/xiaomi/motor?src=<stream>&operation=<1-4>`; it is not merged.
    Build that branch for the server or wait for it, store the go2rtc control stream on the camera
    entry (it is `mi360`, not the transcoded `mi360_mjpeg`), and test physical movement on
    `chuangmi.camera.ipc019`, which is not in the verified model list of the related project.
  - **ONVIF PTZ.** Use ContinuousMove and Stop for cameras that expose ONVIF, sharing the connection
    code with ONVIF discovery.
  - **Other Xiaomi controls.** The camera's MIoT spec exposes night vision, wide dynamic range,
    glimmer full colour, recording mode and a read-only motion tracking flag. Those need a Xiaomi
    cloud or local miIO path with a stored device token, separate from the video session.

  Shape it as actions (Move left, right, up, down, Night vision on/off, Recording mode) and optionally
  a d-pad widget. Keep every control opt-in, store credentials as secrets, and make the plugin work
  unchanged for cameras that only stream. Decide whether to rely on go2rtc and Home Assistant or to
  implement the protocol inside the plugin before writing any of it.

# Roadmap

Planned work for the IP Camera plugin. The 1.1.0 scope is done. **Next** holds the selected feature
ideas, **Later** holds release work and lower-priority capabilities, and **Future** holds the larger
items.

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

## Next

- [ ] **Multi-camera widget**
  Add a grid layout to the camera widget, with two or four cells and a camera picker per cell. Each cell
  is its own video stream session, so one tile can show a small camera wall. Reuse the camera picker,
  the fit setting and the appearance handling; keep the single-camera layout as the default. Test the
  four-cell tree, an empty cell, and the modal on a pressed cell.

- [ ] **Camera health page**
  Add an integration page that lists every camera with its state, source type, resolution, last frame
  age, last error and a Test or Retry button. Use the `page` surface, a timer to refresh the table, and
  the registry's cached state. This gives one place to see why a camera is not playing without opening
  its widget.

- [ ] **Status overlay**
  Draw a small live indicator and the last frame age on the camera tile, and a clear reconnecting state
  instead of a frozen picture. Expose `LastFrameAt` on the stream registry, update a `UiState` once per
  second while the session is open, and make the overlay optional in the widget settings.

- [ ] **Save snapshot action**
  Add an action that writes the current frame as a JPEG. The folder is configurable, with a default
  under the user's Pictures folder and optionally a per-camera override. Use the camera name and a
  timestamp for the file name, create the folder when missing, and return a failed result when there is
  no frame. Prime the stream first if nobody is watching it.

- [ ] **Stream quality presets**
  Offer Low, Balanced and High presets per camera, mapping to a frame rate and maximum width, while
  keeping the current custom values. Show the resulting frame rate and width beside the preset and fall
  back to Custom when either is edited.

## Later

- [ ] **Crop panning inside a filled frame** (blocked on Macro Deck)
  `UiVideoStream` crops inside its own element for `Cover`, so a `UiTransform` around it can only move
  the already-cropped picture. Panning cannot reveal the part of the frame the video component hid. A
  movable crop rectangle needs `Zoom` and `OffsetX`/`OffsetY` on the video component itself, the way
  `UiButton` already frames artwork. Track that client capability, then restore the rectangle editor
  and its live preview.

- [ ] **Full-screen camera folder view**
  Register an `IFolderViewProvider` that draws one camera, or a grid of every camera, across the whole
  folder. Unlike a widget stretched over several cells, a folder view replaces the folder's widget grid
  entirely: the plugin controls the whole folder area, so it can go edge to edge, lay out a camera wall,
  and ignore the per-cell gaps and tile faces. The user creates the folder and selects the view in the
  folder settings; the plugin never manages folders or navigation. Offer a per-folder view with a camera
  picker, Fill or Fit, and an optional label; reuse the existing video stream provider and the widget's
  appearance handling. Add the folder-view configuration tree, the `folder` surface declaration, tests
  and the conformance run.

- [x] **ONVIF discovery**
  A setup step that runs WS-Discovery, lists cameras, and fills the snapshot or RTSP URL from the media
  profiles. Needs credentials handling per camera and a decision on how much ONVIF to support.

- [x] **Store identity and branding**
  Keep "Macro Deck" out of the plugin name, package id, namespace, publisher and icon. The Creator
  Guidelines treat those as identity, and a platform name there implies an official product. Keep
  `IP Camera`, `com.itaimarom.ip-camera` and publisher `ItaiMarom`, with a generic camera icon. Use "Macro
  Deck" only in the description and README, as a statement of compatibility, and check the Store
  title, screenshots and icon against the naming and branding rules before submitting. The manifest,
  id, publisher and icon are already compliant; the final Store listing check happens with Publishing.

- [x] **Marketing materials**
  Five 1920x1080 Store screenshots under `store/`, built from HTML/CSS by `store/build.ps1` and checked
  by `store/validate.ps1`, plus `store/listing.md` with the Store copy. The README uses the hero image
  and a screenshot table. The camera scenes are mockups, not real footage.

- [x] **GitHub repository and Store metadata**
  Public repository at https://github.com/dominic25/ItaiMarom.IpCameraPlugin, set in the manifest, with
  the Store badge in the README and the listing copy under `store/`. The publication check is clean.

- [ ] **Store release and submission**
  In the Creator Portal, create the project with the id `com.itaimarom.ip-camera`, connect the repository
  and publish a GitHub release so the workflow builds the package. Then add that build to a submission,
  declare any AI use, and submit it for review.

## Future

- [ ] **HLS transport**
  Serve HLS alongside MJPEG, offering it first on clients that play it natively. Requires an ffmpeg HLS
  packager, a loopback segment server, cleanup, and the same session lifecycle as the MJPEG relay.
  Only worth it for smoother playback on iPhones; MJPEG works everywhere.

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

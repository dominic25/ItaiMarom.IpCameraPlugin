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
  A short press opens a modal with the same stream at full size, with a close control and the same fit
  mode. Add `SupportsFlows` so a user's own Short Press flow wins over the default, and allow `flows` in
  the widget data schema. Test the modal session and the flow priority.

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

- [x] **Pan the picture in Fill mode, with a preview**
  When Fit is Fill, offer horizontal and vertical position controls, and optionally zoom, so the user
  can choose which part of the cropped picture is visible. `UiVideoStream` has no offset of its own, so
  wrap it in a `UiTransform` and let the tile's clipping crop the moved picture. The live widget preview
  in the editor is the preview. Store `offsetX`, `offsetY` and `zoom` in the widget data, clear them when
  Fit is Fit, and verify that presses still reach the widget with a non-zero offset. Test the tree in the
  web client and the Companion app, since both must apply the transform to the live video.

## Later

- [ ] **Pan and tilt control**
  The Mi 360 moves with Xiaomi's encrypted motor command (`cmdMotorReq` 0x112, operations 1 left,
  2 right, 3 up, 4 down) over the same P2P session go2rtc already holds. go2rtc has an open pull
  request for this (AlexxIT/go2rtc #2531) that adds `POST /api/xiaomi/motor?src=<stream>&operation=<1-4>`
  and ONVIF PTZ, but it is not merged. Decide between building that branch for the server and adding a
  "Move camera" action with left, right, up and down, or leaving Xiaomi PTZ to Home Assistant's
  `xiaomi_camera_native` integration and calling its buttons. Store the go2rtc control stream on the
  camera entry, since the MJPEG stream is the transcoded `_mjpeg` one. Test physical movement on
  `chuangmi.camera.ipc019`, which is not in that project's verified model list. ONVIF PTZ belongs with
  the ONVIF discovery item for cameras that support it.

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

# Roadmap

Planned work for the IP Camera plugin. Items are ordered by value; the first five are the intended 1.1.0
scope, the last three are larger and can wait.

## 1.1.0

- [ ] **Connection test in the camera setup**
  Fetch one frame when the camera entry is submitted and explain a failure precisely: unreachable host,
  401/403, a response that is not JPEG or multipart, or a go2rtc stream that still offers H.265 only.
  Show the reason on the URL field instead of saving a camera that can never produce a picture.
  Cover the check with a fake `HttpMessageHandler` for the reachable and each failure case.
  Decide whether a timeout blocks saving or only warns, since a camera can be temporarily offline.

- [ ] **Tap to enlarge**
  A short press opens a modal with the same stream at full size, with a close control and the same fit
  mode. Add `SupportsFlows` so a user's own Short Press flow wins over the default, and allow `flows` in
  the widget data schema. Test the modal session and the flow priority.

- [ ] **Offline and state feedback**
  When a producer fails, call `UpdateSessionAsync` with `Reconnecting` and a localized message so the
  widget reads "Camera offline, retrying" instead of showing a frozen frame. After repeated failures,
  report an integration issue per camera with a retry action. Clear both when frames return.

- [ ] **Pre-warm on open**
  Start the source in `OpenAsync` and return once the first frame exists, with a timeout below the
  capability invoke timeout. Removes the empty first response from go2rtc and the few seconds of
  "Connecting" after a reconnect. On timeout, refuse with `StreamUnavailable` and a logged reason.

- [ ] **Standard widget appearance**
  Declare `AppearanceProperties` for background, label, label colour, font and accent, read them with
  `UiWidgetAppearance.Read`, and draw a label overlay over the video. Add the standard appearance
  section to the widget configuration and allow the keys in the data schema.

## Later

- [ ] **ONVIF discovery**
  A setup step that runs WS-Discovery, lists cameras, and fills the snapshot or RTSP URL from the media
  profiles. Needs credentials handling per camera and a decision on how much ONVIF to support.

- [ ] **HLS transport**
  Serve HLS alongside MJPEG, offering it first on clients that play it natively. Requires an ffmpeg HLS
  packager, a loopback segment server, cleanup, and the same session lifecycle as the MJPEG relay.
  Only worth it for smoother playback on iPhones; MJPEG works everywhere.

- [ ] **Publishing**
  Add the GitHub repository and matching publisher to `manifest.json`, add the Store badge to the
  README, push the repository, run the CI and release workflows, and submit through the Creator Portal.
  Until then the plugin is installed from a local `.macroDeckPlugin` file.

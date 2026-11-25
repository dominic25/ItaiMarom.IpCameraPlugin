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

- [x] **Multi-camera widget**
  Add a grid layout to the camera widget, with two or four cells and a camera picker per cell. Each cell
  is its own video stream session, so one tile can show a small camera wall. Reuse the camera picker,
  the fit setting and the appearance handling; keep the single-camera layout as the default. Test the
  four-cell tree, an empty cell, and the modal on a pressed cell.
  **Pressed-cell modal is blocked on the 3.0.5 SDK**: the short-press action knows the widget, not the
  cell that was pressed, so the widget opens the first camera in the modal instead. The grid is
  implemented with `layout` (`single`, `split`, `grid`), `camera2`, `camera3` and `camera4`; the status
  overlay remains on the single-camera layout. Revisit the per-cell modal when the action context
  carries the pressed node.

- [x] **Camera health page**
  Add an integration page that lists every camera with its state, source type, resolution, last frame
  age, last error and a Test or Retry button. Use the `page` surface, a timer to refresh the table, and
  the registry's cached state. This gives one place to see why a camera is not playing without opening
  its widget. Implemented as a Cameras page with a one-second refresh and a Retry button per camera; the
  raw error is not shown because ffmpeg messages can contain the camera URL.

- [x] **Status overlay**
  Draw a small live indicator and the last frame age on the camera tile, and a clear reconnecting state
  instead of a frozen picture. Expose `LastFrameAt` on the stream, update the overlay once per second
  while the session is open, and make it optional in the widget settings. Implemented with a
  `LastFrameAt` timestamp on `IFrameStream`, an `overlay` widget key that is on for new widgets and off
  for existing ones, and a one-second session tick that updates a small live or reconnecting pill. A
  background opacity slider (0 to 100) controls the dark strip behind it; 0 draws no strip. Tests cover
  the tree, the states, the background and the frame timestamp.

- [x] **Save snapshot action**
  Add an action that writes the current frame as a JPEG. The folder is configurable, with a default
  under the user's Pictures folder and optionally a per-camera override. Use the camera name and a
  timestamp for the file name, create the folder when missing, and return a failed result when there is
  no frame. Prime the stream first if nobody is watching it. Implemented with the action's own folder
  parameter and `Pictures\IP Camera` when empty; the per-camera override is not added yet.

- [x] **Stream quality presets**
  Offer Low, Balanced and High presets per camera, mapping to a frame rate and maximum width, while
  keeping the current custom values. Show the resulting frame rate and width beside the preset and fall
  back to Custom when either is edited. Implemented as a `quality` key in the camera entry; the preset
  overrides the stored frame rate and width at read time, and Custom keeps them.

- [x] **Camera wall folder view**
  Register an `IFolderViewProvider` that replaces a folder's widget grid with the plugin's own drawing,
  so the folder can go edge to edge with no cell gaps, tile faces or per-widget chrome. Offer two views:
  a camera wall with a chosen set of cameras in a configurable grid, and a single camera filling the
  whole folder. Store the camera set, grid, Fill or Fit and labels per folder through the
  `folder-view-config` entry point, keep the qualified view id stable, and declare the `folder` surface
  as `Shared`. A stretched widget still lives inside the grid and is limited by it, so this is the way to
  a real camera page. The user creates the folder and picks the view; the plugin never touches folders
  or navigation, and Macro Deck keeps its own back button. Reuse the video stream provider, pre-warm and
  appearance handling, run one session per cell, and test the view tree, the configuration round trip
  and the conformance run. DoriDeck's piano-keyboard view is the only public reference implementation
  today: https://github.com/Sarti2004/DoriDeck/blob/main/src/PianoFolder/PluginIntegration.cs
  Implemented as one `camera-wall` view with up to four cameras, one or two columns and Fill or Fit; a
  single camera fills the folder. The same view covers both cases.

## Later

- [ ] **Crop panning inside a filled frame** (blocked on Macro Deck)
  `UiVideoStream` crops inside its own element for `Cover`, so a `UiTransform` around it can only move
  the already-cropped picture. Panning cannot reveal the part of the frame the video component hid. A
  movable crop rectangle needs `Zoom` and `OffsetX`/`OffsetY` on the video component itself, the way
  `UiButton` already frames artwork. Track that client capability, then restore the rectangle editor
  and its live preview.

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
  Five 1920x1080 Store screenshots under `store/`, checked by `store/validate.ps1`, plus
  `store/listing.md` with the Store copy. The README uses the hero image and a screenshot table. The
  first set were HTML/CSS mockups and the moderator rejected them under Creator Guidelines section 10;
  they were replaced with real captures of Macro Deck 3 with the plugin installed, using local test
  streams made from stock camera photographs and composed as Store slides.

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

- [x] **Camera controls with a chosen camera and Xiaomi miIO**
  Move left, Move right, Move up, Move down, Stop and Night vision auto/on/off actions. Every action has
  an optional camera, so a plain button can control a camera with no tile; leaving it empty keeps the
  old pressed-widget behaviour. ONVIF reads capabilities, the media profile and the video source token,
  then sends `ContinuousMove`, `Stop` and `SetImagingSettings`; the control address can differ from the
  stream address so a relayed camera is still reachable. Xiaomi cameras use the local miIO channel:
  a UDP handshake, AES with the device token, then `set_motor` (one step) and `set_night_mode`. The
  address and token are saved per camera, the token as a secret. Stop is a no-op on Xiaomi because each
  motor command is one step. Tested with a fake SOAP handler and a fake UDP device, and the miIO
  handshake against a real Mi 360; a physical move still needs the device token.

- [ ] **Camera controls for vendor APIs**
  Add per-camera control beyond ONVIF and Xiaomi:

  - **Vendor APIs.** Hikvision ISAPI, Axis VAPIX, Dahua CGI and Reolink have PTZ and IR endpoints; add
    one family at a time only where users ask.
  - **Xiaomi settings beyond night mode.** MIoT exposes wide dynamic range, glimmer full colour and
    recording mode (write), plus motion tracking (read). Use local miIO/MIoT with the token stored as a
    secret.

  Shape the remaining controls as actions and optionally a d-pad widget. Keep every control opt-in,
  store credentials as secrets, and make the plugin work unchanged for cameras that only stream.

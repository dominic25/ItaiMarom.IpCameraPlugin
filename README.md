<img width="1920" height="1080" alt="IP Camera for Macro Deck 3" src="store/01-hero.png">

# IP Camera Plugin for Macro Deck 3

A Macro Deck 3 plugin that shows live IP camera pictures on a deck widget. It reads the camera on the
computer that runs Macro Deck and relays the frames to the deck, so a phone or tablet only ever talks
to Macro Deck.

The plugin was built for the Xiaomi Mi 360 1080p camera, and it accepts any camera that offers an HTTP
snapshot, an MJPEG stream, RTSP or ONVIF. Snapshot and MJPEG cameras need nothing else installed; RTSP
is converted with ffmpeg.

<a href="https://macro-deck.app/store/com.itaimarom.ip-camera">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Dark.png">
    <img alt="Open in the Macro Deck Store" src="https://assets.macro-deck.app/branding/Open%20in%20the%20Macro%20Deck%20Store%20Badge%20Light.png" height="56">
  </picture>
</a>

## Screenshots

These are real captures from Macro Deck 3 with the plugin installed, composed into Store slides. The
camera picture is a local test stream.

| | |
| --- | --- |
| ![The camera setup dialog with a stream URL and the connection check](store/02-setup.png) | ![A short press opens the camera in Macro Deck's modal](store/03-modal.png) |
| ![The IP Camera integration page with three ready cameras](store/04-integration.png) | ![A camera filling a folder](store/05-large-view.png) |

## What it adds to Macro Deck

- **A Camera widget**: one, two or four cameras on the same tile. Pick a camera per cell, choose whether
  the pictures fill their cell or fit inside it, and give the widget a label, background and colours. A
  short press opens the first camera in a modal as large as Macro Deck's fixed dialog box allows; the
  status overlay is available on the single-camera layout.
- **A video stream provider**: every configured camera is listed under Source in any widget or action
  that can show a video stream, not only in this plugin's own widget.
- **A camera setup flow**: one Macro Deck configuration entry per camera. The flow fetches one frame
  before saving, so a wrong URL, rejected credentials or a go2rtc stream that only offers H.265 is
  reported immediately. It also discovers ONVIF cameras on the LAN and resolves their RTSP or
  snapshot address. Stream quality can be a Low, Balanced or High preset, or Custom with your own frame
  rate and maximum width.
- **Offline feedback**: a camera that drops out shows "The camera is offline. Reconnecting." on its
  widget, and the integration page offers a Retry action.
- **A camera health page**: a Cameras tab on the integration page lists every camera's state, source,
  resolution and last-frame age, with a Retry button that restarts that camera.
- **A Save snapshot action**: writes the pressed camera's current picture as a JPEG. The folder is set
  per action; empty means `Pictures\IP Camera`.
- **A camera wall folder view**: replace a folder's widget grid with one camera or up to four in a
  configurable wall. Pick it in the folder's settings; Macro Deck keeps its own back button.
- **ONVIF controls**: Move left, Move right, Move up, Move down and Stop actions for pan and tilt, plus
  Night vision auto/on/off. They act on the pressed camera and use its ONVIF device service at
  `http://<host>/onvif/device_service`; a camera with a different ONVIF path or one that only speaks a
  vendor API is not covered yet. Xiaomi PTZ is not implemented.
- **A status overlay** (optional, off for existing widgets): a small live dot and the age of the last
  frame on the tile, or "RECONNECTING" when the source drops. New widgets enable it by default. It can
  be turned off in the widget settings, and a background opacity slider controls the dark strip behind
  it; 0 leaves only the dot and text over the picture.

## Requirements

- Macro Deck 3.0 or newer.
- ffmpeg only for RTSP cameras. Install it and make sure `ffmpeg` is on the `PATH`, or set
  `IPCAM_FFMPEG_PATH` to the executable before starting Macro Deck. Without ffmpeg the integration
  reports an issue and RTSP streams stay disconnected.
- The camera must be reachable from the computer running Macro Deck. A camera on the same LAN is fine;
  it does not have to be reachable from the device showing the deck.

## Adding a camera

1. Open Macro Deck, then **Integrations > IP Camera > Configure**.
2. If ONVIF cameras answer on the network, pick one from the ONVIF field. Otherwise enter a name, a
   source type and the stream URL.
3. Repeat for every camera. Each one appears in the widget's camera picker immediately.

| Source type | What it expects | Example |
| --- | --- | --- |
| HTTP snapshot (JPEG) | A URL that returns one JPEG per request. The plugin polls it at the configured frame rate. | `http://192.168.1.20/snapshot.jpg` |
| HTTP MJPEG | A `multipart/x-mixed-replace` stream. The plugin reads it once and shares it between viewers. | `http://192.168.1.20/video.mjpg` |
| RTSP | An RTSP URL. ffmpeg turns it into JPEG frames, scaled to the configured maximum width. | `rtsp://192.168.1.30:554/Streaming/Channels/101` |

Optional settings are in **Advanced configuration**:

- **Username and password**: HTTP basic authentication for snapshot and MJPEG cameras. The password is
  stored in Macro Deck's encrypted secret store. RTSP credentials are written into the ffmpeg input URL,
  which means they are visible to processes on the same computer (the RTSP protocol has no separate
  credential option).
- **Frames per second**: 1 to 15, default 5. A tile does not need more, and a lower rate leaves the
  camera free for its own recording.
- **Maximum width**: frames are scaled down to this width before they reach Macro Deck, default 640.
  Set it to 0 to keep the camera's native size. Scaling RTSP happens in ffmpeg; scaling snapshots
  happens in the deck client.
- **RTSP transport**: TCP is the safe default. Use UDP only if the camera performs better with it.

### Common camera URLs

The exact path is model specific. These are the usual shapes:

| Camera | Snapshot | MJPEG | RTSP |
| --- | --- | --- | --- |
| Hikvision | `http://<ip>/ISAPI/Streaming/channels/101/picture` | | `rtsp://<ip>:554/Streaming/Channels/101` |
| Dahua | | | `rtsp://<ip>:554/cam/realmonitor?channel=1&subtype=0` |
| Reolink | `http://<ip>/cgi-bin/api.cgi?cmd=Snap&channel=0` | | `rtsp://<ip>:554/h264Preview_01_main` |
| TP-Link Tapo | | | `rtsp://<ip>:554/stream1` (needs a camera account) |
| Xiaomi Mi 360 1080p | | see below | see below |

## Xiaomi Mi 360 1080p

Xiaomi does not document an RTSP or ONVIF endpoint for the Mi 360 1080p, and the stock firmware does
not answer on one. The model therefore needs one of two local routes:

1. **go2rtc (recommended).** go2rtc speaks Xiaomi's own camera protocol and can serve the picture as
   MJPEG without changing the camera. The first connection of each session needs Internet access to
   fetch the encryption keys; the video itself stays on the LAN. Add a stream to `go2rtc.yaml`:

   ```yaml
   streams:
     mi360: xiaomi://<account-id>:<region>@<camera-ip>?did=<device-id>&model=<model-code>
   ```

   The exact `xiaomi://` URL comes from go2rtc's Xiaomi documentation and the tokens from your Mi Home
   account. Then add a **HTTP MJPEG** camera in this plugin with:

   ```text
   http://<go2rtc-host>:1984/api/stream.mjpeg?src=mi360
   ```

   Use go2rtc's `/api/frame.jpeg?src=mi360` as an HTTP snapshot camera if you prefer polling.

2. **A community firmware that exposes RTSP.** Some legacy revisions, such as the MJSXJ02CM, can run
   community hacks. Treat this as unsupported and risky: it can brick the camera or remove Mi Home
   features, and the available hacks do not all include an RTSP server. If the camera does answer on
   RTSP, add an **RTSP** camera with the URL its firmware documents, commonly
   `rtsp://<camera-ip>/live/ch00_0`.

No cloud login, account token, or Xiaomi API is used by this plugin itself.

## How the frames flow

```text
camera --HTTP/RTSP--> plugin (one producer per camera) --multipart MJPEG--> 127.0.0.1:<random port>
                                                                                 |
                                                                        Macro Deck relay
                                                                                 |
                                                                     deck widget / browser / app
```

- One producer runs per camera while at least one client watches it. It stops 20 seconds after the
  last client leaves, so an idle deck does not keep ffmpeg or the camera busy.
- A slow client drops old frames instead of delaying the camera or other clients.
- When the source stalls, the last frame is repeated every 10 seconds so Macro Deck's relay keeps the
  session open instead of dropping it.
- The relay listens on `127.0.0.1` only. The camera URL never reaches a client, and no firewall rule is
  needed.
- Audio is not carried. Macro Deck's video streams play muted.

## Security

- The plugin fetches the camera URL from the computer running Macro Deck, so add only addresses you
  trust. A URL you configure can reach any service that computer can reach.
- Every client signed in to Macro Deck can open the streams, and the devices showing a deck receive the
  picture through Macro Deck. Do not put a camera on a deck that guests can open.
- HTTP camera passwords are stored by Macro Deck in its encrypted secret store and sent as an
  `Authorization` header.
- RTSP passwords are passed to ffmpeg in its input URL, because RTSP has no separate credential
  option. Other processes on the same computer can read that command line while a stream is open.

## Limitations

- MJPEG only. Every Macro Deck client can play it; the plugin does not produce HLS.
- The enlarged view is a Macro Deck modal. Its box is fixed by the client at roughly 34rem by 36rem on
  the desktop, and a plugin cannot make it true full screen.
- Panning inside a filled frame is not supported by Macro Deck's video component today. It crops inside
  its own element, so a transform can only move the cropped picture, not reveal the hidden part.
- ONVIF discovery follows the standard WS-Discovery and WS-Security flow and is covered by protocol
  tests. A real ONVIF camera has not been used yet to verify every vendor's dialect.
- HTTP digest authentication is not supported, only basic. Use a local restreamer such as go2rtc if
  the camera insists on digest.
- No PTZ, no talk-back, no recordings, no motion notifications. It only displays a live picture.
- The picture is polled or streamed at the configured rate; it is not a 30 fps video. At 5 fps a deck
  tile looks live, and the camera stays available for its own app and recordings.

## Development

Requires the .NET 10 SDK and the Macro Deck plugin CLI (the version matches the SDK, 3.0.5 here):

```bash
dotnet tool install --global MacroDeck.Plugin.Cli --version 3.0.5
dotnet build -c Release -warnaserror
dotnet test -c Release
macrodeck-plugin test --project src/IpCamera
```

Run it against the installed Macro Deck app:

```bash
macrodeck-plugin run --project src/IpCamera
```

Approve the pairing prompt in Macro Deck once. The plugin then appears under Integrations with the
camera setup flow.

Package a release:

```bash
macrodeck-plugin build --source src/IpCamera --output ./artifacts
macrodeck-plugin validate --level publication --artifact ./artifacts/*.macroDeckPlugin
```

`macrodeck-plugin build` publishes the Windows, Linux and macOS builds and packs them. The output is
framework dependent and runs on the .NET 10 runtime Macro Deck ships.

### Project layout

```text
src/IpCamera/
  PluginIntegration.cs        lifecycle, capabilities, camera entries
  Composition.cs              DI wiring
  Cameras/                    camera model, config parsing, per-camera pipelines
  Streaming/                  JPEG framing, HTTP snapshot/MJPEG sources, ffmpeg, loopback relay
  VideoStreams/              the video stream provider
  Widgets/                    camera widget view, configuration, UI session adapter
  ConfigFlow/                 the camera setup flow
  Actions/                    the full-screen camera action
  Localization/Strings.resx   all user-facing text
store/                       Store screenshots, listing copy and their check
```

### Publishing

The manifest id is `com.itaimarom.ip-camera`, the publisher is `ItaiMarom`, and the repository is
`https://github.com/dominic25/ItaiMarom.IpCameraPlugin`. To publish to the Macro Deck Store, run the
release workflow by publishing a GitHub release. The Store builds and signs the package from that
workflow.

## Licence

Apache-2.0. See [LICENSE](LICENSE), [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).
ffmpeg is not bundled or redistributed; it is used from the host when it is installed.

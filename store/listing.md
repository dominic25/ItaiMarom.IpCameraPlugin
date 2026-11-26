# Store listing copy

Ready to paste into the Creator Portal.

## Name

IP Camera

## Short description

Live camera tiles and camera walls for Macro Deck: snapshot, MJPEG, RTSP and ONVIF, including the Xiaomi
Mi 360 through go2rtc.

## Long description

Put your cameras on your deck. IP Camera adds a camera widget and a video stream provider to Macro Deck
3, so a tile, a folder or any widget that can show a video stream can display a live picture.

It reads the camera on the computer that runs Macro Deck and relays the frames to the deck, so the
camera does not have to be reachable from the phone or tablet showing it. Add one config entry per
camera and pick it in the widget.

**Four source types**

- HTTP snapshot: a URL that returns one JPEG per request, polled at the rate you choose.
- HTTP MJPEG: a multipart motion JPEG stream, with one upstream connection shared by every viewer.
- RTSP: converted with ffmpeg on the Macro Deck computer and scaled to the width you choose.
- ONVIF: cameras found with WS-Discovery, with their RTSP or snapshot address resolved through
  WS-Security.

**Checked before saving**

The setup fetches one frame from the camera. A wrong host, a rejected password, an HTML page instead of
a picture, or a go2rtc stream that still offers H.265 only gets a clear message on the URL field instead
of a tile that never plays.

**Made for a deck**

- One camera per tile, a split with two, or four cameras in one tile.
- A camera wall that fills a whole folder with one camera or up to four.
- Fill the cell or fit the whole frame.
- A label, background, label colour, font and border through Macro Deck's standard appearance settings.
- A live dot and the age of the last frame, or a reconnecting state when the camera drops out.
- A short press opens the same stream in Macro Deck's modal.

**Move the camera from the deck**

- Left, right, up and down arrows on each camera cell, with a transparency slider.
- The enlarged window keeps the arrows, so a PTZ camera stays controllable while you watch.
- Move, Stop and Night vision actions for ONVIF cameras and the Xiaomi Mi 360.
- Macro Deck gives a tile press either to the controls in its tree or to its Short Press flow, never
  both. The arrows therefore disable the tile's Short Press flow. To run actions and keep the arrows,
  create a Macro Deck script and pick it as the widget's Press script; the plugin runs that script with
  the pressed widget and the pressing client.

**When a camera drops out**

The tile says the camera is offline and reconnecting, and the plugin reports an integration issue with
a Retry action. The stream recovers on its own when the camera answers again.

**Xiaomi Mi 360**

The Mi 360 does not expose RTSP or ONVIF. go2rtc can hold its P2P session and serve a normal MJPEG
stream; point this plugin at that stream. Each connection needs an Internet round trip so go2rtc can
fetch Xiaomi's encryption key. The video itself stays on the LAN. Movement and night vision use the
local miIO channel with the device token from Mi Home. See the repository README for the go2rtc
configuration.

**Requirements**

- Macro Deck 3.0 or newer.
- ffmpeg only for RTSP cameras.
- The camera must be reachable from the computer running Macro Deck.

**Network and privacy**

The plugin connects only to the camera URLs you configure, plus a local go2rtc if you use one. It does
not send camera data to any other service. The video relay listens on 127.0.0.1. Camera passwords and
the Xiaomi token are stored in Macro Deck's secret store. RTSP passwords are passed to ffmpeg in its
command line, because RTSP has no separate credential option.

## Tags

camera, ip camera, rtsp, mjpeg, onvif, xiaomi, go2rtc, security camera, live view, video, ptz, camera
wall

## Links

- Repository: https://github.com/dominic25/ItaiMarom.IpCameraPlugin
- Support: https://github.com/dominic25/ItaiMarom.IpCameraPlugin/issues
- Store: https://macro-deck.app/store/com.itaimarom.ip-camera

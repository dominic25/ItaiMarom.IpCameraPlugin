# Store screenshots

The five images for the Macro Deck Store listing, in upload order. All are 1920x1080 (16:9), the frame
the Store gallery renders them in.

| File | Shows |
| --- | --- |
| `01-hero.png` | The deck with two camera widgets, one filling its tile and one fitting the whole frame, both with a label. |
| `02-setup.png` | The camera setup dialog with a stream URL, source type and the advanced options. |
| `03-modal.png` | A short press opening the same stream in Macro Deck's modal. |
| `04-integration.png` | The IP Camera integration page: ready cameras, actions, widget and video stream capabilities. |
| `05-large-view.png` | A camera filling a folder, edge to edge. |

## Notes

- These are real captures of Macro Deck 3.0.5 running the published plugin, not mockups, drawings or
  HTML renders.
- The camera pictures are stock photographs served from a local MJPEG test server. No real home,
  street, person or user camera appears in them.
- The frames were captured at 3840x2126 and cropped and scaled to 1920x1080. Photo-heavy frames are
  palette-compressed to stay under the 1 MB upload limit.
- The only generated asset in the package is the plugin icon, which the manifest declares.
- Claims match `README.md`: RTSP needs ffmpeg, the Mi 360 needs go2rtc, and ONVIF discovery is covered
  by protocol tests.
- Recapture these after a visible UI change, and check them with `store/validate.ps1`.

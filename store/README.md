# Store screenshots

The five images for the Macro Deck Store listing, in upload order. All are 1920x1080 (16:9), the frame
the Store gallery renders them in.

| File | Shows |
| --- | --- |
| `01-hero.png` | The deck with two camera widgets, one filling its tile and one fitting the whole frame, both with a label. |
| `02-setup.png` | The camera setup dialog with source type, stream URL and advanced options. |
| `03-modal.png` | A short press opening the same stream in Macro Deck's modal. |
| `04-integration.png` | The IP Camera integration page: ready cameras, actions, widget and video stream capabilities. |
| `05-large-view.png` | A camera filling a folder, edge to edge. |

## Rebuilding

```powershell
powershell -ExecutionPolicy Bypass -File store/build.ps1
powershell -ExecutionPolicy Bypass -File store/validate.ps1
```

The slides are HTML/CSS in `src/`, with the real captures in `src/img`. The build uses headless Microsoft
Edge as the renderer; it is not a project dependency. Photo-heavy slides are palette-compressed with
ffmpeg afterwards so each image stays under the 1 MB upload limit.

## Notes

- Every piece of UI inside a screenshot panel is a real capture of Macro Deck 3.0.5 with the plugin
  installed: the widget, the setup dialog, the modal and the integration page. The background, headline,
  pills and the panel frame are presentation, not plugin UI.
- The camera pictures are stock photographs served from a local MJPEG test server. No real home,
  street, person or user camera appears in them.
- The only generated asset in the package is the plugin icon, which the manifest declares.
- Claims match `README.md`: RTSP needs ffmpeg, the Mi 360 needs go2rtc, and ONVIF discovery is covered
  by protocol tests.
- Recapture the panels after a visible UI change, then rebuild and validate.

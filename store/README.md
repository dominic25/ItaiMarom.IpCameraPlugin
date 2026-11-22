# Store screenshots

The five images for the Macro Deck Store listing, in upload order. All are 1920x1080 (16:9), the frame
the Store gallery renders them in.

| File | Shows |
| --- | --- |
| `01-hero.png` | The plugin name, icon, and four camera tiles: snapshot, MJPEG, RTSP and the Mi 360 through go2rtc. |
| `02-setup.png` | The setup dialog with ONVIF discovery, a stream URL, credentials and the live-frame check. |
| `03-mi360.png` | The Mi 360 path: camera to go2rtc to the plugin to the deck, and the note about the cloud key. |
| `04-widget.png` | Fill, Fit, label and colours, and the enlarged modal view. |
| `05-compatibility.png` | The four source types, cameras that work, and the requirements. |

## Rebuilding

```powershell
powershell -ExecutionPolicy Bypass -File store/build.ps1
powershell -ExecutionPolicy Bypass -File store/validate.ps1
```

The build uses headless Microsoft Edge as the renderer. It is not a project dependency. The HTML in
`src/` is the source of every image.

## Notes

- The camera pictures are stylized HTML/CSS mockups, not footage from a real camera. No real home,
  street or person is shown.
- The only generated asset in the package is the plugin icon, which the manifest declares.
- Claims match `README.md`: RTSP needs ffmpeg, the Mi 360 needs go2rtc, and ONVIF discovery is covered
  by protocol tests.
- Keep the subject in the middle third. The Store thumbnails are small and crop to 16:9.
- Update these images when the widget or the setup screen changes.

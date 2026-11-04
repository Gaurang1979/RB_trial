# Hikrobot MVS camera SDK (developer files)

From Hikrobot "Machine Vision Software MVS V5.1.0 (Windows)", build 20260909, camera SDK 4.8.2,
downloaded from the official download center:
https://www.hikrobotics.com/en/machinevision/service/download/

Only the files needed to build against the SDK are kept here:

| Path | What |
| --- | --- |
| `Includes/` | C headers (`MvCameraControl.h` and friends) |
| `DotNet/win64/netstandard2.0/` | `MvCameraControl.Net.dll`, used by `dotnet/HikCamera` |
| `Libraries/win64/MvCameraControl.lib` | C import library, for a future C++ camera path |
| `CLIENT_MVS_Win_license_notice.txt` | Licence notice shipped with MVS |

The camera driver and native runtime (`MvCameraControl.dll`) still come from installing MVS,
or Hikrobot's "Industrial Camera SDK Runtime Package", on each line PC.

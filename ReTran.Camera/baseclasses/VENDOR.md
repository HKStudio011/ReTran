# Vendored DirectShow baseclasses

Source: https://github.com/microsoft/Windows-classic-samples
Path:   Samples/Win7Samples/multimedia/directshow/baseclasses
Commit: 434f6002bdf9cf9829406c3ff2b33387982d6168 (main, depth-1 sparse checkout)
License: see LICENSE in this folder (Microsoft, MIT-style)

Compiled directly into ReTran.Camera.dll - do NOT link strmbase.lib
(the SDK ships that lib without headers). Update by re-copying the folder
from the commit above.

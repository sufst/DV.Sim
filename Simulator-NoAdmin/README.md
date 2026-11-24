# Run DV.Sim without administrator permissions

Double-click **Run-Simulator.cmd** in this folder. For fullscreen, run
`./Run-Simulator.cmd --fullscreen` from this folder in PowerShell.

The launcher opens the simulator and its usual Run settings panel. It supplies
five legacy 64-bit DirectX DLLs from the local `DirectX` folder through the
launcher process's DLL search path.

If the local DLLs are missing, the launcher downloads the
[Microsoft DirectX June 2010 redistributable](https://www.microsoft.com/en-us/download/details.aspx?id=8109),
checks its SHA-256 hash, and extracts the DLLs using the 7-Zip already installed
on this computer and Windows' `expand.exe`. Subsequent launches use the cached
DLLs and work offline.

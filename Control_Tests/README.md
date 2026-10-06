# Centreline positions

Double-click **Run-Centreline.cmd** to open the simulator and Run settings.
Choose a map and click **Run Simulation**. As soon as the map is running, the
launcher obtains the ordered centreline used by Automatic Movement and feeds
it to **control.py**. It also saves CSV and JSON files under
`FSOnline/Saved/Control_Tests/`. It waits up to three minutes for a map.

Edit `run_control(centreline, car)` in `control.py` to add your controller.
The initial function confirms the received path and simulator connection.
See **HOW_TO_CONTROL.txt** in this folder for the car commands and an example.
Python 3.9+ and `msgpack` are required; the launcher can use registered Python
installations as well as `.venv` and PATH.

Coordinates are in metres relative to the car's spawn, using the same fixed map
axes as Automatic Movement; they do not rotate with the car. Each row includes
its zero-based index and track width. The JSON also
records whether the path is a closed loop; for a closed loop, the last waypoint
connects to the first. This reads known simulator cone positions without driving
the car. Generated exports are ignored by Git.

From PowerShell, use `./Control_Tests/Run-Centreline.cmd -Fullscreen` for fullscreen,
or `./Control_Tests/Run-Centreline.cmd -NoLaunch` to export from an existing run.

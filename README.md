# SUFST - DV.Sim

Repo for SUFST's local version of FSDS (Formula Student Driverless Simulator), for ease of access and compilation of control scripts.

Original repo of FSDS can be found [here.](https://github.com/FS-Driverless/Formula-Student-Driverless-Simulator)

## Cloning the repository

Run the following command in terminal to clone repository:

```
git clone https://github.com/sufst/DV.Sim.git
```

This will create a DV.Sim folder in your local User folder.

## Running the simulator

The simulator writes local settings, logs and crash reports into `FSOnline/Saved/`.
These files are ignored by Git, along with Python caches and local environments,
so running the simulator or a controller does not add runtime files to Git status.
Keep shared sensor configuration in the tracked `settings.json`.

- Double-click **Run-Simulator.cmd** to open the simulator in a 1280 x 720 window.
  It opens at the top left of the primary display; Run settings opens at the top right.
  Drag the window edges to resize it, or use the maximize button. Press **Alt+Enter**
  to switch between windowed and fullscreen mode.
  To start directly in fullscreen, run `.\Run-Simulator.cmd --fullscreen` in PowerShell.
- The launcher also opens **Run settings**. Choose **Lidar configuration**, then
  **Hesai Pandar 40P**. Click the green **Add** button to include it, or the red
  **Remove** button to exclude it. The first run starts without a lidar.
  **Placement** lets you set X (forward), Y (left), Z (up) in metres relative to
  the vehicle origin, plus roll, pitch and yaw in degrees. Click **Save placement**.
  Selection and placement are saved locally, even after closing both windows.
- Click **Apply & restart simulator** to load a changed sensor setup. This ends the
  current simulation; choose a map and start it again. Closing the settings window
  leaves the simulator running. Opening `FSDS.exe` directly uses its original
  configuration; use the launcher for saved run settings.
- Select your map/level
- Click **Run Simulation** to start it up
- This will give you standard gamin controls over the car to move it around

## API control

- Set working directory as **DV.Sim/python** and run:
```
python -m pip install -r requirments.txt
```

- Set working directory to either **DV.Sim\Python\examples** or **DV.Sim\Python\Scripts**
- Open **Run-Simulator.cmd** and start a simulation
- In another window, start running the python controller script. This should start to autonomously drive the car!

## Live lidar view

In the settings window, choose **In-Run visuals > Lidar map**. The view connects
automatically when a simulation starts. If FSDS is on its menu or closed, it shows
**Start the run**. Add the Pandar 40P and apply the setup before starting a run;
a running simulation without a lidar shows **Add a lidar first**. Paused or stale
scans clear the view and prompt you to resume the run.

**Sensor POV** looks forward along the lidar's own X axis, with Y to the left and
Z up. **Top-down / 360 degrees** shows returns around the sensor. **Raised POV**
is the first and default view. It looks forward from 2 m behind and initially
1.2 m above the lidar, tilted downward by 15 degrees. Its sliders adjust height
from 0 to 5 m and downward tilt from 0 to 45 degrees while viewing live returns.
Top-down is second and Sensor POV is third. Raised POV follows the sensor's
heading and does not change its placement. Reduce the view range to inspect nearby clusters. These are
raw lidar points; the viewer does not classify cones. The view-range
slider clips distant returns and starts at the sensor's full 200 m range. Dots use
128 shades from red nearby through orange, yellow, green, cyan and blue to purple
far away. The fixed logarithmic distance scale gives finer colour changes nearby,
particularly within 20 m; changing the view range does not change a point's colour.
All three views use
the [actual sensor-frame XYZ returns](https://fs-driverless.github.io/Formula-Student-Driverless-Simulator/v2.2.0/lidar/),
not a camera image or ground-truth map.

Scroll over the plot to zoom around the cursor (0.25x to 32x). Hold the left mouse
button and drag to move around the view. **Reset view** restores the initial zoom
and centre; switching viewpoints also resets navigation. Height, tilt and view
range remain unchanged. Zooming magnifies the scan; it does not change the sensor.

The download icon beside **Lidar map** saves the current scan to your Windows
**Downloads** folder as a uniquely named `lidar-map-*.csv`. It contains only
`x,y,z` columns: raw return positions in metres relative to the lidar, with
X forward, Y left and Z up. The full scan is exported regardless of view range,
zoom or viewpoint; invalid returns are skipped. A live, nonempty scan is required.

The viewer polls at up to 5 Hz while its page is open. It checks every returned
point and draws every valid return inside the current view and range, with no
point-skipping limit. Zooming separates overlapping dots and reveals the full
detail available in that scan; it cannot add returns the sensor did not measure.
It only
reads car state and lidar data; it does not acquire API control or command the
car. Leaving the page or closing the settings window disconnects it. No Python
packages or additional runtime installation are needed.

To run the offline viewer checks (including a local mock RPC server):

```powershell
powershell -NoProfile -STA -ExecutionPolicy Bypass -File .\Launcher\tests\Test-LidarView.ps1
```

## Pandar 40P simulation profile

The profile uses 40 channels, 360-degree horizontal coverage, a -25 to +15 degree
vertical field of view, 200 m maximum range, and 10 Hz scanning. At Hesai's
720,000 points/s single-return rate, this gives 72,000 firings per scan (0.2-degree
horizontal spacing). These values come from the
[Hesai Pandar40P manual](https://www.hesaitech.com/wp-content/uploads/2025/04/Pandar40P_User_Manual_402-en-241220.pdf).

This is an approximation using the existing FSDS lidar: its beams are uniformly
spaced, unlike the Pandar's nonuniform channel angles. It does not reproduce
dual returns, reflectivity-dependent range, the 0.3 m minimum range, or hardware
measurement error. The configurable `Range` parameter is supported by the
[v2.2.0 sensor parser](https://github.com/FS-Driverless/Formula-Student-Driverless-Simulator/blob/v2.2.0/AirSim/AirLib/include/common/AirSimSettings.hpp).
The sensor's API name remains `Lidar`; GPS and other non-lidar sensors from shared
`settings.json` are retained. Existing controllers may need tuning for the new
3D scan density and placement.

The settings panel uses Windows PowerShell and WPF, which are included with
Windows; no Python installation is needed. Preferences and generated settings
live in ignored `FSOnline/Saved/RunSettings/`. Shared `settings.json` stays intact.
The simulator launches with that local folder as its working directory so FSDS
loads its `settings.json` directly; no command-line settings override is used.
Deleting that local folder resets the preferences.

To run configuration checks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Launcher\tests\Test-RunConfiguration.ps1
```

## Slow centreline run

After starting a map, select **Automatic Movement** below **Lidar map** in the
settings window's left menu, then click **Start slow run**. The speed slider
on that page sets the cruise speed from 0.5 to 4 m/s (default
1.5 m/s); the car slows further for corners and the end of an open track.
**Stop slow run** brakes and returns control to the simulator. Closing the
settings window or restarting the simulator also stops the controller.

The launcher forms centreline waypoints between the referee's ground-truth
blue and yellow cones and steers using the car's ground-truth pose. It works
independently of lidar placement or cone detection code. Ground-truth data
stays inside the driving controller; the lidar viewer still shows raw returns.
The controller brakes when track data or car pose is unavailable, the simulation
is paused, or the car leaves the centreline corridor. Maps without published
blue/yellow boundaries cannot use this mode. Complex junctions and skidpad
missions need a mission-specific route and are not supported by this simple
centreline controller.

The offline lidar checks above also cover centreline geometry, steering,
two simulated laps, and automatic driving through a mock simulator API.

To obtain the centreline and feed it to **Control_Tests/control.py**, double-click
**Control_Tests/Run-Centreline.cmd**, then choose a map and start the simulation.
See [Control_Tests](Control_Tests/README.md) and
[HOW_TO_CONTROL.txt](Control_Tests/HOW_TO_CONTROL.txt) for car commands and input coordinates.

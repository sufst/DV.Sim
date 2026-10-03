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
Deleting that local folder resets the preferences.

To run configuration checks:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Launcher\tests\Test-RunConfiguration.ps1
```

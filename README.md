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
  Drag the window edges to resize it, or use the maximize button. Press **Alt+Enter**
  to switch between windowed and fullscreen mode.
  To start directly in fullscreen, run `.\Run-Simulator.cmd --fullscreen` in PowerShell.
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

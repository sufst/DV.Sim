# DV.Sim LiDAR viewer

## What the LiDAR display shows

The **Lidar map** shows a live point cloud. Three viewpoint buttons sit above the plot, with the zoom readout, **Reset view** button, range slider and distance colour legend below it. A **LIVE** indicator appears beside the title while scans are updating. The scan summary shows the number of returns in the latest scan and its age in milliseconds.

## Choose a viewpoint

All three views display the same live LiDAR scan. Every return is an XYZ position in metres relative to the sensor, with **+X forward**, **+Y left** and **+Z up**. The viewpoint changes where those points appear on screen.

| View | How it works |
| --- | --- |
| **Raised POV** | The default view looks in the sensor’s forward direction from 2 m behind it and 1.2 m above it, tilted down by 15°. The two sliders above the plot adjust **Height above lidar** from 0 to 5 m and **Downward tilt** from 0° to 45°. These controls move the virtual viewpoint only. |
| **Top-down / 360°** | Looks straight down, with the LiDAR at the centre before panning. Forward is up and left is left. The view flattens height and shows returns in all directions, including behind the LiDAR. |
| **Sensor POV** | Looks forward from the LiDAR’s mounting position. Points in front are drawn in perspective, so their screen positions reflect their height, sideways offset and depth. Points behind the sensor fall outside this view. |

**The car model is absent from both POV views and the top-down view.** The entire plot uses a near-black background (`#09111D`) with faint grid lines (`#233449`). The white dot (`#FFFFFF`) marks the plot centre before panning and moves with the plot when you drag. In the top-down view it marks the sensor origin. The scene consists of coloured points where the LiDAR measured surfaces. Cones appear as clusters of returns, with distance determining their colour.

## Control the view with the mouse

- **Scroll over the plot** to zoom in or out around the mouse pointer. The zoom runs from **0.25× to 32×** and starts at **1.0×**. Point the cursor at a cluster before scrolling to keep that area in place as you zoom.
- **Hold the left mouse button and drag** to pan the plot horizontally or vertically.
- **Click Reset view** below the plot to restore **1.0×** zoom and centre the view. Selecting a viewpoint also resets pan and zoom. Height, tilt and range settings are kept.
- **Drag the View range slider** below the plot to choose a cutoff between **5 m and 200 m**. It starts at **200 m**. For example, set it to **20 m** to show only returns within 20 m of the LiDAR. In the top-down view, lowering the range also scales the remaining points to fill more of the plot. Mouse-wheel zoom changes magnification and the range slider sets the distance cutoff.

## Read the distance colours

The viewer measures each return’s straight-line distance from the LiDAR as `sqrt(x² + y² + z²)`. Height contributes to this distance even in the top-down view. The plot skips points closer than **0.05 m** and beyond the selected range. Colour is tied to the distance from the LiDAR in every view, including Raised POV.

The plot uses **128 discrete colours** across a fixed logarithmic scale from **0 to 200 m**. Nearby points change colour more quickly per metre, which helps distinguish closely spaced returns. Changing the range, pan or zoom leaves a return’s colour unchanged.

| Distance from LiDAR | Exact plotted colour |
| ---: | --- |
| 0.05 m | Red `#FF3838` |
| 2 m | Amber `#FFC538` |
| 5 m | Yellow-green `#C9FF38` |
| 10 m | Lime green `#59FF38` |
| 20 m | Bright green with a blue tint `#38FF8E` |
| 40 m | Cyan-blue `#38EAFF` |
| 80 m | Blue `#385DFF` |
| 200 m | Purple `#CD38FF` |

The on-screen legend is a continuous gradient with stops at red `#FF3838` (0%), yellow `#FFD538` (21%), green `#38FF38` (42%), cyan `#38FFFF` (63%), blue `#3838FF` (84%) and purple `#CD38FF` (100%). The labels beneath it mark **0, 2, 5, 10, 20, 40, 80 and 200 m**. Use the table for the exact colours the plot draws at those sample distances.


# Stage Warp System

Flow: Launcher → TCP WarpRequest → server validates → WarpCommand → bridge sets WARP_PENDING → module consumes next frame.

Blocked when `dolphinState` is LOADING or WARPING.

Level data: `assets/levels.ntsc-u.json` validated against RAScript courseIDs.

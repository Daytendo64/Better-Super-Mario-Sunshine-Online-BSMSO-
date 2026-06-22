# BSE Architecture

SMSO loads as child module `_SMSO.kxe` after `BetterSunshineEngine.kxe`.

Safe hooks: Stage init/update/draw2D/exit callbacks, Player postEntryModels (experimental bodies).

Avoid: SMS_PATCH_BL on Mario draw, PerformList injection, gpMarioPos swap.

Reference: https://github.com/DotKuribo/BetterSunshineEngine

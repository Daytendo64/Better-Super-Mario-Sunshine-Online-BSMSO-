# Game State Extraction

From `TMario` / `TMarDirector` each frame:

- Position, velocity, rotation from `mTranslation`, `mSpeed`, `mFaceAngle`
- Action from `mAction`, `mActionState`
- Stage from `mAreaID`, `mEpisodeID`
- Health proxy from `mShineCount`

Warp via `setNextStage(((area+1)<<8)|episode, 0)`.

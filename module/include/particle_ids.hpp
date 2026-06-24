#pragma once

#include <Dolphin/types.h>

// SMS Mario particle indices (doldecomp include/System/Particles.hpp)
namespace smso::particles {

// Spin jump
constexpr s32 kSpinBlur = 0x105;       // PARTICLE_MS_M_BLUR2
constexpr s32 kSpinBlurSp = 0x106;     // PARTICLE_MS_M_BLUR2SP
constexpr s32 kSpinShotA = 0x114;      // PARTICLE_MS_M_SPINSHOT_A
constexpr s32 kSpinShotB = 0x115;      // PARTICLE_MS_M_SPINSHOT_B

// Ground pound
constexpr s32 kHipBlur = 0x104;        // PARTICLE_MS_M_BLUR3
constexpr s32 kHipBlurSuperA = 0x11A;  // PARTICLE_MS_M_SPHIPD_A
constexpr s32 kHipBlurSuperB = 0x11B;
constexpr s32 kHipBlurSuperC = 0x11C;
constexpr s32 kHipBlurSuperD = 0x11D;
constexpr s32 kHipDropA = 0x12;        // PARTICLE_MS_HIPDROP_A
constexpr s32 kHipDropB = 0x13;
constexpr s32 kHipDropC = 0x14;

// Landing
constexpr s32 kJumpLandA = 0x10;  // PARTICLE_MS_JUMP_ED_A
constexpr s32 kJumpLandB = 0x11;  // PARTICLE_MS_JUMP_ED_B

// Slides
constexpr s32 kSlipSmoke = 0x103;     // PARTICLE_MS_M_SLIPSMOKE
constexpr s32 kSlideSandA = 0x10F;   // PARTICLE_MS_M_SLIDESAND_A
constexpr s32 kSlideSandB = 0x110;
constexpr s32 kWaterSlideA = 0x1EA;  // PARTICLE_MS_M_WATSLIDE_A
constexpr s32 kWaterSlideB = 0x112; // PARTICLE_MS_M_WATSLIDE_B
constexpr s32 kWaterSlideC = 0x113;
constexpr s32 kWalkDust = 0x15;    // PARTICLE_MS_MARIWALK1_A
constexpr s32 kWalkDustB = 0x16;   // PARTICLE_MS_MARIWALK1_B
constexpr s32 kWalkDustC = 0x17;   // PARTICLE_MS_MARIWALK1_C

// FLUDD / misc (existing)
constexpr s32 kSpraySplashA = 0x1D4;
constexpr s32 kSpraySplashB = 0x1D5;
constexpr s32 kSprayRipple = 0x34;   // PARTICLE_MS_M_WATRUN_A
constexpr s32 kWaterSpray = 0x10D;     // doldecomp TNozzleTrigger::animation — nozzle-bound spray cone
constexpr s32 kBodyBubbleA = 0x10C;  // PARTICLE_MS_M_AWA — swimming/body bubbles, not hover
constexpr s32 kBodyBubbleB = 0x111;  // PARTICLE_MS_M_AWA_S
constexpr s32 kRocketExhaustA = 0x11E; // PARTICLE_MS_M_SEASMOKE
constexpr s32 kTurboWaterRipple = 0x34;  // PARTICLE_MS_M_WATRUN_A — doldecomp runningRippleEffect()
constexpr s32 kTurboDashBoostA = 0xFE;   // doldecomp TMarioEffect::perform — waterboost trail A
constexpr s32 kTurboDashBoostB = 0xFF;   // doldecomp TMarioEffect::perform — waterboost trail B

} // namespace smso::particles

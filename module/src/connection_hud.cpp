#include "connection_hud.hpp"

#include <BetterSMS/game.hxx>
#include <BetterSMS/module.hxx>
#include <Dolphin/GX.h>
#include <Dolphin/MTX.h>
#include <Dolphin/mem.h>
#include <Dolphin/printf.h>
#include <Dolphin/string.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <JSystem/J2D/J2DPrint.hxx>
#include <JSystem/JUtility/JUTColor.hxx>
#include <SMS/System/Application.hxx>

#include "comm_buffer.hpp"
#include "gx_hud_fence.hpp"

namespace smso::connection_hud {

namespace {

static constexpr f32 kToastDurationSec = 4.0f;
static constexpr f32 kToastFadeSec = 0.6f;
static constexpr int kMaxToasts = static_cast<int>(MAX_PLAYERS);
static constexpr int kStatusFontSize = 15;
static constexpr int kToastFontSize = 14;
static constexpr int kToastMinFontSize = 8;
static constexpr int kToastLineGap = 4;
static constexpr int kStatusTagGapPx = 14;
static constexpr int kJ2DPrintDefaultLeading = static_cast<int>(0x80000000);

static constexpr int kOrthoTopY = 16;
static constexpr int kStatusMarginX = 20;
static constexpr int kStatusTopPad = 4;
static constexpr int kScreenEdgePad = 8;
static constexpr int kMeasureWidthPadPx = 8;
static constexpr f32 kMeasureWidthFudge = 1.12f;

// Compact Hide & Seek role roster — top-right strip (stock coin/shine/lives +
// Connected/Tag status own the top-left; FLUDD + TIME sit bottom).
static constexpr int kRoleRosterFontSize = 11;
static constexpr int kRoleRosterLineGap = 1;
static constexpr int kRoleRosterColGap = 12;
// Keep clear of TV/overscan and ortho right clip (flush-right was getting cut off).
static constexpr int kRoleRosterRightPad = 36;
static constexpr int kRoleRosterStatusGap = 16;
static constexpr int kRoleRosterMaxNameChars = 12;
static constexpr int kRoleRosterMaxColWidth = 110;
static constexpr int kRoleRosterToastGap = 6;

// SMS HUD projection spans [-adjustX, 600 + adjustX] (see game.cpp / globals.cpp).
static constexpr int kHudLogicalWidth = 600;

enum class ToastKind : u8 {
    Connected = 0,
    Disconnected = 1,
};

struct ToastState {
    bool active;
    ToastKind kind;
    f32 remainingSec;
    char playerName[MAX_PLAYER_NAME];
};

struct OutlineMetrics {
    int offsetPx;
};

struct ToastLayout {
    int drawXName;
    int drawXStatus;
    int drawY;
    int fontSize;
    char nameText[MAX_PLAYER_NAME];
    char statusText[16];
};

static bool gHasSessionBaseline = false;
static bool gPrevLocalConnected = false;
static u8 gPrevRemoteConnected[MAX_REMOTE_SLOTS] = {};
static char gLastRemoteNames[MAX_REMOTE_SLOTS][MAX_PLAYER_NAME] = {};
static u8 gPendingConnectedToast[MAX_REMOTE_SLOTS] = {};
static u16 gLastRosterHudSequence = 0;

static ToastState gToasts[kMaxToasts] = {};
static int gToastCount = 0;

static f32 getFrameDelta() {
    const f32 fps = BetterSMS::getFrameRate();
    return fps > 1.0f ? (1.0f / fps) : (1.0f / 60.0f);
}

static OutlineMetrics calcOutlineMetrics(int fontSize) {
    OutlineMetrics metrics{};
    if (fontSize < 4)
        return metrics;

    f32 offsetF = static_cast<f32>(fontSize) * 0.11f + 0.35f;
    if (offsetF < 1.0f)
        offsetF = 1.0f;
    if (offsetF > 3.0f)
        offsetF = 3.0f;
    metrics.offsetPx = static_cast<int>(offsetF + 0.5f);
    return metrics;
}

static int getStatusDrawY(int fontSize) {
    const OutlineMetrics outline = calcOutlineMetrics(fontSize);
    return kOrthoTopY + outline.offsetPx + fontSize + kStatusTopPad;
}

static int getHudAdjustX() {
    return static_cast<int>(BetterSMS::getScreenRatioAdjustX());
}

static int getHudLeftEdgeX() { return -getHudAdjustX(); }

static int getHudRightEdgeX() { return kHudLogicalWidth + getHudAdjustX(); }

// PostDraw owns an ephemeral ortho from BSE; gx_hud_fence already restored GX.
// Only force the widescreen HUD projection/viewport for status text — do not
// rewrite mBounds (setPort treats those as pixel viewports and desyncs mOrtho).
static void setupHudWidescreenDraw(J2DOrthoGraph *ctx) {
    if (!ctx)
        return;

    ctx->setup2D();
    ctx->setLookat();
    GXSetViewport(0.0f, 0.0f, 640.0f, 480.0f, 0.0f, 1.0f);
    GXSetScissor(0, 0, 640, 480);

    Mtx44 mtx;
    C_MTXOrtho(mtx, 16, 496, -BetterSMS::getScreenRatioAdjustX(),
               600.0f + BetterSMS::getScreenRatioAdjustX(), -1, 1);
    GXSetProjection(mtx, GX_ORTHOGRAPHIC);
}

static int getStatusBlockHeight(int fontSize) {
    const OutlineMetrics outline = calcOutlineMetrics(fontSize);
    return outline.offsetPx * 2 + fontSize;
}

static int getToastBlockHeight(int fontSize) {
    return getStatusBlockHeight(fontSize) * 2 + kToastLineGap;
}

static void getToastHorizontalBounds(int fontSize, int &outLeft, int &outRight) {
    (void)fontSize;
    outLeft = getHudLeftEdgeX() + kStatusMarginX;
    outRight = getHudRightEdgeX() - kStatusMarginX;
    if (outRight < outLeft)
        outRight = outLeft;
}

static int getStatusDrawX(int fontSize) {
    const OutlineMetrics outline = calcOutlineMetrics(fontSize);
    return getHudLeftEdgeX() + kStatusMarginX + outline.offsetPx;
}

static bool isSessionStableForDetection(const CommBuffer *buf) {
    if (!buf || buf->magic != COMM_MAGIC)
        return false;
    if ((buf->bridgeFlags & BF_CONNECTED) == 0)
        return false;
    if ((buf->bridgeFlags & BF_LOADING) != 0)
        return false;
    if (buf->dolphinState == DS_LOADING || buf->dolphinState == DS_WARPING)
        return false;
    return true;
}

static void formatPlayerLabel(char out[MAX_PLAYER_NAME], const char *name, u8 slot) {
    if (name && name[0] != '\0') {
        // Roster HUD / toast labels are pure usernames, not nametag overlay wires.
        // Prefer copyPurePlayerName so overlay marker bytes never blank the label.
        copyPurePlayerName(out, name);
        if (out[0] != '\0')
            return;
        int i = 0;
        for (; i < static_cast<int>(MAX_PLAYER_NAME) - 1 && name[i] != '\0'; ++i)
            out[i] = name[i];
        out[i] = '\0';
        if (out[0] != '\0')
            return;
    }

    snprintf(out, MAX_PLAYER_NAME, "Player %lu", static_cast<unsigned long>(slot) + 1ul);
}

static bool isFallbackPlayerLabel(const char *label) {
    return label && label[0] == 'P' && label[1] == 'l' && label[2] == 'a' && label[3] == 'y' &&
           label[4] == 'e' && label[5] == 'r' && label[6] == ' ';
}

static void rememberRemoteName(u8 slot, const char *name) {
    if (slot >= MAX_REMOTE_SLOTS)
        return;

    char label[MAX_PLAYER_NAME];
    formatPlayerLabel(label, name, slot);
    memcpy(gLastRemoteNames[slot], label, MAX_PLAYER_NAME);
}

static void resetSessionTracking() {
    gHasSessionBaseline = false;
    gPrevLocalConnected = false;
    gLastRosterHudSequence = 0;
    memset(gPrevRemoteConnected, 0, sizeof(gPrevRemoteConnected));
    memset(gLastRemoteNames, 0, sizeof(gLastRemoteNames));
    memset(gPendingConnectedToast, 0, sizeof(gPendingConnectedToast));
}

static void captureRemoteBaseline(const CommBuffer *buf) {
    const u8 localSlot = buf->localSlot;
    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (static_cast<u8>(slot) == localSlot) {
            gPrevRemoteConnected[slot] = 0;
            continue;
        }

        const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
        const bool connected = snap.connected != 0;
        gPrevRemoteConnected[slot] = connected ? 1 : 0;
        if (connected)
            rememberRemoteName(static_cast<u8>(slot), snap.name);
    }
}

static void pushToast(ToastKind kind, const char *playerName, u8 slot) {
    if (!playerName || playerName[0] == '\0')
        return;

    if (gToastCount >= kMaxToasts) {
        memmove(&gToasts[0], &gToasts[1], static_cast<size_t>(kMaxToasts - 1) * sizeof(ToastState));
        gToastCount = kMaxToasts - 1;
    }

    ToastState &toast = gToasts[gToastCount++];
    toast.active = true;
    toast.kind = kind;
    toast.remainingSec = kToastDurationSec;
    formatPlayerLabel(toast.playerName, playerName, slot);
}

static const char *getToastStatusText(ToastKind kind) {
    return kind == ToastKind::Connected ? "connected" : "disconnected";
}

static void updateToastTimers() {
    if (gToastCount <= 0)
        return;

    const f32 dt = getFrameDelta();
    int write = 0;
    for (int i = 0; i < gToastCount; ++i) {
        gToasts[i].remainingSec -= dt;
        if (gToasts[i].remainingSec > 0.0f)
            gToasts[write++] = gToasts[i];
    }
    gToastCount = write;
}

static f32 toastAlpha(const ToastState &toast) {
    if (!toast.active)
        return 0.0f;
    if (toast.remainingSec >= kToastFadeSec)
        return 1.0f;
    if (toast.remainingSec <= 0.0f)
        return 0.0f;
    return toast.remainingSec / kToastFadeSec;
}

static JUtility::TColor applyAlpha(JUtility::TColor color, f32 alpha) {
    const int alphaByte = static_cast<int>(alpha * 255.0f + 0.5f);
    if (alphaByte <= 0)
        return JUtility::TColor(color.r, color.g, color.b, 0);
    if (alphaByte >= 255)
        return color;
    return JUtility::TColor(color.r, color.g, color.b, static_cast<u8>(alphaByte));
}

static void configurePrinter(J2DPrint &printer, int fontSize, JUtility::TColor topColor,
                             JUtility::TColor bottomColor, bool useGradient) {
    const JUtility::TColor bottom = useGradient ? bottomColor : topColor;
    printer.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, topColor, bottom);
    printer.initiate();
    printer.setFontSize(fontSize, fontSize);
    printer.syncCharMetrics();
}

static void drawOutline(J2DPrint &printer, int x, int y, const OutlineMetrics &metrics,
                        const char *text) {
    if (metrics.offsetPx <= 0)
        return;

    for (int dy = -metrics.offsetPx; dy <= metrics.offsetPx; ++dy) {
        for (int dx = -metrics.offsetPx; dx <= metrics.offsetPx; ++dx) {
            if (dx == 0 && dy == 0)
                continue;

            const int adx = dx < 0 ? -dx : dx;
            const int ady = dy < 0 ? -dy : dy;
            const int cheb = adx > ady ? adx : ady;
            if (cheb < 1 || cheb > metrics.offsetPx)
                continue;

            printer.print(x + dx, y + dy, "%s", text);
        }
    }
}

static void drawOutlinedText(int x, int y, int fontSize, const char *text, JUtility::TColor topColor,
                             JUtility::TColor bottomColor, JUtility::TColor outlineColor, f32 alpha,
                             bool useGradient) {
    if (!text || text[0] == '\0' || alpha <= 0.01f)
        return;

    const OutlineMetrics outlineMetrics = calcOutlineMetrics(fontSize);
    const JUtility::TColor top = applyAlpha(topColor, alpha);
    const JUtility::TColor bottom = applyAlpha(bottomColor, alpha);
    const JUtility::TColor outline = applyAlpha(outlineColor, alpha);
    J2DPrint printer(gpSystemFont, 1);

    if (outlineMetrics.offsetPx > 0) {
        configurePrinter(printer, fontSize, outline, outline, false);
        drawOutline(printer, x, y, outlineMetrics, text);
    }

    configurePrinter(printer, fontSize, top, bottom, useGradient);
    printer.print(x, y, "%s", text);
}

static int measureTextWidth(int fontSize, const char *text) {
    if (!gpSystemFont || !text || text[0] == '\0' || fontSize <= 0)
        return 0;

    J2DPrint measure(gpSystemFont, 1);
    const JUtility::TColor white(255, 255, 255, 255);
    measure.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, white, white);
    measure.initiate();
    measure.setFontSize(fontSize, fontSize);
    measure.syncCharMetrics();

    f32 width = measure.getWidth("%s", text);
    const int baseFontWidth = gpSystemFont->getWidth();
    if (baseFontWidth > 0)
        width *= static_cast<f32>(fontSize) / static_cast<f32>(baseFontWidth);
    width *= kMeasureWidthFudge;
    return static_cast<int>(width + 0.5f) + kMeasureWidthPadPx;
}

static int getTextBlockWidth(int fontSize, const char *text) {
    const OutlineMetrics outline = calcOutlineMetrics(fontSize);
    return measureTextWidth(fontSize, text) + outline.offsetPx * 2;
}

static void truncateRosterName(char *out, size_t outCap, const char *name, int maxChars) {
    if (!out || outCap == 0) {
        return;
    }
    out[0] = '\0';
    if (!name || name[0] == '\0' || maxChars <= 0)
        return;

    const int len = static_cast<int>(strlen(name));
    if (len <= maxChars) {
        snprintf(out, outCap, "%s", name);
        return;
    }

    if (maxChars <= 1) {
        snprintf(out, outCap, ".");
        return;
    }
    // Keep maxChars total including ellipsis so column width stays predictable.
    snprintf(out, outCap, "%.*s.", maxChars - 1, name);
}

// Fit a roster name into maxBlockWidth pixels (and maxChars), shrinking with a trailing '.'.
static void fitRosterName(char *out, size_t outCap, const char *name, int fontSize, int maxBlockWidth,
                          int maxChars) {
    if (!out || outCap == 0) {
        return;
    }
    out[0] = '\0';
    if (!name || name[0] == '\0' || fontSize <= 0 || maxBlockWidth <= 0 || maxChars <= 0)
        return;

    truncateRosterName(out, outCap, name, maxChars);
    if (out[0] == '\0')
        return;

    while (out[0] != '\0' && getTextBlockWidth(fontSize, out) > maxBlockWidth) {
        const int len = static_cast<int>(strlen(out));
        if (len <= 1) {
            out[0] = '\0';
            return;
        }
        // Drop one visible char before the ellipsis (or the last char if no ellipsis yet).
        if (out[len - 1] == '.' && len >= 2) {
            out[len - 2] = '.';
            out[len - 1] = '\0';
        } else {
            out[len - 1] = '\0';
        }
    }
}

static bool isHideSeekSlotOccupied(const CommBuffer *buf, u8 slot) {
    if (!buf || slot >= MAX_PLAYERS)
        return false;
    if (slot == buf->localSlot)
        return (buf->bridgeFlags & BF_CONNECTED) != 0;
    return buf->remoteSnapshots[slot].connected != 0;
}

static void getHideSeekSlotLabel(char out[MAX_PLAYER_NAME], const CommBuffer *buf, u8 slot) {
    if (!buf || slot >= MAX_PLAYERS) {
        out[0] = '\0';
        return;
    }
    if (slot == buf->localSlot)
        formatPlayerLabel(out, buf->localPlayerName, slot);
    else
        formatPlayerLabel(out, buf->remoteSnapshots[slot].name, slot);
}

static int computeRightAlignedDrawX(int rightOuterEdge, int fontSize, const char *text) {
    const OutlineMetrics outline = calcOutlineMetrics(fontSize);
    const int textWidth = measureTextWidth(fontSize, text);
    return rightOuterEdge - outline.offsetPx - textWidth;
}

static void clampTextBlockPosition(int &drawX, int fontSize, const char *text, int leftBound,
                                   int rightBound) {
    const OutlineMetrics outline = calcOutlineMetrics(fontSize);
    const int blockWidth = getTextBlockWidth(fontSize, text);

    int blockLeft = drawX - outline.offsetPx;
    if (blockLeft < leftBound)
        drawX += leftBound - blockLeft;

    int blockRight = drawX - outline.offsetPx + blockWidth;
    if (blockRight > rightBound)
        drawX -= blockRight - rightBound;

    blockLeft = drawX - outline.offsetPx;
    if (blockLeft < leftBound)
        drawX = leftBound + outline.offsetPx;
}

static void drawRoleRosterLine(int colRight, int y, int fontSize, int leftBound, int rightEdge,
                               const char *text, const JUtility::TColor &top,
                               const JUtility::TColor &bottom,
                               const JUtility::TColor &outlineColor) {
    if (!text || text[0] == '\0')
        return;
    int drawX = computeRightAlignedDrawX(colRight, fontSize, text);
    clampTextBlockPosition(drawX, fontSize, text, leftBound, rightEdge);
    drawOutlinedText(drawX, y, fontSize, text, top, bottom, outlineColor, 1.0f, true);
}

// Returns the next free draw Y below the roster (same as statusY when nothing drawn)
// so join/leave toasts can start underneath instead of overlapping the role list.
static int drawHideSeekRoleRoster(const CommBuffer *buf, int statusY, int statusRightX) {
    if (!buf || buf->magic != COMM_MAGIC || buf->gameModeState.mode != GM_HIDE_SEEK)
        return statusY;
    if (!gpSystemFont)
        return statusY;

    const GameModeState &gm = buf->gameModeState;
    u8 hiders[MAX_PLAYERS];
    u8 seekers[MAX_PLAYERS];
    int hiderCount = 0;
    int seekerCount = 0;

    for (u8 slot = 0; slot < MAX_PLAYERS; ++slot) {
        if (!isHideSeekSlotOccupied(buf, slot))
            continue;
        if (gm.roleBySlot[slot] == HSR_SEEKER)
            seekers[seekerCount++] = slot;
        else
            hiders[hiderCount++] = slot;
    }

    if (hiderCount == 0 && seekerCount == 0)
        return statusY;

    const int fontSize = kRoleRosterFontSize;
    const int lineStep = getStatusBlockHeight(fontSize) + kRoleRosterLineGap;
    const int rightEdge = getHudRightEdgeX() - kRoleRosterRightPad;
    // Stay clear of Connected / Tag-is-going on the left so columns are not shoved
    // off the right edge when the status band is wide.
    int leftBound = statusRightX + kRoleRosterStatusGap;
    const int screenLeft = getHudLeftEdgeX() + kStatusMarginX;
    if (leftBound < screenLeft)
        leftBound = screenLeft;

    int available = rightEdge - leftBound;
    if (available < 40)
        return statusY;

    const int headerHiderW = getTextBlockWidth(fontSize, "Hiders");
    const int headerSeekerW = getTextBlockWidth(fontSize, "Seekers");
    char label[MAX_PLAYER_NAME];
    char truncated[MAX_PLAYER_NAME];

    // Prefer dual equal-width columns when both roles exist and both fit; otherwise
    // stack one column so nothing is clipped past the right pad.
    bool dualCol = hiderCount > 0 && seekerCount > 0;
    int hiderColW = headerHiderW;
    int seekerColW = headerSeekerW;
    if (dualCol) {
        const int maxEach = (available - kRoleRosterColGap) / 2;
        int capped = maxEach;
        if (capped > kRoleRosterMaxColWidth)
            capped = kRoleRosterMaxColWidth;
        if (capped < headerHiderW || capped < headerSeekerW) {
            dualCol = false;
        } else {
            hiderColW = capped;
            seekerColW = capped;
        }
    }

    if (!dualCol) {
        int singleW = available;
        if (singleW > kRoleRosterMaxColWidth)
            singleW = kRoleRosterMaxColWidth;
        if (hiderCount > 0 && seekerCount == 0) {
            if (singleW < headerHiderW)
                singleW = headerHiderW;
            hiderColW = singleW;
        } else if (seekerCount > 0 && hiderCount == 0) {
            if (singleW < headerSeekerW)
                singleW = headerSeekerW;
            seekerColW = singleW;
        } else {
            // Stacked single column: both role lists share the same right edge.
            if (singleW < headerSeekerW)
                singleW = headerSeekerW;
            if (singleW < headerHiderW)
                singleW = headerHiderW;
            hiderColW = singleW;
            seekerColW = singleW;
        }
    }

    const int blockW =
        dualCol ? (hiderColW + kRoleRosterColGap + seekerColW)
                : (hiderCount > 0 && seekerCount > 0
                       ? (hiderColW > seekerColW ? hiderColW : seekerColW)
                       : (hiderCount > 0 ? hiderColW : seekerColW));
    int blockLeft = rightEdge - blockW;
    if (blockLeft < leftBound)
        blockLeft = leftBound;

    // Re-clamp column widths if the forced blockLeft ate into the budget.
    if (dualCol) {
        const int fit = rightEdge - blockLeft;
        if (fit < hiderColW + kRoleRosterColGap + seekerColW) {
            const int each = (fit - kRoleRosterColGap) / 2;
            if (each < headerHiderW || each < headerSeekerW) {
                dualCol = false;
            } else {
                hiderColW = each;
                seekerColW = each;
            }
        }
    }

    const int hiderColRight = dualCol ? (blockLeft + hiderColW) : rightEdge;
    const int seekerColRight = rightEdge;

    // Same top band as Connected/Tag status — top-right stays clear of stock HUD.
    int y = statusY;

    const JUtility::TColor outlineColor(0, 0, 0, 255);
    // Match Hide & Seek nametag palette (hider blue / seeker red).
    const JUtility::TColor hiderTop(90, 170, 255, 255);
    const JUtility::TColor hiderBottom(46, 134, 255, 255);
    const JUtility::TColor seekerTop(255, 120, 120, 255);
    const JUtility::TColor seekerBottom(255, 59, 59, 255);

    auto drawHiderName = [&](u8 slot) {
        getHideSeekSlotLabel(label, buf, slot);
        fitRosterName(truncated, sizeof(truncated), label, fontSize, hiderColW,
                      kRoleRosterMaxNameChars);
        drawRoleRosterLine(hiderColRight, y, fontSize, leftBound, rightEdge, truncated, hiderTop,
                           hiderBottom, outlineColor);
    };
    auto drawSeekerName = [&](u8 slot) {
        getHideSeekSlotLabel(label, buf, slot);
        fitRosterName(truncated, sizeof(truncated), label, fontSize, seekerColW,
                      kRoleRosterMaxNameChars);
        drawRoleRosterLine(seekerColRight, y, fontSize, leftBound, rightEdge, truncated, seekerTop,
                           seekerBottom, outlineColor);
    };

    if (dualCol) {
        drawRoleRosterLine(hiderColRight, y, fontSize, leftBound, rightEdge, "Hiders", hiderTop,
                           hiderBottom, outlineColor);
        drawRoleRosterLine(seekerColRight, y, fontSize, leftBound, rightEdge, "Seekers", seekerTop,
                           seekerBottom, outlineColor);
        y += lineStep;
        const int rows = hiderCount > seekerCount ? hiderCount : seekerCount;
        for (int row = 0; row < rows; ++row) {
            if (row < hiderCount)
                drawHiderName(hiders[row]);
            if (row < seekerCount)
                drawSeekerName(seekers[row]);
            y += lineStep;
        }
    } else if (hiderCount > 0 && seekerCount > 0) {
        // Stacked: Hiders block, then Seekers — one right-aligned column.
        drawRoleRosterLine(hiderColRight, y, fontSize, leftBound, rightEdge, "Hiders", hiderTop,
                           hiderBottom, outlineColor);
        y += lineStep;
        for (int i = 0; i < hiderCount; ++i) {
            drawHiderName(hiders[i]);
            y += lineStep;
        }
        y += lineStep / 2;
        drawRoleRosterLine(seekerColRight, y, fontSize, leftBound, rightEdge, "Seekers", seekerTop,
                           seekerBottom, outlineColor);
        y += lineStep;
        for (int i = 0; i < seekerCount; ++i) {
            drawSeekerName(seekers[i]);
            y += lineStep;
        }
    } else if (hiderCount > 0) {
        drawRoleRosterLine(hiderColRight, y, fontSize, leftBound, rightEdge, "Hiders", hiderTop,
                           hiderBottom, outlineColor);
        y += lineStep;
        for (int i = 0; i < hiderCount; ++i) {
            drawHiderName(hiders[i]);
            y += lineStep;
        }
    } else {
        drawRoleRosterLine(seekerColRight, y, fontSize, leftBound, rightEdge, "Seekers", seekerTop,
                           seekerBottom, outlineColor);
        y += lineStep;
        for (int i = 0; i < seekerCount; ++i) {
            drawSeekerName(seekers[i]);
            y += lineStep;
        }
    }

    return y + kRoleRosterToastGap;
}

static void buildToastNameText(char *out, size_t outCap, const char *playerName, int fontSize,
                               int maxBlockWidth) {
    if (!out || outCap == 0 || !playerName || fontSize <= 0 || maxBlockWidth <= 0) {
        if (out && outCap > 0)
            out[0] = '\0';
        return;
    }

    char name[MAX_PLAYER_NAME];
    strncpy(name, playerName, sizeof(name) - 1);
    name[sizeof(name) - 1] = '\0';

    snprintf(out, outCap, "%s", name);
    if (getTextBlockWidth(fontSize, out) <= maxBlockWidth)
        return;

    static const char kEllipsis[] = "...";

    size_t nameLen = strlen(name);
    while (nameLen > 0) {
        if (nameLen == strlen(name)) {
            snprintf(out, outCap, "%s", name);
        } else if (nameLen <= 1) {
            snprintf(out, outCap, "%s", kEllipsis);
        } else {
            snprintf(out, outCap, "%.*s%s", static_cast<int>(nameLen), name, kEllipsis);
        }

        if (getTextBlockWidth(fontSize, out) <= maxBlockWidth)
            return;

        if (nameLen == 0)
            break;

        --nameLen;
    }

    out[0] = '\0';
}

static ToastLayout fitToastLayout(int drawY, const char *playerName, ToastKind kind) {
    ToastLayout layout{};
    layout.drawY = drawY;
    layout.fontSize = kToastMinFontSize;
    if (!playerName || playerName[0] == '\0')
        return layout;

    const char *statusText = getToastStatusText(kind);
    int leftBound = 0;
    int rightBound = 0;

    for (int fontSize = kToastFontSize; fontSize >= kToastMinFontSize; --fontSize) {
        getToastHorizontalBounds(fontSize, leftBound, rightBound);
        const int maxBlockWidth = rightBound - leftBound;
        if (maxBlockWidth <= 0)
            continue;

        buildToastNameText(layout.nameText, sizeof(layout.nameText), playerName, fontSize,
                           maxBlockWidth);
        if (layout.nameText[0] == '\0')
            continue;

        strncpy(layout.statusText, statusText, sizeof(layout.statusText) - 1);
        layout.statusText[sizeof(layout.statusText) - 1] = '\0';

        const int nameBlockWidth = getTextBlockWidth(fontSize, layout.nameText);
        const int statusBlockWidth = getTextBlockWidth(fontSize, layout.statusText);
        if (nameBlockWidth > maxBlockWidth || statusBlockWidth > maxBlockWidth)
            continue;

        layout.fontSize = fontSize;
        layout.drawXName = computeRightAlignedDrawX(rightBound, fontSize, layout.nameText);
        layout.drawXStatus = computeRightAlignedDrawX(rightBound, fontSize, layout.statusText);
        clampTextBlockPosition(layout.drawXName, fontSize, layout.nameText, leftBound, rightBound);
        clampTextBlockPosition(layout.drawXStatus, fontSize, layout.statusText, leftBound,
                               rightBound);
        break;
    }

    return layout;
}

static void getToastColors(ToastKind kind, JUtility::TColor &top, JUtility::TColor &bottom) {
    if (kind == ToastKind::Connected) {
        top = JUtility::TColor(80, 220, 90, 255);
        bottom = JUtility::TColor(40, 170, 60, 255);
    } else {
        top = JUtility::TColor(230, 90, 90, 255);
        bottom = JUtility::TColor(180, 50, 50, 255);
    }
}

static void syncRemoteSnapshotState(const CommBuffer *buf) {
    const u8 localSlot = buf->localSlot;
    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (static_cast<u8>(slot) == localSlot) {
            gPrevRemoteConnected[slot] = 0;
            gPendingConnectedToast[slot] = 0;
            continue;
        }

        const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
        const bool connected = snap.connected != 0;
        gPrevRemoteConnected[slot] = connected ? 1 : 0;
        if (connected) {
            rememberRemoteName(static_cast<u8>(slot), snap.name);
            // Flush a deferred join toast once the remote snapshot carries a real name.
            if (gPendingConnectedToast[slot] != 0) {
                char label[MAX_PLAYER_NAME];
                formatPlayerLabel(label, snap.name, static_cast<u8>(slot));
                if (!isFallbackPlayerLabel(label)) {
                    pushToast(ToastKind::Connected, label, static_cast<u8>(slot));
                    rememberRemoteName(static_cast<u8>(slot), label);
                    gPendingConnectedToast[slot] = 0;
                }
            }
        } else {
            gLastRemoteNames[slot][0] = '\0';
            gPendingConnectedToast[slot] = 0;
        }
    }
}

static void processRosterHudEvents(const CommBuffer *buf) {
    if (!buf || buf->magic != COMM_MAGIC)
        return;

    const u16 latest = buf->rosterHud.latestSequence;
    if (latest == 0 || latest <= gLastRosterHudSequence)
        return;

    while (gLastRosterHudSequence < latest) {
        ++gLastRosterHudSequence;
        const u32 index =
            static_cast<u32>((gLastRosterHudSequence - 1u) % COMM_ROSTER_HUD_RING_SLOTS);
        const RosterHudEvent &ev = buf->rosterHud.events[index];
        if (ev.sequence != gLastRosterHudSequence || ev.kind == RHE_NONE)
            continue;

        char label[MAX_PLAYER_NAME];
        const bool hasEventName = ev.name[0] != '\0';
        if (hasEventName) {
            formatPlayerLabel(label, ev.name, ev.slot);
        } else if (ev.slot < MAX_REMOTE_SLOTS && gLastRemoteNames[ev.slot][0] != '\0') {
            memcpy(label, gLastRemoteNames[ev.slot], MAX_PLAYER_NAME);
        } else if (ev.slot < MAX_REMOTE_SLOTS &&
                   static_cast<u8>(ev.slot) != buf->localSlot &&
                   buf->remoteSnapshots[ev.slot].connected != 0) {
            formatPlayerLabel(label, buf->remoteSnapshots[ev.slot].name, ev.slot);
        } else {
            formatPlayerLabel(label, nullptr, ev.slot);
        }

        if (ev.kind == RHE_CONNECTED) {
            // Empty/fallback names happen when the mailbox race wipes the event name
            // before Dolphin reads it, or the remote snapshot is not ready yet. Defer
            // until syncRemoteSnapshotState sees a real username.
            if (isFallbackPlayerLabel(label)) {
                if (ev.slot < MAX_REMOTE_SLOTS)
                    gPendingConnectedToast[ev.slot] = 1;
            } else {
                pushToast(ToastKind::Connected, label, ev.slot);
                rememberRemoteName(ev.slot, label);
                if (ev.slot < MAX_REMOTE_SLOTS)
                    gPendingConnectedToast[ev.slot] = 0;
            }
            if (ev.slot < MAX_REMOTE_SLOTS)
                gPrevRemoteConnected[ev.slot] = 1;
        } else if (ev.kind == RHE_DISCONNECTED) {
            pushToast(ToastKind::Disconnected, label, ev.slot);
            if (ev.slot < MAX_REMOTE_SLOTS) {
                gPrevRemoteConnected[ev.slot] = 0;
                gLastRemoteNames[ev.slot][0] = '\0';
                gPendingConnectedToast[ev.slot] = 0;
            }
        }
    }
}

} // namespace

void initSystem() {
    resetSessionTracking();
    gToastCount = 0;
    memset(gToasts, 0, sizeof(gToasts));
}

void updateSystem(TApplication *app) {
    (void)app;
    updateToastTimers();

    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return;

    processRosterHudEvents(buf);

    const bool localConnected = (buf->bridgeFlags & BF_CONNECTED) != 0;

    if (!localConnected) {
        if (gPrevLocalConnected || gHasSessionBaseline)
            resetSessionTracking();
        gPrevLocalConnected = false;
        return;
    }

    gPrevLocalConnected = true;

    if (!isSessionStableForDetection(buf))
        return;

    if (!gHasSessionBaseline) {
        captureRemoteBaseline(buf);
        gLastRosterHudSequence = buf->rosterHud.latestSequence;
        gHasSessionBaseline = true;
        return;
    }

    syncRemoteSnapshotState(buf);
}

void drawSystem(TApplication *app, const J2DOrthoGraph *ortho) {
    (void)app;
    if (!ortho || !gpSystemFont)
        return;

    CommBuffer *buf = getCommBuffer();
    const bool localConnected =
        buf && buf->magic == COMM_MAGIC && (buf->bridgeFlags & BF_CONNECTED) != 0;

    auto *ctx = const_cast<J2DOrthoGraph *>(ortho);
    // PostDraw runs after the frame, but still fence so leftover J2DPrint / TEV
    // state cannot poison the next frame's first draws (world + HUD).
    gx_hud_fence::beginOverlay(ctx);
    setupHudWidescreenDraw(ctx);

    const int statusX = getStatusDrawX(kStatusFontSize);
    const int statusY = getStatusDrawY(kStatusFontSize);
    const JUtility::TColor outlineColor(0, 0, 0, 255);

    const char *connectionText = localConnected ? "Connected" : "Disconnected";
    if (localConnected) {
        const JUtility::TColor top(80, 220, 90, 255);
        const JUtility::TColor bottom(40, 170, 60, 255);
        drawOutlinedText(statusX, statusY, kStatusFontSize, connectionText, top, bottom,
                         outlineColor, 1.0f, true);
    } else {
        const JUtility::TColor top(230, 90, 90, 255);
        const JUtility::TColor bottom(180, 50, 50, 255);
        drawOutlinedText(statusX, statusY, kStatusFontSize, connectionText, top, bottom,
                         outlineColor, 1.0f, true);
    }

    // Hide & Seek only: tag round status immediately to the right of Connected/Disconnected.
    // Role roster owns the top-right; join/leave toasts start below it when present.
    int toastY = statusY;
    if (buf && buf->magic == COMM_MAGIC && buf->gameModeState.mode == GM_HIDE_SEEK) {
        const bool tagGoing = (buf->gameModeState.flags & GMF_TAG_ACTIVE) != 0;
        const char *tagText = tagGoing ? "Tag is going" : "Tag is stopped";
        const int tagX =
            statusX + getTextBlockWidth(kStatusFontSize, connectionText) + kStatusTagGapPx;
        if (tagGoing) {
            // Warm gold — readable on SMS scenes; distinct from Connected green / stopped blue.
            const JUtility::TColor top(255, 214, 72, 255);
            const JUtility::TColor bottom(196, 140, 18, 255);
            drawOutlinedText(tagX, statusY, kStatusFontSize, tagText, top, bottom, outlineColor,
                             1.0f, true);
        } else {
            const JUtility::TColor top(120, 170, 230, 255);
            const JUtility::TColor bottom(70, 110, 190, 255);
            drawOutlinedText(tagX, statusY, kStatusFontSize, tagText, top, bottom, outlineColor,
                             1.0f, true);
        }

        const int statusRightX =
            tagX + getTextBlockWidth(kStatusFontSize, tagText);
        toastY = drawHideSeekRoleRoster(buf, statusY, statusRightX);
    }

    if (gToastCount > 0) {
        for (int i = 0; i < gToastCount; ++i) {
            const ToastState &toast = gToasts[i];
            const f32 alpha = toastAlpha(toast);
            if (alpha <= 0.01f)
                continue;

            JUtility::TColor top;
            JUtility::TColor bottom;
            getToastColors(toast.kind, top, bottom);

            const ToastLayout layout = fitToastLayout(toastY, toast.playerName, toast.kind);
            const int lineHeight = getStatusBlockHeight(layout.fontSize);
            const int statusLineY = layout.drawY + lineHeight + kToastLineGap;

            drawOutlinedText(layout.drawXName, layout.drawY, layout.fontSize, layout.nameText, top,
                             bottom, outlineColor, alpha, true);
            drawOutlinedText(layout.drawXStatus, statusLineY, layout.fontSize, layout.statusText, top,
                             bottom, outlineColor, alpha, true);
            toastY += getToastBlockHeight(layout.fontSize) + kToastLineGap;
        }
    }

    gx_hud_fence::endOverlay(ctx);
}

} // namespace smso::connection_hud

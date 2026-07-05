#include "connection_hud.hpp"

#include <BetterSMS/game.hxx>
#include <BetterSMS/module.hxx>
#include <Dolphin/mem.h>
#include <Dolphin/string.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <JSystem/J2D/J2DPrint.hxx>
#include <JSystem/JUtility/JUTColor.hxx>
#include <SMS/MarioUtil/gd-reinit-gx.hxx>
#include <SMS/System/Application.hxx>

#include "comm_buffer.hpp"

namespace smso::connection_hud {

namespace {

struct JUTPoint {
    s32 x;
    s32 y;
};

static JUTPoint *getCoinMidPoint() {
    return reinterpret_cast<JUTPoint *>(SMS_PORT_REGION(0x8040DDC8, 0x80405448, 0, 0));
}

static constexpr f32 kToastDurationSec = 4.0f;
static constexpr f32 kToastFadeSec = 0.6f;
static constexpr int kMaxToasts = 5;
static constexpr int kStatusFontSize = 15;
static constexpr int kToastFontSize = 14;
static constexpr int kToastMinFontSize = 10;
static constexpr int kToastLineGap = 4;
static constexpr int kJ2DPrintDefaultLeading = static_cast<int>(0x80000000);

static constexpr int kFallbackToastX = 498;
static constexpr int kOrthoTopY = 16;
static constexpr int kStatusMarginX = 20;
static constexpr int kStatusTopPad = 4;
static constexpr int kScreenEdgePad = 8;

enum class ToastKind : u8 {
    Connected = 0,
    Disconnected = 1,
};

struct ToastState {
    bool active;
    ToastKind kind;
    f32 remainingSec;
    char text[48];
};

struct OutlineMetrics {
    int offsetPx;
};

struct ToastLayout {
    int drawX;
    int drawY;
    int fontSize;
};

static bool gHasSessionBaseline = false;
static bool gPrevLocalConnected = false;
static u8 gPrevRemoteConnected[MAX_REMOTE_SLOTS] = {};
static char gLastRemoteNames[MAX_REMOTE_SLOTS][MAX_PLAYER_NAME] = {};
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

static void getScreenHorizontalBounds(int fontSize, int &outLeft, int &outRight) {
    const OutlineMetrics outline = calcOutlineMetrics(fontSize);
    const int screenAdjustX = static_cast<int>(BetterSMS::getScreenRatioAdjustX());
    const int orthoWidth = static_cast<int>(BetterSMS::getScreenOrthoWidth());
    outLeft = kScreenEdgePad - screenAdjustX + outline.offsetPx;
    outRight = orthoWidth - kScreenEdgePad - screenAdjustX - outline.offsetPx;
    if (outRight < outLeft)
        outRight = outLeft;
}

static int getToastAnchorX() {
    const int screenAdjustX = static_cast<int>(BetterSMS::getScreenRatioAdjustX());
    const JUTPoint *mid = getCoinMidPoint();

    if (mid && mid->x > 0)
        return mid->x + screenAdjustX;

    return kFallbackToastX + screenAdjustX;
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
        copyPurePlayerName(out, name);
        if (out[0] != '\0')
            return;
    }

    snprintf(out, MAX_PLAYER_NAME, "Player %lu", static_cast<unsigned long>(slot) + 1ul);
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

static void pushToast(ToastKind kind, const char *playerName) {
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
    if (kind == ToastKind::Connected)
        snprintf(toast.text, sizeof(toast.text), "%s connected", playerName);
    else
        snprintf(toast.text, sizeof(toast.text), "%s disconnected", playerName);
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

static void printLayer(int x, int y, int fontSize, const char *text, JUtility::TColor topColor,
                       JUtility::TColor bottomColor, bool useGradient) {
    if (!gpSystemFont || !text || text[0] == '\0')
        return;

    J2DPrint printer(gpSystemFont, 1);
    const JUtility::TColor bottom = useGradient ? bottomColor : topColor;
    printer.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, topColor, bottom);
    printer.initiate();
    printer.setFontSize(fontSize, fontSize);
    printer.syncCharMetrics();
    printer.print(x, y, "%s", text);
}

static void drawOutline(int x, int y, int fontSize, const OutlineMetrics &metrics, const char *text,
                        JUtility::TColor outlineColor) {
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

            printLayer(x + dx, y + dy, fontSize, text, outlineColor, outlineColor, false);
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

    if (outlineMetrics.offsetPx > 0)
        drawOutline(x, y, fontSize, outlineMetrics, text, outline);

    printLayer(x, y, fontSize, text, top, bottom, useGradient);
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
    return static_cast<int>(width + 0.5f);
}

static ToastLayout fitToastLayout(int anchorX, int drawY, const char *text) {
    ToastLayout layout{};
    layout.drawY = drawY;
    layout.fontSize = kToastFontSize;

    int leftBound = 0;
    int rightBound = 0;
    for (int fontSize = kToastFontSize; fontSize >= kToastMinFontSize; --fontSize) {
        getScreenHorizontalBounds(fontSize, leftBound, rightBound);
        const int maxWidth = rightBound - leftBound;
        if (maxWidth <= 0)
            break;

        const int textWidth = measureTextWidth(fontSize, text);
        layout.fontSize = fontSize;

        int drawX = anchorX - textWidth / 2;
        if (drawX < leftBound)
            drawX = leftBound;
        if (drawX + textWidth > rightBound)
            drawX = rightBound - textWidth;
        if (drawX < leftBound)
            drawX = leftBound;

        layout.drawX = drawX;
        if (textWidth <= maxWidth)
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
            continue;
        }

        const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
        const bool connected = snap.connected != 0;
        gPrevRemoteConnected[slot] = connected ? 1 : 0;
        if (connected)
            rememberRemoteName(static_cast<u8>(slot), snap.name);
        else
            gLastRemoteNames[slot][0] = '\0';
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
        formatPlayerLabel(label, ev.name, ev.slot);

        if (ev.kind == RHE_CONNECTED) {
            pushToast(ToastKind::Connected, label);
            rememberRemoteName(ev.slot, ev.name);
            if (ev.slot < MAX_REMOTE_SLOTS)
                gPrevRemoteConnected[ev.slot] = 1;
        } else if (ev.kind == RHE_DISCONNECTED) {
            pushToast(ToastKind::Disconnected, label);
            if (ev.slot < MAX_REMOTE_SLOTS) {
                gPrevRemoteConnected[ev.slot] = 0;
                gLastRemoteNames[ev.slot][0] = '\0';
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

    ReInitializeGX();
    const_cast<J2DOrthoGraph *>(ortho)->setup2D();

    const int screenAdjustX = static_cast<int>(BetterSMS::getScreenRatioAdjustX());
    const int statusX = kStatusMarginX - screenAdjustX;
    const int statusY = getStatusDrawY(kStatusFontSize);
    const JUtility::TColor outlineColor(0, 0, 0, 255);

    if (localConnected) {
        const JUtility::TColor top(80, 220, 90, 255);
        const JUtility::TColor bottom(40, 170, 60, 255);
        drawOutlinedText(statusX, statusY, kStatusFontSize, "Connected", top, bottom, outlineColor,
                         1.0f, true);
    } else {
        const JUtility::TColor top(230, 90, 90, 255);
        const JUtility::TColor bottom(180, 50, 50, 255);
        drawOutlinedText(statusX, statusY, kStatusFontSize, "Disconnected", top, bottom, outlineColor,
                         1.0f, true);
    }

    if (gToastCount <= 0)
        return;

    const int anchorX = getToastAnchorX();
    const int lineStep = kToastFontSize + kToastLineGap;
    for (int i = 0; i < gToastCount; ++i) {
        const ToastState &toast = gToasts[i];
        const f32 alpha = toastAlpha(toast);
        if (alpha <= 0.01f)
            continue;

        JUtility::TColor top;
        JUtility::TColor bottom;
        getToastColors(toast.kind, top, bottom);

        const ToastLayout layout = fitToastLayout(anchorX, statusY + i * lineStep, toast.text);
        drawOutlinedText(layout.drawX, layout.drawY, layout.fontSize, toast.text, top, bottom,
                         outlineColor, alpha, true);
    }
}

} // namespace smso::connection_hud

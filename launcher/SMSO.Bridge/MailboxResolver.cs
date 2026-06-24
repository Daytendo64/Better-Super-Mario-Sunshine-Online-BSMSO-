using System;
using System.Threading;
using SMSO.Net;

namespace SMSO.Bridge;

/// <summary>
/// Resolves the BSMSO CommBuffer host address in Dolphin. Fast path uses known fastmem layout;
/// a bounded background scan handles unusual builds without blocking the 60 Hz bridge loop.
/// </summary>
internal sealed class MailboxResolver
{
    private readonly object _lock = new();
    private IntPtr _processHandle;
    private uint _guestMailbox = ProtocolConstants.DefaultMailboxAddress;
    private UIntPtr _hostAddress = UIntPtr.Zero;
    private DateTime _searchStartedUtc = DateTime.MinValue;
    private DateTime _lastFastAttemptUtc = DateTime.MinValue;
    private DateTime _lastBackgroundScanUtc = DateTime.MinValue;
    private int _backgroundScanRunning;
    private string? _lastError;

    private const int FastRetryMs = 50;
    private const int BackgroundScanCooldownMs = 5000;

    public UIntPtr HostAddress
    {
        get { lock (_lock) return _hostAddress; }
    }

    public bool IsResolved
    {
        get { lock (_lock) return _hostAddress != UIntPtr.Zero; }
    }

    public string? LastError
    {
        get { lock (_lock) return _lastError; }
    }

    public DateTime SearchStartedUtc
    {
        get { lock (_lock) return _searchStartedUtc; }
    }

    public TimeSpan SearchDuration =>
        _searchStartedUtc == DateTime.MinValue ? TimeSpan.Zero : DateTime.UtcNow - _searchStartedUtc;

    public void Bind(IntPtr processHandle, uint guestMailbox)
    {
        lock (_lock)
        {
            if (_processHandle != processHandle)
                DolphinMemoryMap.InvalidateCache();

            _processHandle = processHandle;
            _guestMailbox = guestMailbox;
        }
    }

    public void SetGuestMailbox(uint guestMailbox)
    {
        lock (_lock)
        {
            if (_guestMailbox == guestMailbox)
                return;

            _guestMailbox = guestMailbox;
            InvalidateCore();
        }
    }

    public void Invalidate()
    {
        lock (_lock)
            InvalidateCore();
    }

    private void InvalidateCore()
    {
        _hostAddress = UIntPtr.Zero;
        _lastError = null;
        _searchStartedUtc = DateTime.MinValue;
        _lastFastAttemptUtc = DateTime.MinValue;
        DolphinMemoryMap.InvalidateCache();
    }

    public void Reset()
    {
        lock (_lock)
        {
            InvalidateCore();
            _processHandle = IntPtr.Zero;
            _lastBackgroundScanUtc = DateTime.MinValue;
        }
    }

    /// <returns>True if mailbox is resolved after this call.</returns>
    public bool TryResolve(bool force = false)
    {
        lock (_lock)
        {
            if (_processHandle == IntPtr.Zero)
            {
                _lastError = "Not attached to Dolphin";
                return false;
            }

            if (!force && _hostAddress != UIntPtr.Zero)
                return true;

            if (!force &&
                (DateTime.UtcNow - _lastFastAttemptUtc).TotalMilliseconds < FastRetryMs)
            {
                return _hostAddress != UIntPtr.Zero;
            }

            _lastFastAttemptUtc = DateTime.UtcNow;
            if (_searchStartedUtc == DateTime.MinValue)
                _searchStartedUtc = DateTime.UtcNow;

            if (DolphinMemoryMap.TryResolveMailboxFast(_processHandle, _guestMailbox, out var host))
            {
                _hostAddress = host;
                _lastError = null;
                _searchStartedUtc = DateTime.MinValue;
                return true;
            }

            ScheduleBackgroundScanIfNeededLocked();

            _hostAddress = UIntPtr.Zero;
            var regionCount = DolphinMemoryMap.CountReadableRegions(_processHandle);
            _lastError = regionCount == 0
                ? "Could not read Dolphin memory — run launcher as administrator"
                : SearchDuration.TotalSeconds < 8
                    ? $"Waiting for {ModuleVersionMessages.ModuleFileName} — boot the game and enter a stage"
                    : $"BSMSO mailbox not found — confirm {ModuleVersionMessages.ModuleFileName} is enabled in Kuribo Mods";
            return false;
        }
    }

    public void OnBackgroundScanSucceeded(UIntPtr host)
    {
        lock (_lock)
        {
            _hostAddress = host;
            _lastError = null;
            _searchStartedUtc = DateTime.MinValue;
        }
    }

    private void ScheduleBackgroundScanIfNeededLocked()
    {
        if ((DateTime.UtcNow - _lastBackgroundScanUtc).TotalMilliseconds < BackgroundScanCooldownMs)
            return;

        if (Interlocked.CompareExchange(ref _backgroundScanRunning, 1, 0) != 0)
            return;

        _lastBackgroundScanUtc = DateTime.UtcNow;
        var handle = _processHandle;
        var guest = _guestMailbox;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (handle == IntPtr.Zero)
                    return;

                if (DolphinMemoryMap.TryResolveMailboxScan(handle, guest, out var host))
                    OnBackgroundScanSucceeded(host);
            }
            catch (Exception ex)
            {
                lock (_lock)
                    _lastError = $"Mailbox scan failed: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundScanRunning, 0);
            }
        });
    }
}
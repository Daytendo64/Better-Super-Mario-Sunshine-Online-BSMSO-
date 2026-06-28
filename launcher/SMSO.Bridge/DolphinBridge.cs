using System;
using System.Diagnostics;
using System.IO;
using SMSO.Net;

namespace SMSO.Bridge;

public sealed class DolphinBridge : IDisposable
{
    private readonly MailboxResolver _mailbox = new();
    private readonly object _processLock = new();
    private IntPtr _processHandle = IntPtr.Zero;
    private int _processId;
    private int? _trackedProcessId;
    private uint _guestMailboxAddress = ProtocolConstants.DefaultMailboxAddress;
    private string? _preferredExecutablePath;
    private bool _executablePathVerified;
    private DateTime _nextAttachAttemptUtc = DateTime.MinValue;
    private DateTime _lastAttachStatusLogUtc = DateTime.MinValue;
    private bool _loggedMailboxFound;
    private int _aliveCheckCounter;
    private readonly byte[] _readScratch = new byte[ProtocolConstants.CommBufferSize];
    private readonly byte[] _remoteSnapScratch = new byte[ProtocolConstants.CommRemoteSnapshotsSize];
    private readonly byte[] _nameTagScratch = new byte[ProtocolConstants.CommNameTagAppearancesSize];
    private readonly byte[] _remoteVoiceScratch =
        new byte[ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots];
    private readonly byte[] _gameModeScratch = new byte[ProtocolConstants.CommGameModeStateSize];
    private readonly byte[] _incomingWorldEventScratch = new byte[ProtocolConstants.CommWorldEventSize];

    private const int AttachFailureBackoffMs = 500;
    private const int AttachStatusLogCooldownMs = 6000;
    private const int AliveCheckIntervalReads = 60;

    public event Action<string>? Log;
    public bool IsAttached
    {
        get
        {
            lock (_processLock)
                return _processHandle != IntPtr.Zero;
        }
    }
    public bool HasResolvedMailbox => _mailbox.IsResolved;
    public string? LastResolveError => _mailbox.LastError;
    public TimeSpan MailboxSearchDuration => _mailbox.SearchDuration;
    public int? TrackedProcessId => _trackedProcessId;

    public void SetGuestMailboxAddress(uint address)
    {
        _guestMailboxAddress = address;
        _mailbox.SetGuestMailbox(address);
    }

    public void SetPreferredExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _preferredExecutablePath = null;
            _executablePathVerified = false;
            return;
        }

        try
        {
            _preferredExecutablePath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            _preferredExecutablePath = path;
        }

        _executablePathVerified = false;
    }

    public void SetTrackedProcessId(int? processId)
    {
        lock (_processLock)
        {
            if (_trackedProcessId == processId && _processHandle != IntPtr.Zero)
                return;

            _trackedProcessId = processId;
            if (_processId != processId)
                DetachLocked();
        }
    }

    public void PrepareForRelink()
    {
        _nextAttachAttemptUtc = DateTime.MinValue;
        _mailbox.Invalidate();
        _loggedMailboxFound = false;
    }

    public bool ForceRelink()
    {
        PrepareForRelink();
        Detach();
        return TryAttach();
    }

    public bool TryAttach()
    {
        lock (_processLock)
        {
            if (_processHandle != IntPtr.Zero && _trackedProcessId == _processId)
            {
                if (IsTrackedProcessAlive())
                {
                    if (!_mailbox.IsResolved)
                        TryResolveMailboxAddressLocked();
                    return true;
                }

                DetachLocked();
            }

            if (DateTime.UtcNow < _nextAttachAttemptUtc)
                return _processHandle != IntPtr.Zero;

            if (_processHandle != IntPtr.Zero)
                DetachLocked();

            if (!_trackedProcessId.HasValue)
            {
                _nextAttachAttemptUtc = DateTime.UtcNow.AddMilliseconds(AttachFailureBackoffMs);
                return false;
            }

            try
            {
                using var proc = Process.GetProcessById(_trackedProcessId.Value);
                if (proc.HasExited)
                {
                    _nextAttachAttemptUtc = DateTime.UtcNow.AddMilliseconds(AttachFailureBackoffMs);
                    return false;
                }

                if (!MatchesPreferredPath(proc))
                {
                    _nextAttachAttemptUtc = DateTime.UtcNow.AddMilliseconds(AttachFailureBackoffMs);
                    return false;
                }

                return OpenProcess(proc);
            }
            catch
            {
                _nextAttachAttemptUtc = DateTime.UtcNow.AddMilliseconds(AttachFailureBackoffMs);
                return false;
            }
        }
    }

    private bool IsTrackedProcessAlive()
    {
        if (!_trackedProcessId.HasValue)
            return false;

        try
        {
            using var proc = Process.GetProcessById(_trackedProcessId.Value);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private bool OpenProcess(Process proc)
    {
        _processId = proc.Id;
        _processHandle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE |
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_QUERY_INFORMATION,
            false, _processId);

        if (_processHandle == IntPtr.Zero)
        {
            LogThrottled("Failed to open Dolphin process (try running the launcher as administrator)");
            _nextAttachAttemptUtc = DateTime.UtcNow.AddMilliseconds(AttachFailureBackoffMs);
            return false;
        }

        _nextAttachAttemptUtc = DateTime.MinValue;
        _mailbox.Bind(_processHandle, _guestMailboxAddress);
        _loggedMailboxFound = false;

        if (!TryResolveMailboxAddressLocked(force: true))
        {
            LogThrottled($"Attached to Dolphin PID {_processId} — waiting for {ModuleVersionMessages.ModuleFileName} mailbox");
            return true;
        }

        Log?.Invoke($"Attached to Dolphin PID {_processId} (mailbox @ 0x{_mailbox.HostAddress.ToUInt64():X})");
        return true;
    }

    public bool TryResolveMailboxAddress(bool force = false)
    {
        lock (_processLock)
            return TryResolveMailboxAddressLocked(force);
    }

    private bool TryResolveMailboxAddressLocked(bool force = false)
    {
        if (_processHandle == IntPtr.Zero)
            return false;

        _mailbox.Bind(_processHandle, _guestMailboxAddress);

        if (_mailbox.TryResolve(force))
        {
            if (!_loggedMailboxFound)
            {
                _loggedMailboxFound = true;
                Log?.Invoke($"BSMSO mailbox found @ 0x{_mailbox.HostAddress.ToUInt64():X}");
            }
            return true;
        }

        return false;
    }

    public void InvalidateMailbox() => _mailbox.Invalidate();

    private void LogThrottled(string message)
    {
        if ((DateTime.UtcNow - _lastAttachStatusLogUtc).TotalMilliseconds < AttachStatusLogCooldownMs)
            return;

        _lastAttachStatusLogUtc = DateTime.UtcNow;
        Log?.Invoke(message);
    }

    private bool MatchesPreferredPath(Process process)
    {
        if (string.IsNullOrEmpty(_preferredExecutablePath) || _executablePathVerified)
            return true;

        try
        {
            var main = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(main))
                return false;

            var matches = string.Equals(
                Path.GetFullPath(main),
                _preferredExecutablePath,
                StringComparison.OrdinalIgnoreCase);

            if (matches)
                _executablePathVerified = true;

            return matches;
        }
        catch
        {
            return false;
        }
    }

    public void Detach()
    {
        lock (_processLock)
            DetachLocked();
    }

    private void DetachLocked()
    {
        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        _mailbox.Reset();
        _executablePathVerified = false;
        _loggedMailboxFound = false;
    }

    private UIntPtr MailboxHost => _mailbox.HostAddress;

    private static readonly byte[] CommMagicBytes = { 0x53, 0x4D, 0x53, 0x4F };

    /// <summary>
    /// Write Dolphin comm memory without discarding a still-valid mailbox on transient failures.
    /// </summary>
    private bool TryWriteProcessMemoryLocked(UIntPtr address, byte[] bytes)
    {
        if (NativeMethods.WriteProcessMemory(_processHandle, address, bytes, bytes.Length, out int written) &&
            written == bytes.Length)
            return true;

        if (IsMailboxHostStillValidLocked())
            return false;

        _mailbox.Invalidate();
        return false;
    }

    private bool IsMailboxHostStillValidLocked()
    {
        if (_mailbox.HostAddress == UIntPtr.Zero)
            return false;

        var magic = new byte[4];
        return NativeMethods.ReadProcessMemory(_processHandle, MailboxHost, magic, magic.Length, out int read) &&
               read == magic.Length &&
               magic.AsSpan().SequenceEqual(CommMagicBytes);
    }

    public bool TryReadBuffer(out CommBuffer buffer)
    {
        buffer = CommBuffer.CreateDefault();
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            return TryReadBufferLocked(out buffer);
        }
    }

    private bool TryReadBufferLocked(out CommBuffer buffer)
    {
        buffer = CommBuffer.CreateDefault();
        if (_processHandle == IntPtr.Zero)
            return false;

        try
        {
            _aliveCheckCounter++;
            if (_aliveCheckCounter >= AliveCheckIntervalReads)
            {
                _aliveCheckCounter = 0;
                if (!IsTrackedProcessAlive())
                {
                    DetachLocked();
                    return false;
                }
            }

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            if (!NativeMethods.ReadProcessMemory(
                    _processHandle,
                    MailboxHost,
                    _readScratch,
                    _readScratch.Length,
                    out int read) ||
                read != _readScratch.Length)
            {
                _mailbox.Invalidate();
                if (!TryResolveMailboxAddressLocked(force: true))
                    return false;

                if (!NativeMethods.ReadProcessMemory(
                        _processHandle,
                        MailboxHost,
                        _readScratch,
                        _readScratch.Length,
                        out read) ||
                    read != _readScratch.Length)
                {
                    DetachLocked();
                    return false;
                }
            }

            buffer = CommBufferEndian.FromDolphinBytes(_readScratch);
            if (buffer.Magic != ProtocolConstants.Magic)
            {
                _mailbox.Invalidate();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Dolphin read failed: {ex.Message}");
            DetachLocked();
            return false;
        }
    }

    public bool TryWriteBuffer(CommBuffer buffer)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            buffer.Magic = ProtocolConstants.Magic;
            buffer.Version = ProtocolConstants.CommVersion;
            var bytes = CommBufferEndian.ToDolphinBytes(buffer);
            if (!TryWriteProcessMemoryLocked(MailboxHost, bytes))
                return false;

            return true;
        }
    }

    public bool TryApplyWarpIntent(
        byte targetSlot,
        byte courseId,
        byte episodeId,
        bool isHost,
        bool setWarpPending = true,
        bool setWarpAll = false,
        bool setWarpToPoint = false,
        float warpPosX = 0f,
        float warpPosY = 0f,
        float warpPosZ = 0f,
        float warpFacingY = 0f)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked(force: true))
                return false;

            var control = new byte[ProtocolConstants.CommBridgeControlSize];
            var controlAddress = new UIntPtr(
                MailboxHost.ToUInt64() + ProtocolConstants.CommBridgeControlOffset);

            if (!NativeMethods.ReadProcessMemory(
                    _processHandle,
                    controlAddress,
                    control,
                    control.Length,
                    out int read) ||
                read != control.Length)
            {
                _mailbox.Invalidate();
                return false;
            }

            CommBufferEndian.ApplyWarpIntentToControlSpan(
                control,
                targetSlot,
                courseId,
                episodeId,
                isHost,
                setWarpPending,
                setWarpAll,
                setWarpToPoint,
                warpPosX,
                warpPosY,
                warpPosZ,
                warpFacingY);

            if (!TryWriteProcessMemoryLocked(controlAddress, control))
                return false;

            return true;
        }
    }

    public bool TryWriteRemoteSyncPayload(
        PlayerSnapshot[] remotes,
        NameTagAppearance localAppearance,
        NameTagAppearance[] remoteAppearances,
        MarioVoiceEvent[] remoteVoiceEvents,
        in CommGameModeState gameMode)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            CommBufferEndian.WriteRemoteSnapshotsInto(_remoteSnapScratch, remotes);
            CommBufferEndian.WriteNameTagAppearancesInto(_nameTagScratch, localAppearance, remoteAppearances);
            CommBufferEndian.WriteRemoteMarioVoiceEventsInto(_remoteVoiceScratch, remoteVoiceEvents);
            CommBufferEndian.WriteGameModeStateInto(_gameModeScratch, gameMode);

            var host = MailboxHost.ToUInt64();
            if (!TryWriteProcessMemoryLocked(
                    new UIntPtr(host + ProtocolConstants.CommRemoteSnapshotsOffset),
                    _remoteSnapScratch))
                return false;

            if (!TryWriteProcessMemoryLocked(
                    new UIntPtr(host + ProtocolConstants.CommNameTagAppearancesOffset),
                    _nameTagScratch))
                return false;

            if (!TryWriteProcessMemoryLocked(
                    new UIntPtr(host + ProtocolConstants.CommMarioVoiceEventsOffset +
                                ProtocolConstants.MarioVoiceEventSize),
                    _remoteVoiceScratch))
                return false;

            return TryWriteProcessMemoryLocked(
                new UIntPtr(host + ProtocolConstants.CommGameModeStateOffset),
                _gameModeScratch);
        }
    }

    public bool TryWriteRemoteSnapshotsOnly(PlayerSnapshot[] remotes)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            var bytes = CommBufferEndian.ToRemoteSnapshotsDolphinBytes(remotes);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommRemoteSnapshotsOffset);
            return TryWriteProcessMemoryLocked(address, bytes);
        }
    }

    public bool TryWriteNameTagAppearancesOnly(NameTagAppearance local, NameTagAppearance[] remotes)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            var bytes = CommBufferEndian.ToNameTagAppearancesDolphinBytes(local, remotes);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommNameTagAppearancesOffset);
            return TryWriteProcessMemoryLocked(address, bytes);
        }
    }

    public bool TryWriteRemoteMarioVoiceEventsOnly(MarioVoiceEvent[] remotes)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            var bytes = CommBufferEndian.ToRemoteMarioVoiceEventsDolphinBytes(remotes);
            var address = new UIntPtr(
                MailboxHost.ToUInt64() + ProtocolConstants.CommMarioVoiceEventsOffset +
                ProtocolConstants.MarioVoiceEventSize);
            return TryWriteProcessMemoryLocked(address, bytes);
        }
    }

    public bool TryWriteGameModeStateOnly(in CommGameModeState state)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            var bytes = CommBufferEndian.ToGameModeStateDolphinBytes(state);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommGameModeStateOffset);
            return TryWriteProcessMemoryLocked(address, bytes);
        }
    }

    public bool TryWriteIncomingWorldEventOnly(in CommWorldEvent incoming)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            CommBufferEndian.WriteIncomingWorldEventInto(_incomingWorldEventScratch, incoming);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommIncomingWorldEventOffset);
            return TryWriteProcessMemoryLocked(address, _incomingWorldEventScratch);
        }
    }

    public void Dispose() => Detach();
}

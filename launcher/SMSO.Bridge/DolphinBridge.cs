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
    private readonly byte[] _magicScratch = new byte[4];
    private readonly byte[] _readScratch = new byte[ProtocolConstants.CommBufferSize];
    private readonly byte[] _remoteSyncSnapNameScratch =
        new byte[ProtocolConstants.CommRemoteSnapshotsSize + ProtocolConstants.CommNameTagAppearancesSize];
    private readonly byte[] _remoteSyncVoiceModeScratch =
        new byte[ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots +
                 ProtocolConstants.CommGameModeStateSize];
    private readonly byte[] _incomingWorldEventScratch = new byte[ProtocolConstants.CommWorldEventSize];
    private readonly byte[] _localPendingClearScratch = new byte[ProtocolConstants.CommWorldEventSize];
    private readonly byte[] _localPendingReadScratch = new byte[ProtocolConstants.CommWorldEventSize];
    private readonly byte[] _marioModelIdsScratch = new byte[ProtocolConstants.CommMarioModelIdsSize];
    private readonly byte[] _lastRemoteSyncSnapName =
        new byte[ProtocolConstants.CommRemoteSnapshotsSize + ProtocolConstants.CommNameTagAppearancesSize];
    private readonly byte[] _lastRemoteSyncVoiceMode =
        new byte[ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots +
                 ProtocolConstants.CommGameModeStateSize];
    private bool _hasLastRemoteSyncPayload;
    private int _writeCacheEpoch;

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
    public int WriteCacheEpoch => Volatile.Read(ref _writeCacheEpoch);

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
        lock (_processLock)
        {
            _nextAttachAttemptUtc = DateTime.MinValue;
            _mailbox.Invalidate();
            _loggedMailboxFound = false;
            InvalidateWriteCachesLocked();
        }
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

        var wasResolved = _mailbox.IsResolved;
        var previousHost = _mailbox.HostAddress;
        _mailbox.Bind(_processHandle, _guestMailboxAddress);

        if (_mailbox.TryResolve(force))
        {
            if (!wasResolved || previousHost != _mailbox.HostAddress)
                InvalidateWriteCachesLocked();
            if (!_loggedMailboxFound)
            {
                _loggedMailboxFound = true;
                Log?.Invoke($"BSMSO mailbox found @ 0x{_mailbox.HostAddress.ToUInt64():X}");
            }
            return true;
        }

        return false;
    }

    public void InvalidateMailbox()
    {
        lock (_processLock)
        {
            _mailbox.Invalidate();
            InvalidateWriteCachesLocked();
        }
    }

    public void InvalidateWriteCaches()
    {
        lock (_processLock)
            InvalidateWriteCachesLocked();
    }

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
        InvalidateWriteCachesLocked();
    }

    private void InvalidateWriteCachesLocked()
    {
        _hasLastRemoteSyncPayload = false;
        unchecked
        {
            ++_writeCacheEpoch;
        }
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
        InvalidateWriteCachesLocked();
        return false;
    }

    private bool IsMailboxHostStillValidLocked()
    {
        if (_mailbox.HostAddress == UIntPtr.Zero)
            return false;

        return NativeMethods.ReadProcessMemory(
                   _processHandle, MailboxHost, _magicScratch, _magicScratch.Length, out int read) &&
               read == _magicScratch.Length &&
               _magicScratch.AsSpan().SequenceEqual(CommMagicBytes);
    }

    public bool TryReadBuffer(out CommBuffer buffer)
    {
        lock (_processLock)
        {
            return TryReadBufferLocked(out buffer);
        }
    }

    private bool TryReadBufferLocked(out CommBuffer buffer)
    {
        buffer = default;
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
                InvalidateWriteCachesLocked();
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

            ValidateRemoteSyncCacheLocked(_readScratch);
            buffer = CommBufferEndian.FromDolphinBytes(_readScratch);
            if (buffer.Magic != ProtocolConstants.Magic)
            {
                _mailbox.Invalidate();
                InvalidateWriteCachesLocked();
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

    private void ValidateRemoteSyncCacheLocked(ReadOnlySpan<byte> liveBuffer)
    {
        if (!_hasLastRemoteSyncPayload)
            return;
        if (RemoteSyncPayloadMatches(
                liveBuffer, _lastRemoteSyncSnapName, _lastRemoteSyncVoiceMode))
            return;

        InvalidateWriteCachesLocked();
    }

    internal static bool RemoteSyncPayloadMatches(
        ReadOnlySpan<byte> liveBuffer,
        ReadOnlySpan<byte> expectedSnapshotsAndNames,
        ReadOnlySpan<byte> expectedVoicesAndMode)
    {
        var snapshotAndNameSize =
            ProtocolConstants.CommRemoteSnapshotsSize + ProtocolConstants.CommNameTagAppearancesSize;
        var voiceAndModeOffset =
            ProtocolConstants.CommMarioVoiceEventsOffset + ProtocolConstants.MarioVoiceEventSize;
        var voiceAndModeSize =
            ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots +
            ProtocolConstants.CommGameModeStateSize;
        if (liveBuffer.Length < ProtocolConstants.CommBufferSize ||
            expectedSnapshotsAndNames.Length != snapshotAndNameSize ||
            expectedVoicesAndMode.Length != voiceAndModeSize)
        {
            return false;
        }

        return liveBuffer
                   .Slice(ProtocolConstants.CommRemoteSnapshotsOffset, snapshotAndNameSize)
                   .SequenceEqual(expectedSnapshotsAndNames) &&
               liveBuffer
                   .Slice(voiceAndModeOffset, voiceAndModeSize)
                   .SequenceEqual(expectedVoicesAndMode);
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

            // Remote snapshots and name-tag appearances are contiguous, so a single
            // WriteProcessMemory covers both atomically — no partial state where remotes
            // updated but nametags lag (or vice versa). The local voice event is left
            // untouched (the module owns it). Remote voice + game mode are likewise
            // contiguous and written as one block.
            var voiceRegionSize = ProtocolConstants.MarioVoiceEventSize * ProtocolConstants.MaxRemoteSlots;

            CommBufferEndian.WriteRemoteSnapshotsInto(
                _remoteSyncSnapNameScratch.AsSpan(0, ProtocolConstants.CommRemoteSnapshotsSize), remotes);
            CommBufferEndian.WriteNameTagAppearancesInto(
                _remoteSyncSnapNameScratch.AsSpan(ProtocolConstants.CommRemoteSnapshotsSize,
                    ProtocolConstants.CommNameTagAppearancesSize),
                localAppearance, remoteAppearances);
            CommBufferEndian.WriteRemoteMarioVoiceEventsInto(
                _remoteSyncVoiceModeScratch.AsSpan(0, voiceRegionSize), remoteVoiceEvents);
            CommBufferEndian.WriteGameModeStateInto(
                _remoteSyncVoiceModeScratch.AsSpan(voiceRegionSize, ProtocolConstants.CommGameModeStateSize),
                gameMode);

            // Skip WriteProcessMemory when the serialized remote payload is unchanged (idle remotes).
            if (_hasLastRemoteSyncPayload &&
                _lastRemoteSyncSnapName.AsSpan().SequenceEqual(_remoteSyncSnapNameScratch) &&
                _lastRemoteSyncVoiceMode.AsSpan().SequenceEqual(_remoteSyncVoiceModeScratch))
            {
                return true;
            }

            var host = MailboxHost.ToUInt64();
            if (!TryWriteProcessMemoryLocked(
                    new UIntPtr(host + ProtocolConstants.CommRemoteSnapshotsOffset),
                    _remoteSyncSnapNameScratch))
                return false;

            if (!TryWriteProcessMemoryLocked(
                    new UIntPtr(host + ProtocolConstants.CommMarioVoiceEventsOffset +
                                ProtocolConstants.MarioVoiceEventSize),
                    _remoteSyncVoiceModeScratch))
                return false;

            _remoteSyncSnapNameScratch.CopyTo(_lastRemoteSyncSnapName, 0);
            _remoteSyncVoiceModeScratch.CopyTo(_lastRemoteSyncVoiceMode, 0);
            _hasLastRemoteSyncPayload = true;
            return true;
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
            if (!TryWriteProcessMemoryLocked(address, bytes))
                return false;

            // Partial poke overlaps the remote-sync skip cache — force the next full flush.
            InvalidateWriteCachesLocked();
            return true;
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
            if (!TryWriteProcessMemoryLocked(address, bytes))
                return false;

            // Nametag region is part of _lastRemoteSyncSnapName. Without invalidating, the next
            // TryWriteRemoteSyncPayload can SequenceEqual-skip and leave Dolphin with a mix of
            // fresh nametags + stale cached remotes (or skip a needed remotes rewrite).
            InvalidateWriteCachesLocked();
            return true;
        }
    }

    public bool TryWriteRosterHudOnly(in CommRosterHudSync rosterHud)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            var bytes = CommBufferEndian.ToRosterHudSyncDolphinBytes(rosterHud);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommRosterHudOffset);
            return TryWriteProcessMemoryLocked(address, bytes);
        }
    }

    public bool TryWriteMarioModelIdsOnly(byte[] localModelId, byte[] remoteModelIds)
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            CommBufferEndian.WriteMarioModelIdsInto(_marioModelIdsScratch, localModelId, remoteModelIds);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommMarioModelIdsOffset);
            return TryWriteProcessMemoryLocked(address, _marioModelIdsScratch);
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
            if (!TryWriteProcessMemoryLocked(address, bytes))
                return false;

            InvalidateWriteCachesLocked();
            return true;
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
            if (!TryWriteProcessMemoryLocked(address, bytes))
                return false;

            // Game mode sits in the voice+mode skip-cache block.
            InvalidateWriteCachesLocked();
            return true;
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

    /// <summary>
    /// Zeroes the localPending world-event slot in Dolphin RAM after the bridge has published
    /// it to the network. The module only writes the next queued event when this slot is empty,
    /// so clearing it is the handshake that lets the module advance without overwriting an
    /// unconsumed event (which previously dropped red-coin collections).
    /// </summary>
    public bool TryClearLocalPendingWorldEvent()
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            Array.Clear(_localPendingClearScratch, 0, _localPendingClearScratch.Length);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommWorldSyncOffset);
            return TryWriteProcessMemoryLocked(address, _localPendingClearScratch);
        }
    }

    /// <summary>
    /// Lightweight re-read of localPending only. Used after TryClearLocalPendingWorldEvent so
    /// the same poll can drain a graffiti backlog if the module already flushed the next event.
    /// </summary>
    public bool TryReadLocalPendingWorldEvent(out CommWorldEvent worldEvent)
    {
        worldEvent = default;
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommWorldSyncOffset);
            if (!NativeMethods.ReadProcessMemory(
                    _processHandle,
                    address,
                    _localPendingReadScratch,
                    _localPendingReadScratch.Length,
                    out int read) ||
                read != _localPendingReadScratch.Length)
            {
                return false;
            }

            worldEvent = CommBufferEndian.ReadWorldEventFromDolphinBytes(_localPendingReadScratch);
            return true;
        }
    }

    /// <summary>
    /// Zeroes the incoming world-event slot in Dolphin RAM. Used when an authority resync
    /// replaces pending delivery so a previously stuck durable event cannot block live
    /// shine/blue ownership applies forever.
    /// </summary>
    public bool TryClearIncomingWorldEvent()
    {
        lock (_processLock)
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            if (!_mailbox.IsResolved && !TryResolveMailboxAddressLocked())
                return false;

            Array.Clear(_incomingWorldEventScratch, 0, _incomingWorldEventScratch.Length);
            var address = new UIntPtr(MailboxHost.ToUInt64() + ProtocolConstants.CommIncomingWorldEventOffset);
            return TryWriteProcessMemoryLocked(address, _incomingWorldEventScratch);
        }
    }

    public void Dispose() => Detach();
}

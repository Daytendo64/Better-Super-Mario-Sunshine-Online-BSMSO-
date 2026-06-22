using System.Runtime.InteropServices;

namespace SMSO.Net;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = ProtocolConstants.MarioVoiceEventSize)]
public struct MarioVoiceEvent
{
    public uint SoundId;
    public ushort Sequence;
    public byte Flags;
    public byte Health;
    public byte StageId;
    public byte EpisodeId;
    public byte Reserved0;
    public byte Reserved1;

    public bool IsEmpty => Sequence == 0 || SoundId == 0;
}

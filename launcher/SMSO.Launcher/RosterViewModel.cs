using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SMSO.Launcher;

internal sealed class RosterViewModel : INotifyPropertyChanged
{
    private string _username = "";
    private string _levelName = "";
    private string _episodeName = "";
    private string _modelName = "";
    private string _status = "";
    private string _pingMs = "";
    private int _ordinal = 1;

    /// <summary>Network slot (0-based). Used for warp targets and protocol identity — not shown as the row number.</summary>
    public byte Slot { get; set; }
    public byte StageId { get; set; }
    public byte EpisodeId { get; set; }
    public string MarioModelId { get; set; } = "";

    /// <summary>
    /// 1-based position among currently connected players (roster sorted by <see cref="Slot"/>).
    /// Renumbers on join/leave so the list is always 1..N with no gaps.
    /// </summary>
    public int Ordinal
    {
        get => _ordinal;
        set
        {
            if (_ordinal == value)
                return;
            _ordinal = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ordinal)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    public string Username
    {
        get => _username;
        set
        {
            if (!SetField(ref _username, value))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    /// <summary>Connected Players label: ordinal among connected players + username (e.g. "1. Mario").</summary>
    public string DisplayName => $"{Ordinal}. {Username}";

    public string LevelName
    {
        get => _levelName;
        set => SetField(ref _levelName, value);
    }

    public string EpisodeName
    {
        get => _episodeName;
        set => SetField(ref _episodeName, value);
    }

    public string ModelName
    {
        get => _modelName;
        set => SetField(ref _modelName, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string PingMs
    {
        get => _pingMs;
        set => SetField(ref _pingMs, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

internal sealed class WarpTargetItem
{
    public string Username { get; set; } = "";
    public byte Slot { get; set; }
    public override string ToString() => Username;
}

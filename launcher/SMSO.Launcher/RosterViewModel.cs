using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SMSO.Launcher;

internal sealed class RosterViewModel : INotifyPropertyChanged
{
    private string _username = "";
    private string _levelName = "";
    private string _episodeName = "";
    private string _status = "";
    private string _pingMs = "";

    public byte Slot { get; set; }
    public byte StageId { get; set; }
    public byte EpisodeId { get; set; }

    public string Username
    {
        get => _username;
        set => SetField(ref _username, value);
    }

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

    private void SetField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal sealed class WarpTargetItem
{
    public string Username { get; set; } = "";
    public byte Slot { get; set; }
    public override string ToString() => Username;
}

using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Crate.Wpf.ViewModels;

/// <summary>하단 재생 바. 미리듣기(URL)와 내 파일을 같은 MediaPlayer로 재생한다.</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Action<bool>? _setOwnerPlaying;
    private Action? _ended;
    private bool _dragging;

    [ObservableProperty] private string _title = "재생 중인 곡 없음";
    [ObservableProperty] private string _subtitle = "커버의 재생 버튼을 눌러 미리 들어 보세요";
    [ObservableProperty] private string? _coverUrl;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 30;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";

    // ---- 볼륨 (0~1, 앱을 다시 켜도 유지) ----
    private readonly Crate.Core.JsonCache<double> _store = new("player", TimeSpan.FromDays(36500));
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private double _beforeMute = 0.7;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMuted))] private double _volume = 0.7;
    public bool IsMuted => Volume <= 0.001;

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = Math.Clamp(value, 0, 1);
        // 슬라이더를 끄는 동안 계속 쓰지 않게 멈춘 뒤에 저장
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>스피커 아이콘: 음소거 ↔ 원래 볼륨</summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted) Volume = _beforeMute > 0.01 ? _beforeMute : 0.7;
        else { _beforeMute = Volume; Volume = 0; }
    }

    public PlayerViewModel()
    {
        if (_store.TryGet("volume", out var v)) _volume = Math.Clamp(v, 0, 1);
        _player.Volume = _volume;
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _store.Set("volume", Volume);
            _store.Save();
        };
        _player.MediaOpened += (_, _) =>
        {
            Duration = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 30;
            DurationText = Format(Duration);
        };
        _player.MediaEnded += (_, _) =>
        {
            _player.Stop(); // 다시 누르면 처음부터
            SetPlaying(false);
            _ended?.Invoke();
        };
        _player.MediaFailed += (_, e) =>
        {
            SetPlaying(false);
            Subtitle = $"재생할 수 없습니다: {e.ErrorException?.Message}";
        };
        _timer.Tick += (_, _) =>
        {
            if (_dragging) return;
            Position = _player.Position.TotalSeconds;
            PositionText = Format(Position);
        };
    }

    /// <summary>owner: 재생 상태를 카드에도 표시하기 위한 콜백. ended: 끝까지 들었을 때 (다음 곡 재생 등).</summary>
    /// <summary>지금 재생 중인 추천 카드 (재생 바 앨범아트를 누르면 그 카드로 이동)</summary>
    public ReleaseItem? NowRelease { get; private set; }
    public bool HasNowRelease => NowRelease != null;
    public Action? OpenNowRequested { get; set; }

    [RelayCommand]
    private void OpenNow() => OpenNowRequested?.Invoke();

    /// <summary>같은 곡을 보여 주는 카드가 새로 만들어졌을 때 재생 표시·다음 곡을 그쪽으로 옮긴다</summary>
    public void Rebind(Action<bool> owner, Action? ended, ReleaseItem release)
    {
        _setOwnerPlaying = owner;
        _ended = ended;
        NowRelease = release;
        owner(IsPlaying);
    }

    public void Play(Uri source, string title, string subtitle, string? cover, Action<bool> owner, Action? ended = null,
        ReleaseItem? release = null)
    {
        NowRelease = release;
        OnPropertyChanged(nameof(HasNowRelease));
        _setOwnerPlaying?.Invoke(false);
        _setOwnerPlaying = owner;
        _ended = ended;
        Title = title;
        Subtitle = subtitle;
        CoverUrl = cover;
        Position = 0;
        PositionText = "0:00";
        HasMedia = true;
        _player.Open(source);
        _player.Play();
        SetPlaying(true);
    }

    public bool IsCurrent(Action<bool> owner) => _setOwnerPlaying == owner;

    [RelayCommand]
    public void Toggle()
    {
        if (!HasMedia) return;
        if (IsPlaying) _player.Pause(); else _player.Play();
        SetPlaying(!IsPlaying);
    }

    public void BeginSeek() => _dragging = true;

    public void EndSeek(double seconds)
    {
        _player.Position = TimeSpan.FromSeconds(seconds);
        _dragging = false;
    }

    private void SetPlaying(bool playing)
    {
        IsPlaying = playing;
        _setOwnerPlaying?.Invoke(playing);
        if (playing) _timer.Start(); else _timer.Stop();
    }

    private static string Format(double s) => TimeSpan.FromSeconds(s).ToString(@"m\:ss");
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RecipeSong.Core;

namespace RecipeSong.Wpf.ViewModels;

/// <summary>
/// Reused: 앞 계산이 만든 결과(후보 목록)를 그대로 써서 이번엔 하지 않은 칸.
/// Skipped: 이번 계산에는 없는 칸이거나 저장된 결과·멈춤으로 하지 않은 칸 ('안 함').
/// Fixed: 단계가 아닌 칸(라이브러리·거르기 — 늘 같은 모양)
/// </summary>
public enum StageState { Todo, Current, Done, Reused, Skipped, Fixed }

/// <summary>
/// 계산 종류 — 순서도 창에서 색으로 구분한다 (동시에 여럿 돌 수 있다).
/// Load·Refresh·Precise·Ai 는 순서도를 처음부터 지나는 계산, 나머지는 칸 하나(또는 몇 개)에 닿는 옆 작업.
/// </summary>
public enum RunKind { Load, Refresh, Precise, Ai, Analysis, Follow, Mine, Taste, Tracks, Genres, Similar }

/// <summary>
/// 순서도 창의 칸 하나. 배치(Col 0 왼쪽 · 1 가운데 · 2 오른쪽, Row)는 docs/images/recommendation.mjs 흐름도와 같다.
/// Stage 가 없으면 고정 칸, Follows 가 있으면 그 단계 칸의 상태를 따라간다 (장르 정하는 순서 ↔ 장르 칸).
/// State·Detail·Time 은 '주 계산'(가장 나중에 시작한 추천 계산) 기준. 다른 계산이 있는 자리는 창이 RunInfo 로 따로 그린다.
/// </summary>
public sealed partial class StageItem(string key, int col, int row, string label, string sub, RecStage? stage = null, RecStage? follows = null) : ObservableObject
{
    public string Key { get; } = key;
    public int Col { get; } = col;
    public int Row { get; } = row;
    public string Label { get; } = label;
    public string Sub { get; } = sub;
    public RecStage? Stage { get; } = stage;
    public RecStage? Follows { get; } = follows;
    // 내 곡 소리 기준(Analyze)은 추천과 따로 도는 분석이라 평소엔 고정 칸 (분석 중·끝만 따로 표시)
    [ObservableProperty] private StageState _state = (stage is null or RecStage.Analyze) && follows == null ? StageState.Fixed : StageState.Todo;
    /// <summary>이 단계에서 마지막으로 온 진행 문구</summary>
    [ObservableProperty] private string _detail = "";
    /// <summary>걸린 시간 (끝나면 고정, 진행 중이면 매초 갱신)</summary>
    [ObservableProperty] private string _time = "";
    /// <summary>이 칸에 마지막으로 소식(단계 시작·진행 문구)이 온 때 — 오래 조용하면 창이 '소식 없음'을 붙인다</summary>
    public DateTime LastNews { get; set; }
    internal Stopwatch Watch { get; } = new();
}

/// <summary>칸 사이 화살표: 칸 열쇠 두 개 + 라벨</summary>
public sealed record StageEdge(string From, string To, string Label = "", bool Dashed = false);

/// <summary>돌고 있는(또는 방금 끝난) 계산 하나 — 창 위쪽 목록 한 줄과, 흐름도에서 그 계산이 있는 칸의 색</summary>
public sealed partial class RunInfo(int id, string title, RunKind kind) : ObservableObject
{
    public int Id { get; } = id;
    public string Title { get; } = title;
    public RunKind Kind { get; } = kind;
    public Brush Brush { get; } = Palette[kind].Line;
    public Brush TextBrush { get; } = Palette[kind].Text;
    public Brush Deep { get; } = Palette[kind].Deep;
    public string KindLabel => Kind switch
    {
        RunKind.Load => L.T("보통 계산"),
        RunKind.Refresh => L.T("뒤에서 새로 계산"),
        RunKind.Precise => L.T("촘촘한 계산"),
        RunKind.Ai => L.T("AI·웹진"),
        RunKind.Analysis => L.T("내 곡 분석"),
        RunKind.Follow => L.T("팔로우 신보"),
        RunKind.Mine => L.T("아카이브 뮤지션 모으기"),
        RunKind.Taste => L.T("좋아요·별로 소리"),
        RunKind.Tracks => L.T("수록곡 비슷한 내 곡"),
        RunKind.Genres => L.T("장르 미리 받기"),
        _ => L.T("닮은 앨범 확인"),
    };
    [ObservableProperty] private RecStage? _current;
    [ObservableProperty] private string _stageLabel = "";
    [ObservableProperty] private string _detail = "";
    /// <summary>목록에 보이는 시간: 이 단계 / 전체</summary>
    [ObservableProperty] private string _time = "";
    /// <summary>흐름도 알약에 보이는 시간: 이 단계만</summary>
    [ObservableProperty] private string _stageTime = "";
    [ObservableProperty] private bool _ended;
    /// <summary>제목의 "3/43" (촘촘한 계산 조각) → 0~1, 없으면 null</summary>
    public double? Part { get; } = Regex.Match(title, @"(\d+)/(\d+)") is { Success: true } m && int.Parse(m.Groups[2].Value) > 0
        ? (double)int.Parse(m.Groups[1].Value) / int.Parse(m.Groups[2].Value) : null;
    public bool HasPart => Part != null;
    /// <summary>후보 목록을 앞 계산에서 가져다 썼으면 그 목록을 만든 시각 (시드·비슷한 아티스트·라이브러리 뮤지션 칸이 '재사용')</summary>
    public DateTime? ReusedAt { get; set; }
    internal Stopwatch Total { get; } = Stopwatch.StartNew();
    internal Stopwatch StageWatch { get; } = new();

    /// <summary>종류별 색: 선 · 글자 · 바탕. 보통 계산은 앱 강조색(주황), 분석은 파랑</summary>
    private static readonly Dictionary<RunKind, (Brush Line, Brush Text, Brush Deep)> Palette = new()
    {
        [RunKind.Load] = (B("#F0A04B"), B("#FFC27A"), B("#3D2C18")),
        [RunKind.Refresh] = (B("#E8C547"), B("#F3DB85"), B("#34301A")),
        [RunKind.Precise] = (B("#C792EA"), B("#DDB8F5"), B("#2C2038")),
        [RunKind.Ai] = (B("#F27BA8"), B("#F7A9C6"), B("#38202B")),
        [RunKind.Analysis] = (B("#7EA6FF"), B("#A9C3FF"), B("#18213A")),
        [RunKind.Follow] = (B("#F09A9A"), B("#F6C0C0"), B("#382020")),
        [RunKind.Mine] = (B("#8BD3E6"), B("#B8E5F0"), B("#16292E")),
        [RunKind.Taste] = (B("#B5E07A"), B("#D0ECA8"), B("#25301A")),
        [RunKind.Tracks] = (B("#E6B98A"), B("#F0D2B2"), B("#33281C")),
        [RunKind.Genres] = (B("#A4B8C6"), B("#C8D5DE"), B("#1E252B")),
        [RunKind.Similar] = (B("#E57373"), B("#F0A5A5"), B("#361A1A")),
    };

    private static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// 디버그 순서도 창이 보는 계산 상태. 동시에 도는 계산(보통 계산 · 정밀 분석 · 뒤 계산 · AI·웹진 · 내 곡 분석)을 모두 Runs 에 둔다.
/// 칸의 완료·대기·안 거침은 '주 계산'(가장 나중에 시작한 추천 계산) 하나로 그리고, 나머지 계산은 지금 있는 칸에 자기 색으로 표시한다.
/// 내 곡 소리 분석은 '내 곡 소리 기준' 칸에만 표시한다 (추천 칸은 건드리지 않는다).
/// </summary>
public sealed partial class PipelineMonitor : ObservableObject
{
    public static PipelineMonitor Instance { get; } = new();

    // 배치: 0 왼쪽(입력·설명) · 1 가운데(추천 줄기) · 2 오른쪽(아카이브 뮤지션·거르기) · 3 맨 오른쪽(팔로우 · AI 줄기 · 소리 서비스 칸)
    // 후보 목록(시드 → 비슷한 아티스트 → 디스코그래피, 아카이브·팔로우 뮤지션)은 12시간 재사용, 그 기간 고르기부터는 기간마다
    public IReadOnlyList<StageItem> Stages { get; } =
    [
        new("lib", 1, 0, L.T("내 아카이브 전체"), L.T("플레이리스트와 상관없이")),
        new("base", 0, 1, L.T("내 곡 소리 기준"), L.T("새 곡 디코딩 · CLAP · 평균"), RecStage.Analyze),
        new("seed", 1, 1, L.T("시드 아티스트"), L.T("많이 듣고 좋아한 80명 + 하트·팔로우"), RecStage.Seeds),
        new("mine", 2, 1, L.T("내 아카이브 뮤지션"), L.T("상위 500명 · 신보 항상 앞"), RecStage.Mine),
        new("follow", 3, 1, L.T("팔로우 뮤지션"), L.T("☆ 누른 아티스트 · 신보 항상 앞"), RecStage.Follow),
        new("related", 1, 2, L.T("비슷한 아티스트 2단계"), L.T("Deezer 연관 아티스트"), RecStage.Related),
        new("disco", 1, 3, L.T("디스코그래피"), L.T("후보 아티스트의 앨범 목록 · 3일 캐시"), RecStage.Discography),
        new("releases", 1, 4, L.T("그 기간 고르기"), L.T("가진·차단 빼고 · 연결 점수 상위만 듣기"), RecStage.Releases),
        new("aisearch", 3, 4, L.T("AI 웹 검색 · 웹진 글"), L.T("커넥터별 웹 검색 · 웹진 RSS"), RecStage.AiSearch),
        new("listen", 1, 5, L.T("미리듣기 소리 분석"), L.T("CLAP · 내 곡과 비교"), RecStage.Listen),
        new("aicheck", 3, 5, L.T("Deezer에서 확인"), L.T("실제로 있는 앨범만"), RecStage.AiCheck),
        new("score", 1, 6, L.T("점수"), L.T("닮음 + 연결 + 좋아요 − 별로"), RecStage.Score),
        new("ladder", 0, 7, L.T("장르 정하는 순서"), L.T("Last.fm → Discogs → Deezer → 소리")),
        new("crate", 1, 7, L.T("장르 칸"), L.T("국적과 상관없이 6칸"), RecStage.Genre),
        new("screen", 1, 8, L.T("화면"), L.T("저장하고 칸마다 묶어 보여 줌"), RecStage.Done),
        new("gate", 2, 8, L.T("AI·웹진 거르기"), L.T("일반 추천 하위 25% 선 미만 숨김")),
    ];

    public IReadOnlyList<StageEdge> Edges { get; } =
    [
        new("lib", "base", L.T("전 곡 소리")),
        new("lib", "mine", L.T("2곡 이상 · 하트")),
        new("lib", "seed", L.T("많이 듣고 좋아한")),
        new("seed", "related", L.T("연관 아티스트")),
        new("related", "disco", L.T("후보 아티스트")),
        new("mine", "disco", L.T("항상 포함")),
        new("follow", "disco"),
        new("disco", "releases", L.T("발매일로 거르기")),
        new("releases", "listen", L.T("30초 미리듣기")),
        new("base", "listen", L.T("비교 기준")),
        new("aisearch", "aicheck"),
        new("aicheck", "listen"),
        new("listen", "score", L.T("닮음")),
        new("score", "crate", L.T("점수 높은 순")),
        new("ladder", "crate"),
        new("crate", "screen", L.T("칸마다 묶기")),
        new("gate", "screen", Dashed: true),
    ];

    /// <summary>소리 서비스 요청 줄 (MainViewModel 이 SoundService.Queue 를 넘긴다) — 창의 '소리 서비스' 칸</summary>
    public Func<IReadOnlyList<SoundJob>>? SoundQueue { get; set; }

    /// <summary>창 위쪽 목록: 돌고 있는 계산 + 방금 끝난 주 계산</summary>
    public ObservableCollection<RunInfo> Runs { get; } = [];

    /// <summary>진행 기록 (새것이 위)</summary>
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";

    /// <summary>칸 상태가 아닌 것(다른 계산의 위치·시간)이 바뀌었을 때 — 창이 다시 칠한다</summary>
    public event Action? Changed;

    /// <summary>칸 상태를 그리는 주 계산 (가장 나중에 시작한 추천 계산)</summary>
    public RunInfo? Primary { get; private set; }
    /// <summary>계산 없이 끝났거나 멈춘 이유 (있으면 상태 줄에 시간 대신)</summary>
    private string? _note;

    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _nextId;

    private PipelineMonitor()
    {
        _tick.Tick += (_, _) => Refresh();
        // 이번 실행에서 아직 계산이 없을 때 (순서도 버튼으로 먼저 열면 '끝'이 아니라 이렇게)
        _title = L.T("추천 단계");
        _status = L.T("아직 계산 없음 · 계산이 시작되면 여기에 보여요");
    }

    /// <summary>추천 계산 칸 (분석 칸은 따로)</summary>
    private IEnumerable<StageItem> Steps => Stages.Where(s => s.Stage is { } st && st != RecStage.Analyze);
    private StageItem Base => Stages.First(s => s.Stage == RecStage.Analyze);
    private RunInfo? Find(int id) => Runs.FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// 새 계산을 목록에 올린다. primary 면 주 계산이 되어 칸을 비운다 (옆 작업은 자기가 있는 칸에 색 고리·알약으로만).
    /// 돌려준 번호로 Stage·Message·Finish 를 부른다
    /// </summary>
    public int Begin(string title, RunKind kind, bool primary = true)
    {
        // 끝난 계산은 목록에서 내린다 (주 계산만 끝난 뒤에도 남아 있었다)
        foreach (var old in Runs.Where(r => r.Ended).ToList()) Runs.Remove(old);
        var run = new RunInfo(++_nextId, title, kind);
        Runs.Add(run);
        if (kind == RunKind.Analysis)
        {
            var b = Base;
            b.State = StageState.Current;
            b.Detail = "";
            b.Watch.Restart();
            if (Primary == null) Title = title;
        }
        else if (primary)
        {
            Primary = run;
            Title = title;
            _note = null;
            foreach (var s in Steps) Reset(s);
            Mirror();
        }
        _tick.Start();
        Add(run, L.F("시작: {0}", title));
        Refresh();
        return run.Id;
    }

    private static void Reset(StageItem s)
    {
        s.State = StageState.Todo;
        s.Detail = "";
        s.Time = "";
        s.Watch.Reset();
    }

    public void Stage(int id, RecStage stage)
    {
        if (Find(id) is not { Ended: false } run) return;
        if (run.Current == stage) return;
        run.Current = stage;
        run.StageWatch.Restart();
        if (run.Kind == RunKind.Analysis)
        {
            if (stage == RecStage.Done) EndAnalysis(run);
            Refresh();
            return;
        }
        run.StageLabel = Steps.First(s => s.Stage == stage).Label;
        run.Detail = "";
        if (stage == RecStage.Done) End(run, L.T("끝"));
        else Add(run, "▶ " + run.StageLabel);
        if (run == Primary) MoveNodes(stage);
        Refresh();
    }

    /// <summary>주 계산의 칸 상태: 앞 칸을 완료로, 거치지 않은 칸을 '안 거침'으로</summary>
    private void MoveNodes(RecStage stage)
    {
        var cur = Steps.FirstOrDefault(s => s.State == StageState.Current);
        if (cur != null) { cur.Watch.Stop(); cur.State = StageState.Done; }
        // 이번 계산에서 하지 않은 앞 칸: 후보 목록을 재사용했으면 '재사용', 아니면(팔로우·AI처럼 다른 데서 시작) '안 함'
        foreach (var s in Steps.Where(s => s.Stage < stage && s.State == StageState.Todo))
        {
            // 팔로우 뮤지션 신보는 후보 목록이 아니라 따로 도는 '팔로우 신보' 계산으로 들어온다 → 재사용 대상이 아니다
            if (Primary?.ReusedAt is { } at && s.Stage is RecStage.Mine or RecStage.Seeds or RecStage.Related or RecStage.Discography)
            {
                s.State = StageState.Reused;
                s.Time = Ago(at);
            }
            else s.State = StageState.Skipped;
        }
        var next = Steps.First(s => s.Stage == stage);
        if (stage == RecStage.Done) next.State = StageState.Done;
        else
        {
            // 같은 계산에서 앞 단계로 돌아오면 뒤 칸을 다시 비운다
            foreach (var s in Steps.Where(s => s.Stage > stage)) Reset(s);
            next.State = StageState.Current;
            next.Watch.Restart();
            next.LastNews = DateTime.Now;
        }
        Mirror();
    }

    /// <summary>
    /// 계산이 끝까지 가지 않고 끝났다: 저장된 추천을 그대로 보여 줬거나 멈췄다. 주 계산이면 끝나지 않은 칸을 '안 거침'으로
    /// </summary>
    public void Finish(int id, string note)
    {
        if (Find(id) is not { Ended: false } run) return;
        if (run.Kind == RunKind.Analysis) { EndAnalysis(run); Refresh(); return; }
        if (run == Primary)
        {
            foreach (var s in Steps.Where(s => s.State is StageState.Current or StageState.Todo))
            {
                s.Watch.Stop();
                s.State = StageState.Skipped;
            }
            Mirror();
            _note = note;
        }
        End(run, note);
        Refresh();
    }

    private void End(RunInfo run, string note)
    {
        run.Ended = true;
        run.Total.Stop();
        run.StageWatch.Stop();
        run.StageLabel = note;
        run.Detail = "";
        Add(run, note);
        // 주 계산이 아닌 것은 끝나면 목록에서 내린다
        if (run != Primary) Runs.Remove(run);
        if (Runs.All(r => r.Ended)) _tick.Stop();
    }

    private void EndAnalysis(RunInfo run)
    {
        var b = Base;
        b.Watch.Stop();
        b.State = StageState.Done;
        End(run, L.T("[분석] 끝"));
    }

    /// <summary>따라가는 칸(장르 정하는 순서)을 그 단계 칸과 같은 상태로</summary>
    private void Mirror()
    {
        foreach (var f in Stages.Where(s => s.Follows != null))
        {
            var src = Steps.First(s => s.Stage == f.Follows);
            f.State = src.State;
            f.Detail = src.Detail;
        }
    }

    public void Message(int id, string message)
    {
        if (Find(id) is not { Ended: false } run) return;
        run.Detail = message;
        if (run.Kind == RunKind.Analysis) { Base.Detail = message; Base.LastNews = DateTime.Now; }
        else if (run == Primary)
        {
            var cur = Steps.FirstOrDefault(s => s.State == StageState.Current);
            if (cur != null) { cur.Detail = message; cur.LastNews = DateTime.Now; }
            Mirror();
        }
        Add(run, message);
        Changed?.Invoke();
    }

    private void Add(RunInfo run, string line)
    {
        // 주 계산이 아닌 줄에는 어느 계산인지 붙인다
        var tag = run == Primary ? "" : $"[{run.KindLabel}] ";
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {tag}{line}");
        while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
    }

    /// <summary>재사용 칸에 쓰는 '얼마 전에 만든 것인가'</summary>
    private static string Ago(DateTime at) => (DateTime.Now - at) switch
    {
        { TotalMinutes: < 1 } => L.T("방금"),
        { TotalHours: < 1 } t => L.F("{0}분 전", (int)t.TotalMinutes),
        var t => L.F("{0}시간 전", (int)t.TotalHours),
    };

    /// <summary>후보 목록을 앞 계산에서 가져다 썼다 (StageProgress.Reused)</summary>
    public void SetReused(int id, DateTime builtAt)
    {
        if (Find(id) is { } run) run.ReusedAt = builtAt;
    }

    private static string Clock(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0}s" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void Refresh()
    {
        foreach (var s in Stages.Where(s => s.Watch.Elapsed > TimeSpan.Zero && s.State != StageState.Reused)) s.Time = Clock(s.Watch.Elapsed);
        foreach (var r in Runs) r.StageTime = Clock(r.StageWatch.Elapsed);
        foreach (var r in Runs) r.Time = r.Ended ? Clock(r.Total.Elapsed) : $"{Clock(r.StageWatch.Elapsed)} / {Clock(r.Total.Elapsed)}";
        int running = Runs.Count(r => !r.Ended);
        if (Primary == null && Runs.Count == 0)
        {
            // 아직 추천 계산 없음 (앱을 켤 때 잠깐 돈 옆 작업이 끝난 뒤에도 '진행 중'이 남지 않게)
            Status = L.T("아직 계산 없음 · 계산이 시작되면 여기에 보여요");
            Changed?.Invoke();
            return;
        }
        Status = _note != null && running == 0 ? _note
            : running > 1 ? L.F("{0}개 계산이 함께 진행 중 · 미리듣기·분석은 소리 서비스가 하나씩 처리해요", running)
            : running == 1 ? L.T("진행 중") : L.T("끝");
        Changed?.Invoke();
    }
}

/// <summary>
/// 진행 문구는 원래 받는 쪽(BusyText 등)에, 단계는 PipelineMonitor 에 넘기는 IProgress.
/// Progress&lt;T&gt; 처럼 만든 곳(UI 스레드)으로 옮겨 부른다. 첫 단계가 오면 계산이 시작된 것으로 보고 순서도 창을 띄운다.
/// </summary>
public sealed class StageProgress(string title, Action<string> report, RunKind kind, bool primary = true) : IProgress<string>, IStageProgress
{
    private readonly SynchronizationContext? _ctx = SynchronizationContext.Current;
    private int _run;  // 0 = 아직 시작 전
    private DateTime? _reusedAt;  // 시작 전에 온 '후보 목록 재사용' (시작하면 넘긴다)

    private void Post(Action a)
    {
        if (_ctx == null) a();
        else _ctx.Post(_ => a(), null);
    }

    /// <summary>단계를 기다리지 않고 지금 창에 이 계산을 보이기 시작한다 (기간을 바꿨을 때 창이 앞 기간에 머물지 않게)</summary>
    public void Start() => Post(() =>
    {
        if (_run != 0) return;
        _run = PipelineMonitor.Instance.Begin(title, kind, primary);
        if (_reusedAt is { } at) PipelineMonitor.Instance.SetReused(_run, at);
    });

    /// <summary>앞 계산이 만든 후보 목록을 그대로 쓴다 — 시드·비슷한 아티스트·라이브러리 뮤지션 칸을 '재사용'으로</summary>
    public void Reused(DateTime builtAt) => Post(() =>
    {
        if (_run != 0) PipelineMonitor.Instance.SetReused(_run, builtAt);
        else _reusedAt = builtAt;
    });

    /// <summary>순서도 진행 문구만 (원래 받는 쪽은 그대로) — AI 웹 검색처럼 따로 문구를 모아 보이는 곳에서</summary>
    public void Note(string value) => Post(() =>
    {
        if (_run != 0) PipelineMonitor.Instance.Message(_run, value);
    });

    /// <summary>끝까지 계산하지 않고 끝났다 (저장된 결과·멈춤)</summary>
    public void Finish(string note) => Post(() =>
    {
        if (_run != 0) PipelineMonitor.Instance.Finish(_run, note);
    });

    public void Report(string value) => Post(() =>
    {
        report(value);
        if (_run != 0) PipelineMonitor.Instance.Message(_run, value);
    });

    public void Stage(RecStage stage) => Post(() =>
    {
        if (_run == 0)
        {
            if (stage == RecStage.Done) return;  // 시작도 안 한 계산의 끝 (예: AI 결과가 없을 때)
            _run = PipelineMonitor.Instance.Begin(title, kind, primary);
            if (_reusedAt is { } at) PipelineMonitor.Instance.SetReused(_run, at);
            // 옆 작업(수록곡 펼치기 같은 것)으로는 창을 띄우지 않는다
            if (primary || kind == RunKind.Analysis) PipelineWindow.ShowShared();
        }
        PipelineMonitor.Instance.Stage(_run, stage);
    });
}

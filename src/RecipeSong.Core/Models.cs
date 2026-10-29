namespace RecipeSong.Core;

public enum LibrarySource { ITunes, Winamp }

public enum PeriodKind { Year, Month, Week }

/// <summary>한 곡의 청취 기록. 소스마다 채워지는 필드가 다르다.</summary>
public sealed record TrackRecord
{
    public int? ITunesTrackId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string Album { get; init; } = "";
    public string? AlbumArtist { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
    public int PlayCount { get; init; }
    public bool Loved { get; init; }
    /// <summary>직접 매긴 별점 0~100 (별 하나 = 20). 앨범 별점에서 자동으로 채워진 값은 0.</summary>
    public int Rating { get; init; }
    /// <summary>iTunes에서 '좋아하지 않음'</summary>
    public bool Disliked { get; init; }
    /// <summary>iTunes가 센 건너뛴 횟수</summary>
    public int SkipCount { get; init; }

    /// <summary>
    /// 취향 가중치 0.1~1.0 (ml/library.py track_weight 와 같은 식):
    /// 하트·별 4개 이상은 최대, 별 3개는 0.8 이상, 별 1~2개는 0.4 이하, '좋아하지 않음'은 0.1, 그 외는 재생 수 (12회 이상이면 최대).
    /// 그 외 곡은 스킵(넘긴 비율만큼 최대 절반)과 최근성(반년 지나면 3년에 걸쳐 0.7배까지)으로 낮춘다.
    /// </summary>
    public double Weight
    {
        get
        {
            if (Disliked) return 0.1;
            if (Loved || Rating >= 80) return 1.0;
            var w = 0.35 + 0.65 * Math.Min(1.0, Math.Log(1 + PlayCount) / Math.Log(1 + 12));
            if (Rating >= 60) w = Math.Max(w, 0.8);
            else if (Rating is > 0 and <= 40) w = Math.Min(w, 0.4);
            if (SkipCount >= 2) w *= 1 - 0.5 * SkipCount / (double)(PlayCount + SkipCount);
            var touched = new[] { LastPlayed, DateAdded }.Max();
            if (touched is { } t && (DateTime.UtcNow - t.ToUniversalTime()).TotalDays is var age && age > 180)
                w *= 1 - 0.3 * Math.Min(1.0, (age - 180) / (3 * 365));
            return Math.Round(Math.Max(w, 0.1), 3);
        }
    }
    public DateTime? DateAdded { get; init; }
    public DateTime? LastPlayed { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? Location { get; init; }
    public LibrarySource Source { get; init; }

    public string GroupArtist => string.IsNullOrWhiteSpace(AlbumArtist) ? Artist : AlbumArtist!;

    /// <summary>청취 시기 판정에 쓰는 날짜: 마지막 재생일이 우선, 없으면 추가일.</summary>
    public DateTime? ActivityDate => LastPlayed ?? DateAdded;
}

/// <summary>System: 보관함·구입 항목 등 iTunes 기본 목록, Smart: 조건 기반 자동 목록, User: 직접 만든 목록</summary>
public enum PlaylistKind { User, Smart, Folder, System }

/// <summary>직접 고른 곡 묶음 (iTunes 플레이리스트)</summary>
public sealed record Playlist(
    string Name, string? PersistentId, string? ParentPersistentId,
    PlaylistKind Kind, IReadOnlyList<TrackRecord> Tracks);

public readonly record struct Period(PeriodKind Kind, DateTime Start, DateTime End, string Label)
{
    public bool Contains(DateTime t) => t >= Start && t < End;
    public bool Contains(DateOnly d) => Contains(d.ToDateTime(TimeOnly.MinValue));
    public bool OverlapsYear(int year) => Start.Year <= year && End.AddTicks(-1).Year >= year;

    /// <summary>화면 표시용 이름 (언어를 따른다). Label은 캐시 키라서 그대로 둔다.</summary>
    public string Display => Start == DateTime.MinValue ? L.T("전체") : Kind switch
    {
        PeriodKind.Year => L.F("{0}년", Start.Year),
        PeriodKind.Month => L.F("{0}년 {1}월", Start.Year, Start.Month, Start),
        _ => L.F("{0}년 {1}주차 ({2:M.d} – {3:M.d})",
            System.Globalization.ISOWeek.GetYear(Start), System.Globalization.ISOWeek.GetWeekOfYear(Start), Start, Start.AddDays(6)),
    };
}

public sealed record AlbumSummary(
    string Artist, string Album, int? Year, int PlayCount, int TrackCount,
    string? SampleTrackPath, string? SampleTrackTitle, DateOnly? ReleaseDate = null);

public sealed record ArtistSummary(string Artist, int PlayCount, IReadOnlyList<AlbumSummary> Albums);

public sealed record ListeningSummary(Period Period, IReadOnlyList<ArtistSummary> Artists)
{
    public int TotalPlays => Artists.Sum(a => a.PlayCount);
    public int AlbumCount => Artists.Sum(a => a.Albums.Count);
}

/// <summary>취향 프로필의 한 아티스트와 가중치(0~1). Playlist는 시드를 뽑은 플레이리스트.</summary>
public sealed record TasteArtist(string Artist, double Weight, int PlayCount, string? Playlist = null);

/// <summary>
/// 신보를 점수와 상관없이 항상 보여 줄 뮤지션: 내 라이브러리에서 많이 듣는 뮤지션(TasteProfile.LibraryArtists) · 팔로우.
/// Playlist: 카드 배지 자리에 넣는 값 (TasteProfile.LibraryTag · FollowList.Tag). Style·Region 은 레시피·지역을 쓰던 때의 자리 (지금은 비어 있다).
/// Titles: 내 라이브러리의 이 뮤지션 곡·앨범 제목 (정규화) — Deezer 동명이인 확인용.
/// </summary>
/// <param name="DeezerId">팔로우한 아티스트처럼 Deezer id를 이미 알면 (0이면 이름으로 찾는다)</param>
public sealed record PlaylistArtist(string Artist, string Playlist, string? Style, string Region, int Tracks,
    IReadOnlySet<string> Titles, long DeezerId = 0);

public enum Region { Korea, Japan, International, SoutheastAsia, Unknown }

/// <summary>
/// 기간 안에 발매된, 취향에 맞는 추천 앨범/EP/싱글.
/// Crate·CrateFit·Taste·NearTracks는 소리 분석 결과 (분석 못 했으면 null).
/// </summary>
public sealed record ReleaseRecommendation(
    string Artist, string Album, string RecordType, DateOnly ReleaseDate, string? Label,
    string? CoverUrl, long DeezerAlbumId, string? Link,
    string? GenreName, Region Region,
    string? Crate, double? CrateFit, double? Taste, IReadOnlyList<string> NearTracks,
    IReadOnlyList<string> BecauseOf, bool IsKnownArtist, double Score,
    string? FromPlaylist = null, string? AiReason = null, IReadOnlyList<string>? AiBy = null,
    string? FavNear = null, string? LikedLabel = null, string? WithLiked = null, int Fans = 0,
    string? SourceUrl = null, long ArtistId = 0, IReadOnlyList<TrackPair>? Pairs = null, IReadOnlyList<string>? Genres = null);

/// <summary>이 앨범의 곡 ≈ 가장 닮은 내 곡 ("아티스트 – 곡명"), 소리 서비스가 미리듣기 곡마다 매긴다</summary>
public sealed record TrackPair(string Track, string Near, double Sim);

/// <summary>
/// AI 커넥터가 웹에서 찾은 발매작 (Deezer 확인 전). Region은 AI가 말한 지역 (일본/한국/해외/동남아) — 추천에는 쓰지 않는다.
/// By: 제안한 커넥터 이름 ("Claude", "GPT").
/// </summary>
public sealed record AiPick(string Artist, string Album, string? ReleaseDate, string? Type, string? Region, string Reason, string? Source,
    string By = "")
{
    /// <summary>로그에서 다시 불러온 제안의 이유 자리 (원래 이유는 로그에 없다). 화면은 이 값을 보고 번역·흐리게 표시한다</summary>
    public const string RecoveredReason = "다시 불러온 AI 제안 (추천 이유는 남아 있지 않아요)";
}

public sealed record RecommendationResult(
    Period Period,
    IReadOnlyList<ReleaseRecommendation> Releases,
    bool SoundScored);

/// <summary>
/// 소리 분석 서비스가 돌려주는 앨범 한 장의 판정 + 레이블·장르.
/// 미리듣기가 없어 소리를 못 들었으면 Taste가 null.
/// </summary>
public sealed record AlbumSound(
    double? Taste, IReadOnlyList<string> Near, string? Label, string? Genre,
    double? Fav = null, string? FavNear = null, double? Dis = null, IReadOnlyList<string>? With = null,
    IReadOnlyList<TrackPair>? Pairs = null, IReadOnlyList<string>? Genres = null)
{
    /// <summary>Fav: 저장함 앨범과 닮은 정도, FavNear: 가장 닮은 저장 앨범 ("아티스트 – 앨범")</summary>
    public bool Heard => Taste != null;
}

/// <summary>
/// Trained=false 면 분석 전 (처음 설치): TrainedAt 은 의미 없고 Library 는 라이브러리 곡 수.
/// Compared: 닮음 비교에 쓰는 내 곡 수 (오래 안 들은 곡·지운 곡은 빠진다)
/// </summary>
public sealed record SoundStatus(
    string Device, string? Gpu, DateTime TrainedAt, int Tracks, int Embedded, int NewTracks,
    bool Trained = true, int Library = 0, int Compared = 0);

public sealed record SoundUpdateResult(int Added, int Compared);

public sealed record AlbumArt(string? ArtworkUrl, long? DeezerAlbumId = null);

/// <summary>앨범 수록곡 한 곡 (Deezer). Preview는 30초 미리듣기 URL, Rank는 Deezer 인기 점수 (0~1,000,000).</summary>
public sealed record AlbumTrack(int Number, string Title, string? Artist, TimeSpan Duration, string? Preview, int Rank = 0, long Id = 0);

/// <summary>소리 서비스 요청 줄의 상태: 차례 기다림 · 처리 중 · 앱에서 멈췄지만 서비스가 끝내는 중</summary>
public enum SoundJobState { Waiting, Running, Orphan }

/// <summary>소리 서비스 요청 하나 (cmd: score_albums · update · favorites · track_near …, Message: 마지막 진행 문구)</summary>
public sealed record SoundJob(string Cmd, SoundJobState State, string Message);

using RecipeSong.Core;

namespace RecipeSong.Wpf.ViewModels;

public sealed record EvidenceRow(string Label, string Value);

/// <summary>새 분석을 요청하지 않고 계산 당시 저장한 근거만 보여 준다. 기준 이상·미만만 지금 기준선으로 다시 판정한다.</summary>
public sealed class RecommendationEvidence
{
    public string Score { get; }
    public string Notice { get; }
    public string Scope { get; }
    public IReadOnlyList<EvidenceRow> Reasons { get; }
    public IReadOnlyList<EvidenceRow> Contributions { get; }
    public IReadOnlyList<EvidenceRow> Analysis { get; }
    public IReadOnlyList<EvidenceRow> Tracks { get; }
    public IReadOnlyList<EvidenceRow> Pairs { get; }
    public bool HasContributions => Contributions.Count > 0;
    public bool HasTracks => Tracks.Count > 0;
    public bool HasPairs => Pairs.Count > 0;

    public RecommendationEvidence(ReleaseRecommendation album)
    {
        var evidence = album.Evidence;
        Score = evidence != null || album.Taste != null ? L.F("정렬 점수 {0:0.000}", album.Score) : L.T("소리 분석 기록 없음");
        Notice = evidence == null ? L.T("이 추천은 점수 상세 기록을 지원하기 전에 계산했어요. ‘다시 계산’하면 상세 내역을 볼 수 있어요.")
            : album.Taste == null ? L.T("미리듣기 분석 기록이 없어요. 소리 점수 없이 계산한 결과예요.")
            : L.T("계산 당시의 근거예요. 기준 이상·미만은 지금 기준선으로 다시 판정해요. 닮음 점수는 좋아할 확률이 아니에요.");
        Scope = album.FromPlaylist != null ? L.T("내 라이브러리·팔로우 신보를 먼저 보여요. 내 라이브러리 뮤지션 신보는 취향 기준 없이 보여요.")
            : L.T("정렬 점수는 같은 계산 안에서 비교해요. 다른 기간·추천 묶음과 직접 비교하지 마세요.");
        var reasons = new List<EvidenceRow>();
        if (album.BecauseOf.Count > 0) reasons.Add(new(L.T("추천 출발점"), string.Join(", ", album.BecauseOf.Select(L.T))));
        if (album.MatchedTag is { } tag) reasons.Add(new(L.T("취향과 겹치는 스타일"), tag));
        if (album.MatchedCrew is { } crew) reasons.Add(new(L.T("좋아하는 앨범과 같은 제작진"), crew));
        if (album.LikedLabel is { } label) reasons.Add(new(L.T("좋아하는 레이블"), label));
        if (album.WithLiked is { } with) reasons.Add(new(L.T("좋아하는 아티스트 참여"), with));
        if (album.FavNear is { } favorite) reasons.Add(new(L.T("좋아요와 닮음"), favorite));
        if (album.AiReason is { } ai) reasons.Add(new(L.T("AI·웹진 추천 이유"), ai == AiPick.RecoveredReason ? L.T(ai) : ai));
        if (reasons.Count == 0) reasons.Add(new(L.T("추천 근거"), L.T("추가로 저장된 연결 근거가 없어요.")));
        Reasons = reasons;
        Contributions = evidence?.Contributions.Select(c => new EvidenceRow(ContributionLabel(c.Kind),
            L.F("{0:+0.000;-0.000;0.000}", c.Value))).ToArray() ?? [];
        // 기준 판정은 목록과 같게 지금 기준선으로 (계산 당시 값은 기준선이 바뀌면 목록과 어긋난다)
        var threshold = TasteThresholds.For(album);
        var analysis = new List<EvidenceRow>();
        if (album.Taste is { } taste) analysis.Add(new(L.T("최종 취향 닮음"), L.F("{0:0.000}", taste)));
        if (evidence?.MeanTaste is { } mean) analysis.Add(new(L.T("앨범 평균 벡터 점수"), L.F("{0:0.000}", mean)));
        if (evidence?.TopTaste is { } top) analysis.Add(new(L.T("상위 최대 3곡 평균 점수"), L.F("{0:0.000}", top)));
        if ((threshold ?? evidence?.FitThreshold) is { } line) analysis.Add(new(L.T("취향 기준선"), L.F("{0:0.000}", line)));
        if (album.AnalyzedTracks is { } count) analysis.Add(new(L.T("분석한 곡 수"), L.F("{0}곡", count)));
        if (evidence?.TotalTracks is > 0) analysis.Add(new(L.T("전체 수록곡 수"), L.F("{0}곡", evidence.TotalTracks)));
        if ((TasteGate.FitRatio(album, threshold) ?? album.FitRatio) is { } ratio) analysis.Add(new(L.T("분석 곡 중 기준 이상"), L.F("{0:P0}", ratio)));
        if (evidence != null) analysis.Add(new(L.T("분석 범위"), evidence.HitsOnly ? L.T("인기곡 모드 · 최대 3곡") : L.T("전곡 모드 · 최대 20곡")));
        if (analysis.Count == 0) analysis.Add(new(L.T("소리 분석"), L.T("저장된 분석 정보가 없어요.")));
        Analysis = analysis;
        Tracks = evidence?.Tracks?.OrderByDescending(t => t.Taste).Select(t => new EvidenceRow(
            string.IsNullOrEmpty(t.Title) ? L.F("곡 ID {0}", t.Id) : t.Title,
            t.MatchesDislike == true ? L.F("{0:0.000} · {1}", t.Taste, L.T("불호 곡에 더 가까움")) : t.MeetsThreshold != null
                ? L.F("{0:0.000} · {1}", t.Taste, TasteGate.TrackFits(album, t, threshold) ? L.T("기준 이상") : L.T("기준 미만"))
                : L.F("{0:0.000}", t.Taste))).ToArray() ?? [];
        Pairs = album.Pairs is { Count: > 0 } pairs ? pairs.Select(p => new EvidenceRow(p.Track, p.Near)).ToArray()
            : album.NearTracks.Select(n => new EvidenceRow(L.T("비슷한 내 곡"), n)).ToArray();
    }

    private static string ContributionLabel(string kind) => kind switch
    {
        "sound" => L.T("소리 취향"), "connection" => L.T("아티스트 연결"), "likes" => L.T("좋아요 닮음"),
        "dislikes" => L.T("별로 닮음"), "unheard" => L.T("미분석 기본 점수"), "label" => L.T("레이블 취향"),
        "collaborator" => L.T("좋아하는 아티스트 참여"), "format" => L.T("앨범 형식 취향"),
        "small_artist" => L.T("작은 아티스트 우대"), "metadata" => L.T("태그·제작진 보완"),
        _ => L.T("그 밖의 점수")
    };
}

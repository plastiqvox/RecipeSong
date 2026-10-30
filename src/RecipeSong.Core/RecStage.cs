namespace RecipeSong.Core;

/// <summary>
/// 추천 계산 단계 (docs/images/recommendation.mjs 흐름도의 칸). 작업 현황 창이 지금 단계를 표시한다.
/// 순서가 뜻이 있다: 앞 칸을 건너뛰고 뒤 칸이 오면 앞 칸은 '이번엔 안 함'. Analyze(내 곡 소리 분석)·AiSearch·AiCheck(AI 웹 검색·웹진 글 → Deezer 확인)는
/// 옆 줄기라 맨 앞에 둔다. Mine·Follow·Seeds·Related·Discography 는 후보 목록을 만드는 칸 (12시간 재사용), Releases 부터는 기간마다.
/// </summary>
public enum RecStage { Analyze, AiSearch, AiCheck, Mine, Follow, Seeds, Related, Discography, Releases, Listen, Score, Genre, Done }

/// <summary>진행 문구와 함께 단계도 받는 쪽. ReleaseRecommender 는 progress 가 이것일 때만 단계를 알린다 (문구는 번역되니 단계는 따로 넘긴다).</summary>
public interface IStageProgress
{
    void Stage(RecStage stage);
}

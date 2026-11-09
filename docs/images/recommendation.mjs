// 추천 흐름 그림 원본 (shorts-deck 스킬).
//   node ~/.claude/skills/shorts-deck/build.mjs docs/images/recommendation.mjs
// → recommendation-flow.svg, genre-ladder.svg. 추천 방식을 바꾸면 이 파일을 고치고 다시 만든다.
// tone "focus" 는 통합 뮤지션 ID DB와 ZIP 보유 정보의 분리 등을 강조한다.
export default (k) => {
  const X = { l: 300, c: 800, r: 1300 };
  const Y = (i) => 255 + i * 165;
  const C = (x, i, title, line, tone) => k.card(x, Y(i), title, [line], { w: x === X.c ? 440 : 400, size: 24, tone });

  const lib = C(X.c, 0, "내 라이브러리 전체", "iTunes·플리 · ZIP 보유와 분리");
  const period = k.card(X.c, 90, "추천 기간 · 주 단위 범위", ["적용: 저장된 주만 합쳐 보기", "재계산: 확인 후 모든 주 갱신"], { w: 440, size: 21, tone: "focus" });
  const archive = C(X.l, 3, "앨범 ZIP 보유 목록", "중복 제외·ID 대조만 · 취향 중립", "focus");
  const base = C(X.l, 1, "내 곡 소리 기준", "하트 1 · 90일+ 안 들은 곡 0", "focus");
  const seed = C(X.c, 1, "시드 아티스트", "곡 가중치 합 상위 80명 + 하트·팔로우", "focus");
  const similar = C(X.c, 2, "비슷한 아티스트 2단계", "Deezer 연관 + Last.fm · 상위 1,000명", "focus");
  const mine = C(X.r, 2, "내 아카이브 뮤지션", "가중치 상위 500명 · 후보에 포함", "focus");
  const identity = k.card(X.r, Y(1), "뮤지션 ID · SQLite", ["라이브러리·ZIP 앨범명 대조", "수동 우선 · 보유 정보 변경 시 확인"], { w: 400, size: 22, tone: "focus" });
  const rel = C(X.c, 3, "그 기간 발매작", "가진 앨범·차단 빼고");
  const listen = C(X.c, 4, "곡별 취향·불호 집계", "평균 · 상위곡 · 불호 곡 유사도", "focus");
  const ai = C(X.r, 4, "AI 커넥터 · 웹진", "Deezer에서 있는지 확인");
  const score = C(X.c, 5, "점수 · 메타데이터 보완", "태그·제작진 · 작은 아티스트(Last.fm)", "focus");
  const ladder = C(X.l, 6, "장르 정하는 순서", "Last.fm → Discogs → Deezer …", "focus");
  const crate = C(X.c, 6, "장르 칸", "국적과 상관없이 6칸", "focus");
  const screen = C(X.c, 7, "화면", "인디·록 · 일렉트로닉 · 힙합·R&B …");
  const gate = C(X.r, 7, "공통 취향 기준", "라이브러리 뮤지션 외 · 적합 곡수", "focus");
  const review = C(X.r, 6, "확인 필요", "미분석 · 곡별 근거 부족", "focus");

  const A = (pts, label, o = {}) => k.arrow(pts, { width: 3, head: 15, label, labelSize: 17, ...o });
  const down = (a, b, label) => A([k.edge(a, "bottom"), k.edge(b, "top")], label, { side: "right" });

  const flow = {
    kind: "figure", name: "recommendation-flow", w: 1600, h: Y(7) + 95,
    title: "라이브러리 전체에서 시드와 내 곡 소리 기준을 만들고, 곡별 불호를 반영해 점수를 매긴다. 모든 출처에 공통 취향 기준을 적용하고 미분석·근거 부족은 확인 필요로 분리한다.",
    items: [
      A([k.edge(period, "right"), [1560, period.y], [1560, rel.y], k.edge(rel, "right")]),
      A([k.edge(lib, "left"), [X.l, lib.y], k.edge(base, "top")], "전 곡 소리 (처음 한 번)"),
      A([k.edge(lib, "right"), [X.r, lib.y], k.edge(identity, "top")], "보유 정보"),
      down(identity, mine, "ID 공유"),
      A([k.edge(identity, "left"), k.edge(seed, "right")]),
      down(lib, seed, "많이 듣고 좋아한"),
      down(seed, similar, "연관 아티스트"),
      down(similar, rel, "디스코그래피"),
      A([k.edge(archive, "right"), k.edge(rel, "left")]),
      A([k.edge(mine, "bottom"), [X.r, rel.y], k.edge(rel, "right")], "항상 포함"),
      down(rel, listen, "30초 미리듣기"),
      A([k.edge(base, "bottom"), [X.l, Y(1) + 85], [40, Y(1) + 85], [40, listen.y], k.edge(listen, "left")], "비교 기준 (가장 닮은 20곡)", { side: "right" }),
      A([k.edge(ai, "left"), k.edge(listen, "right")]),
      down(listen, score, "닮음 · 좋아요/별로 닮음"),
      down(score, crate, "점수 높은 순"),
      A([k.edge(ladder, "right"), k.edge(crate, "left")]),
      down(crate, screen, "칸마다 묶기"),
      A([k.edge(gate, "left"), k.edge(screen, "right")], null, { dashed: true }),
      A([k.edge(gate, "top"), k.edge(review, "bottom")], "근거 부족", { side: "right" }),
      period, lib, base, seed, similar, mine, identity, archive, rel, listen, ai, score, ladder, crate, screen, gate, review,
    ],
  };

  const steps = [
    ["Last.fm", "앨범 → 아티스트 태그", "focus"],
    ["Discogs", "스타일 · 토큰 있을 때", "focus"],
    ["Deezer 장르", "Pop·Asian Music 제외"],
    ["닮은 앨범 15장", "칸 아는 앨범끼리 투표", "focus"],
  ];
  const cards = steps.map(([t, l, tone], i) => k.card(230 + i * 380, 150, t, [l], { w: 300, size: 22, tone }));
  const ladderFig = {
    kind: "figure", name: "genre-ladder", w: 1600, h: 270,
    title: "장르 칸은 Last.fm 태그, Discogs, Deezer 장르, 소리가 닮은 앨범 순서로 보고, 앞에서 정해지지 않을 때만 다음으로 넘어간다. 끝까지 모르면 인디·록.",
    items: [
      k.text(800, 50, "앞에서 칸이 정해지지 않으면 다음으로", { size: 19, weight: 500 }),
      ...cards.slice(1).map((c, i) => k.arrow([k.edge(cards[i], "right", 6), k.edge(c, "left", 6)], { width: 3, head: 13 })),
      ...cards,
      k.text(cards[3].x + cards[3].w / 2, 232, "끝까지 모르면 인디·록", { size: 18, weight: 500, anchor: "end" }),
    ],
  };
  return [flow, ladderFig];
};

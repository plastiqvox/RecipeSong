// 추천 흐름 그림 원본 (shorts-deck 스킬).
//   node ~/.claude/skills/shorts-deck/build.mjs docs/images/recommendation.mjs
// → recommendation-flow.svg, genre-ladder.svg. 추천 방식을 바꾸면 이 파일을 고치고 다시 만든다.
// tone "focus" 는 0.2 에서 바뀐 곳 (레시피·국적 구분을 빼고 장르 칸으로, 비슷한 아티스트에 Last.fm 을 더함).
export default (k) => {
  const X = { l: 300, c: 800, r: 1300 };
  const Y = (i) => 90 + i * 165;
  const C = (x, i, title, line, tone) => k.card(x, Y(i), title, [line], { w: x === X.c ? 440 : 400, size: 24, tone });

  const lib = C(X.c, 0, "내 아카이브 전체", "플레이리스트와 상관없이");
  const base = C(X.l, 1, "내 곡 소리 기준", "하트 1 · 90일+ 안 들은 곡 0", "focus");
  const seed = C(X.c, 1, "시드 아티스트", "곡 가중치 합 상위 80명 + 하트·팔로우", "focus");
  const similar = C(X.c, 2, "비슷한 아티스트 2단계", "Deezer 연관 + Last.fm · 상위 1,000명", "focus");
  const mine = C(X.r, 2, "내 아카이브 뮤지션", "가중치 상위 500명 · 신보 항상 앞", "focus");
  const rel = C(X.c, 3, "그 기간 발매작", "가진 앨범·차단 빼고");
  const listen = C(X.c, 4, "미리듣기 소리 분석", "CLAP · 앨범당 최대 20곡");
  const ai = C(X.r, 4, "AI 커넥터 · 웹진", "Deezer에서 있는지 확인");
  const score = C(X.c, 5, "점수", "1.5 × 닮음 + 연결 + 좋아요 − 별로", "focus");
  const ladder = C(X.l, 6, "장르 정하는 순서", "Last.fm → Discogs → Deezer …", "focus");
  const crate = C(X.c, 6, "장르 칸", "국적과 상관없이 6칸", "focus");
  const screen = C(X.c, 7, "화면", "인디·록 · 일렉트로닉 · 힙합·R&B …");
  const gate = C(X.r, 7, "AI·웹진 거르기", "일반 추천 하위 25% 선 미만 숨김", "focus");

  const A = (pts, label, o = {}) => k.arrow(pts, { width: 3, head: 15, label, labelSize: 17, ...o });
  const down = (a, b, label) => A([k.edge(a, "bottom"), k.edge(b, "top")], label, { side: "right" });

  const flow = {
    kind: "figure", name: "recommendation-flow", w: 1600, h: Y(7) + 95,
    title: "라이브러리 전체에서 시드와 내 곡 소리 기준을 만들고, Deezer로 후보를 모아 미리듣기 소리를 비교해 점수를 매긴 뒤 장르 칸으로 나눠 보여 주는 흐름. AI와 웹진 앨범은 닮음 기준을 넘어야 보인다.",
    items: [
      A([k.edge(lib, "left"), [X.l, lib.y], k.edge(base, "top")], "전 곡 소리 (처음 한 번)"),
      A([k.edge(lib, "right"), [X.r, lib.y], k.edge(mine, "top")], "2곡 이상 · 하트"),
      down(lib, seed, "많이 듣고 좋아한"),
      down(seed, similar, "연관 아티스트"),
      down(similar, rel, "디스코그래피"),
      A([k.edge(mine, "bottom"), [X.r, rel.y], k.edge(rel, "right")], "항상 포함"),
      down(rel, listen, "30초 미리듣기"),
      A([k.edge(base, "bottom"), [X.l, listen.y], k.edge(listen, "left")], "비교 기준 (가장 닮은 20곡)"),
      A([k.edge(ai, "left"), k.edge(listen, "right")]),
      down(listen, score, "닮음 · 좋아요/별로 닮음"),
      down(score, crate, "점수 높은 순"),
      A([k.edge(ladder, "right"), k.edge(crate, "left")]),
      down(crate, screen, "칸마다 묶기"),
      A([k.edge(gate, "left"), k.edge(screen, "right")], null, { dashed: true }),
      lib, base, seed, similar, mine, rel, listen, ai, score, ladder, crate, screen, gate,
    ],
  };

  const steps = [
    ["Last.fm", "앨범 → 아티스트 태그", "focus"],
    ["Discogs", "스타일 · 토큰 있을 때", "focus"],
    ["Deezer 장르", "Pop·Asian Music 제외"],
    ["소리 장르 태그", "EffNet 모델이 있을 때"],
    ["닮은 앨범 15장", "칸 아는 앨범끼리 투표", "focus"],
  ];
  const cards = steps.map(([t, l, tone], i) => k.card(170 + i * 315, 150, t, [l], { w: 270, size: 22, tone }));
  const ladderFig = {
    kind: "figure", name: "genre-ladder", w: 1600, h: 270,
    title: "장르 칸은 Last.fm 태그, Discogs, Deezer 장르, 소리 장르 태그, 소리가 닮은 앨범 순서로 보고, 앞에서 정해지지 않을 때만 다음으로 넘어간다. 끝까지 모르면 인디·록.",
    items: [
      k.text(800, 50, "앞에서 칸이 정해지지 않으면 다음으로", { size: 19, weight: 500 }),
      ...cards.slice(1).map((c, i) => k.arrow([k.edge(cards[i], "right", 6), k.edge(c, "left", 6)], { width: 3, head: 13 })),
      ...cards,
      k.text(cards[4].x + cards[4].w / 2, 232, "끝까지 모르면 인디·록", { size: 18, weight: 500, anchor: "end" }),
    ],
  };
  return [flow, ladderFig];
};

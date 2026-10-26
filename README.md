# RecipeSong

디거를 위한 발매 캘린더. 달력에서 연·월·주를 고르면 그 기간에 **발매된** 앨범·EP·싱글 중
내 iTunes 플레이리스트(크레이트) 취향에 맞는 것을, 미리듣기 **소리를 듣고** 골라 준다.

```
src/
  RecipeSong.Wpf    데스크톱 앱 (달력 → 크레이트별 추천 신보 / 내 라이브러리, 미리듣기, 자동 재학습)
  RecipeSong.Core   라이브러리 파싱, 후보 수집(Deezer), 지역 판정(MusicBrainz), 추천 점수, 소리 서비스 클라이언트
  RecipeSong.Cli    엔진 점검용 콘솔
  RecipeSong.Mcp    Claude Desktop/Code용 MCP 서버 (나중에 다듬을 예정)
ml/
  service.py   소리 분석 서비스 (앱이 자동 실행, GPU)
  embed.py     라이브러리 음원 → CLAP 임베딩 (처음 한 번, 약 1시간)
  train.py     크레이트(스타일) × 지역 분류기 학습
  crates.json  iTunes 플레이리스트 → 크레이트/지역 매핑
```

## 주요 기능

- **달력으로 기간 고르기**: 주·월·연. 분석해 둔 기간은 달력 칸 위 회색 선, AI가 찾은 앨범 발매일은 색 점 (Claude 오렌지 / GPT 초록, 비싼 모델일수록 진함).
- **크레이트**: 일본 인디 · Club · Jazz · Rock (소리로 판정), 해외 인디·얼터 · 일렉트로닉 · 소울·힙합 (Deezer 장르), 한국음악. 칩을 끌어서 순서 변경.
- **내 플리 뮤지션 신보는 항상**: 크레이트 플레이리스트에 있는 뮤지션(3천여 명)의 신보는 점수와 상관없이 앞에 나온다. 그 위에 학습 모델의 추가 제안.
- **카드**: 30초 미리듣기(수록곡 골라 듣기·다음 곡 자동), 곡별 인기도, 저장(책갈피), Deezer·YouTube 열기.
- **저장함**: 저장한 앨범은 따로 모아 보고, 취향 학습에도 쓴다 (저장한 앨범과 닮은 신보 가산 + 그 아티스트를 출발점으로).
- **AI 커넥터 (선택)**: Claude·GPT API 키로 웹에서 신보를 더 찾는다. Deezer에서 확인된 것만 카드로. 일일·주간 한도, 사용액·남은 크레딧 추정.
- **결과 저장**: 한 번 분석한 기간과 AI 결과는 저장해 두고 바로 꺼낸다. "다시 분석"으로 새로 계산, 연·월 "정밀 분석"은 월·주 단위로 쪼개 촘촘하게.
- **추천 설정**: 추천 양(기본/많이/최대), 모험 정도(익숙하게/보통/새롭게), 아는 아티스트 빼기, 캐시 위치·용량.

## 실행

```powershell
dotnet run --project src/RecipeSong.Wpf
```

- iTunes 라이브러리(`Music\iTunes\iTunes Music Library.xml`)는 자동으로 찾는다. 없으면 처음에 한 번 묻는다.
- 소리 분석 서비스는 `ml/` 폴더와 `uv`가 있으면 앱이 띄운다. 없으면 아티스트 연결만으로 추천한다.
- 창이 하얗게 나오던 문제 때문에 소프트웨어 렌더링이 기본이다. 하드웨어 렌더링: `CRATE_HW_RENDER=1`.

## 처음 한 번: 소리 학습

```powershell
cd ml
uv sync
uv run python embed.py "C:/Users/<me>/Music/iTunes/iTunes Music Library.xml"   # 약 1시간 (RTX 3080 Ti 기준)
uv run python train.py "C:/Users/<me>/Music/iTunes/iTunes Music Library.xml"   # 몇십 초
```

- 모델: `laion/larger_clap_general`. (`laion/larger_clap_music` 체크포인트는 어떤 입력에도 같은 출력을 내는 고장 상태라 쓰지 않는다.)
- 이후에는 앱이 iTunes 라이브러리 변경을 감지해 **새 곡만 분석하고 재학습**한다. 새 모델이 이전보다 2%p 넘게 나빠지면 이전 모델을 유지한다.
- 플레이리스트를 새로 만들거나 이름을 바꾸면 `ml/crates.json`을 고친다.

## 추천 방식

1. **시드**: 크레이트 플레이리스트마다 많이 들은 아티스트 10명 (지역 필터를 고르면 그 지역 플레이리스트에서 40명)
2. **후보**: Deezer 비슷한 아티스트 2단계 → 상위 1,000명의 디스코그래피 (캐시: 3일)
3. **기간 필터**: 선택한 기간에 발매된 작품 중 이미 가진 앨범 제외, 아티스트 연결 점수 상위 150장
4. **소리**: 각 앨범 미리듣기 최대 3곡 → 크레이트 확률, 지역 확률, 내 곡과의 유사도, 가장 비슷한 내 곡
5. **점수**: 취향 유사도(z) + 2 × 크레이트 적합도 + 0.5 × 아티스트 연결(z)
6. **지역**: 이름 문자(한글·가나 등) → MusicBrainz 국가(백그라운드로 채움) → 소리 추정(확률 0.8 이상일 때만)

## 점검

```powershell
$env:CRATE_ITUNES_XML = "C:\Users\<me>\Music\iTunes\iTunes Music Library.xml"
dotnet run --project src/RecipeSong.Cli -- status
dotnet run --project src/RecipeSong.Cli -- recommend 2026-W43 --region 해외 --type album --top 5
```

캐시와 로그: `%LOCALAPPDATA%\RecipeSong\` (`sound-service.log`는 소리 분석 서비스 로그).

## 한계

- 한국 인디 신보는 Deezer의 아티스트 연결이 빈약해서 후보가 적다. 한국 신보 소스(Spotify `tag:new` 등)가 필요하다.
- 동명이인 아티스트는 Deezer·MusicBrainz 모두에서 섞일 수 있다 (예: 'Dept').
- 미리듣기가 없는 앨범(일본 CD 전용 발매 등)은 소리 판정 없이 '미분류'로 나온다.
- iTunes는 곡별 마지막 재생일만 기록한다. 재생 수는 누적값이다.

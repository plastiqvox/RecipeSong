# RecipeSong

**취향을 디깅하는 달력.** 달력에서 주·월·연을 고르면 그 기간에 나온 앨범·EP·싱글 가운데
내 iTunes 라이브러리와 **소리가 닮은 것**을 골라 준다. 장르 태그나 인기 순위가 아니라
미리듣기를 직접 듣고 내 곡과 비교한다.

![RecipeSong 메인 화면](docs/images/hero.png)

## 이런 점이 다르다

### 내 곡을 기준으로 듣고 고른다

<img src="docs/images/card.png" width="640" alt="추천 카드">

- 내 플레이리스트를 **레시피**(일본 인디 · Jazz · Club · Rock · 해외 인디·얼터 …)로 묶고, 그 소리를 학습한 모델이 발매작을 레시피별로 나눈다.
- 카드마다 **왜 골랐는지**가 보인다: 좋아하는 레이블, 저장·좋아요한 앨범과 닮음, 그리고 **비슷한 내 곡**. 누르면 내 파일로 바로 비교해 들을 수 있다.
- 모델은 내 PC에서 돈다 (CLAP 임베딩 + 분류기, GPU). 라이브러리에 곡이 늘면 새 곡만 분석해 다시 학습한다.

넓은 화면에서 레시피 하나(해외 인디·얼터)만 펼친 모습. 카드마다 추천 이유와, 내 음악 아카이브에 있는 곡 중 소리가 비슷한 곡이 붙는다.

![레시피 하나를 펼친 넓은 화면](docs/images/wide.png)

### 앨범 한 장을 곡 단위로

<img src="docs/images/panel.png" width="360" alt="앨범 창">

- 카드를 열면 오른쪽 창에 수록곡이 나온다. 곡마다 인기도, 30초 미리듣기, **그 곡과 닮은 내 곡**.
- 재생 칸이 앨범 바로 아래 붙어 있어 들으면서 저장 · 좋아요 · 관심 없음 · 팔로우 · 차단을 그 자리에서 한다.
- `Space` 재생/일시정지, `←` `→` 곡, `↑` `↓` 앨범, `Esc` 닫기.

### 달력이 기억한다

<img src="docs/images/calendar.png" width="300" alt="달력">

- 진한 날일수록 내 취향 발매작이 많은 날. 분석해 둔 기간(회색 선), AI가 찾은 날(Claude 주황 · GPT 초록 점), 웹진 소개(보라 점), 팔로우 아티스트 발매(★)가 한눈에 보인다.
- 한 번 계산한 기간은 저장해 두고 바로 연다. 오래된 결과도 먼저 보여 주고 뒤에서 새로 계산한다.
- 지난 해도 그대로 고를 수 있다. 연 보기는 "정밀 분석"으로 월 단위로 쪼개 촘촘하게.

### 더 넓게 찾고 싶을 때

- **AI 커넥터 (선택)**: Claude · GPT API 키를 넣으면 AI가 웹을 검색해 발매작을 더 찾는다. Deezer에서 실제로 있는지 확인한 것만, 같은 소리 판정을 거쳐 카드로 나온다. 하루·주 한도와 사용액 표시.
- **웹진**: Pitchfork · The Quietus · Aquarium Drunkard · Bandcamp Daily · Stereogum 이 소개한 앨범을 6시간마다 가져와 배지로 붙인다.
- **팔로우**: ★를 누른 아티스트의 발매작은 항상 맨 앞에.

## 주요 기능

- **탭**: 추천 발매작 · 내 라이브러리(그 기간에 나온, 내가 가진 앨범) · 저장함 · 관심 없음.
- **필터**: 레시피 칩(끌어서 순서 변경), 형식(앨범·EP·싱글), 지역. 전체 보기는 레시피마다 6장, "전체 N장 보기"로 펼친다.
- **학습 신호**: 저장 · 좋아요(앨범·곡) · 관심 없음 · 차단 사유(소리 때문인지)가 다음 추천에 바로 들어간다. 관심 없음·차단은 10초 안에 되돌릴 수 있다.
- **추천 설정**: 추천 양(기본/많이/최대), 모험 정도(익숙하게/보통/새롭게), 앨범에서 들을 곡(전곡/인기곡 3곡), 아는 아티스트 빼기, 인스트루멘털 버전 빼기, 팔로우·차단 목록.
- **언어**: 한국어 · English.

## 실행

```powershell
dotnet run --project src/RecipeSong.Wpf
```

- Windows 전용 (WPF, .NET 9).
- iTunes 라이브러리(`Music\iTunes\iTunes Music Library.xml`)는 자동으로 찾는다. 없으면 처음에 한 번 묻는다.
- 소리 분석 서비스는 `ml/` 폴더와 `uv`가 있으면 앱이 띄운다. 없으면 아티스트 연결만으로 추천한다.
- 창이 하얗게 나오던 문제 때문에 소프트웨어 렌더링이 기본이다. 하드웨어 렌더링: `CRATE_HW_RENDER=1`.

```
src/
  RecipeSong.Wpf    데스크톱 앱
  RecipeSong.Core   라이브러리 파싱, 후보 수집(Deezer), 지역 판정(MusicBrainz), 추천 점수, AI 커넥터, 소리 서비스 클라이언트
  RecipeSong.Cli    엔진 점검용 콘솔
  RecipeSong.Mcp    Claude Desktop/Code용 MCP 서버 (실험)
ml/
  service.py   소리 분석 서비스 (앱이 자동 실행, GPU)
  embed.py     라이브러리 음원 → CLAP 임베딩 (처음 한 번)
  train.py     레시피(스타일) × 지역 분류기 학습
  crates.json  iTunes 플레이리스트 → 레시피/지역 매핑
```

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

1. **시드**: 레시피 플레이리스트마다 많이 들은 아티스트 (지역 필터를 고르면 그 지역 플레이리스트에서 더 많이)
2. **후보**: Deezer 비슷한 아티스트 2단계 → 상위 아티스트(추천 양에 따라 1,000~3,000명)의 디스코그래피
3. **기간 필터**: 그 기간에 발매된 작품 중 이미 가진 앨범 제외, 아티스트 연결 점수 상위
4. **소리**: 앨범 미리듣기(전곡 최대 20곡 또는 인기곡 3곡) → 레시피 확률, 지역 확률, 내 곡과의 유사도, 곡마다 가장 비슷한 내 곡
5. **점수**: 취향 유사도 + 레시피 적합도 + 아티스트 연결, 저장·좋아요와 닮으면 가산, 관심 없음·차단과 닮으면 감점
6. **지역**: 이름 문자(한글·가나 등) → MusicBrainz 국가 → 커버·소리 추정(확신할 때만)

내 플레이리스트에 있는 뮤지션의 발매작은 점수와 상관없이 항상 나온다.

## 점검

```powershell
$env:CRATE_ITUNES_XML = "C:\Users\<me>\Music\iTunes\iTunes Music Library.xml"
dotnet run --project src/RecipeSong.Cli -- status
dotnet run --project src/RecipeSong.Cli -- recommend 2026-W43 --region 해외 --type album --top 5
```

캐시와 로그: `%LOCALAPPDATA%\RecipeSong\` (`sound-service.log`는 소리 분석 서비스 로그).

## 한계

- 한국 인디 발매작은 Deezer의 아티스트 연결이 빈약해서 후보가 적다.
- 동명이인 아티스트는 Deezer·MusicBrainz 모두에서 섞일 수 있다 (차단 창에서 '이름만 같은 다른 아티스트'로 막는다).
- 미리듣기가 없는 앨범(발매 전, 일본 CD 전용 등)은 소리 판정 없이 나온다.
- iTunes는 곡별 마지막 재생일만 기록한다. 재생 수는 누적값이다.

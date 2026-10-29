"""iTunes Music Library.xml에서 곡과 '직접 만든' 플레이리스트를 읽는다.

곡 키는 iTunes Persistent ID (재생성해도 바뀌지 않음). .NET 쪽도 같은 키로 결과를 읽는다.
"""
from __future__ import annotations

import plistlib
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import unquote

# RecipeSong.Core ITunesLibraryParser.SystemKeys 와 같은 규칙
SYSTEM_KEYS = ("Master", "Distinguished Kind", "Music", "Movies", "TV Shows",
               "Podcasts", "Audiobooks", "Purchased Music", "Books")


@dataclass
class Track:
    pid: str
    title: str
    artist: str
    album: str
    genre: str | None
    play_count: int
    loved: bool
    path: Path | None
    playlists: list[str] = field(default_factory=list)
    rating: int = 0          # 직접 매긴 별점 0~100 (별 하나 = 20). 앨범 별점에서 자동으로 채워진 값은 0
    disliked: bool = False
    skip_count: int = 0
    last_touched: datetime | None = None  # 마지막 재생과 추가 중 늦은 쪽
    added: datetime | None = None

    @property
    def weight(self) -> float:
        """취향 학습 가중치 0.1~1.0: 하트·별 4개 이상은 최대, 낮은 별점·'좋아하지 않음'은 낮게, 그 외는 재생 수·스킵·최근성."""
        return track_weight(self.play_count, self.loved, self.rating, self.disliked, self.skip_count, self.last_touched)

    def taste_weight(self, now: datetime | None = None) -> float:
        """신보와 닮음을 잴 때 이 곡을 얼마나 쳐줄지 0~1. 하트·별 4개 이상은 1.
        한 번도 안 듣고 하트·별점도 없이 석 달 넘게 둔 곡은 뺀다(0): 받아만 두고 듣지 않는 곡이 취향을 흐리지 않게.
        최근에 넣은 곡은 아직 들을 틈이 없었을 수 있어 낮게만 친다. 나머지는 가중치를 넓게 펴서(0.3~1) 많이 들은 곡이 더 크게."""
        if self.loved or self.rating >= 80:
            return 1.0
        if self.play_count == 0 and self.rating == 0 and self.added is not None \
                and ((now or datetime.now(timezone.utc)) - self.added).days > UNPLAYED_DAYS:
            return 0.0
        return round(0.3 + 0.7 * self.weight, 3)


UNPLAYED_DAYS = 90  # 안 들은 곡을 닮음 비교에서 빼기까지 (넣은 날부터)
PLAYS_FULL = 12  # 이만큼 들으면 재생 수만으로 최대 (라이브러리 상위 5% 수준)


def track_weight(plays: int, loved: bool, rating: int, disliked: bool, skips: int = 0,
                 last_touched: datetime | None = None, now: datetime | None = None) -> float:
    """.NET TrackRecord.Weight 와 같은 식.
    스킵: 넘긴 비율만큼 최대 절반까지 낮춘다 (2번 이상 넘겼을 때). 최근성: 반년이 지나면 3년에 걸쳐 0.7배까지.
    하트·별 4개 이상은 언제 들었든 최대."""
    import math
    if disliked:
        return 0.1
    if loved or rating >= 80:
        return 1.0
    w = 0.35 + 0.65 * min(1.0, math.log1p(plays) / math.log1p(PLAYS_FULL))
    if rating >= 60:
        w = max(w, 0.8)
    elif 0 < rating <= 40:
        w = min(w, 0.4)
    if skips >= 2:
        w *= 1 - 0.5 * skips / (plays + skips)
    if last_touched is not None:
        age = ((now or datetime.now(timezone.utc)) - last_touched).days
        if age > 180:
            w *= 1 - 0.3 * min(1.0, (age - 180) / (3 * 365))
    return round(max(w, 0.1), 3)


def _latest(*dates) -> datetime | None:
    ds = [d if d.tzinfo else d.replace(tzinfo=timezone.utc) for d in dates if isinstance(d, datetime)]
    return max(ds) if ds else None


def location_to_path(url: str | None) -> Path | None:
    if not url or not url.startswith("file://"):
        return None
    p = url[len("file://localhost/"):] if url.startswith("file://localhost/") else url[len("file:///"):]
    return Path(unquote(p))


def load(xml_path: str | Path, min_playlist_size: int = 15) -> tuple[dict[str, Track], dict[str, list[str]]]:
    with open(xml_path, "rb") as f:
        lib = plistlib.load(f)

    tracks: dict[str, Track] = {}
    by_id: dict[int, str] = {}
    for t in lib.get("Tracks", {}).values():
        if t.get("Podcast") or t.get("Movie") or t.get("TV Show") or "video" in t.get("Kind", "").lower():
            continue
        pid = t.get("Persistent ID")
        if not pid or "Name" not in t:
            continue
        tracks[pid] = Track(
            pid=pid,
            title=t["Name"],
            artist=t.get("Album Artist") or t.get("Artist") or "Unknown Artist",
            album=t.get("Album", ""),
            genre=t.get("Genre"),
            play_count=t.get("Play Count", 0),
            loved=bool(t.get("Loved") or t.get("Favorited")),
            path=location_to_path(t.get("Location")),
            rating=0 if t.get("Rating Computed") else int(t.get("Rating", 0)),
            disliked=bool(t.get("Disliked")),
            skip_count=int(t.get("Skip Count", 0)),
            last_touched=_latest(t.get("Play Date UTC"), t.get("Date Added")),
            added=_latest(t.get("Date Added")),
        )
        by_id[t["Track ID"]] = pid

    playlists: dict[str, list[str]] = {}
    for p in lib.get("Playlists", []):
        if any(k in p for k in SYSTEM_KEYS) or p.get("Visible") is False or p.get("Folder") \
                or "Smart Info" in p or "Smart Criteria" in p:
            continue
        pids = [by_id[i["Track ID"]] for i in p.get("Playlist Items", []) if i.get("Track ID") in by_id]
        pids = list(dict.fromkeys(pids))
        if len(pids) >= min_playlist_size:
            playlists[p["Name"]] = pids
            for pid in pids:
                tracks[pid].playlists.append(p["Name"])

    return tracks, playlists

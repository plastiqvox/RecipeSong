"""iTunes Music Library.xml에서 곡과 '직접 만든' 플레이리스트를 읽는다.

곡 키는 iTunes Persistent ID (재생성해도 바뀌지 않음). .NET 쪽도 같은 키로 결과를 읽는다.
"""
from __future__ import annotations

import plistlib
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import unquote

# Crate.Core ITunesLibraryParser.SystemKeys 와 같은 규칙
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

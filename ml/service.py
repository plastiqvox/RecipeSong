"""RecipeSong 소리 분석 서비스. WPF 앱이 실행해 두고 표준 입출력으로 JSON을 한 줄씩 주고받는다.

요청:  {"id": 1, "cmd": "status" | "score_albums" | "update", "args": {...}}
응답:  {"id": 1, "ok": true, "result": {...}}  /  {"id": 1, "ok": false, "error": "..."}
진행:  {"id": 1, "event": "progress", "message": "...", "done": 3, "total": 10}

stdout은 프로토콜 전용이다. 로그는 모두 stderr로 보낸다.

    uv run python service.py --xml "C:/Users/me/Music/iTunes/iTunes Music Library.xml"
"""
from __future__ import annotations

import argparse
import io
import json
import re
import os
import sys
import threading
import time
import traceback
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
os.environ.setdefault("TRANSFORMERS_VERBOSITY", "error")

import av  # noqa: E402
import numpy as np  # noqa: E402
import requests  # noqa: E402
import torch  # noqa: E402

import covers  # noqa: E402
import effnet  # noqa: E402
import embed  # noqa: E402
import library  # noqa: E402
import train  # noqa: E402

PROTO = sys.stdout
sys.stdout = sys.stderr  # 라이브러리들이 print 해도 프로토콜이 깨지지 않게
PROTO.reconfigure(encoding="utf-8", line_buffering=True)

DATA = Path(__file__).parent / "data"
ALBUM_CACHE = DATA / "album_emb.npz"  # CLAP. EffNet은 album_emb_discogs_effnet.npz
ALBUM_INFO = DATA / "album_info.json"  # 레이블·장르 (바뀌지 않으므로 영구 캐시)
ALBUM_COVER = DATA / "album_cover.npz"
TRACK_STYLES = DATA / "track_styles.npz"  # 신보 곡마다 Discogs 스타일 400개 확률 (EffNet 장르판, 장르 태그용)
TRACK_EFFNET = DATA / "track_effnet.npz"  # 신보 곡마다 EffNet 장르판 임베딩 (레시피 보조)
ALBUM_EFFNET = DATA / "album_effnet.npz"  # 앨범 EffNet 임베딩 (곡 평균)
GENRE_MIN = 0.04  # 태그: 가장 높은 스타일의 40% 이상이면서 이 확률 이상 (최대 3개). 400개에 확률이 나뉘어 값이 낮다  # 신보 커버 CLIP 임베딩 (앨범 id)
FAILED = DATA / "failed_tracks.json"  # 디코딩 실패한 곡 → '새 곡'으로 다시 세지 않음
KNN = 20
MAX_TRACKS = 20      # 전곡 모드에서 미리듣기를 들을 최대 곡 수
HIT_TRACKS = 3       # 인기곡 모드: Deezer 인기(rank) 상위 곡 수
ACCEPT_MARGIN = 0.02  # 재학습 모델이 이보다 더 나빠지면 이전 모델 유지


def send(obj: dict) -> None:
    PROTO.write(json.dumps(obj, ensure_ascii=False) + "\n")


def log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


def softmax(z: np.ndarray) -> np.ndarray:
    e = np.exp(z - z.max())
    return e / e.sum()


class Service:
    def __init__(self, xml: str):
        self.xml = xml
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self._clap = None
        self._effnet = None
        self.track_mode = "all"  # 앨범에서 들을 곡: "all" 전곡 / "top" 인기곡 (score_albums 요청마다 앱 설정을 따른다)
        self._covers = None
        self._genre = None
        self.track_styles = load_npz(TRACK_STYLES)
        self.track_effnet = load_npz(TRACK_EFFNET)
        self.album_effnet = load_npz(ALBUM_EFFNET)
        self.cover_cache: dict[str, np.ndarray] = {}
        if ALBUM_COVER.exists():
            d = np.load(ALBUM_COVER, allow_pickle=False)
            self.cover_cache = dict(zip(d["keys"].tolist(), d["emb"].astype(np.float32)))
        self._cache_file: Path | None = None
        self._http = requests.Session()
        self._http.mount("https://", requests.adapters.HTTPAdapter(pool_connections=32, pool_maxsize=32))
        self._dz_lock = threading.Lock()
        self._dz_next = 0.0
        self.album_cache: dict[str, np.ndarray] = {}
        self.album_info: dict[str, dict] = json.loads(ALBUM_INFO.read_text(encoding="utf-8")) if ALBUM_INFO.exists() else {}
        # 저장함(즐겨찾기) 앨범: 신보가 이 앨범들과 얼마나 닮았는지도 본다
        self.fav_keys: list[str] = []
        self.fav_names: dict[str, str] = {}
        self.fav_x: np.ndarray | None = None
        # 별로 표시한 앨범: 이와 닮은 신보는 점수를 깎는다
        self.dis_keys: list[str] = []
        self.dis_x: np.ndarray | None = None
        self.reload()

    # ---- 상태 -------------------------------------------------------------
    def reload(self) -> None:
        self.tracks, self.playlists = library.load(self.xml)
        self.model = json.loads(train.MODEL_PATH.read_text(encoding="utf-8"))
        # 소리 모델은 학습한 임베딩 폴더를 따른다: emb_discogs_effnet → EffNet (16kHz), 그 밖 → CLAP (48kHz)
        self.out = train.DATA / self.model["embeddings"]
        self.is_effnet = self.model["embeddings"] == f"emb_{effnet.NAME}"
        self.sr = effnet.SR if self.is_effnet else embed.SR
        cache_file = DATA / f"album_emb_{effnet.NAME}.npz" if self.is_effnet else ALBUM_CACHE
        if cache_file != self._cache_file:  # 모델이 바뀌면 앨범 소리 캐시도 그 모델 것으로
            self._cache_file = cache_file
            self.album_cache = {}
            if cache_file.exists():
                d = np.load(cache_file, allow_pickle=False)
                self.album_cache = dict(zip(d["keys"].tolist(), d["emb"].astype(np.float32)))
        self.mean = np.array(self.model["mean"], np.float32)
        keys, emb = train.load_embeddings(self.model["embeddings"])
        self.keys = keys
        self.keyset = set(keys)
        self.lib = train.preprocess(emb, self.mean).astype(np.float32)
        self.meta = [(self.tracks[k].artist, self.tracks[k].title) if k in self.tracks else ("?", "?") for k in keys]
        # 곡 가중치: 취향 유사도에서 많이 듣고 좋아한 곡과 닮을수록 더 쳐준다 (0.6~1.0배)
        self.lib_w = np.array([0.6 + 0.4 * self.tracks[k].weight if k in self.tracks else 0.8 for k in keys], np.float32)
        self.heads = {name: (np.array(self.model[name]["coef"], np.float32),
                             np.array(self.model[name]["intercept"], np.float32),
                             self.model[name]["classes"]) for name in ("style", "region")}
        # 소리 + 커버 지역 판정 (커버를 아는 신보만)
        # 레시피: 소리 + 장르(EffNet) — EffNet 임베딩을 아는 앨범만
        se = self.model.get("style_effnet")
        self.style_effnet = (np.array(se["coef"], np.float32), np.array(se["intercept"], np.float32), se["classes"],
                             np.array(se["effnet_mean"], np.float32), float(se["effnet_scale"])) if se else None
        rc = self.model.get("region_cover")
        self.region_cover = (np.array(rc["coef"], np.float32), np.array(rc["intercept"], np.float32), rc["classes"],
                             np.array(rc["cover_mean"], np.float32), float(rc["cover_scale"])) if rc else None
        self.xml_mtime = os.path.getmtime(self.xml)
        log(f"[service] 라이브러리 {len(self.tracks)}곡, 임베딩 {len(keys)}곡 ({self.model['embeddings']}), 모델 {self.model['trained_at']}")

    def failed(self) -> set[str]:
        return set(json.loads(FAILED.read_text(encoding="utf-8"))) if FAILED.exists() else set()

    def new_tracks(self, include_failed: bool = False) -> list:
        skip = self.keyset if include_failed else self.keyset | self.failed()
        return [t for t in self.tracks.values() if t.pid not in skip and t.path and t.path.exists()]

    def status(self, _args: dict, _rid) -> dict:
        if os.path.getmtime(self.xml) != self.xml_mtime:
            self.tracks, self.playlists = library.load(self.xml)
            self.xml_mtime = os.path.getmtime(self.xml)
        return {
            "device": self.device,
            "sound_model": "Discogs-EffNet" if self.is_effnet else "CLAP",
            "gpu": torch.cuda.get_device_name(0) if self.device == "cuda" else None,
            "trained_at": self.model["trained_at"],
            "tracks": self.model["tracks"],
            "embedded": len(self.keys),
            "new_tracks": len(self.new_tracks()),
            "styles": self.model["style"]["classes"],
            "style_accuracy": self.model["style"]["cv_accuracy"],
            "regions": self.model["region"]["classes"],
            "region_accuracy": self.model["region"]["cv_accuracy"],
            "region_cover_accuracy": (self.model.get("region_cover") or {}).get("cv_accuracy"),
        }

    # ---- 임베딩 -----------------------------------------------------------
    def clap(self):
        if self._clap is None:
            from transformers import ClapModel, ClapProcessor
            model = ClapModel.from_pretrained(embed.MODEL_ID).to(self.device).eval()
            if self.device == "cuda":
                model = model.half()
            self._clap = (model, ClapProcessor.from_pretrained(embed.MODEL_ID))
        return self._clap

    def embed_clips(self, groups: list[list[np.ndarray]]) -> list[np.ndarray | None]:
        """곡(앨범)마다 클립 묶음 → 평균 임베딩 (L2 정규화)."""
        if self.is_effnet:
            if self._effnet is None:
                self._effnet = effnet.EffNet()
            return self._effnet.embed_groups(groups)
        model, proc = self.clap()
        flat, owner = [], []
        for i, clips in enumerate(groups):
            flat += clips
            owner += [i] * len(clips)
        out: list[np.ndarray | None] = [None] * len(groups)
        if not flat:
            return out
        feats = []
        for s in range(0, len(flat), 72):
            inp = proc(audios=flat[s:s + 72], sampling_rate=embed.SR, return_tensors="pt").to(self.device)
            if self.device == "cuda":
                inp = {k: (v.half() if v.is_floating_point() else v) for k, v in inp.items()}
            with torch.no_grad():
                f = embed.features(model, inp).float()
            feats.append(torch.nn.functional.normalize(f, dim=-1).cpu().numpy())
        f = np.concatenate(feats)
        owner_arr = np.array(owner)
        for i in set(owner):
            e = f[owner_arr == i].mean(0)
            out[i] = e / (np.linalg.norm(e) + 1e-8)
        return out

    def genre(self):
        """장르 태그용 EffNet 장르판 (모델 파일이 없으면 None — 태그만 빠진다)"""
        if self._genre is None:
            try:
                self._genre = effnet.EffNet("discogs_effnet")
            except Exception:
                log(traceback.format_exc())
                self._genre = False
        return self._genre or None

    def covers(self) -> covers.CoverModel:
        if self._covers is None:
            self._covers = covers.CoverModel(self.device)
        return self._covers

    def ensure_covers(self, albums: list) -> None:
        """신보 커버(C#이 보낸 Deezer 주소)를 받아 CLIP으로. 지역 판정에만 쓴다."""
        if self.region_cover is None:
            return
        todo = [(str(a["id"]), a["cover"]) for a in albums
                if a.get("cover") and str(a["id"]) not in self.cover_cache and not str(a["id"]).startswith("t")]
        if not todo:
            return

        def get(url: str):
            try:
                return covers.open_image(self._http.get(url, timeout=20).content)
            except requests.RequestException:
                return None
        with ThreadPoolExecutor(16) as pool:
            imgs = list(pool.map(lambda kv: get(kv[1]), todo))
        ok = [(k, im) for (k, _), im in zip(todo, imgs) if im is not None]
        if not ok:
            return
        for (k, _), e in zip(ok, self.covers().embed([im for _, im in ok])):
            self.cover_cache[k] = e
        ks = list(self.cover_cache)
        np.savez(ALBUM_COVER, keys=np.array(ks), emb=np.stack([self.cover_cache[k] for k in ks]).astype(np.float16))

    def deezer(self, path: str) -> dict:
        """Deezer API (5초당 50회 제한 → 요청 간격 110ms 이상)."""
        for attempt in range(3):
            with self._dz_lock:
                wait = self._dz_next - time.time()
                self._dz_next = max(time.time(), self._dz_next) + 0.11
            if wait > 0:
                time.sleep(wait)
            try:
                r = self._http.get("https://api.deezer.com" + path, timeout=20).json()
            except (requests.RequestException, ValueError):
                continue
            if "error" in r:  # 할당량 초과 등
                time.sleep(2 ** attempt)
                continue
            return r
        return {}

    def album_detail(self, album_id: str) -> list[tuple[int, str, str]]:
        """앨범 상세 한 번으로 레이블·장르(캐시)와 미리듣기 곡 (곡 id, 제목, URL — URL은 만료되므로 매번 새로).
        전곡 모드면 수록 순서대로 최대 20곡, 인기곡 모드면 Deezer 인기 상위 3곡."""
        r = self.deezer(f"/album/{album_id}")
        genres = (r.get("genres") or {}).get("data") or []
        if r:
            old = self.album_info.get(album_id, {})
            self.album_info[album_id] = {**old, "label": r.get("label"), "genre": genres[0]["name"] if genres else None,
                                         "with": collaborators(r)}
        tracks = [t for t in (r.get("tracks") or {}).get("data", []) if t.get("preview")]
        if self.track_mode == "top":
            tracks = sorted(tracks, key=lambda t: -(t.get("rank") or 0))[:HIT_TRACKS]
        return [(t["id"], t.get("title", ""), t["preview"]) for t in tracks[:MAX_TRACKS]]

    def preview_tracks(self, album_id: str) -> list[tuple[int | None, str | None, list[np.ndarray] | None, list[np.ndarray] | None]]:
        """미리듣기를 곡별 클립으로: [(곡 id, 제목, 클립)]. "t곡id" 는 곡 하나 (하트를 누른 곡).
        이미 들은 곡(곡 임베딩 캐시)은 받지 않고 클립 자리에 None — 전곡·인기곡을 오가도 다시 받지 않는다."""
        if album_id.startswith("t"):
            preview = self.deezer(f"/track/{album_id[1:]}").get("preview")
            items = [(None, None, preview)] if preview else []
        else:
            items = self.album_detail(album_id)
        out = []
        for tid, title, url in items:
            if tid is not None and all(f"t{tid}" in c for c in (self.album_cache, self.track_styles, self.track_effnet)):
                out.append((tid, title, None, None))
                continue
            try:
                data = self._http.get(url, timeout=20).content
            except requests.RequestException:
                continue
            clips = decode_preview(data, self.sr)
            if clips:
                # 장르 태그용 16kHz (EffNet). 곡 하트("t곡id" 앨범 키)는 태그가 필요 없다
                out.append((tid, title, clips, decode_preview(data, effnet.SR) if tid is not None and self.genre() else None))
        return out

    def nearest(self, e: np.ndarray) -> tuple[str, float]:
        """곡(앨범) 임베딩 하나 → 가장 닮은 내 곡 ("아티스트 – 곡명", 곡 가중치를 곱한 유사도)"""
        x = train.preprocess(e[None, :], self.mean)[0]
        sims = (self.lib @ x) * self.lib_w
        j = int(np.argmax(sims))
        return f"{self.meta[j][0]} – {self.meta[j][1]}", round(float(sims[j]), 4)

    def analyze_albums(self, ids: list[str], pool: ThreadPoolExecutor) -> None:
        """수록곡 미리듣기를 곡별로 들어 곡 임베딩("t곡id")·앨범 임베딩(곡 평균)·곡 짝(이 앨범 곡 ≈ 내 곡)을 만든다.
        곡 짝은 수록곡 목록과 같은 규칙: 가장 닮은 곡과 0.03 이내, 최대 3곡, 내 곡 중복 없이."""
        per = list(pool.map(self.preview_tracks, ids))
        flat = [(i, tid, title) for i, tr in enumerate(per) for tid, title, c, _ in tr if c is not None]
        embs = self.embed_clips([c for tr in per for _, _, c, _ in tr if c is not None])
        for (i, tid, title), e in zip(flat, embs):
            if e is not None and tid is not None:
                self.album_cache[f"t{tid}"] = e
        # 장르: 곡마다 스타일 확률 (CPU)
        g16 = [(tid, c16) for tr in per for tid, _, _, c16 in tr if c16]
        if g16 and self.genre():
            for (tid, _), (st, ef) in zip(g16, self.genre().styles_and_embeddings([c for _, c in g16])):
                if st is not None:
                    self.track_styles[f"t{tid}"] = st
                    self.track_effnet[f"t{tid}"] = ef
        by_album: dict[int, list] = {}
        new = iter(embs)
        for i, tr in enumerate(per):
            for tid, title, c, _ in tr:
                e = next(new) if c is not None else self.album_cache.get(f"t{tid}")
                if e is not None:
                    by_album.setdefault(i, []).append((tid, title, e))
        for i, aid in enumerate(ids):
            items = by_album.get(i)
            if not items:
                continue
            m = np.mean([e for *_, e in items], axis=0)
            self.album_cache[aid] = (m / (np.linalg.norm(m) + 1e-8)).astype(np.float32)
            if items[0][0] is not None:
                pairs = sorted(({"track": title, "near": near, "sim": sim} for tid, title, e in items for near, sim in [self.nearest(e)]),
                               key=lambda x: -x["sim"])
                top, seen = [], set()
                for p in pairs:
                    if p["sim"] < pairs[0]["sim"] - 0.03 or len(top) == 3:
                        break
                    if p["near"] not in seen:
                        seen.add(p["near"])
                        top.append(p)
                info = self.album_info.setdefault(aid, {})
                info["pairs"] = top
                info["tracks_mode"] = self.track_mode
                info["genres"] = self.album_genres([tid for tid, *_ in items])
                efs = [self.track_effnet[f"t{tid}"] for tid, *_ in items if f"t{tid}" in self.track_effnet]
                if efs:
                    m = np.mean(efs, axis=0)
                    self.album_effnet[aid] = (m / (np.linalg.norm(m) + 1e-8)).astype(np.float32)
                info["effnet"] = bool(efs)

    def album_genres(self, track_ids: list) -> list[str]:
        """곡마다의 스타일 확률 평균 → 상위 3개 중 가장 높은 것의 40% 이상·GENRE_MIN 이상 ("Jazz-Funk" — Discogs "Jazz---Jazz-Funk" 의 뒤쪽)"""
        sts = [self.track_styles[f"t{t}"] for t in track_ids if f"t{t}" in self.track_styles]
        g = self.genre()
        if not sts or not g or not g.classes:
            return []
        p = np.mean(sts, axis=0)
        top = np.argsort(-p)[:3]
        floor = max(GENRE_MIN, 0.4 * float(p[top[0]]))
        return [g.classes[i].split("---")[-1] for i in top if p[i] >= floor]

    def save_track_styles(self) -> None:
        save_npz(TRACK_STYLES, self.track_styles)
        save_npz(TRACK_EFFNET, self.track_effnet)
        save_npz(ALBUM_EFFNET, self.album_effnet)

    # ---- 신보 점수 --------------------------------------------------------
    def ensure_embedded(self, albums: list, rid, what: str = "미리듣기 소리 분석 중") -> None:
        """아직 소리를 모르는 앨범, 또는 다른 모드(전곡·인기곡, 예전의 앞 3곡)로 들은 앨범만 다시 임베딩한다 (album_emb.npz 캐시)."""
        def stale(k: str) -> bool:
            info = self.album_info.get(k, {})
            return k not in self.album_cache or (not k.startswith("t") and (info.get("tracks_mode") != self.track_mode
                                                                             or ("effnet" not in info and self.genre() is not None)))
        todo = [a for a in albums if stale(str(a["id"]))]
        done = 0
        with ThreadPoolExecutor(16) as pool:
            # 이미 소리를 아는 앨범도 레이블·장르가 없으면 채운다
            missing_info = [str(a["id"]) for a in albums if str(a["id"]) in self.album_cache and str(a["id"]) not in self.album_info
                            and not str(a["id"]).startswith("t")]
            list(pool.map(self.album_detail, missing_info))
            for s in range(0, len(todo), 32):
                batch = todo[s:s + 32]
                self.analyze_albums([str(a["id"]) for a in batch], pool)
                done += len(batch)
                send({"id": rid, "event": "progress", "message": f"{what} {done}/{len(todo)}",
                      "done": done, "total": len(todo)})
        if todo:
            self.save_album_cache()
            self.save_track_styles()
        if todo or missing_info:
            ALBUM_INFO.write_text(json.dumps(self.album_info, ensure_ascii=False), encoding="utf-8")

    def score_albums(self, args: dict, rid) -> dict:
        albums = args["albums"]
        self.track_mode = "top" if args.get("tracks") == "top" else "all"
        self.ensure_embedded(albums, rid)
        try:
            self.ensure_covers(albums)
        except Exception:  # 커버는 보조라 실패해도 소리 점수는 낸다
            log(traceback.format_exc())
        results = []
        for a in albums:
            key = str(a["id"])
            info = self.album_info.get(key, {})
            e = self.album_cache.get(key)
            if e is None:
                results.append({"id": a["id"], "ok": False, **info})
                continue
            results.append({"id": a["id"], "ok": True, **info, **self.describe(e, key)})
        if self.fav_x is not None:
            log(f"[service] 점수 {len(results)}장 (저장함 {len(self.fav_keys)}장과 비교)")
        return {"albums": results}

    # ---- 즐겨찾기 (저장함) --------------------------------------------------
    def favorites(self, args: dict, rid) -> dict:
        """좋아요 목록을 받는다: [{id, name}] — 앨범 하트는 앨범 id, 곡 하트는 "t곡id". 소리를 모르면 미리듣기로 분석한다."""
        items = args.get("albums") or []
        self.ensure_embedded(items, rid, "저장한 앨범 소리 분석 중")
        self.fav_names = {str(a["id"]): a.get("name", "") for a in items}
        self.fav_keys = [str(a["id"]) for a in items if str(a["id"]) in self.album_cache]
        self.fav_x = self.stack(self.fav_keys)
        dislikes = args.get("dislikes") or []
        if dislikes:
            self.ensure_embedded(dislikes, rid, "별로 표시한 앨범 소리 분석 중")
        self.dis_keys = [str(a["id"]) for a in dislikes if str(a["id"]) in self.album_cache]
        self.dis_x = self.stack(self.dis_keys)
        log(f"[service] 좋아요 {len(self.fav_keys)}/{len(items)}개, 별로 {len(self.dis_keys)}/{len(dislikes)}장 반영")
        return {"count": len(self.fav_keys), "dislikes": len(self.dis_keys)}

    # ---- 카드의 곡 짝 (이 앨범 곡 ≈ 내 곡) — 짝 기능 전에 분석한 앨범을 뒤에서 채운다 ----
    def album_pairs(self, args: dict, rid) -> dict:
        ids = [str(i) for i in args.get("ids") or []]
        need = [i for i in ids if "pairs" not in self.album_info.get(i, {})]
        if need:
            with ThreadPoolExecutor(16) as pool:
                self.analyze_albums(need, pool)
            self.save_album_cache()
            ALBUM_INFO.write_text(json.dumps(self.album_info, ensure_ascii=False), encoding="utf-8")
        return {"albums": [{"id": int(i), "pairs": self.album_info.get(i, {}).get("pairs", [])} for i in ids]}

    # ---- 수록곡마다 비슷한 내 곡 (수록곡을 펼쳤을 때) ----------------------
    def track_near(self, args: dict, rid) -> dict:
        """[{id, preview}] → 곡마다 미리듣기를 들어 가장 닮은 내 곡 하나. 곡 임베딩은 "t곡id" 로 앨범 캐시에 같이 둔다 (곡 하트와 같은 키)."""
        items = [(f"t{t['id']}", t.get("preview")) for t in args.get("tracks") or []]
        todo = [(k, url) for k, url in items if url and k not in self.album_cache]

        def clips(url: str) -> list[np.ndarray]:
            try:
                return decode_preview(self._http.get(url, timeout=20).content, self.sr)
            except requests.RequestException:
                return []
        if todo:
            with ThreadPoolExecutor(8) as pool:
                groups = list(pool.map(lambda kv: clips(kv[1]), todo))
            for (k, _), e in zip(todo, self.embed_clips(groups)):
                if e is not None:
                    self.album_cache[k] = e
            self.save_album_cache()
        out = []
        for k, _ in items:
            if (e := self.album_cache.get(k)) is not None:
                near, sim = self.nearest(e)
                out.append({"id": int(k[1:]), "near": near, "sim": sim})
        return {"tracks": out}

    def stack(self, keys: list[str]) -> np.ndarray | None:
        return (train.preprocess(np.stack([self.album_cache[k] for k in keys]).astype(np.float32), self.mean).astype(np.float32)
                if keys else None)

    def describe(self, e: np.ndarray, key: str | None = None) -> dict:
        x = train.preprocess(e[None, :], self.mean)[0]
        res = {}
        for name, (coef, icpt, classes) in self.heads.items():
            res[name] = {c: round(float(p), 4) for c, p in zip(classes, softmax(coef @ x + icpt))}
        if self.style_effnet is not None and key is not None and (ef := self.album_effnet.get(key)) is not None:
            coef, icpt, classes, emean, scale = self.style_effnet
            xs = np.concatenate([x, scale * train.preprocess(ef[None, :], emean)[0]])
            res["style"] = {c: round(float(p), 4) for c, p in zip(classes, softmax(coef @ xs + icpt))}
        if self.region_cover is not None and key is not None and (cv := self.cover_cache.get(key)) is not None:
            coef, icpt, classes, cmean, scale = self.region_cover
            xc = np.concatenate([x, scale * train.preprocess(cv[None, :], cmean)[0]])
            res["region"] = {c: round(float(p), 4) for c, p in zip(classes, softmax(coef @ xc + icpt))}
        sims = (self.lib @ x) * self.lib_w
        near = np.argpartition(-sims, KNN)[:KNN]
        near = near[np.argsort(-sims[near])]
        res["taste"] = round(float(sims[near].mean()), 4)
        res["near"] = [f"{self.meta[j][0]} – {self.meta[j][1]}" for j in near[:3]]
        # 저장한 앨범과 닮은 정도: 가장 닮은 3장 평균 (자기 자신은 뺀다)
        if self.fav_x is not None:
            fs = self.fav_x @ x
            idx = [i for i in np.argsort(-fs) if self.fav_keys[i] != key][:3]
            if idx:
                res["fav"] = round(float(fs[idx].mean()), 4)
                res["fav_near"] = self.fav_names.get(self.fav_keys[idx[0]], "")
        # 별로 표시한 앨범과 닮은 정도
        if self.dis_x is not None:
            ds = self.dis_x @ x
            idx = [i for i in np.argsort(-ds) if self.dis_keys[i] != key][:3]
            if idx:
                res["dis"] = round(float(ds[idx].mean()), 4)
        return res

    def save_album_cache(self) -> None:
        if self.album_cache:
            ks = list(self.album_cache)
            np.savez(self._cache_file, keys=np.array(ks), emb=np.stack([self.album_cache[k] for k in ks]).astype(np.float16))

    # ---- 자동 재학습 ------------------------------------------------------
    def update(self, args: dict, rid) -> dict:
        self.tracks, self.playlists = library.load(self.xml)
        todo = self.new_tracks()
        keys, embs = [], []
        with ThreadPoolExecutor(10) as pool:
            for s in range(0, len(todo), 24):
                batch = todo[s:s + 24]
                clips = list(pool.map(lambda t: embed.decode_clips(t.path, self.sr) or [], batch))
                for t, e in zip(batch, self.embed_clips(clips)):
                    if e is not None:
                        keys.append(t.pid)
                        embs.append(e)
                send({"id": rid, "event": "progress", "message": f"새 곡 분석 중 {min(s + 24, len(todo))}/{len(todo)}",
                      "done": min(s + 24, len(todo)), "total": len(todo)})
        if keys:
            embed.save_shard(self.out, keys, embs)
        failed = self.failed() | ({t.pid for t in todo} - set(keys))
        FAILED.write_text(json.dumps(sorted(failed)), encoding="utf-8")

        try:  # 새 앨범 커버도 (지역 판정용)
            covers.update_library(self.tracks, self.covers, log=log)
        except Exception:
            log(traceback.format_exc())
        send({"id": rid, "event": "progress", "message": "재학습 중…", "done": 0, "total": 0})
        old = self.model
        new = train.run(self.xml, self.model["embeddings"], log=log)
        accepted = (new["style"]["cv_accuracy"] >= old["style"]["cv_accuracy"] - ACCEPT_MARGIN
                    and new["region"]["cv_accuracy"] >= old["region"]["cv_accuracy"] - ACCEPT_MARGIN)
        if accepted:
            train.save(new)
        self.reload()
        return {"added": len(keys), "accepted": accepted,
                "old_style_accuracy": old["style"]["cv_accuracy"], "new_style_accuracy": new["style"]["cv_accuracy"]}


FEAT = re.compile(r"[\(\[](?:feat\.?|ft\.?|featuring|with)\s+([^\)\]]+)[\)\]]", re.IGNORECASE)


def collaborators(album: dict) -> list[str]:
    """수록곡에 참여한 다른 아티스트: 곡 아티스트가 앨범 아티스트와 다르거나, 제목의 (feat. X)."""
    main = ((album.get("artist") or {}).get("name") or "").lower()
    names: list[str] = []
    for t in (album.get("tracks") or {}).get("data", []):
        a = (t.get("artist") or {}).get("name") or ""
        if a and a.lower() != main:
            names.append(a)
        for m in FEAT.findall(t.get("title") or ""):
            names += [x.strip() for x in re.split(r",|&| and | x ", m) if x.strip()]
    return list(dict.fromkeys(names))[:8]


def load_npz(path: Path) -> dict[str, np.ndarray]:
    if not path.exists():
        return {}
    d = np.load(path, allow_pickle=False)
    return dict(zip(d["keys"].tolist(), d["emb"].astype(np.float32)))


def save_npz(path: Path, cache: dict[str, np.ndarray]) -> None:
    if cache:
        ks = list(cache)
        np.savez(path, keys=np.array(ks), emb=np.stack([cache[k] for k in ks]).astype(np.float16))


def decode_preview(data: bytes, sr: int = embed.SR) -> list[np.ndarray]:
    """30초 미리듣기 MP3 → 모노 (소리 모델의 샘플레이트), 앞/중간/뒤 10초 구간."""
    try:
        with av.open(io.BytesIO(data)) as c:
            rs = av.AudioResampler(format="flt", layout="mono", rate=sr)
            buf = [o.to_ndarray()[0] for f in c.decode(c.streams.audio[0]) for o in rs.resample(f)]
    except Exception:
        return []
    if not buf:
        return []
    x = np.concatenate(buf).astype(np.float32)
    peak = float(np.abs(x).max())
    if peak > 1:
        x /= peak
    n = sr * embed.CLIP_SEC
    if len(x) < n:
        return [x] if len(x) > sr else []
    return [x[s:s + n] for s in np.linspace(0, len(x) - n, 3).astype(int)]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", required=True)
    args = ap.parse_args()

    t0 = time.time()
    svc = Service(args.xml)
    send({"event": "ready", "seconds": round(time.time() - t0, 1)})
    handlers = {"status": svc.status, "score_albums": svc.score_albums, "update": svc.update, "favorites": svc.favorites,
                "track_near": svc.track_near, "album_pairs": svc.album_pairs}

    for line in sys.stdin:
        line = line.strip().lstrip("﻿")
        if not line:
            continue
        rid = None
        try:
            req = json.loads(line)
            rid = req.get("id")
            if req.get("cmd") == "quit":
                break
            result = handlers[req["cmd"]](req.get("args") or {}, rid)
            send({"id": rid, "ok": True, "result": result})
        except Exception as ex:  # 한 요청이 실패해도 서비스는 계속
            log(traceback.format_exc())
            send({"id": rid, "ok": False, "error": f"{type(ex).__name__}: {ex}"})


if __name__ == "__main__":
    main()

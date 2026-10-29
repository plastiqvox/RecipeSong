"""RecipeSong 소리 분석 서비스. WPF 앱이 실행해 두고 표준 입출력으로 JSON을 한 줄씩 주고받는다.

요청:  {"id": 1, "cmd": "status" | "score_albums" | "update" | "genre_neighbors" | ..., "args": {...}}
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
FAILED = DATA / "failed_tracks.json"  # 디코딩 실패한 곡 → '새 곡'으로 다시 세지 않음
KNN = 20
MAX_TRACKS = 20      # 전곡 모드에서 미리듣기를 들을 최대 곡 수
HIT_TRACKS = 3       # 인기곡 모드: Deezer 인기(rank) 상위 곡 수
GROUP_KNN = 15  # 장르를 모르는 앨범: 장르를 아는 앨범 중 소리가 가장 닮은 이만큼의 칸을 본다


def send(obj: dict) -> None:
    PROTO.write(json.dumps(obj, ensure_ascii=False) + "\n")


def log(msg: str) -> None:
    print(msg, file=sys.stderr, flush=True)


class Service:
    def __init__(self, xml: str):
        self.xml = xml
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        self._clap = None
        self._effnet = None
        self.track_mode = "all"  # 앨범에서 들을 곡: "all" 전곡 / "top" 인기곡 (score_albums 요청마다 앱 설정을 따른다)
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
        if not train.MODEL_PATH.exists():
            # 학습 전 (다른 사람이 처음 설치): 소리 판정 없이 뜨고, update 가 라이브러리를 분석해 처음 학습한다.
            # 분석은 오래 걸려 중간에 꺼질 수 있다 → 이미 저장한 조각은 다시 듣지 않는다
            self.model = None
            self.out = train.DATA / train.DEFAULT_EMB
            self.is_effnet = False
            self.sr = embed.SR
            self.keys = train.load_embeddings(train.DEFAULT_EMB)[0] if any(self.out.glob("shard_*.npz")) else []
            self.keyset = set(self.keys)
            self.xml_mtime = os.path.getmtime(self.xml)
            log(f"[service] 라이브러리 {len(self.tracks)}곡, 학습 전 (분석해 둔 곡 {len(self.keys)})")
            return
        self.model = json.loads(train.MODEL_PATH.read_text(encoding="utf-8"))
        if self.model.get("version") != train.VERSION:
            # 예전(레시피 분류기) 모델: 임베딩은 그대로 쓰고 기준(평균)만 라이브러리 전체로 다시 낸다 (몇 초)
            self.model = train.run(self.xml, self.model.get("embeddings", train.DEFAULT_EMB), log=log)
            train.save(self.model)
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
        # 곡 가중치: 많이 듣고 좋아한 곡과 닮을수록 더 쳐준다 (0.3~1배, 하트는 1). 오래 안 들은 곡은 0 → 비교에서 빠진다.
        # 라이브러리에서 지운 곡도 0
        self.lib_w = np.array([self.tracks[k].taste_weight() if k in self.tracks else 0.0 for k in keys], np.float32)
        self.xml_mtime = os.path.getmtime(self.xml)
        log(f"[service] 라이브러리 {len(self.tracks)}곡, 임베딩 {len(keys)}곡 ({self.model['embeddings']}), "
            f"닮음 비교 {int((self.lib_w > 0).sum())}곡, 기준 {self.model['trained_at']}")

    def failed(self) -> set[str]:
        return set(json.loads(FAILED.read_text(encoding="utf-8"))) if FAILED.exists() else set()

    def new_tracks(self, include_failed: bool = False) -> list:
        skip = self.keyset if include_failed else self.keyset | self.failed()
        return [t for t in self.tracks.values() if t.pid not in skip and t.path and t.path.exists()]

    def status(self, _args: dict, _rid) -> dict:
        if os.path.getmtime(self.xml) != self.xml_mtime:
            # 하트·재생 수가 바뀌면 곡 가중치도 다시 (임베딩은 그대로)
            self.reload()
        if self.model is None:
            return {"trained": False, "device": self.device, "sound_model": "CLAP",
                    "gpu": torch.cuda.get_device_name(0) if self.device == "cuda" else None,
                    "embedded": len(self.keys), "new_tracks": len(self.new_tracks()), "library": len(self.tracks)}
        return {
            "trained": True,
            "device": self.device,
            "sound_model": "Discogs-EffNet" if self.is_effnet else "CLAP",
            "gpu": torch.cuda.get_device_name(0) if self.device == "cuda" else None,
            "trained_at": self.model["trained_at"],
            "tracks": self.model["tracks"],
            "embedded": len(self.keys),
            "new_tracks": len(self.new_tracks()),
            "compared": int((self.lib_w > 0).sum()),
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
            inp = proc(audio=flat[s:s + 72], sampling_rate=embed.SR, return_tensors="pt").to(self.device)
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

    def preview_tracks(self, album_id: str) -> list[tuple[int | None, str | None, list[np.ndarray] | None]]:
        """미리듣기를 곡별 클립으로: [(곡 id, 제목, 클립)]. "t곡id" 는 곡 하나 (하트를 누른 곡).
        이미 들은 곡(곡 임베딩 캐시)은 받지 않고 클립 자리에 None — 전곡·인기곡을 오가도 다시 받지 않는다."""
        if album_id.startswith("t"):
            preview = self.deezer(f"/track/{album_id[1:]}").get("preview")
            items = [(None, None, preview)] if preview else []
        else:
            items = self.album_detail(album_id)
        out = []
        for tid, title, url in items:
            if tid is not None and f"t{tid}" in self.album_cache:
                out.append((tid, title, None))
                continue
            try:
                data = self._http.get(url, timeout=20).content
            except requests.RequestException:
                continue
            clips = decode_preview(data, self.sr)
            if clips:
                out.append((tid, title, clips))
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
        flat = [(i, tid, title) for i, tr in enumerate(per) for tid, title, c in tr if c is not None]
        embs = self.embed_clips([c for tr in per for _, _, c in tr if c is not None])
        for (i, tid, title), e in zip(flat, embs):
            if e is not None and tid is not None:
                self.album_cache[f"t{tid}"] = e
        by_album: dict[int, list] = {}
        new = iter(embs)
        for i, tr in enumerate(per):
            for tid, title, c in tr:
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
                # 예전 EffNet 장르 태그는 지운다 (장르 배지는 앱이 Last.fm 태그로 붙인다)
                info.pop("genres", None)
                info.pop("effnet", None)

    # ---- 신보 점수 --------------------------------------------------------
    def ensure_embedded(self, albums: list, rid, what: str = "미리듣기 소리 분석 중") -> None:
        """아직 소리를 모르는 앨범, 또는 다른 모드(전곡·인기곡, 예전의 앞 3곡)로 들은 앨범만 다시 임베딩한다 (album_emb.npz 캐시)."""
        def stale(k: str) -> bool:
            info = self.album_info.get(k, {})
            return k not in self.album_cache or (not k.startswith("t") and info.get("tracks_mode") != self.track_mode)
        todo = [a for a in albums if stale(str(a["id"]))]
        done = 0
        # 처음 보는 앨범만 듣는다는 걸 알린다 (전체 장수만 보이면 전부 다시 듣는 것처럼 보였다)
        if todo and len(todo) < len(albums):
            send({"id": rid, "event": "progress", "message": f"새로 들을 앨범 {len(todo)}장 (나머지 {len(albums) - len(todo)}장은 이미 들음)",
                  "done": 0, "total": len(todo)})
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
        if todo or missing_info:
            ALBUM_INFO.write_text(json.dumps(self.album_info, ensure_ascii=False), encoding="utf-8")

    def score_albums(self, args: dict, rid) -> dict:
        if self.model is None:
            return {"albums": []}
        albums = args["albums"]
        self.track_mode = "top" if args.get("tracks") == "top" else "all"
        self.ensure_embedded(albums, rid)
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
        if self.model is None:
            return {"count": 0, "dislikes": 0}
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
        if self.model is None:
            return {"albums": []}
        ids = [str(i) for i in args.get("ids") or []]
        need = [i for i in ids if "pairs" not in self.album_info.get(i, {})]
        if need:
            with ThreadPoolExecutor(16) as pool:
                self.analyze_albums(need, pool)
            self.save_album_cache()
            ALBUM_INFO.write_text(json.dumps(self.album_info, ensure_ascii=False), encoding="utf-8")
        return {"albums": [{"id": int(i), "pairs": self.album_info.get(i, {}).get("pairs", [])} for i in ids]}

    # ---- 별로·차단한 앨범과 아주 닮은 추천 (그 자리에서 같이 숨길지 묻는다) ----
    def similar_albums(self, args: dict, rid) -> dict:
        if self.model is None:
            return {"albums": []}
        """target 앨범과 ids 앨범들의 소리 유사도 (평균을 뺀 코사인). min 이상만 높은 순으로.
        저장된 앨범 약 1만 장끼리 0.85 이상은 상위 0.3% 정도라 '아주 닮음' 기준으로 쓴다."""
        t = self.album_cache.get(str(args["target"]))
        if t is None:
            return {"albums": []}
        x = train.preprocess(t[None, :], self.mean)[0]
        ids = [str(i) for i in args.get("ids") or [] if str(i) in self.album_cache and str(i) != str(args["target"])]
        if not ids:
            return {"albums": []}
        sims = train.preprocess(np.stack([self.album_cache[i] for i in ids]), self.mean) @ x
        lo = float(args.get("min", 0.85))
        out = sorted(({"id": int(i), "sim": round(float(s), 4)} for i, s in zip(ids, sims) if s >= lo), key=lambda a: -a["sim"])
        return {"albums": out}

    def dislike_matches(self, args: dict, rid) -> dict:
        if self.model is None:
            return {"albums": []}
        """ids 중 refs(직접 별로·'소리 때문' 차단한 앨범)의 어느 앨범과 min 이상 닮은 것. 다시 분석한 뒤 뺄지 묻는 데 쓴다.
        바로 넘긴 앨범까지 넣으면 기준이 93장이 되어 '어느 하나와는 닮은' 앨범이 1만 장 중 1천 장 넘게 걸렸다 →
        직접 고른 앨범만, 0.92 (직접 별로 30장 기준 1만 장 중 31장, 한 주 250장이면 1장 안팎)."""
        refs = [str(r) for r in args.get("refs") or [] if str(r) in self.album_cache]
        ids = [str(i) for i in args.get("ids") or [] if str(i) in self.album_cache and str(i) not in refs]
        if not refs or not ids:
            return {"albums": []}
        R = train.preprocess(np.stack([self.album_cache[r] for r in refs]), self.mean)
        sims = train.preprocess(np.stack([self.album_cache[i] for i in ids]), self.mean) @ R.T
        best = sims.argmax(axis=1)
        lo = float(args.get("min", 0.92))
        out = [{"id": int(i), "sim": round(float(sims[k, best[k]]), 4), "like": int(refs[best[k]])}
               for k, i in enumerate(ids) if sims[k, best[k]] >= lo]
        return {"albums": sorted(out, key=lambda a: -a["sim"])}

    # ---- 수록곡마다 비슷한 내 곡 (수록곡을 펼쳤을 때) ----------------------
    def track_near(self, args: dict, rid) -> dict:
        if self.model is None:
            return {"tracks": []}
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

    # ---- 장르 칸을 모르는 앨범 (장르 DB 에 없거나 Pop·Asian Music 처럼 넓은 장르) --------------
    def genre_neighbors(self, args: dict, rid) -> dict:
        """known {앨범 id: 칸} 중 소리가 가장 닮은 GROUP_KNN 장의 칸을 유사도로 가중 투표한다.
        ids 마다 {"id", "group", "share"} — share 는 이긴 칸의 몫 (낮으면 애매한 앨범)"""
        if self.model is None:
            return {"albums": []}
        known = [(str(k), g) for k, g in (args.get("known") or {}).items() if str(k) in self.album_cache]
        ids = [str(i) for i in args.get("ids") or [] if str(i) in self.album_cache]
        if len(known) < 3 or not ids:
            return {"albums": []}
        K = train.preprocess(np.stack([self.album_cache[k] for k, _ in known]), self.mean)
        groups = np.array([g for _, g in known])
        sims = train.preprocess(np.stack([self.album_cache[i] for i in ids]), self.mean) @ K.T
        out = []
        for row, i in zip(sims, ids):
            top = np.argsort(-row)[:GROUP_KNN]
            w = np.maximum(row[top], 0) + 1e-6
            votes: dict[str, float] = {}
            for g, v in zip(groups[top], w):
                votes[g] = votes.get(g, 0.0) + float(v)
            best = max(votes, key=votes.get)
            out.append({"id": int(i), "group": best, "share": round(votes[best] / sum(votes.values()), 3)})
        return {"albums": out}

    def stack(self, keys: list[str]) -> np.ndarray | None:
        return (train.preprocess(np.stack([self.album_cache[k] for k in keys]).astype(np.float32), self.mean).astype(np.float32)
                if keys else None)

    def describe(self, e: np.ndarray, key: str | None = None) -> dict:
        x = train.preprocess(e[None, :], self.mean)[0]
        res = {}
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
        keys, embs, done = [], [], set()
        with ThreadPoolExecutor(10) as pool:
            for s in range(0, len(todo), 24):
                batch = todo[s:s + 24]
                clips = list(pool.map(lambda t: embed.decode_clips(t.path, self.sr) or [], batch))
                for t, e in zip(batch, self.embed_clips(clips)):
                    if e is not None:
                        keys.append(t.pid)
                        embs.append(e)
                        done.add(t.pid)
                if len(keys) >= 500:  # 처음 분석은 한 시간 안팎 → 중간에 꺼져도 이어서 하게 조각으로 저장
                    embed.save_shard(self.out, keys, embs)
                    keys, embs = [], []
                send({"id": rid, "event": "progress", "message": f"새 곡 분석 중 {min(s + 24, len(todo))}/{len(todo)}",
                      "done": min(s + 24, len(todo)), "total": len(todo)})
        if keys:
            embed.save_shard(self.out, keys, embs)
        failed = self.failed() | ({t.pid for t in todo} - done)
        FAILED.write_text(json.dumps(sorted(failed)), encoding="utf-8")

        old = self.model
        train.save(train.run(self.xml, old["embeddings"] if old else train.DEFAULT_EMB, log=log))
        self.reload()
        return {"added": len(done), "compared": int((self.lib_w > 0).sum())}


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
                "track_near": svc.track_near, "album_pairs": svc.album_pairs, "similar_albums": svc.similar_albums,
                "dislike_matches": svc.dislike_matches, "genre_neighbors": svc.genre_neighbors}

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

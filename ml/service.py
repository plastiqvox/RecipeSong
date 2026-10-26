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

import embed  # noqa: E402
import library  # noqa: E402
import train  # noqa: E402

PROTO = sys.stdout
sys.stdout = sys.stderr  # 라이브러리들이 print 해도 프로토콜이 깨지지 않게
PROTO.reconfigure(encoding="utf-8", line_buffering=True)

DATA = Path(__file__).parent / "data"
ALBUM_CACHE = DATA / "album_emb.npz"
ALBUM_INFO = DATA / "album_info.json"  # 레이블·장르 (바뀌지 않으므로 영구 캐시)
FAILED = DATA / "failed_tracks.json"  # 디코딩 실패한 곡 → '새 곡'으로 다시 세지 않음
KNN = 20
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
        self.out = embed.out_dir(embed.MODEL_ID)
        self._clap = None
        self._http = requests.Session()
        self._http.mount("https://", requests.adapters.HTTPAdapter(pool_connections=32, pool_maxsize=32))
        self._dz_lock = threading.Lock()
        self._dz_next = 0.0
        self.album_cache: dict[str, np.ndarray] = {}
        if ALBUM_CACHE.exists():
            d = np.load(ALBUM_CACHE, allow_pickle=False)
            self.album_cache = dict(zip(d["keys"].tolist(), d["emb"].astype(np.float32)))
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
        self.mean = np.array(self.model["mean"], np.float32)
        keys, emb = train.load_embeddings(self.model["embeddings"])
        self.keys = keys
        self.keyset = set(keys)
        self.lib = train.preprocess(emb, self.mean).astype(np.float32)
        self.meta = [(self.tracks[k].artist, self.tracks[k].title) if k in self.tracks else ("?", "?") for k in keys]
        self.heads = {name: (np.array(self.model[name]["coef"], np.float32),
                             np.array(self.model[name]["intercept"], np.float32),
                             self.model[name]["classes"]) for name in ("style", "region")}
        self.xml_mtime = os.path.getmtime(self.xml)
        log(f"[service] 라이브러리 {len(self.tracks)}곡, 임베딩 {len(keys)}곡, 모델 {self.model['trained_at']}")

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
            "gpu": torch.cuda.get_device_name(0) if self.device == "cuda" else None,
            "trained_at": self.model["trained_at"],
            "tracks": self.model["tracks"],
            "embedded": len(self.keys),
            "new_tracks": len(self.new_tracks()),
            "styles": self.model["style"]["classes"],
            "style_accuracy": self.model["style"]["cv_accuracy"],
            "regions": self.model["region"]["classes"],
            "region_accuracy": self.model["region"]["cv_accuracy"],
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

    def album_detail(self, album_id: str) -> list[str]:
        """앨범 상세 한 번으로 레이블·장르(캐시)와 미리듣기 URL(만료되므로 매번 새로)을 얻는다."""
        r = self.deezer(f"/album/{album_id}")
        genres = (r.get("genres") or {}).get("data") or []
        if r:
            self.album_info[album_id] = {"label": r.get("label"), "genre": genres[0]["name"] if genres else None}
        return [t["preview"] for t in (r.get("tracks") or {}).get("data", []) if t.get("preview")][:3]

    def preview_clips(self, album_id: str) -> list[np.ndarray]:
        clips: list[np.ndarray] = []
        for url in self.album_detail(album_id):
            try:
                clips += decode_preview(self._http.get(url, timeout=20).content)
            except requests.RequestException:
                pass
        return clips

    # ---- 신보 점수 --------------------------------------------------------
    def ensure_embedded(self, albums: list, rid, what: str = "미리듣기 소리 분석 중") -> None:
        """아직 소리를 모르는 앨범만 미리듣기를 받아 임베딩한다 (album_emb.npz 캐시)."""
        todo = [a for a in albums if str(a["id"]) not in self.album_cache]
        done = 0
        with ThreadPoolExecutor(16) as pool:
            # 이미 소리를 아는 앨범도 레이블·장르가 없으면 채운다
            missing_info = [str(a["id"]) for a in albums if str(a["id"]) in self.album_cache and str(a["id"]) not in self.album_info]
            list(pool.map(self.album_detail, missing_info))
            for s in range(0, len(todo), 32):
                batch = todo[s:s + 32]
                clips = list(pool.map(lambda a: self.preview_clips(str(a["id"])), batch))
                for a, e in zip(batch, self.embed_clips(clips)):
                    if e is not None:
                        self.album_cache[str(a["id"])] = e
                done += len(batch)
                send({"id": rid, "event": "progress", "message": f"{what} {done}/{len(todo)}",
                      "done": done, "total": len(todo)})
        if todo:
            self.save_album_cache()
        if todo or missing_info:
            ALBUM_INFO.write_text(json.dumps(self.album_info, ensure_ascii=False), encoding="utf-8")

    def score_albums(self, args: dict, rid) -> dict:
        albums = args["albums"]
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
        """저장함 앨범 목록을 받는다: [{id, name}]. 소리를 모르는 앨범은 미리듣기로 분석한다."""
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
        log(f"[service] 저장함 {len(self.fav_keys)}/{len(items)}장, 별로 {len(self.dis_keys)}/{len(dislikes)}장 반영")
        return {"count": len(self.fav_keys), "dislikes": len(self.dis_keys)}

    def stack(self, keys: list[str]) -> np.ndarray | None:
        return (train.preprocess(np.stack([self.album_cache[k] for k in keys]).astype(np.float32), self.mean).astype(np.float32)
                if keys else None)

    def describe(self, e: np.ndarray, key: str | None = None) -> dict:
        x = train.preprocess(e[None, :], self.mean)[0]
        res = {}
        for name, (coef, icpt, classes) in self.heads.items():
            res[name] = {c: round(float(p), 4) for c, p in zip(classes, softmax(coef @ x + icpt))}
        sims = self.lib @ x
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
            np.savez(ALBUM_CACHE, keys=np.array(ks), emb=np.stack([self.album_cache[k] for k in ks]).astype(np.float16))

    # ---- 자동 재학습 ------------------------------------------------------
    def update(self, args: dict, rid) -> dict:
        self.tracks, self.playlists = library.load(self.xml)
        todo = self.new_tracks()
        keys, embs = [], []
        with ThreadPoolExecutor(10) as pool:
            for s in range(0, len(todo), 24):
                batch = todo[s:s + 24]
                clips = list(pool.map(lambda t: embed.decode_clips(t.path) or [], batch))
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


def decode_preview(data: bytes) -> list[np.ndarray]:
    """30초 미리듣기 MP3 → 48kHz 모노, 앞/중간/뒤 10초 구간."""
    try:
        with av.open(io.BytesIO(data)) as c:
            rs = av.AudioResampler(format="flt", layout="mono", rate=embed.SR)
            buf = [o.to_ndarray()[0] for f in c.decode(c.streams.audio[0]) for o in rs.resample(f)]
    except Exception:
        return []
    if not buf:
        return []
    x = np.concatenate(buf).astype(np.float32)
    peak = float(np.abs(x).max())
    if peak > 1:
        x /= peak
    n = embed.SR * embed.CLIP_SEC
    if len(x) < n:
        return [x] if len(x) > embed.SR else []
    return [x[s:s + n] for s in np.linspace(0, len(x) - n, 3).astype(int)]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--xml", required=True)
    args = ap.parse_args()

    t0 = time.time()
    svc = Service(args.xml)
    send({"event": "ready", "seconds": round(time.time() - t0, 1)})
    handlers = {"status": svc.status, "score_albums": svc.score_albums, "update": svc.update, "favorites": svc.favorites}

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

"""라이브러리 음원을 CLAP 임베딩으로 바꾼다 (GPU).

곡마다 25% / 50% / 75% 지점에서 10초씩 잘라 임베딩하고 평균 → L2 정규화.
결과는 data/embeddings/shard_*.npz 에 조금씩 저장하므로 중간에 멈춰도 이어서 실행된다.

    uv run python embed.py "C:/Users/me/Music/iTunes/iTunes Music Library.xml" [--limit 200]
"""
from __future__ import annotations

import argparse
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import av
import numpy as np
import torch
from tqdm import tqdm
from transformers import ClapModel, ClapProcessor

import library

# laion/larger_clap_music 은 허브 체크포인트가 고장나 있다 (어떤 입력이든 같은 출력) → 쓰지 말 것
MODEL_ID = "laion/larger_clap_general"
SR = 48_000
CLIP_SEC = 10
POSITIONS = (0.25, 0.5, 0.75)
DATA_DIR = Path(__file__).parent / "data"
OUT_DIR = DATA_DIR / "embeddings"


def out_dir(model_id: str, tag: str = "") -> Path:
    return DATA_DIR / ("emb_" + model_id.split("/")[-1] + (f"_{tag}" if tag else ""))


def decode_clips(path: Path) -> list[np.ndarray] | None:
    """파일에서 POSITIONS 지점의 10초 구간을 48kHz 모노 float32로 꺼낸다."""
    try:
        with av.open(str(path)) as c:
            stream = c.streams.audio[0]
            duration = (c.duration or 0) / av.time_base
            if duration < 1:
                return None
            clips = []
            for pos in (POSITIONS if duration > CLIP_SEC * 2 else (0.0,)):
                start = max(0.0, min(duration * pos - CLIP_SEC / 2, duration - CLIP_SEC))
                c.seek(int(start * av.time_base), any_frame=False, backward=True)
                resampler = av.AudioResampler(format="flt", layout="mono", rate=SR)
                buf, need = [], SR * CLIP_SEC
                for frame in c.decode(stream):
                    if frame.time is not None and frame.time + frame.samples / frame.sample_rate < start:
                        continue
                    for out in resampler.resample(frame):
                        buf.append(out.to_ndarray()[0])
                    if sum(len(b) for b in buf) >= need:
                        break
                audio = np.concatenate(buf) if buf else np.zeros(0, np.float32)
                if len(audio) < SR:  # 1초 미만이면 버림
                    continue
                # 스테레오 → 모노 합산에서 ±1을 넘을 수 있어 피크를 맞춘다
                peak = float(np.abs(audio).max())
                if peak > 1.0:
                    audio = audio / peak
                clips.append(audio[:need].astype(np.float32))
            return clips or None
    except Exception:
        return None


def load_done(out: Path) -> set[str]:
    done = set()
    for f in out.glob("shard_*.npz"):
        done.update(np.load(f, allow_pickle=False)["keys"].tolist())
    return done


def save_shard(out: Path, keys: list[str], embs: list[np.ndarray]) -> None:
    out.mkdir(parents=True, exist_ok=True)
    n = len(list(out.glob("shard_*.npz")))
    np.savez(out / f"shard_{n:05d}.npz", keys=np.array(keys), emb=np.stack(embs).astype(np.float16))


def stratified_sample(tracks: list, per_playlist: int, seed: int = 0) -> list:
    """모델 비교용: 플레이리스트마다 최대 N곡씩 고르게 뽑는다."""
    import random
    rng = random.Random(seed)
    groups: dict[str, list] = {}
    for t in tracks:
        if len(t.playlists) == 1:
            groups.setdefault(t.playlists[0], []).append(t)
    picked = []
    for g in groups.values():
        rng.shuffle(g)
        picked.extend(g[:per_playlist])
    return picked


def features(model: ClapModel, inputs) -> torch.Tensor:
    out = model.get_audio_features(**inputs)
    # transformers 버전에 따라 텐서 또는 출력 객체
    return out if isinstance(out, torch.Tensor) else out.pooler_output


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("xml")
    ap.add_argument("--model", default=MODEL_ID)
    ap.add_argument("--limit", type=int, default=0, help="시험용: 앞에서 N곡만")
    ap.add_argument("--sample", type=int, default=0, help="모델 비교용: 플레이리스트마다 N곡씩")
    ap.add_argument("--batch", type=int, default=16, help="한 번에 GPU에 올릴 곡 수")
    ap.add_argument("--workers", type=int, default=8, help="디코딩 스레드 수")
    args = ap.parse_args()

    out = out_dir(args.model, f"s{args.sample}" if args.sample else "")
    tracks, _ = library.load(args.xml)
    done = load_done(out)
    pool_tracks = stratified_sample(list(tracks.values()), args.sample) if args.sample else list(tracks.values())
    todo = [t for t in pool_tracks if t.pid not in done and t.path and t.path.exists()]
    if args.limit:
        todo = todo[: args.limit]
    print(f"모델 {args.model} → {out.name} / 전체 {len(tracks)}곡 / 완료 {len(done)} / 이번에 {len(todo)}곡")
    if not todo:
        return

    device = "cuda" if torch.cuda.is_available() else "cpu"
    model = ClapModel.from_pretrained(args.model).to(device).eval()
    if device == "cuda":
        model = model.half()
    processor = ClapProcessor.from_pretrained(args.model)

    keys, embs, failed = [], [], 0
    t0 = time.time()
    with ThreadPoolExecutor(args.workers) as pool, tqdm(total=len(todo), unit="곡") as bar:
        for i in range(0, len(todo), args.batch):
            batch = todo[i: i + args.batch]
            decoded = list(pool.map(lambda t: decode_clips(t.path), batch))
            flat, owner = [], []
            for j, clips in enumerate(decoded):
                if clips is None:
                    failed += 1
                    continue
                flat.extend(clips)
                owner.extend([j] * len(clips))
            if flat:
                inputs = processor(audios=flat, sampling_rate=SR, return_tensors="pt").to(device)
                if device == "cuda":
                    inputs = {k: (v.half() if v.is_floating_point() else v) for k, v in inputs.items()}
                with torch.no_grad():
                    f = features(model, inputs).float()
                f = torch.nn.functional.normalize(f, dim=-1).cpu().numpy()
                owner_arr = np.array(owner)
                for j in sorted(set(owner)):
                    e = f[owner_arr == j].mean(axis=0)
                    keys.append(batch[j].pid)
                    embs.append(e / (np.linalg.norm(e) + 1e-8))
            bar.update(len(batch))
            if len(keys) >= 500:
                save_shard(out, keys, embs)
                keys, embs = [], []
    if keys:
        save_shard(out, keys, embs)
    dt = time.time() - t0
    print(f"완료: {len(todo) - failed}곡, 실패 {failed}곡, {dt:.0f}초 ({len(todo) / max(dt, 1):.1f}곡/초)")


if __name__ == "__main__":
    main()

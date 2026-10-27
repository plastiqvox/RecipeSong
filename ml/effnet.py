"""Discogs-EffNet (MTG Essentia) 임베딩: Discogs 스타일 400개를 맞히도록 학습한 음악 전용 모델.

Essentia(TensorflowInputMusiCNN)와 같은 입력을 numpy로 만든다:
16kHz 모노 → 512 프레임 / 256 홉 Hann → 파워 스펙트럼 → Slaney 멜 96밴드 (0~8kHz, 면적 정규화)
→ log10(1 + 10000·x) → 128프레임(약 2초) 패치, 64프레임씩 겹쳐서.
패치마다 1280차원 임베딩을 내고, 곡은 패치 평균 → L2 정규화.

모델: data/models/discogs-effnet-bsdynamic-1.onnx (https://essentia.upf.edu/models, CC BY-NC-SA 4.0 — 개인용)
"""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np

SR = 16_000
NAME = "discogs_effnet"
MODELS_DIR = Path(__file__).parent / "data" / "models"
# 이름 → ONNX 파일. genre: 스타일 400개 분류로 학습 / multi: 같은 아티스트·레이블·앨범·곡끼리 가깝게 (유사도 학습, 배치 64 고정)
VARIANTS = {
    "discogs_effnet": "discogs-effnet-bsdynamic-1.onnx",
    "discogs_multi": "discogs_multi_embeddings-effnet-bs64-1.onnx",
}
MODEL = MODELS_DIR / VARIANTS[NAME]
FRAME, HOP, BANDS, PATCH, PATCH_HOP = 512, 256, 96, 128, 64


def _hz_to_mel(f):
    """Slaney 멜: 1kHz까지 선형, 그 위는 로그"""
    f = np.asarray(f, np.float64)
    lin = f / (200.0 / 3)
    return np.where(f >= 1000, 15 + np.log(np.maximum(f, 1e-9) / 1000) / (np.log(6.4) / 27), lin)


def _mel_to_hz(m):
    m = np.asarray(m, np.float64)
    return np.where(m >= 15, 1000 * np.exp((np.log(6.4) / 27) * (m - 15)), m * (200.0 / 3))


def _filterbank() -> np.ndarray:
    freqs = np.linspace(0, SR / 2, FRAME // 2 + 1)
    edges = _mel_to_hz(np.linspace(_hz_to_mel(0), _hz_to_mel(SR / 2), BANDS + 2))
    fb = np.zeros((BANDS, len(freqs)), np.float32)
    for i in range(BANDS):
        lo, c, hi = edges[i], edges[i + 1], edges[i + 2]
        tri = np.minimum((freqs - lo) / (c - lo), (hi - freqs) / (hi - c))
        fb[i] = np.maximum(0, tri) * (2.0 / (hi - lo))  # 삼각형 면적 1
    return fb


_FB = _filterbank()
_WIN = np.hanning(FRAME).astype(np.float32)


def melspec(audio: np.ndarray) -> np.ndarray:
    """16kHz 모노 → (프레임, 96)"""
    if len(audio) < FRAME:
        audio = np.pad(audio, (0, FRAME - len(audio)))
    n = 1 + (len(audio) - FRAME) // HOP
    idx = np.arange(FRAME)[None, :] + HOP * np.arange(n)[:, None]
    spec = np.abs(np.fft.rfft(audio[idx] * _WIN, axis=1)) ** 2
    return np.log10(1 + 10000 * (spec.astype(np.float32) @ _FB.T))


def patches(audio: np.ndarray) -> np.ndarray:
    """(패치, 128, 96). 2초가 안 되면 0으로 채운 패치 하나."""
    m = melspec(audio)
    if len(m) < PATCH:
        m = np.pad(m, ((0, PATCH - len(m)), (0, 0)))
    starts = range(0, len(m) - PATCH + 1, PATCH_HOP)
    return np.stack([m[s:s + PATCH] for s in starts]).astype(np.float32)


class EffNet:
    def __init__(self, name: str = NAME):
        import onnxruntime as ort
        opts = ort.SessionOptions()
        opts.log_severity_level = 3
        self.sess = ort.InferenceSession(str(MODELS_DIR / VARIANTS[name]), opts, providers=["CPUExecutionProvider"])
        self.inp = self.sess.get_inputs()[0].name
        # 배치 크기가 고정인 모델(bs64)은 모자란 만큼 0으로 채워 돌리고 잘라 낸다
        b = self.sess.get_inputs()[0].shape[0]
        self.batch = b if isinstance(b, int) else 256
        outs = [o.name for o in self.sess.get_outputs()]
        # 출력 순서: 스타일 400 (sigmoid), 임베딩 1280
        shapes = {o.name: o.shape[-1] for o in self.sess.get_outputs()}
        self.out_emb = next(n for n in outs if shapes[n] == 1280 and "embed" in n.lower()) if "embeddings" in outs \
            else next(n for n in outs if shapes[n] == 1280)
        # 장르판만 스타일 400개 확률(sigmoid)을 낸다
        self.out_pred = next((n for n in outs if shapes[n] == 400), None)
        meta = MODELS_DIR / VARIANTS[name].replace(".onnx", ".json")
        self.classes: list[str] = json.loads(meta.read_text(encoding="utf-8"))["classes"] if meta.exists() else []

    def embed_groups(self, groups: list[list[np.ndarray]]) -> list[np.ndarray | None]:
        """곡(앨범)마다 16kHz 클립 묶음 → 패치 평균 임베딩 (L2 정규화)."""
        out: list[np.ndarray | None] = [None] * len(groups)
        ps, owner = [], []
        for i, clips in enumerate(groups):
            for c in clips:
                p = patches(c)
                ps.append(p)
                owner += [i] * len(p)
        if not ps:
            return out
        x = np.concatenate(ps)
        feats = np.concatenate([self._run(x[s:s + self.batch]) for s in range(0, len(x), self.batch)])
        owner_arr = np.array(owner)
        for i in set(owner):
            e = feats[owner_arr == i].mean(0)
            out[i] = (e / (np.linalg.norm(e) + 1e-8)).astype(np.float32)
        return out

    def _run(self, x: np.ndarray, out: str | None = None) -> np.ndarray:
        n = len(x)
        if self.sess.get_inputs()[0].shape[0] == self.batch and n < self.batch:
            x = np.concatenate([x, np.zeros((self.batch - n, *x.shape[1:]), np.float32)])
        return self.sess.run([out or self.out_emb], {self.inp: x})[0][:n]

    def styles(self, groups: list[list[np.ndarray]]) -> list[np.ndarray | None]:
        """곡마다 16kHz 클립 묶음 → Discogs 스타일 400개 확률 (패치 평균). 장르판 모델에서만."""
        out: list[np.ndarray | None] = [None] * len(groups)
        ps, owner = [], []
        for i, clips in enumerate(groups):
            for c in clips:
                p = patches(c)
                ps.append(p)
                owner += [i] * len(p)
        if not ps or self.out_pred is None:
            return out
        x = np.concatenate(ps)
        pred = np.concatenate([self._run(x[s:s + self.batch], self.out_pred) for s in range(0, len(x), self.batch)])
        owner_arr = np.array(owner)
        for i in set(owner):
            out[i] = pred[owner_arr == i].mean(0).astype(np.float32)
        return out


def main() -> None:
    """라이브러리 전체를 EffNet 임베딩으로 (data/emb_discogs_effnet). 중간에 멈춰도 이어서 한다.

        uv run python effnet.py "C:/Users/me/Music/iTunes/iTunes Music Library.xml"
    """
    import argparse
    import time
    from concurrent.futures import ThreadPoolExecutor

    from tqdm import tqdm

    import embed
    import library

    ap = argparse.ArgumentParser()
    ap.add_argument("xml")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--variant", default=NAME, choices=list(VARIANTS))
    args = ap.parse_args()

    out = embed.DATA_DIR / f"emb_{args.variant}"
    tracks, _ = library.load(args.xml)
    done = embed.load_done(out)
    todo = [t for t in tracks.values() if t.pid not in done and t.path and t.path.exists()]
    if args.limit:
        todo = todo[: args.limit]
    print(f"EffNet({args.variant}) → {out.name} / 전체 {len(tracks)}곡 / 완료 {len(done)} / 이번에 {len(todo)}곡")
    model = EffNet(args.variant)
    keys, embs, failed, t0 = [], [], 0, time.time()
    with ThreadPoolExecutor(args.workers) as pool, tqdm(total=len(todo), unit="곡") as bar:
        for i in range(0, len(todo), 32):
            batch = todo[i:i + 32]
            clips = list(pool.map(lambda t: embed.decode_clips(t.path, SR) or [], batch))
            for t, e in zip(batch, model.embed_groups(clips)):
                if e is None:
                    failed += 1
                else:
                    keys.append(t.pid)
                    embs.append(e)
            bar.update(len(batch))
            if len(keys) >= 500:
                embed.save_shard(out, keys, embs)
                keys, embs = [], []
    if keys:
        embed.save_shard(out, keys, embs)
    dt = time.time() - t0
    print(f"완료: {len(todo) - failed}곡, 실패 {failed}곡, {dt:.0f}초")


if __name__ == "__main__":
    main()

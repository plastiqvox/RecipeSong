"""앨범 평균에 묻히는 취향 곡을 보완한다. 못 읽은 곡은 분모에서 제외한다."""
from __future__ import annotations

import numpy as np


def taste_scores(embeddings: np.ndarray, lib: np.ndarray, weights: np.ndarray, neighbors: int = 20) -> np.ndarray:
    active = np.flatnonzero(weights > 0)
    if not len(active):
        return np.zeros(len(embeddings))
    # 곡마다 행렬곱을 따로 호출하지 않고 앨범의 곡들을 한 번에 비교한다.
    sims = ((embeddings @ lib.T) * weights)[:, active]
    count = min(neighbors, len(active))
    return np.partition(sims, -count, axis=1)[:, -count:].mean(axis=1)


def fit_threshold(lib: np.ndarray, weights: np.ndarray, neighbors: int = 20) -> float | None:
    active = np.flatnonzero(weights > 0)
    if len(active) < 2:
        return None
    refs = np.flatnonzero(weights >= 0.8)
    if not len(refs):
        refs = active
    refs = refs[np.linspace(0, len(refs) - 1, min(96, len(refs)), dtype=int)]
    sims = (lib[refs] @ lib[active].T) * weights[active]
    sims[np.arange(len(refs)), np.searchsorted(active, refs)] = -np.inf
    count = min(neighbors, len(active) - 1)
    scores = np.partition(sims, -count, axis=1)[:, -count:].mean(axis=1)
    return float(np.quantile(scores, 0.25))


def aggregate(base: float, scores: list[float], threshold: float) -> dict:
    values = np.asarray([s for s in scores if np.isfinite(s)], dtype=np.float64)
    if not len(values):
        return {"taste": base}
    top = float(np.sort(values)[-3:].mean())
    coverage = float((values >= threshold).mean())
    # 한 곡만 들은 앨범의 100% 적합을 전곡 적합처럼 보상하지 않는다.
    confidence = min(1.0, len(values) / 3)
    taste = base + confidence * (0.35 * (top - base) + 0.04 * (coverage - 0.5))
    return {"taste": round(taste, 4), "mean_taste": base, "top_taste": round(top, 4),
            "fit_ratio": round(coverage, 4), "fit_threshold": round(threshold, 4),
            "analyzed_tracks": len(values)}

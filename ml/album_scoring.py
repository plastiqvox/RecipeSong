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


def reference_threshold(refs: np.ndarray, lib: np.ndarray, weights: np.ndarray, exclude: np.ndarray,
                        neighbors: int = 20, quantile: float = 0.25) -> float | None:
    """기준 곡들(내 곡의 미리듣기)을 신보와 같은 방식으로 점수 매긴 분포의 하위 quantile. exclude[i, j]: i 의 비교에서 뺄 라이브러리 곡."""
    active = weights > 0
    if active.sum() < 2 or not len(refs):
        return None
    sims = (refs @ lib.T) * weights
    sims[:, ~active] = -np.inf
    sims[exclude] = -np.inf
    count = min(neighbors, int(active.sum()) - 1)
    scores = np.partition(sims, -count, axis=1)[:, -count:].mean(axis=1)
    scores = scores[np.isfinite(scores)]
    return float(np.quantile(scores, quantile)) if len(scores) else None


def negative_feedback(embeddings: np.ndarray, positives: np.ndarray | None, negatives: np.ndarray | None) -> tuple[np.ndarray, np.ndarray]:
    if positives is None or negatives is None or not len(positives) or not len(negatives):
        return np.zeros(len(embeddings)), np.zeros(len(embeddings), dtype=bool)
    positive = (embeddings @ positives.T).max(axis=1)
    negative = (embeddings @ negatives.T).max(axis=1)
    # 정규화 코사인을 같은 기준으로 비교한다. 작은 차이는 불호로 확정하지 않는다.
    margin = np.maximum(0, negative - positive - 0.03)
    return 0.5 * margin, margin > 0


def aggregate(base: float, scores: list[float], threshold: float, rejected: list[bool] | None = None) -> dict:
    finite = np.isfinite(scores)
    values = np.asarray(scores, dtype=np.float64)[finite]
    if not len(values):
        return {"taste": base}
    rejected_values = np.asarray(rejected, dtype=bool)[finite] if rejected is not None else np.zeros(len(values), dtype=bool)
    eligible = values[~rejected_values]
    top = float(np.sort(eligible if len(eligible) else values)[-3:].mean())
    coverage = float(((values >= threshold) & ~rejected_values).mean())
    # 한 곡만 들은 앨범의 100% 적합을 전곡 적합처럼 보상하지 않는다.
    confidence = min(1.0, len(values) / 3)
    taste = base + confidence * (0.35 * (top - base) + 0.04 * (coverage - 0.5))
    return {"taste": round(taste, 4), "mean_taste": base, "top_taste": round(top, 4),
            "fit_ratio": round(coverage, 4), "fit_threshold": round(threshold, 4),
            "analyzed_tracks": len(values)}

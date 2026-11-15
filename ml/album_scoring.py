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


def hub_scores(lib: np.ndarray, refs: np.ndarray | None, neighbors: int = 20, chunk: int = 512) -> np.ndarray:
    """내 곡마다 참고 앨범 중 가장 닮은 neighbors 장과의 평균 유사도. 무엇과도 닮게 나오는 곡(허브)일수록 크다.
    참고 앨범이 neighbors 장보다 적으면 0 (보정 없음)."""
    if refs is None or len(refs) < neighbors:
        return np.zeros(len(lib), np.float32)
    top = None
    for s in range(0, len(refs), chunk):
        sims = refs[s:s + chunk] @ lib.T
        block = sims if top is None else np.concatenate([top, sims])
        count = min(neighbors, len(block))
        top = np.partition(block, -count, axis=0)[-count:]
    return top.mean(axis=0).astype(np.float32)


def closest_tracks(x: np.ndarray, lib: np.ndarray, weights: np.ndarray, hub: np.ndarray | None, count: int = 1) -> list[int]:
    """신보 곡(앨범) 하나와 가장 닮은 내 곡 (카드의 '비슷한 내 곡'). 곡 가중치는 비교 대상(> 0)을 거르는 데만 쓴다.
    가중치를 곱하면 하트·별 4개(가중치 최대) 곡이 소리가 덜 닮아도 이겨, 카드 4만 장의 첫 곡 40%를 15곡이 차지했다.
    허브 점수도 빼서(2·유사도 − 허브, CSLS) 무엇과도 닮게 나오는 곡의 이점을 없앤다. 취향 점수(taste_scores)는 가중치를 그대로 쓴다."""
    active = np.flatnonzero(weights > 0)
    if not len(active):
        return []
    score = 2 * (lib[active] @ x) - (hub[active] if hub is not None else 0)
    count = min(count, len(active))
    idx = np.argpartition(-score, count - 1)[:count]
    return active[idx[np.argsort(-score[idx])]].tolist()


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

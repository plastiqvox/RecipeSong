"""크레이트 모델 학습 + 평가: 스타일(크레이트) × 지역 두 축.

- 입력: embed.py 의 CLAP 임베딩, crates.json (플레이리스트 → 스타일/지역 매핑)
- 스타일: style이 지정된 플레이리스트(일본 곡)로 학습. 해외·한국 곡의 스타일은 이 모델로 추정한다.
- 지역: 모든 매핑된 플레이리스트로 학습 (일본/한국/해외)
- 평가: 아티스트(컴필레이션은 앨범) 단위 5-fold 교차검증 → 같은 앨범 곡이 학습/시험에 섞이지 않음
- 출력: data/crate_model.json (.NET에서 내적 + softmax로 바로 적용 가능한 형태)

    uv run python train.py "C:/Users/me/Music/iTunes/iTunes Music Library.xml"
"""
from __future__ import annotations

import argparse
import json
from collections import Counter
from datetime import datetime
from pathlib import Path

import numpy as np
from sklearn.linear_model import LogisticRegression
from sklearn.model_selection import StratifiedGroupKFold

import covers
import library

ROOT = Path(__file__).parent
DATA = ROOT / "data"
DEFAULT_EMB = "emb_larger_clap_general"
COVER_SCALE = 0.7  # 소리+커버 지역 판정에서 커버 부분의 비중
EFFNET_FOLDER = "emb_discogs_effnet"  # 레시피 보조: EffNet 장르판 임베딩 (장르 태그를 낼 때 신보도 같이 나온다)
EFFNET_SCALE = 1.0


def crates_path() -> Path:
    """레시피 설정: 앱의 레시피 설정 창이 data/crates.json 에 쓴다. 없으면 저장소의 crates.json (만든 사람의 플레이리스트)"""
    user = DATA / "crates.json"
    return user if user.exists() else ROOT / "crates.json"


def load_embeddings(folder: str = DEFAULT_EMB) -> tuple[list[str], np.ndarray]:
    keys, embs = [], []
    for f in sorted((DATA / folder).glob("shard_*.npz")):
        d = np.load(f, allow_pickle=False)
        keys.extend(d["keys"].tolist())
        embs.append(d["emb"].astype(np.float32))
    return keys, np.concatenate(embs)


def preprocess(x: np.ndarray, mean: np.ndarray) -> np.ndarray:
    """CLAP 임베딩은 한쪽으로 쏠려 있어서 평균을 빼고 다시 정규화한다."""
    x = x - mean
    return x / (np.linalg.norm(x, axis=1, keepdims=True) + 1e-8)


def fit_eval(x, y, groups, classes, title, C, log=print, weights=None):
    """교차검증 결과를 출력하고, 전체 데이터로 학습한 모델과 정확도를 돌려준다.
    weights: 곡 가중치 (많이 듣고 좋아한 곡을 더 중요하게 배운다). 정확도는 가중치 없이 잰다."""
    w = np.ones(len(y)) if weights is None else np.asarray(weights)
    if len(classes) < 2:  # 하나뿐이면 고를 것이 없다 → 늘 그 하나
        log(f"\n[{title}] {classes[0] if classes else '없음'} 하나뿐이라 학습하지 않음")
        return Constant(x.shape[1]), 1.0
    # 다른 사람 라이브러리는 레시피마다 아티스트가 적을 수 있다 → 아티스트 수에 맞춰 나눈다 (2보다 적으면 교차검증 없이)
    splits = min(5, min(len(set(groups[y == i])) for i in range(len(classes))))
    if splits < 2:
        log(f"\n[{title}] 레시피마다 아티스트가 2명 이상 있어야 정확도를 잴 수 있어요 (정확도 생략)")
        return LogisticRegression(C=C, class_weight="balanced", max_iter=3000).fit(x, y, sample_weight=w), 0.0
    cv = StratifiedGroupKFold(n_splits=splits, shuffle=True, random_state=0)
    pred = np.empty_like(y)
    for tr, te in cv.split(x, y, groups):
        pred[te] = LogisticRegression(C=C, class_weight="balanced", max_iter=3000).fit(x[tr], y[tr], sample_weight=w[tr]).predict(x[te])
    acc = float((pred == y).mean())
    log(f"\n[{title}] 정확도 {acc:.1%} (찍기 {Counter(y).most_common(1)[0][1] / len(y):.1%})")
    for i, c in enumerate(classes):
        m = y == i
        log(f"   {c:8s} {m.sum():5d}곡  맞힘 {(pred[m] == i).mean():.0%}")
    clf = LogisticRegression(C=C, class_weight="balanced", max_iter=3000).fit(x, y, sample_weight=w)
    return clf, acc


class Constant:
    """분류할 것이 하나뿐일 때: 늘 그 하나 (LogisticRegression 과 같은 모양)"""
    def __init__(self, dim: int):
        self.coef_ = np.zeros((1, dim))
        self.intercept_ = np.zeros(1)

    def predict(self, x: np.ndarray) -> np.ndarray:
        return np.zeros(len(x), dtype=int)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("xml")
    ap.add_argument("--emb", default=DEFAULT_EMB, help="data/ 아래 임베딩 폴더")
    ap.add_argument("--C", type=float, default=2.0)
    args = ap.parse_args()
    out = run(args.xml, args.emb, args.C)
    save(out)
    print(f"\n저장: {MODEL_PATH}")


MODEL_PATH = DATA / "crate_model.json"


def save(model: dict) -> None:
    MODEL_PATH.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")


def run(xml: str, emb_folder: str = DEFAULT_EMB, C: float = 2.0, log=print) -> dict:
    """학습하고 모델 dict를 돌려준다 (저장은 save)."""
    config = json.loads(crates_path().read_text(encoding="utf-8"))
    mapping, ignore = config["playlists"], set(config.get("ignore", []))

    tracks, _ = library.load(xml)
    keys, emb = load_embeddings(emb_folder)
    index = {k: i for i, k in enumerate(keys)}

    rows, styles, regions, groups, weights, album_keys = [], [], [], [], [], []
    for t in tracks.values():
        pls = [p for p in t.playlists if p not in ignore]
        if len(pls) != 1 or pls[0] not in mapping or t.pid not in index:
            continue
        rows.append(index[t.pid])
        styles.append(mapping[pls[0]]["style"])
        regions.append(mapping[pls[0]]["region"])
        va = t.artist.lower() in ("various artists", "va", "v.a.")
        groups.append(f"{t.artist}|{t.album}" if va else t.artist)
        weights.append(t.weight)
        album_keys.append(covers.album_key(t.artist, t.album))

    if len(rows) < 30:
        raise ValueError(f"레시피 플레이리스트에서 분석한 곡이 {len(rows)}곡뿐이에요. 레시피 설정에서 플레이리스트를 더 골라 주세요 "
                         "(한 곡이 여러 레시피 플레이리스트에 같이 있으면 학습에서 빠져요)")
    mean = emb[rows].mean(axis=0)
    x = preprocess(emb[rows], mean)
    groups = np.array(groups)
    weights = np.array(weights)
    log(f"곡 가중치: 평균 {weights.mean():.2f} · 최대(하트·별 4개 이상) {int((weights >= 1.0).sum())}곡")
    log(f"학습 데이터 {len(rows)}곡 (제외 플레이리스트: {', '.join(sorted(ignore)) or '없음'})")

    style_classes = sorted({s for s in styles if s})
    has_style = np.array([s is not None for s in styles])
    ys = np.array([style_classes.index(s) if s else -1 for s in styles])
    style_clf, style_acc = fit_eval(x[has_style], ys[has_style], groups[has_style], style_classes, "스타일 (크레이트)", C, log,
                                    weights[has_style])

    region_classes = sorted(set(regions))
    yr = np.array([region_classes.index(r) for r in regions])
    region_clf, region_acc = fit_eval(x, yr, groups, region_classes, "지역", C, log, weights)

    def head(clf, classes, acc):
        coef, icpt = clf.coef_, clf.intercept_
        if len(classes) == 2 and coef.shape[0] == 1:
            # 둘 중 하나: sklearn 은 두 번째 클래스 쪽 한 줄만 준다 → softmax 로 쓰게 [0, w] 두 줄로
            coef, icpt = np.vstack([np.zeros_like(coef), coef]), np.concatenate([[0.0], icpt])
        return {"classes": classes, "coef": coef.round(6).tolist(),
                "intercept": icpt.round(6).tolist(), "cv_accuracy": round(acc, 4)}

    # 지역: 소리 + 앨범 커버 (커버 글자·디자인이 지역을 잘 알려 준다). 커버가 있는 곡만으로 따로 학습한다.
    region_cover = None
    cover = covers.load()
    has_cover = np.array([k in cover for k in album_keys])
    if has_cover.sum() > 1000:
        cv = np.stack([cover[k] for k, h in zip(album_keys, has_cover) if h])
        cover_mean = cv.mean(axis=0)
        xc = np.hstack([x[has_cover], COVER_SCALE * preprocess(cv, cover_mean)])
        rc_clf, rc_acc = fit_eval(xc, yr[has_cover], groups[has_cover], region_classes, "지역 (소리 + 커버)", C, log, weights[has_cover])
        region_cover = {**head(rc_clf, region_classes, rc_acc), "cover_mean": cover_mean.round(6).tolist(), "cover_scale": COVER_SCALE}

    # 레시피: 소리(CLAP) + 장르(EffNet 장르판 임베딩). 장르에 특화된 모델이라 레시피 경계(Jazz/Rock 등)를 보탠다
    style_effnet = None
    if (DATA / EFFNET_FOLDER).exists():
        ek, ee = load_embeddings(EFFNET_FOLDER)
        eidx = {k: i for i, k in enumerate(ek)}
        pids = [keys[r] for r in rows]
        has_eff = np.array([p in eidx for p in pids]) & has_style
        if has_eff.sum() > 1000:
            ev = ee[[eidx[p] for p, h in zip(pids, has_eff) if h]]
            eff_mean = ev.mean(axis=0)
            xe = np.hstack([x[has_eff], EFFNET_SCALE * preprocess(ev, eff_mean)])
            se_clf, se_acc = fit_eval(xe, ys[has_eff], groups[has_eff], style_classes, "스타일 (소리 + 장르)", C, log, weights[has_eff])
            style_effnet = {**head(se_clf, style_classes, se_acc), "effnet_mean": eff_mean.round(6).tolist(), "effnet_scale": EFFNET_SCALE}

    # 스타일 라벨이 없는 곡(해외·한국)에 스타일 추정 결과 분포
    no_style = ~has_style
    if no_style.any():
        guess = style_clf.predict(x[no_style])
        log("\n[스타일 추정: 스타일 라벨이 없는 곡]")
        for r in sorted({regions[i] for i in np.where(no_style)[0]}):
            m = np.array([regions[i] == r for i in np.where(no_style)[0]])
            dist = Counter(style_classes[g] for g in guess[m])
            log(f"   {r}: " + ", ".join(f"{k} {v / m.sum():.0%}" for k, v in dist.most_common()))

    return {
        "embeddings": emb_folder,
        "trained_at": datetime.now().isoformat(timespec="seconds"),
        "tracks": len(rows),
        "mean": mean.round(6).tolist(),
        "style": head(style_clf, style_classes, style_acc),
        "region": head(region_clf, region_classes, region_acc),
        "region_cover": region_cover,
        "style_effnet": style_effnet,
        "config": config,
    }


if __name__ == "__main__":
    main()

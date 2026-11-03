"""기존 라이브러리를 읽기만 하며 같은 아티스트를 제외한 전후 점수를 비교한다."""
import argparse
from collections import defaultdict
import json
import os
from pathlib import Path
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "ml"))
import album_scoring
import library
import train


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--xml")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    xml = args.xml
    if not xml:
        settings = json.loads((Path(os.environ["APPDATA"]) / "RecipeSong/settings.json").read_text(encoding="utf-8-sig"))
        xml = settings.get("ITunesLibraryPath")
    if not xml:
        raise SystemExit("Library XML path unavailable; pass --xml.")
    tracks, _ = library.load(xml)
    model = json.loads(train.MODEL_PATH.read_text(encoding="utf-8"))
    keys, raw = train.load_embeddings(model["embeddings"])
    mean = np.array(model["mean"], np.float32)
    lib = train.preprocess(raw, mean)
    weights = np.array([tracks[k].taste_weight() if k in tracks else 0 for k in keys], np.float32)
    groups = defaultdict(list)
    for i, key in enumerate(keys):
        if key in tracks and tracks[key].album:
            groups[(tracks[key].artist.casefold(), tracks[key].album.casefold())].append(i)
    # 같은 앨범 집합으로 전후를 비교한다. 음원·임베딩·모델은 변경하지 않는다.
    selected = sorted((k, v) for k, v in groups.items() if 3 <= len(v) <= 20)
    selected = selected[::max(1, len(selected) // 64)][:64]
    rows = []
    for (artist, album), indices in selected:
        mask = np.array([k in tracks and tracks[k].artist.casefold() != artist and weights[i] > 0 for i, k in enumerate(keys)])
        reference, reference_weights = lib[mask], weights[mask]
        threshold = album_scoring.fit_threshold(reference, reference_weights)
        if threshold is None:
            continue
        def scores(vectors):
            return album_scoring.taste_scores(vectors, reference, reference_weights)
        center = train.preprocess(raw[indices].mean(axis=0, keepdims=True), mean)
        before = float(scores(center)[0])
        per_track = scores(lib[indices]).tolist()
        result = album_scoring.aggregate(before, per_track, threshold)
        songs = [tracks[keys[i]] for i in indices]
        liked = sum(not t.disliked and (t.loved or t.rating >= 80 or t.play_count >= 12) for t in songs)
        disliked = sum(t.disliked or 0 < t.rating <= 40 for t in songs)
        # 미청취·중립은 불호 정답으로 쓰지 않는다. 일부 곡만 좋아하는 앨범도 양성일 수 있다.
        label = "positive" if liked >= 2 else "negative" if liked == 0 and disliked >= len(songs) / 2 else "unknown"
        rows.append({"artist": artist, "album": album, "label": label, "before": before, **result})
    def auc(field):
        positive = [r[field] for r in rows if r["label"] == "positive"]
        negative = [r[field] for r in rows if r["label"] == "negative"]
        if not positive or not negative:
            return None
        return float(np.mean([float(a > b) + .5 * (a == b) for a in positive for b in negative]))
    summary = {"albums": len(rows), "library_vectors": len(keys), "artist_holdout": True,
               "positive_albums": sum(r["label"] == "positive" for r in rows),
               "negative_albums": sum(r["label"] == "negative" for r in rows),
               "before_auc": auc("before"), "after_auc": auc("taste"),
               "increased": sum(r["taste"] > r["before"] for r in rows),
               "decreased": sum(r["taste"] < r["before"] for r in rows)}
    Path(args.out).write_text(json.dumps({"summary": summary, "albums": rows}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary))


if __name__ == "__main__":
    main()

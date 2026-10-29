"""취향 기준 만들기: 라이브러리 전체 곡의 CLAP 임베딩 평균.

레시피(플레이리스트) 분류기는 쓰지 않는다. 신보는 내 곡 전체와의 소리 유사도로 고르고, 칸은 장르 DB(Last.fm·Discogs·Deezer)로 나눈다.
여기서 하는 일은 임베딩 평균(유사도를 잴 때 빼는 쏠림)을 구해 data/crate_model.json 에 두는 것뿐이라 몇 초면 끝난다.

    uv run python train.py "C:/Users/me/Music/iTunes/iTunes Music Library.xml"
"""
from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path

import numpy as np

import library

ROOT = Path(__file__).parent
DATA = ROOT / "data"
DEFAULT_EMB = "emb_larger_clap_general"
MODEL_PATH = DATA / "crate_model.json"
VERSION = 2  # 1: 레시피·지역 분류기 (플레이리스트로 학습) → 2: 임베딩 평균만


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


def save(model: dict) -> None:
    MODEL_PATH.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")


def run(xml: str, emb_folder: str = DEFAULT_EMB, log=print) -> dict:
    """라이브러리에 아직 있는 곡의 임베딩 평균 (지운 곡은 뺀다). 저장은 save."""
    tracks, _ = library.load(xml)
    keys, emb = load_embeddings(emb_folder)
    rows = [i for i, k in enumerate(keys) if k in tracks]
    if len(rows) < 30:
        raise ValueError(f"분석한 곡이 {len(rows)}곡뿐이에요. iTunes 라이브러리에 곡 파일이 있는지 확인해 주세요")
    log(f"취향 기준: 라이브러리 {len(rows)}곡의 소리")
    return {
        "version": VERSION,
        "embeddings": emb_folder,
        "trained_at": datetime.now().isoformat(timespec="seconds"),
        "tracks": len(rows),
        "mean": emb[rows].mean(axis=0).round(6).tolist(),
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("xml")
    ap.add_argument("--emb", default=DEFAULT_EMB, help="data/ 아래 임베딩 폴더")
    args = ap.parse_args()
    save(run(args.xml, args.emb))
    print(f"\n저장: {MODEL_PATH}")


if __name__ == "__main__":
    main()

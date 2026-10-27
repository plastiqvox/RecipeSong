"""앨범 커버 임베딩 (CLIP, GPU): 지역 판정 보조.

커버에 들어간 글자(일본어·한국어)와 디자인이 지역을 잘 알려 준다 (앨범 4,615장 시험: 지역 83.2% → 87.6%, 한국 72% → 77%).
레시피·취향에는 거의 도움이 안 돼서 지역 판정에만 쓴다.

- 라이브러리: 음원 파일에 들어 있는 커버를 앨범마다 하나 → data/cover_emb.npz (keys = "아티스트|앨범")
- 신보: Deezer 커버 이미지 → service.py가 data/album_cover.npz에 앨범 id로 저장

    uv run python covers.py "C:/Users/me/Music/iTunes/iTunes Music Library.xml"
"""
from __future__ import annotations

import argparse
import io
import re
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image

import library

MODEL_ID = "openai/clip-vit-base-patch32"
OUT = Path(__file__).parent / "data" / "cover_emb.npz"


def album_key(artist: str, album: str) -> str:
    n = lambda s: re.sub(r"\s+", " ", (s or "").lower()).strip()  # noqa: E731
    return f"{n(artist)}|{n(album)}"


def open_image(data: bytes) -> Image.Image | None:
    try:
        img = Image.open(io.BytesIO(data)).convert("RGB")
        img.thumbnail((256, 256))
        return img
    except Exception:
        return None


def read_cover(path: Path) -> Image.Image | None:
    """m4a(covr) · mp3(APIC) · flac(picture)에 들어 있는 첫 커버"""
    try:
        import mutagen
        f = mutagen.File(str(path))
        if f is None:
            return None
        tags = f.tags or {}
        if "covr" in tags:
            data = bytes(tags["covr"][0])
        elif getattr(f, "pictures", None):
            data = f.pictures[0].data
        else:
            apic = [v for k, v in tags.items() if str(k).startswith("APIC")]
            data = apic[0].data if apic else None
        return open_image(data) if data else None
    except Exception:
        return None


class CoverModel:
    """CLIP 이미지 인코더 (처음 쓸 때 올린다)"""

    def __init__(self, device: str):
        from transformers import CLIPModel, CLIPProcessor
        self.device = device
        self.model = CLIPModel.from_pretrained(MODEL_ID).to(device).eval()
        self.proc = CLIPProcessor.from_pretrained(MODEL_ID)

    def embed(self, images: list[Image.Image]) -> np.ndarray:
        import torch
        out = []
        for s in range(0, len(images), 64):
            inp = self.proc(images=images[s:s + 64], return_tensors="pt").to(self.device)
            with torch.no_grad():
                f = self.model.get_image_features(**inp)
                f = f if isinstance(f, torch.Tensor) else f.pooler_output
            out.append(torch.nn.functional.normalize(f.float(), dim=-1).cpu().numpy())
        return np.concatenate(out) if out else np.zeros((0, 512), np.float32)


def load() -> dict[str, np.ndarray]:
    if not OUT.exists():
        return {}
    d = np.load(OUT, allow_pickle=False)
    return dict(zip(d["keys"].tolist(), d["emb"].astype(np.float32)))


def update_library(tracks: dict, model_factory, log=print) -> int:
    """아직 커버 벡터가 없는 라이브러리 앨범만 분석해 cover_emb.npz에 더한다. 더한 수를 돌려준다."""
    have = load()
    albums: dict[str, library.Track] = {}
    for t in tracks.values():
        k = album_key(t.artist, t.album)
        if t.album and t.path and t.path.exists() and k not in have:
            albums.setdefault(k, t)
    if not albums:
        return 0
    items = list(albums.items())
    with ThreadPoolExecutor(8) as pool:
        imgs = list(pool.map(lambda kv: read_cover(kv[1].path), items))
    ok = [(k, im) for (k, _), im in zip(items, imgs) if im is not None]
    if ok:
        emb = model_factory().embed([im for _, im in ok])
        have.update({k: e for (k, _), e in zip(ok, emb)})
        ks = list(have)
        np.savez(OUT, keys=np.array(ks), emb=np.stack([have[k] for k in ks]).astype(np.float16))
    log(f"[covers] 새 앨범 {len(items)}개 중 커버 {len(ok)}개 분석")
    return len(ok)


def main() -> None:
    import torch
    ap = argparse.ArgumentParser()
    ap.add_argument("xml")
    args = ap.parse_args()
    tracks, _ = library.load(args.xml)
    t0 = time.time()
    device = "cuda" if torch.cuda.is_available() else "cpu"
    n = update_library(tracks, lambda: CoverModel(device))
    print(f"커버 {n}개 더함 · 전체 {len(load())}개 · {time.time() - t0:.0f}초 → {OUT.name}")


if __name__ == "__main__":
    main()

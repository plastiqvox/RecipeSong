"""이미 받은 공용 모델은 그대로 읽고 새 다운로드는 앱 전용 HF_HUB_CACHE에 둔다."""
from __future__ import annotations

import os
import re
from pathlib import Path


def model_source(model_id: str) -> str:
    legacy = os.environ.get("RECIPESONG_LEGACY_MODEL_CACHE")
    if not legacy or not re.fullmatch(r"[\w.-]+/[\w.-]+", model_id):
        return model_id
    name = "models--" + model_id.replace("/", "--")
    owned = os.environ.get("HF_HUB_CACHE")
    if owned and (Path(owned) / name).exists():
        return model_id
    try:
        repo = Path(legacy) / name
        revision = (repo / "refs" / "main").read_text().strip()
        if not re.fullmatch(r"[0-9a-f]{40}", revision):
            return model_id
        snapshot = repo / "snapshots" / revision
        required = ("config.json", "preprocessor_config.json", "tokenizer_config.json", "tokenizer.json",
                    "vocab.json", "merges.txt", "special_tokens_map.json")
        if all((snapshot / f).is_file() for f in required) and any(
            (snapshot / f).is_file() for f in ("pytorch_model.bin", "model.safetensors")
        ):
            return str(snapshot)
    except OSError:
        pass
    return model_id

import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("model_cache", Path(__file__).parents[1] / "ml/model_cache.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ModelCacheTests(unittest.TestCase):
    def test_cached_legacy_model_is_reused_without_importing_or_downloading(self):
        with tempfile.TemporaryDirectory(prefix="recipesong-model-cache-") as folder:
            base = Path(folder)
            repo = base / "legacy/models--example--model"
            revision = "a" * 40
            snapshot = repo / "snapshots" / revision
            snapshot.mkdir(parents=True)
            (repo / "refs").mkdir()
            (repo / "refs/main").write_text(revision)
            for name in ("config.json", "preprocessor_config.json", "tokenizer_config.json", "tokenizer.json",
                         "vocab.json", "merges.txt", "special_tokens_map.json", "model.safetensors"):
                (snapshot / name).write_text("fixture")
            with patch.dict(os.environ, {"RECIPESONG_LEGACY_MODEL_CACHE": str(base / "legacy"), "HF_HUB_CACHE": str(base / "owned")}):
                self.assertEqual(module.model_source("example/model"), str(snapshot))
                self.assertFalse((base / "owned").exists())
                owned = base / "owned/models--example--model"
                owned.mkdir(parents=True)
                self.assertEqual(module.model_source("example/model"), "example/model")
                self.assertEqual(module.model_source("../unsafe"), "../unsafe")
                owned.rmdir()
                (repo / "refs/main").write_text("../../outside")
                self.assertEqual(module.model_source("example/model"), "example/model")


if __name__ == "__main__":
    unittest.main()

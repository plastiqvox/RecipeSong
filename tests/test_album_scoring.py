"""음원·네트워크·사용자 서비스 없이 실제 집계와 프로토콜 경로를 검증한다."""
import ast
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "ml"))
import album_scoring


class AlbumScoringChecks(unittest.TestCase):
    def test_batch_scores_exclude_inactive_and_support_small_libraries(self):
        lib = np.array([[1., 0.], [0., 1.]])
        actual = album_scoring.taste_scores(np.array([[1., 0.], [0., 1.]]), lib, np.array([0., 1.]))
        np.testing.assert_allclose(actual, [0., 1.])

    def test_threshold_excludes_self_and_inactive_tracks(self):
        lib = np.array([[1., 0.], [0., 1.], [1., 0.]])
        self.assertEqual(album_scoring.fit_threshold(lib, np.array([1., 1., 0.])), 0)
        self.assertIsNone(album_scoring.fit_threshold(lib, np.array([1., 0., 0.])))

    def test_good_tracks_survive_album_average(self):
        result = album_scoring.aggregate(.4, [.8, .8, .75, .2, .2, .2, .2], .6)
        self.assertGreater(result["taste"], .4)
        self.assertEqual(result["analyzed_tracks"], 7)

    def test_coverage_changes_rank_with_same_best_tracks(self):
        sparse = album_scoring.aggregate(.5, [.8, .8, .8, .2, .2], .6)
        broad = album_scoring.aggregate(.5, [.8, .8, .8, .7, .7], .6)
        self.assertGreater(broad["taste"], sparse["taste"])

    def test_missing_tracks_are_not_dislikes(self):
        self.assertEqual(album_scoring.aggregate(.5, [], .6), {"taste": .5})
        result = album_scoring.aggregate(.5, [.8, float("nan")], .6)
        self.assertEqual(result["fit_ratio"], 1)
        self.assertEqual(result["analyzed_tracks"], 1)
        self.assertLess(result["taste"], album_scoring.aggregate(.5, [.8] * 3, .6)["taste"])

    def test_service_returns_new_score_using_cached_tracks(self):
        # 서비스 생성자는 모델·캐시를 저장할 수 있어 호출하지 않는다. 실제 두 메서드만 격리 실행.
        tree = ast.parse((ROOT / "ml/service.py").read_text(encoding="utf-8"))
        methods = [n for c in tree.body if isinstance(c, ast.ClassDef) for n in c.body
                   if isinstance(n, ast.FunctionDef) and n.name in ("describe", "score_albums", "embed_clips")]
        module = ast.Module(body=[ast.ClassDef(name="Scorer", bases=[], keywords=[], body=methods, decorator_list=[])], type_ignores=[])
        namespace = {"np": np, "KNN": 20, "album_scoring": album_scoring, "log": lambda *_: None,
                     "train": SimpleNamespace(preprocess=lambda x, mean: x / (np.linalg.norm(x, axis=1, keepdims=True) + 1e-8))}
        exec(compile(ast.fix_missing_locations(module), "service-scoring", "exec"), namespace)
        scorer = namespace["Scorer"]()
        self.assertEqual(scorer.embed_clips([]), [])
        scorer.model = {}
        scorer.mean = np.zeros(2)
        scorer.lib = np.tile([1., 0.], (2, 1))
        scorer.lib_w = np.ones(2)
        scorer.meta = [("Fixture", "One"), ("Fixture", "Two")]
        scorer.fav_x = scorer.dis_x = None
        scorer.track_fit_threshold = .6
        scorer.ensure_embedded = lambda *_: None
        scorer.album_cache = {"1": np.array([0., 1.]), **{f"t{i}": np.array([1., 0.]) for i in range(3)}}
        scorer.album_info = {"1": {"track_ids": ["0", "1", "2", "missing"]}}
        result = scorer.score_albums({"albums": [{"id": 1}]}, 1)["albums"][0]
        self.assertGreater(result["taste"], result["mean_taste"])
        self.assertEqual(result["fit_ratio"], 1)
        self.assertEqual(result["analyzed_tracks"], 3)
        scorer.lib_w[:] = 0
        self.assertEqual(scorer.describe(np.array([1., 0.]))["taste"], 0)


if __name__ == "__main__":
    unittest.main()

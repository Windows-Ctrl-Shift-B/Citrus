"""Regression checks for bounded, cancellable folder scans."""
from concurrent.futures import Future
import importlib.util
import io
from pathlib import Path
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('scanning_app', ROOT / 'src/strata.py')
strata = importlib.util.module_from_spec(spec)
spec.loader.exec_module(strata)


class ControlledPool:
    """Execute only when the test's wait hook allows it, without racing real threads."""
    def __init__(self, max_workers):
        self.max_workers = max_workers
        self.submissions = []
        self.peak_pending = 0

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def submit(self, fn, *args):
        future = Future()
        self.submissions.append((future, fn, args))
        self.peak_pending = max(self.peak_pending,
                                sum(not fut.done() for fut, _, _ in self.submissions))
        return future

    def finish_one(self, futures, **kwargs):
        for future, fn, args in self.submissions:
            if future in futures and not future.done():
                future.set_result(fn(*args))
                return {future}, set(futures) - {future}
        completed = {future for future in futures if future.done()}
        return completed, set(futures) - completed


class ScanningTests(unittest.TestCase):
    def setUp(self):
        self.output = patch.object(strata.sys, 'stdout', io.StringIO())
        self.output.start()
        self.addCleanup(self.output.stop)
        self.clear = patch.object(strata, 'clear')
        self.clear.start()
        self.addCleanup(self.clear.stop)

    def make_tree(self, root, count=20):
        for i in range(count):
            directory = root / ('dir%02d' % i)
            directory.mkdir()
            (directory / 'file').write_bytes(b'x' * (i + 1))

    def test_worker_budget_and_pending_jobs_are_bounded(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.make_tree(Path(tmp))
            for cpus, expected in [(None, 1), (1, 1), (2, 1), (4, 3), (128, 4)]:
                with self.subTest(cpus=cpus):
                    pools = []
                    def pool_factory(max_workers):
                        pool = ControlledPool(max_workers)
                        pools.append(pool)
                        return pool
                    def complete(futures, **kwargs):
                        return pools[0].finish_one(futures, **kwargs)
                    app = strata.App()
                    app.path = tmp
                    with patch.object(strata.os, 'cpu_count', return_value=cpus), \
                         patch.object(strata, 'ThreadPoolExecutor', side_effect=pool_factory), \
                         patch.object(strata, 'wait', side_effect=complete), \
                         patch.object(strata, 'esc_pressed', return_value=False):
                        app.scan()
                    self.assertEqual(pools[0].max_workers, expected)
                    self.assertLessEqual(pools[0].peak_pending, expected)
                    self.assertEqual(len(pools[0].submissions), 20)
                    self.assertEqual(sorted(row['size'] for row in app.entries), list(range(1, 21)))
                    self.assertEqual(len(app.cache), 20)
                    self.assertEqual(app.status, '')

    def test_escape_cancels_before_any_folder_finishes(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.make_tree(Path(tmp))
            pool = ControlledPool(4)
            waits = []
            def waiting(futures, **kwargs):
                waits.append(kwargs)
                if len(waits) == 1:
                    # No finished task: the main thread must still poll Escape.
                    return set(), set(futures)
                return pool.finish_one(futures, **kwargs)
            app = strata.App()
            app.path = tmp
            with patch.object(strata.os, 'cpu_count', return_value=8), \
                 patch.object(strata, 'ThreadPoolExecutor', return_value=pool), \
                 patch.object(strata, 'wait', side_effect=waiting), \
                 patch.object(strata, 'esc_pressed', side_effect=[False, True]):
                app.scan()
            self.assertTrue(app.cancel.is_set())
            self.assertEqual(app.cache, {})
            self.assertEqual(len(pool.submissions), 4)
            self.assertEqual(waits[0]['timeout'], 0.05)
            self.assertIn('partial', app.status)

    def test_cached_and_forced_scan_sizes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            self.make_tree(root, count=1)
            (root / 'loose').write_bytes(b'12345')
            app = strata.App()
            app.path = tmp
            with patch.object(strata, 'esc_pressed', return_value=False):
                app.scan()
                (root / 'dir00/file').write_bytes(b'123456789')
                app.scan()
                self.assertEqual({r['name']: r['size'] for r in app.entries}, {'dir00': 1, 'loose': 5})
                app.scan(force=True)
                self.assertEqual({r['name']: r['size'] for r in app.entries}, {'dir00': 9, 'loose': 5})

    def test_dir_size_checks_cancellation_inside_wide_folder(self):
        cancel = threading.Event()
        visited = []
        def entries():
            for i in range(1000):
                visited.append(i)
                if i == 5:
                    cancel.set()
                yield SimpleNamespace(is_symlink=lambda: False,
                                      is_dir=lambda **kw: False,
                                      stat=lambda **kw: SimpleNamespace(st_size=1))
        directory = unittest.mock.MagicMock()
        directory.__enter__.return_value = entries()
        with patch.object(strata.os, 'scandir', return_value=directory):
            total = strata.dir_size('/fake', cancel)
        self.assertEqual(total, 64)
        self.assertEqual(len(visited), 65)


if __name__ == '__main__':
    unittest.main()

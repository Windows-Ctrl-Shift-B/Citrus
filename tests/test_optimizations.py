"""Run with: python -m unittest discover -s tests -v (no third-party packages)."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('strata', ROOT / 'src/strata.py')
strata = importlib.util.module_from_spec(spec)
spec.loader.exec_module(strata)


class CollectTests(unittest.TestCase):
    def test_limited_results_match_full_stable_sort(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for i in range(650):
                directory = root / str(i % 45)
                directory.mkdir(exist_ok=True)
                (directory / str(i)).write_bytes(b'x' * (i % 123))
            app = strata.App()
            app.path = tmp
            progress = []
            all_files = app.collect(20, lambda *args: None)
            expected = sorted(all_files, key=lambda row: row[1], reverse=True)
            for limit in (1, 30, 500, 1000):
                actual = app.collect(20, lambda *args: progress.append(args), limit=limit)
                self.assertEqual(actual, expected[:limit])
            self.assertTrue(progress)
            self.assertGreater(progress[-1][1], 30)
            app.cancel.set()
            self.assertEqual(app.collect(0, lambda *args: None, limit=30), [])

    def test_empty_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            app = strata.App()
            app.path = tmp
            self.assertEqual(app.collect(0, lambda *args: None, limit=500), [])

    def test_packaged_sources_match(self):
        package = (ROOT / 'Citrus.cmd').read_text(encoding='utf-8')
        for marker, filename in [('CS', 'StrataCmd.cs'), ('PY', 'strata.py')]:
            embedded = package.split('#<' + marker + '>\n', 1)[1].split('#</' + marker + '>', 1)[0]
            self.assertEqual(embedded.strip(), (ROOT / 'src' / filename).read_text(encoding='utf-8-sig').strip())


@unittest.skipUnless(os.name == 'nt', 'requires the Windows .NET Framework compiler')
class WindowsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        cls.probe = cls.root / 'probe.exe'
        csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        if not csc.exists():
            csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
        cls.app = cls.root / 'Citrus.exe'
        subprocess.run([str(csc), '/nologo', '/optimize', '/reference:Microsoft.VisualBasic.dll',
                        '/reference:System.Management.dll', '/out:' + str(cls.app),
                        str(ROOT / 'src/StrataCmd.cs')], check=True)
        subprocess.run([str(csc), '/nologo', '/out:' + str(cls.probe),
                        str(ROOT / 'tests/WindowsProbe.cs')], check=True)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def probe_run(self, *args):
        return subprocess.run([str(self.probe), str(self.app), *map(str, args)],
                              check=True, capture_output=True, text=True)

    def test_top_selection(self):
        self.probe_run('top')

    def test_streamed_zip_round_trip(self):
        source = self.root / 'source'
        source.mkdir()
        (source / 'nested').mkdir()
        contents = {'empty': b'', 'nested/caf\u00e9.txt': b'citrus' * 40000,
                    'random.bin': os.urandom(2 * 1024 * 1024 + 17)}
        for name, data in contents.items():
            (source / name).write_bytes(data)
        output = self.root / 'files.zip'
        self.probe_run('zip', source, output)
        with zipfile.ZipFile(output) as archive:
            self.assertIsNone(archive.testzip())
            self.assertEqual(set(archive.namelist()), set(contents))
            for name, data in contents.items():
                self.assertEqual(archive.read(name), data)
        self.probe_run('zip', source / 'random.bin', self.root / 'single.zip')
        with zipfile.ZipFile(self.root / 'single.zip') as archive:
            self.assertEqual(archive.read('random.bin'), contents['random.bin'])

    def test_cli_biggest(self):
        source = self.root / 'biggest'
        source.mkdir()
        for i in range(40):
            with (source / ('file%02d' % i)).open('wb') as stream:
                stream.truncate(1024 * 1024 + i)
        result = subprocess.run([str(self.app), '--biggest', str(source)],
                                check=True, capture_output=True, text=True)
        self.assertEqual([line.rsplit('file', 1)[1] for line in result.stdout.splitlines()],
                         ['%02d' % i for i in range(39, 9, -1)])

    def test_launcher_keeps_only_executable(self):
        env = dict(os.environ, LOCALAPPDATA=str(self.root / 'localappdata'))
        cache = Path(env['LOCALAPPDATA']) / 'Citrus'
        command = 'cmd.exe /d /s /c ""%s" --version"' % (ROOT / 'Citrus.cmd')
        result = subprocess.run(command, env=env, cwd=self.root,
                                capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('Citrus 1.0', result.stdout)
        self.assertEqual([p.name for p in cache.iterdir()], ['Citrus.exe'])
        built_at = (cache / 'Citrus.exe').stat().st_mtime_ns
        # Cached launches also remove leftovers from older releases.
        (cache / 'Citrus.cs').write_text('old build source')
        (cache / 'citrus.ico').write_bytes(b'old icon')
        subprocess.run(command, env=env, cwd=self.root,
                       check=True, capture_output=True)
        self.assertEqual([p.name for p in cache.iterdir()], ['Citrus.exe'])
        self.assertEqual((cache / 'Citrus.exe').stat().st_mtime_ns, built_at)


if __name__ == '__main__':
    unittest.main()

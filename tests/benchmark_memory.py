"""Compare a baseline build with the current build using disposable test data.

python tests/benchmark_memory.py path/to/baseline.exe path/to/baseline.py
Requires Windows, Python 3 and the .NET Framework compiler; no extra packages.
"""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import tracemalloc
from types import SimpleNamespace
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def scan_peak(path, limited):
    spec = importlib.util.spec_from_file_location('benchmark_app', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    class Directory:
        def __enter__(self):
            return (SimpleNamespace(path='/fixture/file%d' % i,
                                    is_symlink=lambda: False,
                                    is_dir=lambda **kw: False,
                                    stat=lambda **kw: SimpleNamespace(st_size=1048576))
                    for i in range(100000))
        def __exit__(self, *args):
            pass
    app = module.App()
    app.path = '/fixture'
    with patch.object(module.os, 'scandir', return_value=Directory()):
        tracemalloc.start()
        if limited:
            rows = app.collect(1048576, lambda *args: None, limit=500)
        else:
            rows = app.collect(1048576, lambda *args: None)
            rows.sort(key=lambda row: row[1], reverse=True)
            rows = rows[:500]
        peak = tracemalloc.get_traced_memory()[1]
        tracemalloc.stop()
    assert len(rows) == 500
    return peak


def main():
    baseline, baseline_py = map(Path, sys.argv[1:])
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        if not csc.exists():
            csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
        probe = tmp / 'probe.exe'
        subprocess.run([str(csc), '/nologo', '/out:' + str(probe),
                        str(ROOT / 'tests/WindowsProbe.cs')], check=True)
        source = tmp / 'random.bin'
        with source.open('wb') as stream:
            for _ in range(64):
                stream.write(os.urandom(1024 * 1024))
        for label, app in [('baseline', baseline), ('updated', ROOT / 'Citrus.exe')]:
            archive_path = tmp / (label + '.zip')
            result = subprocess.run([str(probe), str(app.resolve()), 'zip', str(source), str(archive_path)],
                                    check=True, capture_output=True, text=True)
            with zipfile.ZipFile(archive_path) as archive:
                assert archive.testzip() is None
            print(label, 'zip', result.stdout.strip(), 'archive_bytes=' + str(archive_path.stat().st_size))
        print('baseline python scan peak_bytes=' + str(scan_peak(baseline_py, False)))
        print('updated python scan peak_bytes=' + str(scan_peak(ROOT / 'src/strata.py', True)))
        original = subprocess.run(['git', 'show', '4ff2c35:Citrus.cmd'], cwd=ROOT,
                                  check=True, capture_output=True).stdout
        launcher = tmp / 'baseline.cmd'
        launcher.write_bytes(original.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n'))
        for label, script in [('baseline', launcher), ('updated', ROOT / 'Citrus.cmd')]:
            cache_root = tmp / (label + '-cache')
            subprocess.run('cmd.exe /d /s /c ""%s" --version"' % script,
                           env=dict(os.environ, LOCALAPPDATA=str(cache_root)), cwd=tmp,
                           check=True, capture_output=True, timeout=60)
            print(label, 'launcher_cache_bytes=' + str(sum(p.stat().st_size for p in (cache_root / 'Citrus').iterdir())))


if __name__ == '__main__':
    main()

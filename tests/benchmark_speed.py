"""Windows throughput comparison with disposable files, no third-party packages.

Build the current executable, then:
  python tests/benchmark_speed.py path/to/baseline.exe
Times are medians of three runs. Cached metadata is not a cold-drive benchmark.
"""
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    apps = [('baseline', Path(sys.argv[1]).resolve()), ('updated', ROOT / 'Citrus.exe')]
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        if not csc.exists():
            csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
        probe = tmp / 'probe.exe'
        subprocess.run([str(csc), '/nologo', '/out:' + str(probe),
                        str(ROOT / 'tests/WindowsProbe.cs')], check=True)

        def run(app, *args):
            result = subprocess.run([str(probe), str(app), *map(str, args)],
                                    check=True, capture_output=True, text=True, timeout=60)
            return {key: float(value) for key, value in
                    (line.split('=', 1) for line in result.stdout.splitlines())}

        for kind in ('compressible', 'random'):
            source = tmp / (kind + '.bin')
            with source.open('wb') as stream:
                for i in range(64):
                    stream.write(b'Citrus!\0' * (1024 * 128) if kind == 'compressible' else os.urandom(1024 * 1024))
            readings = {label: [] for label, _ in apps}
            archive = tmp / 'test.zip'
            for repeat in range(3):
                for label, app in (apps if repeat % 2 == 0 else apps[::-1]):
                    readings[label].append(run(app, 'zip', source, archive))
                    with zipfile.ZipFile(archive) as zipped:
                        assert zipped.testzip() is None
                    archive.unlink()
            for label, app in apps:
                print(json.dumps(dict(workload='zip-' + kind, build=label,
                                      elapsed_ms=statistics.median(r['elapsed_ms'] for r in readings[label]),
                                      cpu_ms=statistics.median(r['cpu_ms'] for r in readings[label]),
                                      peak_bytes=max(r['peak_working_set_bytes'] for r in readings[label]),
                                      exe_bytes=app.stat().st_size)), flush=True)
            source.unlink()

        fixture = tmp / 'scan'
        fixture.mkdir()
        for d in range(32):
            folder = fixture / str(d)
            folder.mkdir()
            for f in range(64):
                (folder / str(f)).write_bytes(b'x' * 128)
        old_workers = min(32, max(8, (os.cpu_count() or 1) * 4))
        new_workers = min(4, max(1, (os.cpu_count() or 1) - 1))
        readings = {label: [] for label, _ in apps}
        for repeat in range(3):
            for label, app in (apps if repeat % 2 == 0 else apps[::-1]):
                workers = old_workers if label == 'baseline' else new_workers
                metrics = run(app, 'scan', fixture, workers)
                assert metrics['scan_total_bytes'] == 32 * 64 * 128
                readings[label].append(metrics)
        for label, _ in apps:
            print(json.dumps(dict(workload='warm-metadata-scan', build=label,
                                  elapsed_ms=statistics.median(r['scan_elapsed_ms'] for r in readings[label]),
                                  cpu_ms=statistics.median(r['scan_cpu_ms'] for r in readings[label]),
                                  passes_per_run=readings[label][0]['scan_repeats'],
                                  workers=readings[label][0]['scan_workers'])), flush=True)


if __name__ == '__main__':
    main()

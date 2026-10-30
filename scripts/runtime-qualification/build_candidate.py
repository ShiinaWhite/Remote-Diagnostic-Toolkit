"""Build a fixed official-source candidate in ignored developer artifacts only.

No global installs, source patches, dependency re-resolution, release upload, or RDT client integration.
Run `prepare`, `install`, `bundle`, `assets`, then `sea`; inspect each log before proceeding.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import pathlib
import subprocess
import tarfile
import urllib.request
import zipfile

REPO = pathlib.Path(__file__).resolve().parents[2]
BASE = REPO / 'artifacts/runtime-qualification'
SOURCE = BASE / 'upstream'
TOOLS = BASE / 'build-tools'
IDENTITY_FILE = pathlib.Path(__file__).with_name('runtime-identity.json')
IDENTITY = json.loads(IDENTITY_FILE.read_text(encoding='utf-8'))
COMMIT = IDENTITY['upstream_commit']
NODE_VERSION = IDENTITY['node_version']
PNPM_VERSION = IDENTITY['pnpm_version']
NODE_DIR = TOOLS / f'node-v{NODE_VERSION}-win-x64'
NODE = NODE_DIR / 'node.exe'
PNPM = TOOLS / 'package/bin/pnpm.cjs'
ASSET_PACKAGES = ['node-repl-host', 'android-emulator-plugin', 'ios-simulator-plugin',
                  'browser-use-plugin', 'documents-plugin', 'pdf-plugin', 'presentations-plugin', 'spreadsheets-plugin']


def download(url: str, path: pathlib.Path) -> bytes:
    if not url.startswith('https://'):
        raise ValueError('HTTPS_REQUIRED')
    data = urllib.request.urlopen(url, timeout=120).read()
    path.write_bytes(data)
    return data


def check_source() -> None:
    actual = subprocess.check_output(['git', '-C', str(SOURCE), 'rev-parse', 'HEAD'], text=True).strip()
    if actual != COMMIT:
        raise ValueError('SOURCE_COMMIT_MISMATCH')
    subprocess.run(['git', '-C', str(SOURCE), 'diff', '--exit-code', '--'], check=True)
    if hashlib.sha256((SOURCE/'pnpm-lock.yaml').read_bytes()).hexdigest() != IDENTITY['source_lock_sha256']:
        raise ValueError('UPSTREAM_LOCK_CHANGED')


def prepare() -> None:
    TOOLS.mkdir(parents=True, exist_ok=True)
    node_archive = f'node-v{NODE_VERSION}-win-x64.zip'
    sums = download(f'https://nodejs.org/dist/v{NODE_VERSION}/SHASUMS256.txt', TOOLS/'node-SHASUMS256.txt').decode()
    expected = next(line.split()[0] for line in sums.splitlines() if line.split()[-1] == node_archive)
    node_data = download(f'https://nodejs.org/dist/v{NODE_VERSION}/{node_archive}', TOOLS/node_archive)
    if hashlib.sha256(node_data).hexdigest() != expected or expected != IDENTITY['node_archive_sha256']:
        raise ValueError('NODE_ARCHIVE_HASH_MISMATCH')
    with zipfile.ZipFile(TOOLS/node_archive) as archive:
        if any(pathlib.PurePosixPath(name).is_absolute() or '..' in pathlib.PurePosixPath(name).parts for name in archive.namelist()):
            raise ValueError('UNSAFE_ARCHIVE')
        archive.extractall(TOOLS)
    metadata = json.loads(download(f'https://registry.npmjs.org/pnpm/{PNPM_VERSION}', TOOLS/'pnpm-metadata.json'))
    pnpm_data = download(metadata['dist']['tarball'], TOOLS/f'pnpm-{PNPM_VERSION}.tgz')
    if 'sha512-' + base64.b64encode(hashlib.sha512(pnpm_data).digest()).decode() != metadata['dist']['integrity']:
        raise ValueError('PNPM_INTEGRITY_MISMATCH')
    if hashlib.sha256(pnpm_data).hexdigest() != IDENTITY['pnpm_archive_sha256']:
        raise ValueError('PNPM_PIN_MISMATCH')
    with tarfile.open(TOOLS/f'pnpm-{PNPM_VERSION}.tgz') as archive:
        archive.extractall(TOOLS, filter='data')
    if not SOURCE.exists():
        subprocess.run(['git', 'clone', '--filter=blob:none', '--no-checkout', IDENTITY['upstream_repository'], str(SOURCE)], check=True)
        subprocess.run(['git', '-C', str(SOURCE), 'checkout', '--detach', COMMIT], check=True)
    check_source()
    provenance = {'node_url': f'https://nodejs.org/dist/v{NODE_VERSION}/{node_archive}',
                  'node_archive_sha256': expected, 'pnpm_url': metadata['dist']['tarball'],
                  'pnpm_archive_integrity': metadata['dist']['integrity'], 'upstream_commit': COMMIT}
    (BASE/'toolchain-provenance.json').write_text(json.dumps(provenance, indent=2), encoding='utf-8')


def environment() -> dict:
    for path in (BASE/'build-home', BASE/'build-temp', TOOLS/'bin'):
        path.mkdir(parents=True, exist_ok=True)
    (TOOLS/'bin/pnpm.cmd').write_text(f'@"{NODE}" "{PNPM}" %*\n')
    system = pathlib.Path(os.environ['SystemRoot'])
    env = {key: os.environ[key] for key in ('SystemRoot','WINDIR','SystemDrive','COMSPEC','PATHEXT') if key in os.environ}
    env.update(HOME=str(BASE/'build-home'), USERPROFILE=str(BASE/'build-home'),
        APPDATA=str(BASE/'build-home/AppData/Roaming'), LOCALAPPDATA=str(BASE/'build-home/AppData/Local'),
        TEMP=str(BASE/'build-temp'), TMP=str(BASE/'build-temp'), PNPM_HOME=str(TOOLS/'bin'),
        npm_config_cache=str(BASE/'npm-cache'), COREPACK_ENABLE_PROJECT_SPEC='0',
        npm_config_update_notifier='false',
        TURBO_TELEMETRY_DISABLED='1', DO_NOT_TRACK='1',
        PATH=os.pathsep.join(map(str, (TOOLS/'bin', NODE_DIR, system/'System32',
            system/'System32/WindowsPowerShell/v1.0', pathlib.Path(r'C:\Program Files\Git\cmd')))))
    return env


def run_pnpm(arguments: list[str]) -> None:
    check_source()
    if subprocess.check_output([str(NODE), '--version'], text=True).strip() != 'v' + NODE_VERSION:
        raise ValueError('NODE_VERSION_MISMATCH')
    if hashlib.sha256(NODE.read_bytes()).hexdigest() != IDENTITY['node_executable_sha256']:
        raise ValueError('NODE_EXECUTABLE_HASH_MISMATCH')
    command = [str(NODE), str(PNPM), *arguments]
    log = BASE / ('build-' + OPTIONS.step + '.log')
    with log.open('a', encoding='utf-8') as stream:
        stream.write(json.dumps({'arguments': arguments, 'commit': COMMIT}) + '\n')
        stream.flush()
        result = subprocess.run(command, cwd=SOURCE, env=environment(), stdout=stream, stderr=subprocess.STDOUT)
    print(json.dumps({'step': OPTIONS.step, 'exit': result.returncode, 'log': str(log.relative_to(REPO))}))
    if result.returncode:
        raise SystemExit(result.returncode)
    check_source()


def inject() -> None:
    check_source()
    dist = SOURCE/'apps/zcode-cli/packages/cli/dist'
    binary = dist/'zcode-windows-x64.exe'
    blob = dist/'zcode-win-x64.sea.blob'
    # The official build produced both inputs and removed the invalidated Node signature.
    # Execute the exact locked postject JS entry with an argument array, avoiding the
    # upstream .cmd shell quoting failure for a development checkout containing spaces.
    import re
    marker = re.search(rb'NODE_SEA_FUSE_[a-z0-9]+:0', binary.read_bytes(), re.I)
    if not marker or not blob.is_file():
        raise ValueError('OFFICIAL_SEA_PREPARATION_REQUIRED')
    package = SOURCE/'node_modules/postject'
    manifest = json.loads((package/'package.json').read_text(encoding='utf-8'))
    entry = manifest['bin']
    if isinstance(entry, dict):
        entry = entry['postject']
    if manifest['version'] != '1.0.0-alpha.6':
        raise ValueError('POSTJECT_VERSION_MISMATCH')
    command = [str(NODE), str(package/entry), str(binary), 'NODE_SEA_BLOB', str(blob),
               '--sentinel-fuse', marker.group(0).removesuffix(b':0').decode()]
    with (BASE/'build-inject.log').open('w', encoding='utf-8') as log:
        subprocess.run(command, cwd=SOURCE, env=environment(), stdout=log, stderr=subprocess.STDOUT, check=True)
    print(json.dumps({'injection': 'complete', 'sha256': hashlib.sha256(binary.read_bytes()).hexdigest(),
                      'source_commit': COMMIT, 'postject_version': manifest['version']}))
    check_source()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('step', choices=['prepare', 'install', 'bundle', 'assets', 'sea', 'inject'])
    OPTIONS = parser.parse_args()
    if os.name != 'nt':
        raise SystemExit('WINDOWS_BUILD_HOST_REQUIRED')
    if OPTIONS.step == 'prepare':
        prepare()
    elif OPTIONS.step == 'install':
        # Suppress unrelated desktop/download lifecycle hooks. Required CLI/SEA assets
        # are built explicitly below and SEA's own completeness checks remain enabled.
        run_pnpm(['install','--frozen-lockfile','--ignore-scripts','--filter','@zcode/cli...']
            + [argument for name in ASSET_PACKAGES for argument in ('--filter', '@zcode/'+name+'...')]
            + ['--store-dir','../pnpm-store'])
    elif OPTIONS.step == 'bundle':
        run_pnpm(['--filter','@zcode/cli...','build'])
        run_pnpm(['exec','tsc','-b','packages/shared'])
    elif OPTIONS.step == 'assets':
        run_pnpm([argument for name in ASSET_PACKAGES for argument in ('--filter', '@zcode/'+name+'...')] + ['build'])
    elif OPTIONS.step == 'sea':
        run_pnpm(['--filter','@zcode/cli','sea','--target','win-x64','--node-binary','win-x64='+str(NODE)])
    elif OPTIONS.step == 'inject':
        inject()

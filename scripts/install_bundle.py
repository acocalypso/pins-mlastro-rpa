#!/usr/bin/env python3
"""Verify, install or restore an MLAstro integration bundle on Linux."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parent

def fail(message):
    raise RuntimeError(message)

def hash_file(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for part in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(part)
    return h.hexdigest()

def no_symlinks(path):
    path = Path(path)
    if not path.is_absolute() or '..' in path.parts: fail('Paths must be absolute and contain no parent traversal: ' + str(path))
    for p in [path, *path.parents]:
        if p.is_symlink(): fail('Symlink in path: ' + str(p))
    return path

def verify():
    manifest = json.loads((ROOT / 'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('format') != 1 or not isinstance(manifest.get('files'), dict): fail('Invalid manifest')
    expected = manifest['files']
    for rel, sha in expected.items():
        p = PurePosixPath(rel)
        if p.is_absolute() or '..' in p.parts or '.' in p.parts or '\\' in rel or not re.fullmatch(r'[0-9a-f]{64}', sha): fail('Unsafe manifest entry')
        path = ROOT.joinpath(*p.parts)
        no_symlinks(path)
        if not path.is_file() or hash_file(path) != sha: fail('Missing or modified bundle file: ' + rel)
    actual = set()
    for path in ROOT.rglob('*'):
        if path.is_symlink(): fail('Symlink in bundle: ' + str(path))
        if path.is_file(): actual.add(path.relative_to(ROOT).as_posix())
    if actual != set(expected) | {'manifest.json'}: fail('Unexpected or missing bundle files')
    required = {'install_bundle.py', 'USER-GUIDE.md', 'BUILD.md', 'UPSTREAM.md', 'SOURCE-NOTICE.md', 'licenses/MPL-2.0.txt', 'licenses/GPL-3.0.txt', 'licenses/dotnet/LICENSE.txt', 'licenses/dotnet/THIRD-PARTY-NOTICES.txt', 'licenses/system-io-ports/THIRD-PARTY-NOTICES.txt', 'payload/plugin/NINA.Plugins.MLAstroRPA.Linux.dll',  'payload/ninaAPI/ninaAPI.dll', 'payload/web/index.html'}
    if not required <= set(expected): fail('Missing required component')
    for rel in expected:
        if rel not in required and not rel.startswith('payload/web/'): fail('Unexpected component: ' + rel)
    return manifest

def get_json(port, endpoint):
    with urllib.request.urlopen(f'http://127.0.0.1:{port}{endpoint}', timeout=3) as r:
        value = json.load(r)
    if not isinstance(value, dict): fail('Invalid status response')
    return value

def require_false(obj, keys, label):
    if not isinstance(obj, dict) or any(obj.get(k) is not False for k in keys): fail(label + ' busy or unknown')

def idle(fresh=False):
    try:
        status = get_json(1891, '/status')
    except Exception:
        if not fresh: raise RuntimeError('MLAstro status unavailable; upgrade refused')
    else:
        require_false(status, ['automationEnabled', 'sessionRunning', 'manualMoveActive'], 'MLAstro')
        if status.get('apiVersion') == 2:
            if status.get('continuousJogSupported') is not False: fail('MLAstro jog capability unknown')
            if 'jogActive' in status and status['jogActive'] is not False: fail('MLAstro jog active or unknown')
        elif 'apiVersion' not in status or status['apiVersion'] == 1:
            require_false(status, ['jogActive'], 'MLAstro jog')
        else: fail('Unsupported MLAstro status version')
        if status.get('motionInhibited') is not False: fail('MLAstro motion state faulted or unknown')
    tppa = get_json(5000, '/api/tppa/info')
    if tppa.get('Success') is not True: fail('TPPA status unavailable')
    require_false(tppa, ['IsRunning'], 'TPPA')
    camera = get_json(1888, '/v2/api/equipment/camera/info')
    mount = get_json(1888, '/v2/api/equipment/mount/info')
    if camera.get('Success') is not True or mount.get('Success') is not True: fail('Equipment status unavailable')
    require_false(camera.get('Response'), ['IsExposing', 'LiveViewEnabled'], 'Camera')
    require_false(mount.get('Response'), ['Slewing'], 'Mount')

def installed_health():
    # Verify both direct state and the newly installed full ninaAPI relay.
    idle(False)
    status = get_json(1888, '/v2/api/mlastro/status')
    if status.get('apiVersion') != 2 or status.get('continuousJogSupported') is not False:
        fail('Installed relay or MLAstro API version is incompatible')
    watchdog = get_json(1888, '/v2/api/mlastro/watchdog')
    if (type(watchdog.get('supported')) is not bool or 'enabled' not in watchdog
            or (watchdog['enabled'] is not None and type(watchdog['enabled']) is not bool)
            or not isinstance(watchdog.get('code'), str) or not watchdog['code']):
        fail('Installed relay watchdog response is invalid')

def service(action, name):
    subprocess.run(['systemctl', action, name], check=True)

def atomic_copy(source, target, executable=False, metadata=None):
    no_symlinks(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    no_symlinks(target)
    tmp = target.with_name('.' + target.name + '.mlastro-tmp')
    if tmp.exists() or tmp.is_symlink(): fail('Temporary file already exists: ' + str(tmp))
    try:
        shutil.copyfile(source, tmp)
        if metadata is not None:
            os.chmod(tmp, metadata['mode'])
            if hasattr(os, 'chown'): os.chown(tmp, metadata['uid'], metadata['gid'])
        else:
            os.chmod(tmp, 0o755 if executable else 0o644)
        os.replace(tmp, target)
    finally:
        if tmp.exists(): tmp.unlink()

def destination_root(path):
    path = no_symlinks(Path(path))
    forbidden = {Path('/'), Path('/bin'), Path('/boot'), Path('/dev'), Path('/etc'), Path('/lib'), Path('/lib64'), Path('/proc'), Path('/root'), Path('/run'), Path('/sbin'), Path('/sys'), Path('/tmp'), Path('/usr'), Path('/var')}
    if path == Path(path.anchor) or path in forbidden: fail('Unsafe destination root: ' + str(path))
    return path

def targets(args, manifest):
    plugin_root = destination_root(args.plugin_root)
    plugin = plugin_root / 'MLAstroRPA.Linux'
    web = destination_root(args.web_root)
    nina = no_symlinks(Path(args.nina_dll))
    destination_root(nina.parent)
    home = destination_root(args.pins_home)
    if nina.name != 'ninaAPI.dll': fail('The installed ninaAPI file must be named ninaAPI.dll')
    if not all(x.is_dir() for x in [home, Path(args.plugin_root), web]) or not nina.is_file(): fail('PINS home, plugin root, web root and installed ninaAPI DLL must exist')
    backup = home / '.mlastro-backups'
    if any(p.is_relative_to(backup) or backup.is_relative_to(p) for p in (plugin, web, nina)):
        fail('Destinations must not overlap the backup directory')
    if len({str(plugin), str(web), str(nina)}) != 3 or nina.is_relative_to(plugin) or nina.is_relative_to(web) or plugin.is_relative_to(web) or web.is_relative_to(plugin): fail('Destination paths overlap')
    out = []
    for rel in manifest['files']:
        if rel.startswith('payload/plugin/'):
            dest = plugin / rel.removeprefix('payload/plugin/')
        elif rel == 'payload/ninaAPI/ninaAPI.dll': dest = nina
        elif rel.startswith('payload/web/'): dest = web / rel.removeprefix('payload/web/')
        else: continue
        no_symlinks(dest)
        out.append((rel, dest))
    return sorted(out, key=lambda item: item[0] == 'payload/web/index.html')

def snapshot(args, files):
    base = no_symlinks(Path(args.pins_home) / '.mlastro-backups')
    base.mkdir(exist_ok=True, mode=0o700)
    backup = base / datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    backup.mkdir(mode=0o700)
    records = []
    for i, (_, dest) in enumerate(files):
        no_symlinks(dest)
        exists = dest.exists()
        if exists:
            if not dest.is_file(): fail('Destination is not a regular file: ' + str(dest))
            shutil.copy2(dest, backup / str(i))
        metadata = None
        if exists:
            stat = dest.stat()
            metadata = {'mode': stat.st_mode & 0o7777, 'uid': stat.st_uid, 'gid': stat.st_gid}
        records.append({'path': str(dest), 'exists': exists, 'file': str(i), 'metadata': metadata})
    (backup / 'restore.json').write_text(json.dumps({'service': args.service, 'files': records}, indent=2), encoding='utf-8')
    return backup

def restore(backup, args):
    backup = no_symlinks(Path(backup))
    base = no_symlinks(Path(args.pins_home) / '.mlastro-backups')
    if backup.parent != base or not backup.is_dir(): fail('Backup must be a direct child of the PINS backup directory')
    data = json.loads((backup / 'restore.json').read_text(encoding='utf-8'))
    if data['service'] != args.service: fail('Backup service does not match')
    allowed = [Path(args.plugin_root) / 'MLAstroRPA.Linux', Path(args.web_root)]
    nina = Path(args.nina_dll)
    for item in data['files']:
        dst = no_symlinks(Path(item['path']))
        if not (dst == nina or any(dst.is_relative_to(p) for p in allowed)): fail('Unsafe backup destination')
        if not re.fullmatch(r'\d+', item['file']): fail('Unsafe backup file')
        if item['exists']:
            src = no_symlinks(backup / item['file'])
            atomic_copy(src, dst, metadata=item['metadata'])
        elif dst.exists(): dst.unlink()

def main():
    p = argparse.ArgumentParser(description=__doc__)
    mode = p.add_mutually_exclusive_group(required=True)
    mode.add_argument('--check', action='store_true')
    mode.add_argument('--install', action='store_true')
    mode.add_argument('--rollback', metavar='BACKUP')
    for name in ('pins-home', 'plugin-root', 'nina-dll', 'web-root', 'service'): p.add_argument('--' + name, required=True)
    p.add_argument('--fresh-install', action='store_true', help='Permit missing MLAstro status ONLY when plugin is not installed')
    args = p.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.@-]*(?:\.service)?', args.service): p.error('Invalid systemd service name')
    manifest = verify()
    files = targets(args, manifest)
    plugin_file = Path(args.plugin_root) / 'MLAstroRPA.Linux/NINA.Plugins.MLAstroRPA.Linux.dll'
    if args.fresh_install and (plugin_file.exists() or args.rollback): fail('--fresh-install is only for a first installation')
    idle(args.fresh_install)
    print('Verified bundle and idle status; version:', manifest['version'], flush=True)
    if args.check: return
    if os.geteuid() != 0: fail('Installation requires appropriate privileges (run as root)')
    if args.rollback:
        service('stop', args.service)
        try: restore(args.rollback, args)
        except Exception:
            print('Restore incomplete; service remains stopped. Recover from backup before starting.', file=sys.stderr)
            raise
        service('start', args.service)
        print('Restored', args.rollback)
        return
    backup = snapshot(args, files)
    print('Backup:', backup, flush=True)
    service('stop', args.service)
    start_attempted = False
    try:
        for rel, dest in files:
            atomic_copy(ROOT / rel, dest, False)
        start_attempted = True
        service('start', args.service)
        for _ in range(30):
            try:
                installed_health()
                break
            except Exception:
                time.sleep(1)
        else: fail('Post-install status check failed')
    except Exception:
        # If the new service started, stop it before restoring its files.
        if start_attempted:
            subprocess.run(['systemctl', 'stop', args.service], check=True)
        restore(backup, args)
        service('start', args.service)
        raise
    print('Installation complete. Keep backup:', backup)

if __name__ == '__main__':
    try: main()
    except Exception as exc:
        print('ERROR:', exc, file=sys.stderr)
        sys.exit(1)

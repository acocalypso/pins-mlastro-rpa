#!/usr/bin/env python3
"""Assemble a deliberately small, versioned integration bundle from built artifacts."""
import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path

VERSIONS = {
    "pins": "3.3.0.1058 nightly", "dotnet_runtime": "10",
    "tppa": "2.2.8.0", "ninaAPI": "2.2.15.2 (custom full build with relay)",
    "touch_n_stars": "6.5.0-beta22-beta / 73e378a66783d474c91cf971c4ab91d5e446b26e",
    "rpa_firmware": "1.13.0",
}

def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()

def copy_file(src, dst):
    if src.is_symlink() or not src.is_file():
        raise ValueError(f'Not a regular, non-symlink file: {src}')
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(src, dst)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ('version', 'plugin', 'worker', 'nina-api', 'web-dir', 'output'):
        p.add_argument('--' + name, required=True)
    p.add_argument('--source-revision', help='40-hex corresponding source commit')
    p.add_argument('--component-version', action='append', default=[], metavar='NAME=VERSION', help='Optional supplied version claim; not verified')
    args = p.parse_args()
    declared_versions = {}
    for entry in args.component_version:
        name, sep, value = entry.partition('=')
        if not sep or not re.fullmatch(r'[A-Za-z0-9_.-]+', name) or not value or name in declared_versions:
            p.error('component-version must be a unique NAME=VERSION')
        declared_versions[name] = value
    if args.source_revision and not re.fullmatch(r'[0-9a-fA-F]{40}', args.source_revision):
        p.error('source-revision must be a 40-hex commit')
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,63}', args.version):
        p.error('version must be a short alphanumeric release label')
    root = Path(args.output).absolute()
    if root.exists():
        p.error('output must not already exist')
    web = Path(args.web_dir)
    if web.is_symlink() or not web.is_dir() or not (web / 'index.html').is_file():
        p.error('web-dir must be a real build directory with index.html')
    # These are the only backend files admitted; never copy a host library directory.
    inputs = [(Path(args.plugin), 'payload/plugin/NINA.Plugins.MLAstroRPA.Linux.dll'),
              (Path(args.worker), 'payload/plugin/usb-worker/Pins.MLAstro.UsbWorker'),
              (Path(args.nina_api), 'payload/ninaAPI/ninaAPI.dll')]
    for f in web.rglob('*'):
        if f.is_symlink():
            p.error(f'web build contains symlink: {f}')
        if f.is_file():
            rel = f.relative_to(web)
            # A built web directory should contain web assets, never source, credentials or build config.
            if any(x.startswith('.') for x in rel.parts) or f.suffix.lower() in {'.pem', '.key', '.p12', '.pfx', '.env', '.dll', '.csproj', '.apk'}:
                p.error(f'forbidden web build file: {rel}')
            inputs.append((f, 'payload/web/' + rel.as_posix()))
    try:
        root.mkdir(parents=True)
        for src, dst in inputs:
            copy_file(src, root / dst)
        installer = Path(__file__).with_name('install_bundle.py')
        copy_file(installer, root / 'install_bundle.py')
        source_root = Path(__file__).resolve().parents[1]
        for doc in ('USER-GUIDE.md', 'BUILD.md', 'UPSTREAM.md'):
            copy_file(source_root / doc, root / doc)
        revision = args.source_revision.lower() if args.source_revision else 'not supplied'
        notice = ('# Corresponding source and attribution\n\n'
                  'Source repository: https://github.com/ydutertre/pins-mlastro-rpa\n\n'
                  f'Source revision: {revision}\n\n'
                  'The source export contains the MLAstro plugin and worker, ninaAPI relay and portable PINS build project, and Touch-N-Stars integration patches. See UPSTREAM.md for pinned upstream baselines, licenses and attribution, and BUILD.md for build instructions and required dependencies. The backend and relay are MPL-2.0; the Touch-N-Stars integration is GPL-3.0. License texts are included in licenses/. Native Android builds, if distributed, need their corresponding source and notices separately.\n')
        (root / 'SOURCE-NOTICE.md').write_text(notice, encoding='utf-8')
        copy_file(source_root / 'LICENSE', root / 'licenses/MPL-2.0.txt')
        copy_file(source_root / 'integration/touch-n-stars/LICENSE', root / 'licenses/GPL-3.0.txt')
        for name in ('dotnet/LICENSE.txt', 'dotnet/THIRD-PARTY-NOTICES.txt', 'system-io-ports/THIRD-PARTY-NOTICES.txt'):
            copy_file(source_root / 'licenses' / name, root / 'licenses' / name)
        files = {str(f.relative_to(root)).replace('\\', '/'): digest(f)
                 for f in sorted(root.rglob('*')) if f.is_file()}
        manifest = {'format': 1, 'version': args.version,
                    'source_repository': 'https://github.com/ydutertre/pins-mlastro-rpa',
                    'source_revision': args.source_revision.lower() if args.source_revision else None, 'target_baseline_unverified_for_these_artifacts': VERSIONS,
                    'declared_component_versions_unverified': declared_versions,
                    'components': ['MLAstro Linux plugin', 'Linux ARM64 USB worker',
                                   'full ninaAPI relay build', 'Touch-N-Stars web build'],
                    'files': files}
        (root / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        print(f'Created {root}; review manifest and distribute corresponding source and licenses.')
    except Exception:
        shutil.rmtree(root, ignore_errors=True)
        raise

if __name__ == '__main__':
    main()

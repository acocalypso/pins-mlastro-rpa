import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1] / 'scripts'
spec = importlib.util.spec_from_file_location('installer', SCRIPTS / 'install_bundle.py')
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)

class BundleChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        for n in ['plugin', 'worker', 'api']:
            (self.root / n).write_bytes(n.encode())
        (self.root / 'web/assets').mkdir(parents=True)
        (self.root / 'web/index.html').write_text('entry')
        (self.root / 'web/assets/a.js').write_text('asset')
        self.bundle = self.root / 'bundle'
        subprocess.run([sys.executable, str(SCRIPTS / 'build_bundle.py'), '--version', 'test', '--source-revision', 'a' * 40, '--component-version', 'plugin=declared', '--plugin', str(self.root / 'plugin'), '--worker', str(self.root / 'worker'), '--nina-api', str(self.root / 'api'), '--web-dir', str(self.root / 'web'), '--output', str(self.bundle)], check=True, capture_output=True)
        self.root_patch = patch.object(installer, 'ROOT', self.bundle)
        self.root_patch.start()
    def tearDown(self):
        self.root_patch.stop()
        self.temp.cleanup()
    def test_hash_and_extra_files(self):
        manifest = installer.verify()
        self.assertEqual(manifest['declared_component_versions_unverified'], {'plugin': 'declared'})
        self.assertIn('target_baseline_unverified_for_these_artifacts', manifest)
        self.assertNotIn('tested_baseline', manifest)
        self.assertEqual(manifest['source_revision'], 'a' * 40)
        self.assertIn('a' * 40, (self.bundle / 'SOURCE-NOTICE.md').read_text())
        self.assertTrue((self.bundle / 'BUILD.md').is_file())
        self.assertTrue((self.bundle / 'UPSTREAM.md').is_file())
        for name in ('dotnet/LICENSE.txt', 'dotnet/THIRD-PARTY-NOTICES.txt', 'system-io-ports/THIRD-PARTY-NOTICES.txt'):
            self.assertIn('licenses/' + name, manifest['files'])
            self.assertTrue((self.bundle / 'licenses' / name).is_file())
        (self.bundle / 'payload/web/assets/a.js').write_text('tampered')
        with self.assertRaises(RuntimeError): installer.verify()
        (self.bundle / 'payload/web/assets/a.js').write_text('asset')
        (self.bundle / 'extra').write_text('extra')
        with self.assertRaises(RuntimeError): installer.verify()
    def test_traversal_rejected(self):
        m = json.loads((self.bundle / 'manifest.json').read_text())
        m['files']['../outside'] = '0' * 64
        (self.bundle / 'manifest.json').write_text(json.dumps(m))
        with self.assertRaises(RuntimeError): installer.verify()
    def test_snapshot_restores_old_and_removes_new(self):
        home = self.root / 'home'; home.mkdir()
        plugin = home / 'plugins'; plugin.mkdir()
        web = home / 'web'; web.mkdir()
        nina = home / 'ninaAPI.dll'; nina.write_text('old api')
        (web / 'index.html').write_text('old entry')
        args = SimpleNamespace(pins_home=str(home), plugin_root=str(plugin), web_root=str(web), nina_dll=str(nina), service='pins.service')
        files = installer.targets(args, installer.verify())
        backup = installer.snapshot(args, files)
        for rel, dest in files: installer.atomic_copy(self.bundle / rel, dest)
        self.assertEqual((web / 'index.html').read_text(), 'entry')
        installer.restore(backup, args)
        self.assertEqual((web / 'index.html').read_text(), 'old entry')
        self.assertEqual(nina.read_text(), 'old api')
        self.assertFalse((web / 'assets/a.js').exists())
    def test_independent_plugin_and_web_roots(self):
        home = self.root / 'app'; home.mkdir()
        plugin = self.root / 'independent-plugins'; plugin.mkdir()
        web = self.root / 'independent-web'; web.mkdir()
        nina = plugin / 'ninaAPI.dll'; nina.write_text('old api')
        args = SimpleNamespace(pins_home=str(home), plugin_root=str(plugin), web_root=str(web), nina_dll=str(nina), service='pins.service')
        files = installer.targets(args, installer.verify())
        backup = installer.snapshot(args, files)
        for rel, dest in files: installer.atomic_copy(self.bundle / rel, dest)
        installer.restore(backup, args)
        self.assertEqual(nina.read_text(), 'old api')
        self.assertFalse((web / 'index.html').exists())
        self.assertFalse((plugin / 'MLAstroRPA.Linux/NINA.Plugins.MLAstroRPA.Linux.dll').exists())
        args.nina_dll = str(plugin / 'other.dll')
        with self.assertRaises(RuntimeError): installer.targets(args, installer.verify())
        args.nina_dll = str(nina)
        args.plugin_root = str(plugin / '..' / 'independent-plugins')
        with self.assertRaises(RuntimeError): installer.targets(args, installer.verify())

    def test_failed_health_restores_snapshot(self):
        home = self.root / 'home'; home.mkdir()
        plugin = home / 'plugins'; plugin.mkdir()
        web = home / 'web'; web.mkdir()
        nina = home / 'ninaAPI.dll'; nina.write_text('old api')
        (web / 'index.html').write_text('old entry')
        argv = ['install_bundle.py', '--install', '--pins-home', str(home), '--plugin-root', str(plugin), '--nina-dll', str(nina), '--web-root', str(web), '--service', 'pins.service']
        with patch.object(sys, 'argv', argv), patch.object(installer.os, 'geteuid', return_value=0, create=True), patch.object(installer, 'idle'), patch.object(installer, 'installed_health', side_effect=OSError('relay route missing')), patch.object(installer, 'service'), patch.object(installer.subprocess, 'run'), patch.object(installer.time, 'sleep'):
            with self.assertRaises(RuntimeError): installer.main()
        self.assertEqual(nina.read_text(), 'old api')
        self.assertEqual((web / 'index.html').read_text(), 'old entry')
        self.assertFalse((web / 'assets/a.js').exists())

    def test_failed_manual_restore_does_not_start_service(self):
        home = self.root / 'home'; home.mkdir()
        plugin = home / 'plugins'; plugin.mkdir()
        web = home / 'web'; web.mkdir()
        nina = home / 'ninaAPI.dll'; nina.write_text('old api')
        args = ['install_bundle.py', '--rollback', str(home / '.mlastro-backups/bad'), '--pins-home', str(home), '--plugin-root', str(plugin), '--nina-dll', str(nina), '--web-root', str(web), '--service', 'pins.service']
        with patch.object(sys, 'argv', args), patch.object(installer.os, 'geteuid', return_value=0, create=True), patch.object(installer, 'idle'), patch.object(installer, 'service') as svc, patch.object(installer, 'restore', side_effect=RuntimeError('broken backup')):
            with self.assertRaises(RuntimeError): installer.main()
        svc.assert_called_once_with('stop', 'pins.service')

    def test_installed_health_requires_relay_routes(self):
        direct = {'apiVersion': 2, 'automationEnabled': False, 'sessionRunning': False,
                  'manualMoveActive': False, 'continuousJogSupported': False, 'motionInhibited': False}
        def request(port, endpoint):
            if port == 1891: return direct
            if port == 5000: return {'Success': True, 'IsRunning': False}
            if endpoint == '/v2/api/mlastro/status': return direct
            if endpoint == '/v2/api/mlastro/watchdog': return {'supported': False, 'enabled': None, 'code': 'wifi_required'}
            return {'Success': True, 'Response': {'IsExposing': False, 'LiveViewEnabled': False, 'Slewing': False}}
        with patch.object(installer, 'get_json', side_effect=request): installer.installed_health()
        def missing(port, endpoint):
            if endpoint.startswith('/v2/api/mlastro/'): raise OSError('route missing')
            return request(port, endpoint)
        with patch.object(installer, 'get_json', side_effect=missing):
            with self.assertRaises(OSError): installer.installed_health()
        def old_relay(port, endpoint):
            if endpoint == '/v2/api/mlastro/status': return {'apiVersion': 1}
            return request(port, endpoint)
        with patch.object(installer, 'get_json', side_effect=old_relay):
            with self.assertRaises(RuntimeError): installer.installed_health()
        def invalid_watchdog(port, endpoint):
            if endpoint == '/v2/api/mlastro/watchdog': return {'supported': True, 'enabled': 'false', 'code': 'ok'}
            return request(port, endpoint)
        with patch.object(installer, 'get_json', side_effect=invalid_watchdog):
            with self.assertRaises(RuntimeError): installer.installed_health()

    def test_v2_status_and_legacy_jog_validation(self):
        status = {'apiVersion': 2, 'automationEnabled': False, 'sessionRunning': False,
                  'manualMoveActive': False, 'continuousJogSupported': False, 'motionInhibited': False}
        responses = {1891: status, 5000: {'Success': True, 'IsRunning': False},
                     1888: {'Success': True, 'Response': {'IsExposing': False, 'LiveViewEnabled': False, 'Slewing': False}}}
        with patch.object(installer, 'get_json', side_effect=lambda port, endpoint: responses[port]):
            installer.idle()
            status['jogActive'] = True
            with self.assertRaises(RuntimeError): installer.idle()
            status['jogActive'] = False
            installer.idle()
            status['continuousJogSupported'] = True
            with self.assertRaises(RuntimeError): installer.idle()
            status.pop('continuousJogSupported')
            with self.assertRaises(RuntimeError): installer.idle()
            status.pop('apiVersion')
            installer.idle()
            status.pop('jogActive')
            with self.assertRaises(RuntimeError): installer.idle()
            status['apiVersion'] = 3
            with self.assertRaises(RuntimeError): installer.idle()

    def test_idle_fails_closed(self):
        responses = {1891:{'automationEnabled':False,'sessionRunning':False,'manualMoveActive':False,'jogActive':False,'motionInhibited':False}, 5000:{'Success':True,'IsRunning':False}, 1888:{'Success':True,'Response':{'IsExposing':False,'LiveViewEnabled':False,'Slewing':False}}}
        with patch.object(installer, 'get_json', side_effect=lambda port, endpoint: responses[port]):
            installer.idle()
            responses[5000]['IsRunning'] = True
            with self.assertRaises(RuntimeError): installer.idle()
            responses[5000].pop('IsRunning')
            with self.assertRaises(RuntimeError): installer.idle()

if __name__ == '__main__': unittest.main()

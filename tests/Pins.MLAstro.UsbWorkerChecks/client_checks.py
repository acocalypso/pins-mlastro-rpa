#!/usr/bin/env python3
"""Pause the managed parent with SIGSTOP while the worker owns a private PTY."""
import json
import os
import queue
import signal
import subprocess
import sys
import threading
import time
from worker_checks import Firmware, wait_until


class Parent:
    def __init__(self, harness, worker, firmware):
        self.proc = subprocess.Popen([harness, worker, firmware.path], stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
        self.events = []
        self.pending = queue.Queue()
        self.thread = threading.Thread(target=self.read, daemon=True)
        self.thread.start()
        self.take('opened')

    def read(self):
        for line in self.proc.stdout:
            event = json.loads(line)
            self.events.append(event)
            self.pending.put(event)

    def take(self, kind, op=None, timeout=8):
        until = time.monotonic() + timeout
        while time.monotonic() < until:
            event = self.pending.get(timeout=max(.01, until-time.monotonic()))
            if event['type'] == kind and (op is None or event.get('op') == op):
                return event
        raise AssertionError((kind, op, self.events[-10:]))

    def call(self, op, **fields):
        self.proc.stdin.write(json.dumps(dict(op=op, **fields))+'\n')
        self.proc.stdin.flush()
        return self.take('result', op)

    def prepare(self):
        assert self.call('send', text='[MLAstroRPA-TC]\n', motion=False)['ok']
        time.sleep(.6)
        assert self.call('allow')['ok']

    def close(self):
        if self.proc.poll() is None:
            os.kill(self.proc.pid, signal.SIGCONT)
            try: self.call('close')
            except Exception: self.proc.kill()
        self.proc.wait(timeout=6)


def idle_pause(harness, worker):
    firmware = Firmware()
    parent = Parent(harness, worker, firmware)
    try:
        parent.prepare()
        before = time.monotonic()
        os.kill(parent.proc.pid, signal.SIGSTOP)
        time.sleep(4)
        os.kill(parent.proc.pid, signal.SIGCONT)
        result = parent.call('status')
        assert result['connected'] and result['ready'], result
        queries = [stamp for stamp, line in firmware.snapshot() if line == '?' and stamp >= before]
        assert len(queries) >= 15, queries
        assert max(b-a for a, b in zip(queries, queries[1:])) < .8, queries
        assert not any(e['type'] == 'fault' for e in parent.events), parent.events
        parent.call('fence')
        assert not parent.call('send', text='MAzR:1\n', motion=True)['ok']
        assert firmware.count('MAzR:1') == 0
    finally:
        parent.close()
        firmware.close()


def active_pause(harness, worker):
    firmware = Firmware()
    parent = Parent(harness, worker, firmware)
    try:
        parent.prepare()
        assert parent.call('send', text='JoRe:1\n', motion=False)['ok']
        assert parent.call('send', text='MAzR:1\n', motion=True)['ok']
        firmware.ready = False
        os.kill(parent.proc.pid, signal.SIGSTOP)
        time.sleep(3)
        assert firmware.count('STOP:1') == 0, 'finite move inherited obsolete jog deadline'
        os.kill(parent.proc.pid, signal.SIGCONT)
        assert parent.call('status')['connected']
        assert firmware.count('MAzR:1') == 1, 'movement replayed after parent pause'
    finally:
        parent.close()
        firmware.close()


if __name__ == '__main__':
    if sys.platform != 'linux' or len(sys.argv) != 3:
        raise SystemExit('Usage on Linux: client_checks.py <ClientHarness> <USB worker>')
    idle_pause(*sys.argv[1:]); print('PASS managed parent SIGSTOP: idle USB heartbeat survives', flush=True)
    active_pause(*sys.argv[1:]); print('PASS managed parent SIGSTOP: finite move tolerates scheduling pause without replay', flush=True)

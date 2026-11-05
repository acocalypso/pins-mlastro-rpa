#!/usr/bin/env python3
"""Linux-only USB worker integration checks using a private PTY, never a physical port.

Run: python3 worker_checks.py -- <dotnet-or-worker-executable> [worker-dll]
The worker command is appended with --port, --parent, and --session.
"""
import argparse
import json
import os
import pty
import queue
import select
import subprocess
import sys
import threading
import time
import tty
import uuid

READY = '<READY|Mpos:0,0|>SLvl:1,Back:1,AzBl:77917,AlBl:1000,AzPH:0,AlPH:0\n'
def ticks(offset=0):
    # Linux .NET Stopwatch.GetTimestamp uses CLOCK_MONOTONIC nanoseconds.
    return time.monotonic_ns() + int(offset * 1_000_000_000)


def wait_until(predicate, timeout=5, message='timed out'):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(.01)
    raise AssertionError(message)


class Firmware:
    def __init__(self, identity=True):
        self.master, self.slave = pty.openpty()
        tty.setraw(self.slave)
        os.set_blocking(self.master, False)
        self.path = os.ttyname(self.slave)
        if not self.path.startswith('/dev/pts/'):
            raise RuntimeError('Refusing to use anything other than a fresh PTY')
        self.identity = identity
        self.ready = True
        self.telemetry = True
        self.lines = []
        self.closed = False
        self.lock = threading.Lock()
        self.thread = threading.Thread(target=self.loop, daemon=True)
        self.thread.start()

    def emit(self, line):
        try:
            os.write(self.master, line.encode())
        except OSError:
            pass

    def loop(self):
        pending = b''
        next_frame = time.monotonic()
        while not self.closed:
            try:
                readable, _, _ = select.select([self.master], [], [], .03)
                if readable:
                    chunk = os.read(self.master, 8192)
                    if not chunk:
                        continue
                    pending += chunk
                    while b'\n' in pending:
                        raw, pending = pending.split(b'\n', 1)
                        line = raw.decode(errors='replace').strip()
                        with self.lock:
                            self.lines.append((time.monotonic(), line))
                        if line == '[MLAstroRPA-TC]':
                            self.emit('ok,firmware 1.13.0,SN:SIMULATOR\n' if self.identity else 'ok\n')
                if self.telemetry and time.monotonic() >= next_frame:
                    self.emit(READY if self.ready else '<BUSY|Mpos:0,0|>\n')
                    next_frame = time.monotonic() + .1
            except (OSError, ValueError):
                return

    def snapshot(self):
        with self.lock:
            return list(self.lines)

    def count(self, prefix):
        return sum(line.startswith(prefix) for _, line in self.snapshot())

    def close(self):
        self.closed = True
        self.thread.join(timeout=2)
        os.close(self.master)
        os.close(self.slave)


class Worker:
    def __init__(self, command, firmware, parent=None):
        self.session = str(uuid.uuid4())
        self.proc = subprocess.Popen(command + ['--port', firmware.path, '--parent', str(parent or os.getpid()),
                                                '--session', self.session],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     text=True, bufsize=1)
        self.events = []
        self.lock = threading.Lock()
        self.pause_read = threading.Event()
        self.reader = threading.Thread(target=self.read, daemon=True)
        self.reader.start()
        self.next_id = 0

    def read(self):
        for raw in self.proc.stdout:
            while self.pause_read.is_set():
                time.sleep(.01)
            try:
                item = json.loads(raw)
            except json.JSONDecodeError as exc:
                item = {'type': 'invalid-json', 'error': str(exc), 'raw': raw}
            with self.lock:
                self.events.append(item)

    def send(self, kind, **fields):
        self.next_id += 1
        data = {'type': kind, 'id': self.next_id, 'session': self.session, **fields}
        self.proc.stdin.write(json.dumps(data) + '\n')
        self.proc.stdin.flush()
        return self.next_id

    def event(self, predicate, timeout=5):
        def find():
            with self.lock:
                return next((event for event in self.events if predicate(event)), None)
        return wait_until(find, timeout, 'worker event not received; events=' + repr(self.events[-10:]))

    def ack(self, identifier, timeout=5):
        return self.event(lambda x: x.get('type') == 'ack' and x.get('id') == identifier, timeout)

    def opened(self):
        return self.event(lambda x: x.get('type') == 'opened', 10)

    def close(self):
        self.pause_read.clear()
        if self.proc.poll() is None:
            self.proc.kill()
        self.proc.wait(timeout=3)
        self.reader.join(timeout=2)


def with_worker(command, check, identity=True):
    firmware = Firmware(identity)
    worker = Worker(command, firmware)
    try:
        check(worker, firmware)
    finally:
        worker.close()
        firmware.close()


def expect_ack(worker, kind, expected=True, **fields):
    identifier = worker.send(kind, **fields)
    result = worker.ack(identifier)
    assert bool(result.get('ok')) is expected, result
    return result


def prepare(worker):
    worker.opened()
    expect_ack(worker, 'send', generation=0, text='[MLAstroRPA-TC]\n', expiresAt=ticks(2))
    worker.event(lambda x: x.get('type') == 'line' and x.get('text', '').lower().startswith('ok,firmware'))
    worker.event(lambda x: x.get('type') == 'line' and x.get('text', '').startswith('<READY|'))
    expect_ack(worker, 'allow', generation=0)


def idle_heartbeat(worker, firmware):
    prepare(worker)
    start = time.monotonic()
    before = firmware.count('?')
    time.sleep(2.5)  # beyond firmware's 1.2s watchdog while parent sends no pings
    after = firmware.count('?')
    assert after - before >= 8, firmware.snapshot()
    queries = [t for t, line in firmware.snapshot() if line == '?' and t >= start]
    assert len(queries) >= 8 and max(b - a for a, b in zip(queries, queries[1:])) < .65, queries
    assert worker.proc.poll() is None, 'idle worker exited while parent was paused'
    assert not any(e.get('type') == 'fault' for e in worker.events), worker.events
    worker.send('ping')


def active_lease_timeout(worker, firmware):
    prepare(worker)
    worker.send('ping')
    expect_ack(worker, 'send', generation=0, text='MAzR:1\n', expiresAt=ticks(2))
    wait_until(lambda: firmware.count('MAzR:1'), message='motion not sent')
    wait_until(lambda: firmware.count('STOP:1'), 3, 'active parent timeout did not STOP')
    worker.event(lambda x: x.get('type') == 'fault', 3)
    before = firmware.count('MAlU:1')
    expect_ack(worker, 'send', expected=False, generation=0, text='MAlU:1\n', expiresAt=ticks(2))
    time.sleep(.2)
    assert firmware.count('MAlU:1') == before, 'movement sent after lease fault'


def eof_stops(worker, firmware):
    prepare(worker)
    worker.send('ping')
    expect_ack(worker, 'send', generation=0, text='MAzR:1\n', expiresAt=ticks(2))
    worker.proc.stdin.close()
    wait_until(lambda: firmware.count('STOP:1'), 4, 'EOF did not send STOP')
    worker.proc.wait(timeout=5)


def identity_guard(worker, firmware):
    worker.opened()
    expect_ack(worker, 'send', generation=0, text='[MLAstroRPA-TC]\n', expiresAt=ticks(2))
    time.sleep(.5)
    expect_ack(worker, 'send', expected=False, generation=0, text='MAzR:1\n', expiresAt=ticks(2))
    assert firmware.count('STOP:') == 0, firmware.snapshot()
    assert firmware.count('MAz') == 0 and firmware.count('MAl') == 0
    if worker.proc.poll() is None:
        worker.proc.stdin.close()
        worker.proc.wait(timeout=5)
    assert firmware.count('STOP:') == 0, 'unknown device received STOP'


def stale_requests(worker, firmware):
    prepare(worker)
    expect_ack(worker, 'send', expected=False, generation=0, text='MAzR:-1\n', expiresAt=ticks(2))
    expect_ack(worker, 'fence', generation=1)
    expect_ack(worker, 'send', expected=False, generation=0, text='MAzR:1\n', expiresAt=ticks(2))
    expect_ack(worker, 'allow', generation=1)
    expect_ack(worker, 'send', expected=False, generation=1, text='MAzR:1\n', expiresAt=ticks(-1))
    assert firmware.count('MAzR:1') == 0, firmware.snapshot()


def busy_no_ready(worker, firmware):
    prepare(worker)
    firmware.ready = False
    worker.event(lambda x: x.get('type') == 'line' and x.get('text', '').startswith('<BUSY|'))
    expect_ack(worker, 'fence', generation=1)
    expect_ack(worker, 'allow', expected=False, generation=1)
    expect_ack(worker, 'send', expected=False, generation=1, text='MAzR:1\n', expiresAt=ticks(2))
    assert firmware.count('MAzR:1') == 0, firmware.snapshot()


def firmware_estop(worker, firmware):
    prepare(worker)
    firmware.emit('ERROR:Serial heartbeat timeout -> ESTOP\n')
    worker.event(lambda x: x.get('type') == 'fault', 3)
    expect_ack(worker, 'allow', expected=False, generation=0)
    expect_ack(worker, 'send', expected=False, generation=0, text='MAzR:1\n', expiresAt=ticks(2))
    assert firmware.count('MAzR:1') == 0, firmware.snapshot()


def active_telemetry_silence(worker, firmware):
    prepare(worker)
    worker.send('ping')
    expect_ack(worker, 'send', generation=0, text='MAzR:1\n', expiresAt=ticks(2))
    firmware.telemetry = False
    deadline = time.monotonic() + 4
    while time.monotonic() < deadline and not firmware.count('STOP:1'):
        worker.send('ping')
        time.sleep(.15)
    assert firmware.count('STOP:1'), 'active stale telemetry did not trigger STOP despite live parent'
    worker.event(lambda x: x.get('type') == 'fault', 2)
    expect_ack(worker, 'send', expected=False, generation=0, text='MAlU:1\n', expiresAt=ticks(2))
    assert firmware.count('MAlU:1') == 0


def idle_telemetry_recovery(worker, firmware):
    prepare(worker)
    firmware.telemetry = False
    time.sleep(7)
    assert worker.proc.poll() is None, 'idle gap prematurely closed worker'
    assert not any(e.get('type') == 'fault' for e in worker.events), worker.events[-10:]
    expect_ack(worker, 'send', expected=False, generation=0, text='MAzR:1\n', expiresAt=ticks(2))
    firmware.telemetry = True
    time.sleep(.5)
    worker.send('ping')
    expect_ack(worker, 'send', generation=0, text='MAzR:1\n', expiresAt=ticks(2))


def idle_telemetry_timeout(worker, firmware):
    prepare(worker)
    firmware.telemetry = False
    worker.proc.wait(timeout=19)
    assert worker.proc.poll() is not None, 'idle silence did not close worker'


def fifo_and_timestamps(worker, firmware):
    prepare(worker)
    sent = [worker.send('send', generation=0, text=f'SLvl:{n}\n', expiresAt=ticks(5)) for n in (1, 2, 3)]
    for identifier in sent:
        assert worker.ack(identifier)['ok']
    observed = [line for _, line in firmware.snapshot() if line.startswith('SLvl:')]
    assert observed == ['SLvl:1', 'SLvl:2', 'SLvl:3'], observed
    before = time.monotonic_ns()
    firmware.emit('test-timestamp\n')
    result = worker.event(lambda x: x.get('type') == 'line' and x.get('text') == 'test-timestamp')
    assert before <= result.get('receivedAt', 0) <= time.monotonic_ns(), result


def output_backpressure(worker, firmware):
    prepare(worker)
    worker.pause_read.set()
    def flood():
        for _ in range(12000):
            if firmware.closed:
                return
            firmware.emit(READY)
    threading.Thread(target=flood, daemon=True).start()
    time.sleep(1)
    count = firmware.count('?')
    time.sleep(1.5)
    assert firmware.count('?') - count >= 4, 'output backpressure blocked USB heartbeat'
    worker.pause_read.clear()


def parent_death(command):
    parent = subprocess.Popen(['sleep', '30'])
    firmware = Firmware()
    worker = Worker(command, firmware, parent.pid)
    try:
        prepare(worker)
        worker.send('ping')
        expect_ack(worker, 'send', generation=0, text='MAzR:1\n', expiresAt=ticks(2))
        parent.terminate()
        parent.wait(timeout=3)
        wait_until(lambda: firmware.count('STOP:1'), 4, 'parent death did not send STOP')
        worker.proc.wait(timeout=5)
    finally:
        if parent.poll() is None:
            parent.kill()
            parent.wait()
        worker.close()
        firmware.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command or sys.platform != 'linux':
        parser.error('Linux and a worker command are required')
    tests = [(idle_heartbeat, True), (active_lease_timeout, True), (eof_stops, True),
             (identity_guard, False), (stale_requests, True), (busy_no_ready, True),
             (firmware_estop, True), (active_telemetry_silence, True),
             (idle_telemetry_recovery, True), (idle_telemetry_timeout, True),
             (fifo_and_timestamps, True), (output_backpressure, True)]
    for test, identity in tests:
        with_worker(command, test, identity)
        print('PASS', test.__name__, flush=True)
    parent_death(command)
    print('PASS parent_death', flush=True)


if __name__ == '__main__':
    main()

import test from 'node:test';
import assert from 'node:assert/strict';
import { createJogController } from '../jog.js';

test('release before start response stops the returned lease', async () => {
  let resolveStart;
  const calls = [];
  const controller = createJogController({
    request: (op, body) => {
      calls.push([op, body]);
      return op === 'jog/start'
        ? new Promise((resolve) => {
            resolveStart = resolve;
          })
        : Promise.resolve();
    },
    onError: (error) => {
      throw error;
    },
    onChange: () => {},
  });
  const started = controller.start('az', 1);
  controller.stop();
  resolveStart({ leaseId: 'lease' });
  await started;
  assert.deepEqual(
    calls.map(([op]) => op),
    ['jog/start', 'jog/stop']
  );
});

test('stop is idempotent and cancels renewals', async () => {
  const calls = [];
  const controller = createJogController({
    request: async (op) => {
      calls.push(op);
      return { leaseId: 'lease' };
    },
    onError: (error) => {
      throw error;
    },
    onChange: () => {},
    interval: 5,
  });
  await controller.start('alt', -1);
  controller.stop();
  controller.stop();
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.deepEqual(calls, ['jog/start', 'jog/stop']);
});

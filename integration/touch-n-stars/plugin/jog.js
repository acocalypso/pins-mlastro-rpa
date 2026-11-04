export function createJogController({ request, onError, onChange, interval = 200 }) {
  let current = null;
  function stop() {
    const session = current;
    if (!session) return;
    current = null;
    clearInterval(session.timer);
    onChange(false);
    if (session.leaseId) request('jog/stop', { leaseId: session.leaseId }).catch(onError);
  }
  async function start(axis, direction) {
    if (current) return;
    const session = { leaseId: null, timer: null };
    current = session;
    onChange(true);
    try {
      const result = await request('jog/start', { axis, direction });
      if (!result?.leaseId) throw new Error('Jog start returned no lease.');
      session.leaseId = result.leaseId;
      if (current !== session) {
        await request('jog/stop', { leaseId: session.leaseId });
        return;
      }
      session.timer = setInterval(() => {
        request('jog/renew', { leaseId: session.leaseId }).catch((error) => {
          if (current === session) stop();
          onError(error);
        });
      }, interval);
    } catch (error) {
      if (current === session) stop();
      onError(error);
    }
  }
  return { start, stop };
}

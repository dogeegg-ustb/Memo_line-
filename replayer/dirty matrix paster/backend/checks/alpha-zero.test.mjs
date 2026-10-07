import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import { composePackets } from '../composition.mjs';

test('alpha-zero replacement and simultaneous equality include hidden RGB', t => {
  const root = fileURLToPath(new URL('../../artifacts/alpha-zero-checks/', import.meta.url));
  fs.mkdirSync(root, { recursive: true });
  const workspace = fs.mkdtempSync(path.join(root, 'case-'));
  t.after(() => {
    const target = path.resolve(workspace);
    assert.ok(target.startsWith(path.resolve(root) + path.sep));
    fs.rmSync(target, { recursive: true, force: true });
  });
  let sequence = 0;
  function packet(ticks, rgba) {
    const id = `alpha-${++sequence}`, dir = path.join(workspace, id);
    fs.mkdirSync(dir);
    const manifest = { schema: 'dirty-matrix-image-diff/v1', kind: 'diff', id,
      after: { width: 1, height: 1 },
      now: { width: 1, height: 1, triggerTicks: ticks, sessionId: 'session', generation: 1,
        layer: { id: 1, uuid: 'layer', name: 'layer' } }, recognizer: { frequency: 1000 },
      labels: [{ id: 'label', imageIds: ['image'], impactRange: { canvasWidth: 1, canvasHeight: 1,
        tileSize: 1, originX: 0, originY: 0, rowRuns: [{ row: 0, startColumn: 0, endColumnExclusive: 1 }] } }],
      images: [{ id: 'image', image: 'image.png', maskImage: 'mask.png',
        bounds: { left: 0, top: 0, right: 1, bottom: 1 }, changedPixels: 1, labelIds: ['label'] }] };
    fs.writeFileSync(path.join(dir, 'manifest.json'), JSON.stringify(manifest));
    fs.writeFileSync(path.join(dir, 'image.png'), PNG.sync.write({ width: 1, height: 1, data: Buffer.from(rgba) }));
    fs.writeFileSync(path.join(dir, 'mask.png'), PNG.sync.write({ width: 1, height: 1, data: Buffer.from([255, 255, 255, 255]) }));
    return dir;
  }
  const painted = packet(1, [1, 2, 3, 255]);
  const erased = packet(2, [11, 22, 33, 0]);
  assert.deepEqual([...composePackets([erased, painted]).rgba], [11, 22, 33, 0]);
  const equal = packet(2, [11, 22, 33, 0]);
  assert.deepEqual([...composePackets([equal, erased, painted]).rgba], [11, 22, 33, 0]);
  const conflicting = packet(2, [0, 0, 0, 0]);
  assert.throws(() => composePackets([erased, conflicting]), /同一发生时间/);
});

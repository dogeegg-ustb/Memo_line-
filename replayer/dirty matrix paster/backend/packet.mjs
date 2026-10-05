import fs from 'node:fs';
import path from 'node:path';
import { PNG } from 'pngjs';

const MAX_PIXELS = 64_000_000;
function check(condition, message) { if (!condition) throw new Error(message); }
function integer(value, name) { check(Number.isSafeInteger(value), `${name} 必须是整数`); return value; }
function size(width, height, name) {
  integer(width, `${name}.width`); integer(height, `${name}.height`);
  check(width > 0 && height > 0 && width <= 30000 && height <= 30000 && width * height <= MAX_PIXELS,
    `${name} 尺寸无效或超过 PSD/内存限制`);
}
export function packetFile(input) {
  const resolved = path.resolve(input);
  return fs.statSync(resolved).isDirectory() ? path.join(resolved, 'manifest.json') : resolved;
}
export function describePacket(input) {
  const filename = fs.realpathSync(packetFile(input));
  const manifest = JSON.parse(fs.readFileSync(filename, 'utf8'));
  check(manifest.schema === 'dirty-matrix-image-diff/v1', '不支持的差异包 schema');
  check(manifest.kind === 'baseline' || manifest.kind === 'diff', '不支持的差异包 kind');
  check(typeof manifest.id === 'string' && manifest.id.length > 0, '缺少差异包 id');
  check(Array.isArray(manifest.images) && Array.isArray(manifest.labels), '缺少 images/labels');
  const current = manifest.now ?? manifest.after;
  return { manifest, filename, id:manifest.id,
    triggerTicks:current?.triggerTicks ?? manifest.capture?.triggerTicks ?? manifest.recognizer?.toTicks ?? null,
    sessionId:current?.sessionId ?? manifest.recognizer?.sessionId ?? null,
    generation:current?.generation ?? null, frequency:manifest.recognizer?.frequency ?? null };
}
function readPng(root, relative, width, height) {
  check(typeof relative === 'string' && relative.length > 0 && !path.isAbsolute(relative), 'PNG 路径必须是包内相对路径');
  const filename = fs.realpathSync(path.resolve(root, relative));
  const relation = path.relative(root, filename);
  check(relation !== '..' && !relation.startsWith(`..${path.sep}`) && !path.isAbsolute(relation), 'PNG 路径超出差异包');
  const bytes = fs.readFileSync(filename);
  const signature = Buffer.from([137,80,78,71,13,10,26,10]);
  check(bytes.length >= 33 && bytes.subarray(0,8).equals(signature), `无效 PNG: ${relative}`);
  check(bytes.readUInt32BE(16) === width && bytes.readUInt32BE(20) === height, `PNG 尺寸与 bounds 不一致: ${relative}`);
  const png = PNG.sync.read(bytes, { skipRescale: true });
  check(png.depth === 8 && png.width === width && png.height === height && png.data.length === width * height * 4,
    `只支持原尺寸 8 位 RGBA PNG: ${relative}`);
  return png;
}
function coverage(matrix, bounds, canvas) {
  check(matrix && matrix.canvasWidth === canvas.width && matrix.canvasHeight === canvas.height, '脏矩阵画布尺寸不一致');
  const { tileSize, originX, originY, rowRuns } = matrix;
  integer(tileSize, 'tileSize'); integer(originX, 'originX'); integer(originY, 'originY');
  check(tileSize > 0 && tileSize <= 30000 && Array.isArray(rowRuns) && rowRuns.length <= MAX_PIXELS, '无效脏矩阵');
  const { left, top, right, bottom } = bounds, width = right - left, height = bottom - top;
  const result = Buffer.alloc(width * height);
  for (const run of rowRuns) {
    const row = integer(run.row, 'row'), start = integer(run.startColumn, 'startColumn'), end = integer(run.endColumnExclusive, 'endColumnExclusive');
    check(row >= 0 && start >= 0 && end > start, '无效矩阵行区间');
    const x0 = originX + start * tileSize, x1 = originX + end * tileSize;
    const y0 = originY + row * tileSize, y1 = y0 + tileSize;
    check([x0,x1,y0,y1].every(Number.isSafeInteger), '矩阵坐标溢出');
    const l = Math.max(left, 0, x0), r = Math.min(right, canvas.width, x1);
    const t = Math.max(top, 0, y0), b = Math.min(bottom, canvas.height, y1);
    if (r <= l || b <= t) continue;
    for (let y = t; y < b; y++) result.fill(1, (y-top)*width+l-left, (y-top)*width+r-left);
  }
  return result;
}
export function loadPacket(input) {
  const descriptor = describePacket(input), { filename, manifest } = descriptor, root = path.dirname(filename);
  if (manifest.kind === 'baseline' || manifest.images.length === 0) {
    check(manifest.kind !== 'baseline' || manifest.images.length === 0, 'baseline 不应包含差异 image');
    return { status: 'skipped', id: manifest.id, reason: '该包没有差异 image；不会使用 canvasPreviewImage 代替', manifestPath: filename };
  }
  const current = manifest.now;
  check(current && manifest.after, 'diff 缺少 after/now');
  const canvas = { width: current.width, height: current.height };
  size(canvas.width, canvas.height, 'canvas');
  check(manifest.after.width === canvas.width && manifest.after.height === canvas.height, 'after/now 画布尺寸不一致');
  const labels = new Map();
  for (const label of manifest.labels) {
    check(typeof label.id === 'string' && !labels.has(label.id), '重复或无效 label id');
    labels.set(label.id, label);
  }
  const ids = new Set();
  const patches = manifest.images.map(image => {
    check(typeof image.id === 'string' && !ids.has(image.id), '重复或无效 image id'); ids.add(image.id);
    const bounds = image.bounds;
    check(bounds && ['left','top','right','bottom'].every(k => Number.isSafeInteger(bounds[k])), '无效 image bounds');
    const { left, top, right, bottom } = bounds, width = right-left, height = bottom-top;
    size(width, height, 'patch');
    check(left >= 0 && top >= 0 && right <= canvas.width && bottom <= canvas.height, 'image bounds 超出画布');
    const primary = image.image ?? image.nowImage; // Compatibility with older watcher packets only.
    check(primary !== manifest.canvasPreviewImage && primary !== image.differenceImage && primary !== image.afterImage,
      'image 不能引用预览、difference 或 after 图像');
    const png = readPng(root, primary, width, height);
    const maskPng = readPng(root, image.maskImage, width, height);
    check(Array.isArray(image.labelIds) && image.labelIds.length > 0, 'image 缺少关联脏矩阵');
    const allowed = Buffer.alloc(width * height);
    for (const id of image.labelIds) {
      const label = labels.get(id); check(label, `找不到关联 label: ${id}`);
      check(Array.isArray(label.imageIds) && label.imageIds.includes(image.id), `label/image 关联不一致: ${id}`);
      const covered = coverage(label.impactRange, bounds, canvas);
      for (let i = 0; i < covered.length; i++) allowed[i] |= covered[i];
    }
    const mask = Buffer.alloc(width * height); let changed = 0, erased = 0;
    for (let i = 0; i < mask.length; i++) {
      const offset = i*4, alpha = maskPng.data[offset+3];
      if (alpha === 0) continue;
      check(alpha === 255 && maskPng.data[offset] === 255 && maskPng.data[offset+1] === 255 && maskPng.data[offset+2] === 255,
        'maskImage 必须使用不透明白色表示变化像素');
      check(allowed[i] !== 0, `变化像素超出关联脏矩阵: ${image.id} @ (${left+i%width}, ${top+Math.floor(i/width)})`);
      mask[i] = 255; changed++; if (png.data[offset+3] === 0) erased++;
    }
    check(changed > 0 && changed === image.changedPixels, `changedPixels 与 maskImage 不一致: ${image.id}`);
    return { id: image.id, bounds: { left, top, right, bottom }, width, height, image: primary,
      maskImage: image.maskImage, labelIds: image.labelIds, rgba: png.data, mask, changedPixels: changed, erasedPixels: erased };
  });
  const bounds = { left: Math.min(...patches.map(p=>p.bounds.left)), top: Math.min(...patches.map(p=>p.bounds.top)),
    right: Math.max(...patches.map(p=>p.bounds.right)), bottom: Math.max(...patches.map(p=>p.bounds.bottom)) };
  const width = bounds.right-bounds.left, height = bounds.bottom-bounds.top;
  const rgba = Buffer.alloc(width*height*4), mask = Buffer.alloc(width*height);
  for (const patch of patches) for (let y = 0; y < patch.height; y++) for (let x = 0; x < patch.width; x++) {
    const source = y*patch.width+x; if (patch.mask[source] === 0) continue;
    const target = (y+patch.bounds.top-bounds.top)*width+x+patch.bounds.left-bounds.left;
    if (mask[target]) check(rgba.subarray(target*4,target*4+4).equals(patch.rgba.subarray(source*4,source*4+4)), '重叠补丁的 RGBA 冲突');
    patch.rgba.copy(rgba,target*4,source*4,source*4+4); mask[target] = 255;
  }
  let changedPixels = 0, erasedPixels = 0;
  for (let i = 0; i < mask.length; i++) if (mask[i]) { changedPixels++; if (rgba[i*4+3] === 0) erasedPixels++; }
  const linked = new Set(patches.flatMap(p=>p.labelIds));
  const metadata = { schema: 'dirty-matrix-layer/v1', id: manifest.id, manifestPath: filename, canvas, bounds,
    width, height, layerName: current.layer?.name ?? 'Dirty matrix', targetLayer: current.layer ?? null,
    triggerTicks: descriptor.triggerTicks, sessionId:descriptor.sessionId, generation:descriptor.generation,
    frequency:descriptor.frequency, pixelFormat: 'RGBA8-straight', maskFormat: 'Gray8',
    replacementRule: 'mask=255: destination RGBA := source RGBA, including alpha=0; mask=0: unchanged',
    changedPixels, erasedPixels, images: patches.map(({rgba,mask,...p})=>p),
    dirtyMatrices: [...labels.values()].filter(l=>linked.has(l.id)).map(l=>({ labelId:l.id, imageIds:l.imageIds, impactRange:l.impactRange })) };
  return { status: 'ready', metadata, rgba, mask };
}

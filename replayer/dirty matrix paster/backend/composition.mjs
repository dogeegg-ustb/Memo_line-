import { createHash } from 'node:crypto';
import { describePacket, loadPacket } from './packet.mjs';

function check(condition,message) { if (!condition) throw new Error(message); }
function layerKey(layer) {
  if (typeof layer?.uuid === 'string' && layer.uuid.length) return `uuid:${layer.uuid}`;
  if (Number.isSafeInteger(layer?.id)) return `id:${layer.id}`;
  throw new Error('合成包缺少可确认的目标图层身份');
}
// Start with transparent RGBA state; mask records which pixels have a known selected update.
export function composePackets(inputs) {
  check(Array.isArray(inputs) && inputs.length > 0,'请选择至少一个脏矩阵包');
  const descriptors=[], seenPaths=new Set(), seenIds=new Set(), skippedPackets=[];
  let duplicateSelectionCount=0;
  for (const input of inputs) {
    const d=describePacket(input), canonical=d.filename.toLowerCase();
    if (seenPaths.has(canonical)) { duplicateSelectionCount++; continue; }
    seenPaths.add(canonical);
    check(!seenIds.has(d.id),`不同目录具有相同包 ID: ${d.id}`); seenIds.add(d.id);
    if (d.manifest.kind === 'baseline' || d.manifest.images.length === 0) {
      const skipped=loadPacket(d.filename); skippedPackets.push(skipped); continue;
    }
    check(Number.isSafeInteger(d.triggerTicks) && d.triggerTicks >= 0,`缺少有效发生时间 triggerTicks: ${d.id}`);
    check(typeof d.sessionId === 'string' && d.sessionId.length > 0,`缺少录制会话 sessionId: ${d.id}`);
    descriptors.push(d);
  }
  if (!descriptors.length) return {status:'skipped',reason:'所选包没有差异 image，剪贴板保持不变',selectedCount:inputs.length,skippedPackets,duplicateSelectionCount};
  descriptors.sort((a,b)=>a.triggerTicks-b.triggerTicks || a.id.localeCompare(b.id,'en'));
  const first=descriptors[0], current=first.manifest.now;
  check(current && Number.isSafeInteger(current.width) && Number.isSafeInteger(current.height),'无效合成画布');
  const canvas={width:current.width,height:current.height}, identity=layerKey(current.layer);
  for (const d of descriptors) {
    const now=d.manifest.now;
    check(d.sessionId === first.sessionId,'不能合成不同录制会话：各会话的 ticks 起点不同');
    check(d.generation === first.generation,'不能合成不同录制代次的脏矩阵');
    check(d.frequency === first.frequency,'发生时间 ticks 的频率不一致');
    check(now?.width === canvas.width && now?.height === canvas.height,'合成包的画布尺寸不一致');
    check(layerKey(now.layer) === identity,'所选脏矩阵属于不同目标图层');
  }
  // Load and apply one packet at a time, retaining only the accumulating RGBA state.
  let state, known, bounds, replacementWrites=0, overwrittenPixels=0, previousTicks=null;
  const order=[], images=[], dirtyMatrices=[];
  const timeCounts=new Map(); for (const d of descriptors) timeCounts.set(d.triggerTicks,(timeCounts.get(d.triggerTicks)??0)+1);
  let sameTimeMask;
  for (const d of descriptors) {
    const packet=loadPacket(d.filename), m=packet.metadata;
    // Inputs might be updated by a producer between the descriptor read and PNG read.
    check(m.triggerTicks === d.triggerTicks && m.sessionId === d.sessionId &&
      m.generation === d.generation && m.frequency === d.frequency &&
      m.canvas.width === canvas.width && m.canvas.height === canvas.height && layerKey(m.targetLayer) === identity,
      `读取期间包的时间、图层或画布发生变化: ${d.id}`);
    if (!state) { state=Buffer.alloc(canvas.width*canvas.height*4); known=Buffer.alloc(canvas.width*canvas.height); bounds={...m.bounds}; }
    else { bounds.left=Math.min(bounds.left,m.bounds.left); bounds.top=Math.min(bounds.top,m.bounds.top);
      bounds.right=Math.max(bounds.right,m.bounds.right); bounds.bottom=Math.max(bounds.bottom,m.bounds.bottom); }
    if (d.triggerTicks !== previousTicks) sameTimeMask=timeCounts.get(d.triggerTicks)>1 ? Buffer.alloc(known.length) : undefined;
    previousTicks=d.triggerTicks;
    for (let y=0;y<m.height;y++) for (let x=0;x<m.width;x++) {
      const src=y*m.width+x; if (!packet.mask[src]) continue;
      const dest=(y+m.bounds.top)*canvas.width+x+m.bounds.left;
      if (sameTimeMask?.[dest]) check(state.subarray(dest*4,dest*4+4).equals(packet.rgba.subarray(src*4,src*4+4)),
        `同一发生时间的包具有冲突像素，无法确定先后: ${d.id}`);
      if (known[dest]) overwrittenPixels++;
      // Copy all four straight channels even when the source alpha is zero.
      packet.rgba.copy(state,dest*4,src*4,src*4+4); known[dest]=255;
      if (sameTimeMask) sameTimeMask[dest]=255;
      replacementWrites++;
    }
    order.push({id:m.id,manifestPath:m.manifestPath,triggerTicks:m.triggerTicks,changedPixels:m.changedPixels,erasedPixels:m.erasedPixels});
    images.push(...m.images.map(p=>({...p,packetId:m.id,triggerTicks:m.triggerTicks})));
    dirtyMatrices.push(...m.dirtyMatrices.map(p=>({...p,packetId:m.id,triggerTicks:m.triggerTicks})));
  }
  const width=bounds.right-bounds.left,height=bounds.bottom-bounds.top;
  const rgba=Buffer.alloc(width*height*4),mask=Buffer.alloc(width*height);
  for (let y=0;y<height;y++) {
    const start=(y+bounds.top)*canvas.width+bounds.left;
    state.copy(rgba,y*width*4,start*4,(start+width)*4); known.copy(mask,y*width,start,start+width);
  }
  let changedPixels=0,erasedPixels=0;
  for(let i=0;i<mask.length;i++) if(mask[i]) {changedPixels++;if(rgba[i*4+3]===0) erasedPixels++;}
  const last=descriptors.at(-1), signature=createHash('sha256').update(JSON.stringify(order)).digest('hex').slice(0,12);
  const metadata={schema:'dirty-matrix-layer/v1',id:`composition-${first.triggerTicks}-${last.triggerTicks}-${signature}`,
    canvas,bounds,width,height,layerName:last.manifest.now.layer?.name ?? 'Dirty matrix',targetLayer:last.manifest.now.layer,
    triggerTicks:last.triggerTicks,sessionId:first.sessionId,generation:first.generation,frequency:first.frequency,
    pixelFormat:'RGBA8-straight',maskFormat:'Gray8',
    replacementRule:'mask=255: destination RGBA := source RGBA, including alpha=0; mask=0: unchanged',
    changedPixels,erasedPixels,images,dirtyMatrices,
    composition:{mode:'chronological-rgba-replacement',initialState:'transparent',selectedCount:inputs.length,
      appliedCount:order.length,duplicateSelectionCount,fromTicks:first.triggerTicks,toTicks:last.triggerTicks,
      replacementWrites,overwrittenPixels,order,skippedPackets}};
  return {status:'ready',metadata,rgba,mask};
}

import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import { loadPacket } from '../packet.mjs';
import { exportPacket } from '../engine.mjs';

const project = fileURLToPath(new URL('../../',import.meta.url));
const checks = path.join(project,'artifacts','checks'); fs.mkdirSync(checks,{recursive:true});
function png(dir,filename,width,height,data) { fs.writeFileSync(path.join(dir,filename),PNG.sync.write({width,height,data})); }
function fixture(change = ()=>{}) {
  const dir = fs.mkdtempSync(path.join(checks,'fixture-')), width=5,height=3;
  const data = Buffer.alloc(width*height*4), mask = Buffer.alloc(data.length);
  data.set([1,19,249,73],0); data.set([14,32,52,0],(width*height-1)*4);
  mask.fill(255,0,4); mask.fill(255,(width*height-1)*4);
  const matrix = {canvasWidth:13,canvasHeight:11,tileSize:4,originX:1,originY:2,
    rowRuns:[{row:0,startColumn:0,endColumnExclusive:2},{row:1,startColumn:0,endColumnExclusive:2}]};
  const m = {schema:'dirty-matrix-image-diff/v1',kind:'diff',id:'synthetic',
    after:{width:13,height:11},now:{width:13,height:11,layer:{id:7,name:'测试图层',uuid:'synthetic-layer'}},
    labels:[{id:'label-a',impactRange:matrix,imageIds:['image-a']}],
    images:[{id:'image-a',bounds:{left:3,top:4,right:8,bottom:7},changedPixels:2,
      image:'actual.png',nowImage:'wrong.png',afterImage:'before.png',maskImage:'mask.png',differenceImage:'difference.png',labelIds:['label-a']}],
    canvasPreviewImage:'deliberately-missing-preview.png'};
  change(m,data,mask);
  png(dir,'actual.png',width,height,data); png(dir,'mask.png',width,height,mask);
  fs.writeFileSync(path.join(dir,'manifest.json'),JSON.stringify(m));
  return {dir,m,data,mask};
}
test('使用 image 原始 RGBA，验证非零矩阵原点，透明擦除不丢失',async()=>{
  const f=fixture(), result=await exportPacket(f.dir,path.join(f.dir,'output'));
  assert.equal(result.verified,true); assert.equal(result.erasedPixels,1); assert.equal(result.changedPixels,2);
  assert.deepEqual(result.bounds,{left:3,top:4,right:8,bottom:7});
  assert.equal(result.canvas.width,13); assert.equal(result.canvas.height,11);
  assert.deepEqual(fs.readFileSync(result.rgbaPath),f.data);
  assert.equal(fs.readFileSync(result.maskPath).at(-1),255);
  const standard=PNG.sync.read(fs.readFileSync(result.clipboardImagePath));
  assert.deepEqual(standard.data.subarray((4*13+3)*4,(4*13+3)*4+4),Buffer.from([1,19,249,73]));
});
test('纯透明擦除补丁保留尺寸与蒙版，禁止 alpha 裁切',async()=>{
  const f=fixture((m,d)=>d.fill(0)), result=await exportPacket(f.dir,path.join(f.dir,'output'));
  assert.equal(result.width,5); assert.equal(result.height,3); assert.equal(result.erasedPixels,2);
  assert.equal(fs.readFileSync(result.maskPath).filter(v=>v===255).length,2);
});
test('基准包跳过，绝不读 canvasPreviewImage',()=>{
  const f=fixture(m=>{m.kind='baseline';m.images=[];m.now=null}); assert.equal(loadPacket(f.dir).status,'skipped');
});
test('旧包仅在缺少 image 时使用 nowImage',()=>{
  const f=fixture(m=>{delete m.images[0].image;m.images[0].nowImage='actual.png'}); assert.equal(loadPacket(f.dir).metadata.images[0].image,'actual.png');
});
test('半开矩阵右界外的变化像素必须拒绝',()=>{
  const f=fixture(m=>{m.labels[0].impactRange.rowRuns.forEach(r=>r.endColumnExclusive=1)});
  assert.throws(()=>loadPacket(f.dir),/超出关联脏矩阵/);
});
test('矩阵之外的非变化像素不会成为替换区域',()=>{
  const f=fixture((m,d)=>d.set([100,20,50,255],4)); const p=loadPacket(f.dir);
  assert.deepEqual(p.rgba.subarray(4,8),Buffer.alloc(4)); assert.equal(p.mask[1],0);
});
test('拒绝 PNG/bounds 尺寸不匹配',()=>{
  const f=fixture(m=>m.images[0].bounds.right++); assert.throws(()=>loadPacket(f.dir),/尺寸与 bounds/);
});
test('拒绝越界路径',()=>{
  const f=fixture(m=>m.images[0].image='../outside.png'); fs.copyFileSync(path.join(f.dir,'actual.png'),path.join(checks,'outside.png'));
  assert.throws(()=>loadPacket(f.dir),/路径超出/);
});
test('拒绝将 preview 当作 image',()=>{
  const f=fixture(m=>m.images[0].image=m.canvasPreviewImage); assert.throws(()=>loadPacket(f.dir),/不能引用预览/);
});
test('拒绝缺少 mask 的数据包',()=>{
  const f=fixture(m=>delete m.images[0].maskImage); assert.throws(()=>loadPacket(f.dir),/PNG 路径/);
});
test('拒绝非法矩阵区间和像素计数',()=>{
  const a=fixture(m=>m.labels[0].impactRange.rowRuns[0].endColumnExclusive=0); assert.throws(()=>loadPacket(a.dir),/矩阵行区间/);
  const b=fixture(m=>m.images[0].changedPixels=3); assert.throws(()=>loadPacket(b.dir),/changedPixels/);
});
test('拒绝非白色蒙版和重复冲突补丁',()=>{
  const a=fixture((m,d,k)=>k[0]=30); assert.throws(()=>loadPacket(a.dir),/不透明白色/);
  const b=fixture(m=>{m.images.push({...m.images[0],id:'image-b',image:'conflict.png'});m.labels[0].imageIds.push('image-b')});
  const conflict=Buffer.from(b.data);conflict[0]=111;png(b.dir,'conflict.png',5,3,conflict);
  assert.throws(()=>loadPacket(b.dir),/RGBA 冲突/);
});
test('独立的多个 image 合成一个坐标蒙版图层',async()=>{
  const f=fixture(m=>{
    m.images.push({...m.images[0],id:'image-b',bounds:{left:8,top:4,right:13,bottom:7}});
    m.labels[0].imageIds.push('image-b');m.labels[0].impactRange.rowRuns.forEach(r=>r.endColumnExclusive=3);
  });
  const result=await exportPacket(f.dir,path.join(f.dir,'output'));
  assert.equal(result.width,10);assert.equal(result.images.length,2);assert.equal(result.changedPixels,4);
});
test('这 7 个真实包的 PSD 读回检查',async t=>{
  const root=process.env.DIRTY_MATRIX_PACKETS_ROOT ?? path.resolve(project,'../../Organizer/canvas layer watcher/publish/win-x64/layer-diffs/packets');
  const ids=['trigger-00000000000666281525-7f616539','trigger-00000000000784015010-837b22a9','trigger-00000000000877612019-68287a8c',
    'trigger-00000000000917275037-35d50518','trigger-00000000000955782253-c0ef3076','trigger-00000000001726040625-3bc00438','trigger-00000000002075039984-4a873da8'];
  if (!ids.every(id=>fs.existsSync(path.join(root,id,'manifest.json')))) { t.skip('本机没有示例数据包'); return; }
  const reports=[];
  for (const id of ids) {
    const report=await exportPacket(path.join(root,id),path.join(project,'artifacts','examples',id)); reports.push(report);
    if (id===ids[0]) assert.equal(report.status,'skipped');
    else { assert.equal(report.verified,true);assert.equal(report.canvas.width,4961);assert.equal(report.canvas.height,7016); }
  }
  assert.ok(reports.at(-1).erasedPixels>0,'最后一包必须保留真实擦除');
  fs.writeFileSync(path.join(project,'artifacts','examples','results.json'),JSON.stringify(reports,null,2));
});

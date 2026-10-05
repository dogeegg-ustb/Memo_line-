import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import { composePackets } from '../composition.mjs';
import { exportComposition } from '../engine.mjs';

const project=fileURLToPath(new URL('../../',import.meta.url)),checks=path.join(project,'artifacts','composition-checks');
fs.mkdirSync(checks,{recursive:true});
let sequence=0;
function fixture(ticks,pixels,edit=()=>{}) {
  const dir=fs.mkdtempSync(path.join(checks,'packet-')),w=2,h=2;
  const rgba=Buffer.alloc(w*h*4),mask=Buffer.alloc(rgba.length);
  for(const [index,color] of pixels) {rgba.set(color,index*4);mask.fill(255,index*4,index*4+4);}
  const m={schema:'dirty-matrix-image-diff/v1',kind:'diff',id:`test-${++sequence}`,
    after:{width:10,height:6},now:{width:10,height:6,triggerTicks:ticks,sessionId:'same-session',generation:1,layer:{id:1,uuid:'same-layer',name:'测试'}},
    recognizer:{frequency:10000000},canvasPreviewImage:'unused-preview.png',
    labels:[{id:'label',imageIds:['image'],impactRange:{canvasWidth:10,canvasHeight:6,tileSize:1,originX:0,originY:0,
      rowRuns:[{row:2,startColumn:1,endColumnExclusive:3},{row:3,startColumn:1,endColumnExclusive:3}]}}],
    images:[{id:'image',image:'image.png',maskImage:'mask.png',bounds:{left:1,top:2,right:3,bottom:4},changedPixels:pixels.length,labelIds:['label']}]};
  edit(m);fs.writeFileSync(path.join(dir,'manifest.json'),JSON.stringify(m));
  fs.writeFileSync(path.join(dir,'image.png'),PNG.sync.write({width:w,height:h,data:rgba}));
  fs.writeFileSync(path.join(dir,'mask.png'),PNG.sync.write({width:w,height:h,data:mask}));return {dir,m,rgba};
}
function pixel(c,index=0) {return [...c.rgba.subarray(index*4,index*4+4)];}
test('打乱输入仍按发生时间合成，半透明像素完整替换而不混色',()=>{
  const a=fixture(10,[[0,[255,0,0,255]],[1,[0,255,0,255]]]);
  const b=fixture(20,[[0,[7,31,211,83]]]);
  const c=composePackets([b.dir,a.dir]);
  assert.deepEqual(pixel(c),[7,31,211,83]);assert.deepEqual(pixel(c,1),[0,255,0,255]);
  assert.deepEqual(c.metadata.composition.order.map(p=>p.triggerTicks),[10,20]);
  assert.equal(c.metadata.changedPixels,2);assert.equal(c.metadata.composition.overwrittenPixels,1);
  assert.deepEqual(c.rgba,composePackets([a.dir,b.dir]).rgba);
});
test('后发生的透明擦除替换原像素，之后可再次绘制',()=>{
  const a=fixture(10,[[0,[255,60,20,255]]]),b=fixture(20,[[0,[11,22,33,0]]]),d=fixture(30,[[0,[1,2,3,127]]]);
  const erased=composePackets([b.dir,a.dir]);assert.deepEqual(pixel(erased),[11,22,33,0]);
  assert.equal(erased.mask[0],255);assert.equal(erased.metadata.erasedPixels,1);
  const painted=composePackets([d.dir,a.dir,b.dir]);assert.deepEqual(pixel(painted),[1,2,3,127]);assert.equal(painted.metadata.erasedPixels,0);
});
test('未被后包 mask 标记的透明像素不清除之前状态',()=>{
  const a=fixture(10,[[0,[255,0,0,255]]]),b=fixture(20,[[1,[0,0,255,255]]]);
  const c=composePackets([a.dir,b.dir]);assert.deepEqual(pixel(c),[255,0,0,255]);assert.equal(c.mask[2],0);
});
test('不同位置按完整画布坐标合成，所有 PNG 与内部 RGBA 一致且不生成 PSD',()=>{
  const a=fixture(10,[[0,[255,0,0,255]]]);
  const b=fixture(20,[[3,[14,32,52,0]]],m=>{
    m.images[0].bounds={left:6,top:3,right:8,bottom:5};
    m.labels[0].impactRange.rowRuns=[{row:3,startColumn:6,endColumnExclusive:8},{row:4,startColumn:6,endColumnExclusive:8}];
  });
  const out=fs.mkdtempSync(path.join(checks,'output-')),report=exportComposition([b.dir,a.dir],out);
  assert.deepEqual(report.bounds,{left:1,top:2,right:8,bottom:5});assert.equal(report.composition.appliedCount,2);
  const full=PNG.sync.read(fs.readFileSync(report.clipboardImagePath));
  assert.deepEqual([...full.data.subarray((2*10+1)*4,(2*10+1)*4+4)],[255,0,0,255]);
  assert.deepEqual([...full.data.subarray((4*10+7)*4,(4*10+7)*4+4)],[14,32,52,0]);
  assert.deepEqual(PNG.sync.read(fs.readFileSync(report.imagePath)).data,fs.readFileSync(report.rgbaPath));
  assert.equal(report.psdPath,undefined);assert.equal(fs.existsSync(path.join(out,'layer.psd')),false);
});
test('baseline 自动跳过，全是 baseline 返回 skipped',()=>{
  const baseline=fixture(0,[],m=>{m.kind='baseline';m.now=null;m.images=[]});const a=fixture(10,[[0,[0,0,0,255]]]);
  const c=composePackets([baseline.dir,a.dir]);assert.equal(c.metadata.composition.appliedCount,1);assert.equal(c.metadata.composition.skippedPackets.length,1);
  assert.equal(composePackets([baseline.dir]).status,'skipped');
});
test('缺少时间、超出安全整数的时间、不同会话、图层、代次或频率拒绝合成',()=>{
  const a=fixture(10,[[0,[0,0,0,255]]]);
  for(const [edit,pattern] of [
    [m=>delete m.now.triggerTicks,/发生时间/],[m=>m.now.triggerTicks=Number.MAX_SAFE_INTEGER+1,/发生时间/],
    [m=>m.now.sessionId='another-session',/不同录制会话/],[m=>m.now.layer.uuid='another-layer',/不同目标图层/],
    [m=>m.now.generation=2,/不同录制代次/],[m=>m.recognizer.frequency=1000,/频率不一致/]
  ]) {const b=fixture(20,[[0,[0,0,0,255]]],edit);assert.throws(()=>composePackets([a.dir,b.dir]),pattern);}
});
test('同一时间的冲突像素拒绝，同值和不相交像素可合成',()=>{
  const a=fixture(10,[[0,[255,0,0,255]]]),b=fixture(10,[[0,[0,0,255,255]]]);
  assert.throws(()=>composePackets([a.dir,b.dir]),/同一发生时间/);
  const same=fixture(10,[[0,[255,0,0,255]]]),other=fixture(10,[[1,[0,255,0,255]]]);
  assert.equal(composePackets([other.dir,same.dir,a.dir]).metadata.changedPixels,2);
});
test('重复选择同一文件去重，不同文件重复 ID 拒绝',()=>{
  const a=fixture(10,[[0,[255,0,0,255]]]);const c=composePackets([a.dir,a.dir]);
  assert.equal(c.metadata.composition.appliedCount,1);assert.equal(c.metadata.composition.duplicateSelectionCount,1);
  const b=fixture(20,[[0,[255,0,0,255]]],m=>m.id=a.m.id);assert.throws(()=>composePackets([a.dir,b.dir]),/相同包 ID/);
});
test('真实 7 包逆序输入，按原始 PNG 独立重放核对全部 RGBA 和更新范围',t=>{
  const root=process.env.DIRTY_MATRIX_PACKETS_ROOT ?? path.resolve(project,'../../Organizer/canvas layer watcher/publish/win-x64/layer-diffs/packets');
  const ids=['trigger-00000000000666281525-7f616539','trigger-00000000000784015010-837b22a9','trigger-00000000000877612019-68287a8c',
    'trigger-00000000000917275037-35d50518','trigger-00000000000955782253-c0ef3076','trigger-00000000001726040625-3bc00438','trigger-00000000002075039984-4a873da8'];
  if(!ids.every(id=>fs.existsSync(path.join(root,id,'manifest.json')))) {t.skip('没有真实示例包');return;}
  const inputs=ids.map(id=>path.join(root,id)),out=path.join(project,'artifacts','composed-examples');
  const report=exportComposition(inputs.toReversed(),out),w=4961,h=7016;
  const expected=Buffer.alloc(w*h*4),known=Buffer.alloc(w*h);
  for(const input of inputs.slice(1)) {
    const m=JSON.parse(fs.readFileSync(path.join(input,'manifest.json'),'utf8'));
    for(const image of m.images) {
      const rgba=PNG.sync.read(fs.readFileSync(path.join(input,image.image))),mask=PNG.sync.read(fs.readFileSync(path.join(input,image.maskImage)));
      for(let y=0;y<rgba.height;y++) for(let x=0;x<rgba.width;x++) {
        const src=(y*rgba.width+x)*4;if(mask.data[src+3]===0) continue;
        const dest=(y+image.bounds.top)*w+x+image.bounds.left;
        rgba.data.copy(expected,dest*4,src,src+4);known[dest]=255;
      }
    }
  }
  assert.deepEqual(PNG.sync.read(fs.readFileSync(report.clipboardImagePath)).data,expected);
  const actualMask=fs.readFileSync(report.maskPath);
  for(let y=0;y<report.height;y++) assert.deepEqual(actualMask.subarray(y*report.width,(y+1)*report.width),
    known.subarray((y+report.bounds.top)*w+report.bounds.left,(y+report.bounds.top)*w+report.bounds.right));
  assert.deepEqual(report.composition.order.map(p=>p.id),ids.slice(1));
  assert.equal(report.changedPixels,1140506);assert.equal(report.erasedPixels,297201);assert.equal(report.composition.overwrittenPixels,415661);
  fs.writeFileSync(path.join(project,'artifacts','selection.json'),JSON.stringify(inputs.toReversed(),null,2));
});

import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { PNG } from 'pngjs';
import { loadPacket } from './packet.mjs';
import { composePackets } from './composition.mjs';

function pngFile(filename, width, height, data) { fs.writeFileSync(filename, PNG.sync.write({width,height,data})); }
function same(actual, expected, name) {
  if (!Buffer.from(actual).equals(Buffer.from(expected))) throw new Error(`数据读回校验失败: ${name}`);
}
export function exportComposition(inputs,outputDirectory) {
  const composed=composePackets(inputs); if (composed.status==='skipped') return composed;
  const {metadata:m,rgba,mask}=composed,{width,height,bounds,canvas}=m;
  const output=path.resolve(outputDirectory);fs.mkdirSync(output,{recursive:true});
  const maskRgba=Buffer.alloc(width*height*4), fullCanvas=Buffer.alloc(canvas.width*canvas.height*4);
  for(let i=0;i<mask.length;i++) {maskRgba.fill(mask[i],i*4,i*4+3);maskRgba[i*4+3]=255;}
  for(let y=0;y<height;y++) rgba.copy(fullCanvas,((y+bounds.top)*canvas.width+bounds.left)*4,y*width*4,(y+1)*width*4);
  const rgbaPath=path.join(output,'layer.rgba'),maskPath=path.join(output,'mask.gray');
  fs.writeFileSync(rgbaPath,rgba);fs.writeFileSync(maskPath,mask);
  same(fs.readFileSync(rgbaPath),rgba,'合成 RGBA');same(fs.readFileSync(maskPath),mask,'合成替换范围');
  const imagePath=path.join(output,'layer.png'),maskImagePath=path.join(output,'mask.png'),clipboardImagePath=path.join(output,'clipboard.png');
  pngFile(imagePath,width,height,rgba);pngFile(maskImagePath,width,height,maskRgba);
  pngFile(clipboardImagePath,canvas.width,canvas.height,fullCanvas);
  const report={...m,status:'ready',rgbaPath,maskPath,imagePath,maskImagePath,clipboardImagePath,
    verified:true,verification:'internal-rgba-state-file-readback',clipboardContract:'MemoLine.DirtyMatrix.Layer.v1',cspNativeClipboardCompatible:false};
  fs.writeFileSync(path.join(output,'layer.json'),JSON.stringify(report,null,2));return report;
}
export async function exportPacket(input, outputDirectory) {
  const { writePsdBuffer }=await import('ag-psd');
  const { default:Psd }=await import('@webtoon/psd');
  const packet = loadPacket(input);
  if (packet.status === 'skipped') return packet;
  const { metadata:m, rgba, mask } = packet, { width, height, bounds, canvas } = m;
  const output = path.resolve(outputDirectory); fs.mkdirSync(output, {recursive:true});
  const maskRgba = Buffer.alloc(width*height*4);
  for (let i=0;i<mask.length;i++) { maskRgba.fill(mask[i],i*4,i*4+3); maskRgba[i*4+3]=255; }
  // Provide a correct full-size merged preview to PSD importers. Never use canvasPreviewImage.
  const composite = Buffer.alloc(canvas.width*canvas.height*4);
  for (let y=0;y<height;y++) rgba.copy(composite,((y+bounds.top)*canvas.width+bounds.left)*4,y*width*4,(y+1)*width*4);
  const document = { width:canvas.width, height:canvas.height, bitsPerChannel:8,
    imageData:{width:canvas.width,height:canvas.height,data:composite}, children:[{
      name:`${m.layerName} · ${m.id}`, left:bounds.left, top:bounds.top, opacity:1, blendMode:'normal',
      imageData:{width,height,data:rgba},
      mask:{left:bounds.left,top:bounds.top,defaultColor:0,disabled:false,positionRelativeToLayer:false,
        imageData:{width,height,data:maskRgba}}
    }] };
  const psdPath = path.join(output,'layer.psd');
  fs.writeFileSync(psdPath,writePsdBuffer(document,{trimImageData:false,noBackground:true,generateThumbnail:false}));
  // Independently reopen the actual file with the lightweight parser, including the mask channel.
  const bytes = fs.readFileSync(psdPath);
  const parsed = Psd.parse(bytes.buffer.slice(bytes.byteOffset,bytes.byteOffset+bytes.byteLength));
  if (parsed.width !== canvas.width || parsed.height !== canvas.height || parsed.layers.length !== 1)
    throw new Error('PSD 读回画布或图层数量不一致');
  const layer = parsed.layers[0];
  if (layer.left !== bounds.left || layer.top !== bounds.top || layer.width !== width || layer.height !== height)
    throw new Error('PSD 图层坐标读回不一致');
  const decoded = await layer.composite(false,false), decodedMaskRgba = await layer.userMask();
  if (!decodedMaskRgba || decodedMaskRgba.length !== width*height*4) throw new Error('PSD 读回缺少真实蒙版通道');
  // @webtoon/psd expands a grayscale user mask to opaque RGBA.
  const decodedMask = Buffer.alloc(width*height);
  for (let i=0;i<decodedMask.length;i++) decodedMask[i]=decodedMaskRgba[i*4];
  const md = layer.maskData;
  if (md.left !== bounds.left || md.top !== bounds.top || md.right !== bounds.right || md.bottom !== bounds.bottom ||
      md.backgroundColor !== 0 || md.flags.layerMaskDisabled || md.flags.positionRelativeToLayer)
    throw new Error('PSD 蒙版坐标或标志读回不一致');
  same(decoded,rgba,'RGBA'); same(decodedMask,mask,'蒙版');
  fs.writeFileSync(path.join(output,'layer.rgba'),decoded);
  fs.writeFileSync(path.join(output,'mask.gray'),decodedMask);
  pngFile(path.join(output,'layer.png'),width,height,decoded);
  pngFile(path.join(output,'mask.png'),width,height,maskRgba);
  pngFile(path.join(output,'clipboard.png'),canvas.width,canvas.height,composite);
  const report = {...m,status:'ready',psdPath,rgbaPath:path.join(output,'layer.rgba'),maskPath:path.join(output,'mask.gray'),
    imagePath:path.join(output,'layer.png'),maskImagePath:path.join(output,'mask.png'),clipboardImagePath:path.join(output,'clipboard.png'),
    psdEditor:'ag-psd 30.2.0',psdParser:'@webtoon/psd 0.4.0',verified:true,
    clipboardContract:'MemoLine.DirtyMatrix.Layer.v1',cspNativeClipboardCompatible:false};
  fs.writeFileSync(path.join(output,'layer.json'),JSON.stringify(report,null,2));
  return report;
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    if (process.argv.length === 4 && process.argv[2] === '--compose') {
      const request=JSON.parse(fs.readFileSync(process.argv[3],'utf8'));
      process.stdout.write(JSON.stringify(exportComposition(request.inputs,request.outputDirectory))+'\n');
    } else {
      if (process.argv.length !== 4) throw new Error('用法: node engine.mjs --compose <请求JSON> / <差异包> <PSD输出目录>');
      process.stdout.write(JSON.stringify(await exportPacket(process.argv[2],process.argv[3]))+'\n');
    }
  } catch (error) { process.stderr.write(error.message+'\n'); process.exitCode=1; }
}

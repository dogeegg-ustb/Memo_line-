// IPC adapter for host checks. Execute the actual --compose CLI and transport its raw output files.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const engine = fileURLToPath(new URL('../engine.mjs', import.meta.url));

try {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  const request = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  if (!Array.isArray(request.cases) || request.cases.length === 0)
    throw new Error('Parity request requires cases.');
  const cases = request.cases.map(item => {
    if (typeof item.name !== 'string' || !Array.isArray(item.inputs))
      throw new Error('Invalid parity case.');
    if (item.inputs.length === 0) throw new Error('Parity case requires packet inputs.');
    const caseRoot = path.dirname(path.resolve(item.inputs[0]));
    const requestPath = path.join(caseRoot, 'compose-request.json');
    const outputDirectory = path.join(caseRoot, 'compose-output');
    fs.writeFileSync(requestPath, JSON.stringify({ inputs: item.inputs, outputDirectory }));
    const child = spawnSync(process.execPath, [engine, '--compose', requestPath],
      { encoding: 'utf8', windowsHide: true, maxBuffer: 1024 * 1024 });
    if (child.error) throw child.error;
    if (child.status !== 0)
      return { name: item.name, status: 'rejected', exitCode: child.status, message: child.stderr.trim() };
    const report = JSON.parse(child.stdout);
    if (report.status !== 'ready' || report.verified !== true)
      throw new Error('Production compose CLI did not return a verified ready report.');
    return { name: item.name, status: 'ready', exitCode: child.status, canvas: report.canvas,
      bounds: report.bounds, changedPixels: report.changedPixels,
      rgba: fs.readFileSync(report.rgbaPath).toString('base64'),
      mask: fs.readFileSync(report.maskPath).toString('base64') };
  });
  process.stdout.write(JSON.stringify({ cases }) + '\n');
} catch (error) {
  process.stderr.write(error.message + '\n');
  process.exitCode = 1;
}

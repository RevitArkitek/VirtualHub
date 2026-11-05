// Validates a .glb with the official Khronos glTF-Validator.
//   node tools/gltfcheck/validate.mjs <file.glb>
import { readFileSync } from 'node:fs';
import validator from 'gltf-validator';

const file = process.argv[2];
if (!file) { console.error('usage: validate.mjs <file.glb>'); process.exit(2); }

const buf = readFileSync(file);
const report = await validator.validateBytes(new Uint8Array(buf), {
  uri: file,
  maxIssues: 100,
});

const { issues } = report;
console.log(`validator ${report.validatorVersion}  glTF ${report.info.version}`);
console.log(`vertices  ${report.info.totalVertexCount}`);
console.log(`triangles ${report.info.totalTriangleCount}`);
console.log(`errors ${issues.numErrors}  warnings ${issues.numWarnings}  infos ${issues.numInfos}  hints ${issues.numHints}`);
for (const m of issues.messages) {
  console.log(`  [${m.severity === 0 ? 'ERROR' : m.severity === 1 ? 'warn' : m.severity === 2 ? 'info' : 'hint'}] ${m.code} @ ${m.pointer}: ${m.message}`);
}
process.exit(issues.numErrors > 0 ? 1 : 0);

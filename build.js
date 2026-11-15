'use strict';
// Builds dist/RobloxProductManager.exe, a single Windows executable that contains Node.js,
// server.js and index.html. Uses Node's built-in "single executable application" feature.
// No npm packages are needed. Run on Windows with:  node build.js
//
// Steps (the same ones Node's `postject` tool performs):
//   1. Make the app blob with `node --experimental-sea-config`.
//   2. Copy node.exe and remove its code signature (it would be invalid after changes anyway).
//   3. Flip the SEA "fuse" so the executable runs the embedded app instead of acting as plain Node.
//   4. Add the blob as an RT_RCDATA resource named NODE_SEA_BLOB (Win32 UpdateResource via PowerShell).

const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

if (process.platform !== 'win32') {
  console.error('build.js makes a Windows .exe, so run it on Windows.');
  process.exit(1);
}

const root = __dirname;
const dist = path.join(root, 'dist');
const exe = path.join(dist, 'RobloxProductManager.exe');
const blob = path.join(dist, 'sea-prep.blob');
const seaConfig = path.join(dist, 'sea-config.json');
const FUSE = 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2:';

fs.mkdirSync(dist, { recursive: true });

// 1. App blob
fs.writeFileSync(seaConfig, JSON.stringify({
  main: path.join(root, 'server.js'),
  output: blob,
  disableExperimentalSEAWarning: true,
  useCodeCache: true,
  assets: { 'index.html': path.join(root, 'index.html') },
}, null, 2));
execFileSync(process.execPath, ['--experimental-sea-config', seaConfig], { stdio: 'inherit' });

// 2 + 3. Copy node.exe, strip the signature, flip the fuse.
let bin = fs.readFileSync(process.execPath);
bin = stripSignature(bin);
const at = bin.indexOf(FUSE);
if (at < 0 || bin.indexOf(FUSE, at + 1) >= 0) throw new Error('Could not find exactly one SEA fuse in node.exe.');
if (bin[at + FUSE.length] !== 0x30 /* '0' */) throw new Error('This node.exe already has the SEA fuse flipped.');
bin[at + FUSE.length] = 0x31; // '1'
fs.writeFileSync(exe, bin);

// 4. Embed the blob as a resource.
const ps = `
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SeaResource {
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  public static extern bool UpdateResource(IntPtr handle, IntPtr type, string name, ushort language, byte[] data, uint size);
  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool EndUpdateResource(IntPtr handle, bool discard);
}
'@
$data = [IO.File]::ReadAllBytes($env:SEA_BLOB)
$h = [SeaResource]::BeginUpdateResource($env:SEA_EXE, $false)
if ($h -eq [IntPtr]::Zero) { throw "BeginUpdateResource failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
if (-not [SeaResource]::UpdateResource($h, [IntPtr]10, 'NODE_SEA_BLOB', 0, $data, [uint32]$data.Length)) {
  $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error(); [void][SeaResource]::EndUpdateResource($h, $true); throw "UpdateResource failed: $err"
}
if (-not [SeaResource]::EndUpdateResource($h, $false)) { throw "EndUpdateResource failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
`;
execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', ps], {
  stdio: 'inherit',
  env: { ...process.env, SEA_EXE: exe, SEA_BLOB: blob },
});

fs.rmSync(blob, { force: true });
fs.rmSync(seaConfig, { force: true });
console.log(`Built ${exe} (${(fs.statSync(exe).size / 1048576).toFixed(1)} MB)`);

// Removes the Authenticode signature (certificate table) from a PE file.
function stripSignature(buf) {
  const pe = buf.readUInt32LE(0x3c);
  if (buf.toString('latin1', pe, pe + 4) !== 'PE\0\0') throw new Error('node.exe is not a PE file.');
  const opt = pe + 24;
  const dirs = opt + (buf.readUInt16LE(opt) === 0x20b ? 112 : 96); // PE32+ : PE32
  const security = dirs + 4 * 8; // data directory #4
  const certAt = buf.readUInt32LE(security);
  buf.writeUInt32LE(0, security);
  buf.writeUInt32LE(0, security + 4);
  return certAt > 0 && certAt < buf.length ? buf.subarray(0, certAt) : buf;
}

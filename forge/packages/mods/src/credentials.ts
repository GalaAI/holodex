import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { execa } from "execa";

/**
 * The OS credential store. On Windows this is Windows Credential Manager,
 * reached through the same advapi32 calls Holodex uses
 * (ArkStationClient.TryReadWindowsCredential / TryWriteWindowsCredential:
 * CredReadW, CredWriteW with a UTF-16 blob, CRED_TYPE_GENERIC,
 * CRED_PERSIST_LOCAL_MACHINE), run from a short PowerShell script because
 * Node has no binding of its own and no keyring package is installed.
 *
 * Rules:
 * - 4GE writes and deletes only targets that start with "4GE:".
 * - Secrets travel to PowerShell on stdin, never on a command line, and
 *   never reach a log, a response or a file.
 * - Reads are limited to "4GE:" targets plus the Holodex targets listed in
 *   READABLE_FOREIGN_TARGETS (the user's own Nebius key that Holodex keeps).
 */

export const CREDENTIAL_PREFIX = "4GE:";

/** Holodex targets 4GE may read (never write) so the user does not paste a key twice. */
export const READABLE_FOREIGN_TARGETS = new Set(["Omniwand:NebiusTokenFactory"]);

export interface CredentialStore {
  readonly kind: "windows" | "memory" | "unavailable";
  /** Which of the targets exist. */
  has(targets: string[]): Promise<Record<string, boolean>>;
  /** The secret, for in-process use only (an env var for a CLI or an HTTP header). */
  read(target: string): Promise<string | undefined>;
  write(target: string, secret: string): Promise<void>;
  delete(target: string): Promise<boolean>;
}

export class CredentialError extends Error {
  override readonly name = "CredentialError";
}

export function assertWritableTarget(target: string): void {
  if (!target.startsWith(CREDENTIAL_PREFIX) || target.length > 200 || /[\r\n]/.test(target)) {
    throw new CredentialError(`4GE only writes credentials under targets that start with ${CREDENTIAL_PREFIX}`);
  }
}

export function assertReadableTarget(target: string): void {
  if (target.startsWith(CREDENTIAL_PREFIX) || READABLE_FOREIGN_TARGETS.has(target)) return;
  throw new CredentialError("4GE does not read that credential target");
}

const PS_SCRIPT = String.raw`
$ErrorActionPreference = 'Stop'
$req = [Console]::In.ReadToEnd() | ConvertFrom-Json
$dll = [string]$req.dll
if ($dll -and (Test-Path -LiteralPath $dll)) { Add-Type -LiteralPath $dll } else {
$def = @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public struct CREDENTIAL {
  public uint Flags; public uint Type; public System.IntPtr TargetName; public System.IntPtr Comment;
  public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public uint CredentialBlobSize;
  public System.IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public System.IntPtr Attributes;
  public System.IntPtr TargetAlias; public System.IntPtr UserName;
}
[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool CredReadW(string target, uint type, uint flags, out System.IntPtr cred);
[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool CredWriteW(ref CREDENTIAL cred, uint flags);
[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool CredDeleteW(string target, uint type, uint flags);
[System.Runtime.InteropServices.DllImport("advapi32.dll")]
public static extern void CredFree(System.IntPtr cred);
'@
if ($dll) {
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dll) | Out-Null
  $own = [IO.Path]::ChangeExtension($dll, "$PID.dll")
  Add-Type -Namespace FourGE -Name Cred -MemberDefinition $def -OutputAssembly $own -OutputType Library
  Add-Type -LiteralPath $own
  try { if (-not (Test-Path -LiteralPath $dll)) { Copy-Item -LiteralPath $own -Destination $dll -ErrorAction Stop } } catch { }
} else { Add-Type -Namespace FourGE -Name Cred -MemberDefinition $def }
}
function Read-Cred([string]$t) {
  $p = [IntPtr]::Zero
  if (-not [FourGE.Cred]::CredReadW($t, 1, 0, [ref]$p) -or $p -eq [IntPtr]::Zero) { return $null }
  try {
    $c = [System.Runtime.InteropServices.Marshal]::PtrToStructure($p, [type][FourGE.Cred+CREDENTIAL])
    if ($c.CredentialBlobSize -le 0) { return '' }
    $b = New-Object byte[] $c.CredentialBlobSize
    [System.Runtime.InteropServices.Marshal]::Copy($c.CredentialBlob, $b, 0, $b.Length)
    if ($b.Length -ge 2 -and $b[1] -eq 0) { return [Text.Encoding]::Unicode.GetString($b).TrimEnd([char]0) }
    return [Text.Encoding]::UTF8.GetString($b).TrimEnd([char]0)
  } finally { [FourGE.Cred]::CredFree($p) }
}
switch ($req.op) {
  'has' {
    $out = @{}
    foreach ($t in $req.targets) { $p = [IntPtr]::Zero; $ok = [FourGE.Cred]::CredReadW($t, 1, 0, [ref]$p); if ($ok) { [FourGE.Cred]::CredFree($p) }; $out[$t] = [bool]$ok }
    [Console]::Out.Write((ConvertTo-Json -Compress -InputObject @{ ok = $true; found = $out }))
  }
  'read' {
    $v = Read-Cred $req.target
    [Console]::Out.Write((ConvertTo-Json -Compress -InputObject @{ ok = $true; found = ($null -ne $v); value = $v }))
  }
  'write' {
    $bytes = [Text.Encoding]::Unicode.GetBytes([string]$req.secret)
    $blob = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($bytes.Length)
    $tp = [System.Runtime.InteropServices.Marshal]::StringToCoTaskMemUni($req.target)
    $up = [System.Runtime.InteropServices.Marshal]::StringToCoTaskMemUni('4GE')
    try {
      [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $blob, $bytes.Length)
      $c = New-Object FourGE.Cred+CREDENTIAL
      $c.Type = 1; $c.Persist = 2; $c.TargetName = $tp; $c.UserName = $up
      $c.CredentialBlob = $blob; $c.CredentialBlobSize = [uint32]$bytes.Length
      $ok = [FourGE.Cred]::CredWriteW([ref]$c, 0)
      [Console]::Out.Write((ConvertTo-Json -Compress -InputObject @{ ok = [bool]$ok }))
    } finally {
      [System.Runtime.InteropServices.Marshal]::FreeHGlobal($blob)
      [System.Runtime.InteropServices.Marshal]::FreeCoTaskMem($tp)
      [System.Runtime.InteropServices.Marshal]::FreeCoTaskMem($up)
    }
  }
  'delete' {
    $ok = [FourGE.Cred]::CredDeleteW($req.target, 1, 0)
    [Console]::Out.Write((ConvertTo-Json -Compress -InputObject @{ ok = $true; deleted = [bool]$ok }))
  }
}
`;

const ENCODED_SCRIPT = Buffer.from(PS_SCRIPT, "utf16le").toString("base64");

/**
 * Compiling the advapi32 signatures takes PowerShell a few seconds, so the
 * first call saves the compiled helper (it holds no secret) and later calls
 * load it. The file name carries a hash of the script, so a changed script
 * never loads a stale helper.
 */
export function helperDllPath(env: Record<string, string | undefined> = process.env): string {
  const base = env.LOCALAPPDATA ? join(env.LOCALAPPDATA, "Holodex", "4ge", "cache") : join(tmpdir(), "4ge-cache");
  const hash = createHash("sha256").update(PS_SCRIPT).digest("hex").slice(0, 12);
  return join(base, `credman-${hash}.dll`);
}

interface PsReply {
  ok: boolean;
  found?: boolean | Record<string, boolean>;
  value?: string | null;
  deleted?: boolean;
}

/** Windows Credential Manager through PowerShell and advapi32. */
export class WindowsCredentialStore implements CredentialStore {
  readonly kind = "windows" as const;
  private readonly dll: string;

  constructor(dll: string = helperDllPath()) {
    this.dll = dll;
  }

  private async call(body: Record<string, unknown>): Promise<PsReply> {
    const request = { ...body, dll: this.dll };
    const result = await execa(
      "powershell.exe",
      ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", ENCODED_SCRIPT],
      { input: JSON.stringify(request), reject: false, timeout: 20_000, windowsHide: true, stripFinalNewline: true },
    );
    if (result.exitCode !== 0) {
      // Never forward stderr: it could echo the request.
      throw new CredentialError("Windows Credential Manager did not answer");
    }
    try {
      return JSON.parse(String(result.stdout)) as PsReply;
    } catch {
      throw new CredentialError("Windows Credential Manager answered in an unexpected shape");
    }
  }

  async has(targets: string[]): Promise<Record<string, boolean>> {
    targets.forEach(assertReadableTarget);
    if (targets.length === 0) return {};
    const reply = await this.call({ op: "has", targets });
    const found = (reply.found ?? {}) as Record<string, boolean>;
    return Object.fromEntries(targets.map((target) => [target, found[target] === true]));
  }

  async read(target: string): Promise<string | undefined> {
    assertReadableTarget(target);
    const reply = await this.call({ op: "read", target });
    return reply.found === true && typeof reply.value === "string" && reply.value.length > 0 ? reply.value : undefined;
  }

  async write(target: string, secret: string): Promise<void> {
    assertWritableTarget(target);
    if (secret.length === 0) throw new CredentialError("An empty credential cannot be stored");
    const reply = await this.call({ op: "write", target, secret });
    if (!reply.ok) throw new CredentialError("Windows Credential Manager refused to store the credential");
  }

  async delete(target: string): Promise<boolean> {
    assertWritableTarget(target);
    const reply = await this.call({ op: "delete", target });
    return reply.deleted === true;
  }
}

/** In-memory store for tests and for a sandbox with no OS store. */
export class MemoryCredentialStore implements CredentialStore {
  readonly kind = "memory" as const;
  private readonly values = new Map<string, string>();

  constructor(seed: Record<string, string> = {}) {
    for (const [target, value] of Object.entries(seed)) this.values.set(target, value);
  }

  async has(targets: string[]): Promise<Record<string, boolean>> {
    targets.forEach(assertReadableTarget);
    return Object.fromEntries(targets.map((target) => [target, this.values.has(target)]));
  }

  async read(target: string): Promise<string | undefined> {
    assertReadableTarget(target);
    return this.values.get(target);
  }

  async write(target: string, secret: string): Promise<void> {
    assertWritableTarget(target);
    if (secret.length === 0) throw new CredentialError("An empty credential cannot be stored");
    this.values.set(target, secret);
  }

  async delete(target: string): Promise<boolean> {
    assertWritableTarget(target);
    return this.values.delete(target);
  }
}

/** Off Windows there is no store 4GE trusts yet; it says so instead of writing a file. */
export class UnavailableCredentialStore implements CredentialStore {
  readonly kind = "unavailable" as const;
  async has(targets: string[]): Promise<Record<string, boolean>> {
    return Object.fromEntries(targets.map((target) => [target, false]));
  }
  async read(): Promise<string | undefined> {
    return undefined;
  }
  async write(): Promise<void> {
    throw new CredentialError(
      "This PC has no credential store 4GE can use yet. Set the provider's environment variable for the 4GE server instead.",
    );
  }
  async delete(): Promise<boolean> {
    return false;
  }
}

export function defaultCredentialStore(platform: NodeJS.Platform = process.platform): CredentialStore {
  return platform === "win32" ? new WindowsCredentialStore() : new UnavailableCredentialStore();
}

export function adapterCredentialTarget(slug: string): string {
  return `${CREDENTIAL_PREFIX}adapter:${slug}`;
}

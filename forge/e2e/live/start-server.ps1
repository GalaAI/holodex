# Start the built 4GE server for the live integration run, the way Holodex does
# (ForgeHost.cs): the user's Nebius Token Factory key goes into the server's
# environment only. The key is read from Windows Credential Manager with the
# same CredReadW reader and UTF-16 or UTF-8 rule as Holodex's
# ArkStationClient.TryReadWindowsCredential, and is never printed or written.
#
#   pwsh forge/e2e/live/start-server.ps1 -DataRoot <dir> -AinboxQueue <dir> -Log <file>
param(
  [Parameter(Mandatory = $true)][string]$DataRoot,
  [Parameter(Mandatory = $true)][string]$AinboxQueue,
  [Parameter(Mandatory = $true)][string]$Log,
  [int]$Port = 17400,
  [string]$Target = "Omniwand:NebiusTokenFactory"
)
$ErrorActionPreference = "Stop"

if (-not ("Forge.CredReader" -as [type])) {
  Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace Forge {
  public static class CredReader {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW {
      public uint Flags; public uint Type; public IntPtr TargetName; public IntPtr Comment;
      public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
      public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist;
      public uint AttributeCount; public IntPtr Attributes; public IntPtr TargetAlias; public IntPtr UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr cred);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr cred);
    public static string Read(string target) {
      IntPtr ptr;
      if (!CredReadW(target, 1, 0, out ptr) || ptr == IntPtr.Zero) return null;
      try {
        var cred = (CREDENTIALW)Marshal.PtrToStructure(ptr, typeof(CREDENTIALW));
        if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
        var bytes = new byte[cred.CredentialBlobSize];
        Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
        if (bytes.Length >= 2 && bytes[1] == 0) return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
      } finally { CredFree(ptr); }
    }
  }
}
"@
}

$key = [Forge.CredReader]::Read($Target)
if ([string]::IsNullOrWhiteSpace($key)) {
  Write-Output "No credential named $Target; the server starts with no model connected."
} else {
  $env:NEBIUS_API_KEY = $key.Trim()
}
$key = $null
$env:FORGE_DATA_ROOT = $DataRoot
$env:FORGE_AINBOX_QUEUE = $AinboxQueue
$env:FORGE_PORT = "$Port"

$forge = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Log) | Out-Null
& node (Join-Path $forge "apps/server/dist/main.js") *>> $Log

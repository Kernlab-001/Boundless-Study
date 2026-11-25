// BPE Stage 1 — Target binary that AppLocker should BLOCK for bpe_stage1_test.
// Compiled with .NET Framework 4 csc.exe for universal standard-user NTFS RX access.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;

[assembly: AssemblyTitle("BpeStage1BlockedTarget")]
[assembly: AssemblyDescription("BPE Stage 1 AppLocker Enforcement Test - Blocked Target")]
[assembly: AssemblyVersion("1.0.0.0")]

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("STAGE1_EXECUTED");
        Console.WriteLine("TARGET: bpe-stage1-blocked.exe");

        string markerPath = Path.Combine(
            @"C:\BpeStage1Proof\out",
            "default-blocked-marker.json");

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--marker", StringComparison.OrdinalIgnoreCase))
            {
                markerPath = args[i + 1];
                break;
            }
        }

        string userSid = string.Empty;
        using (WindowsIdentity id = WindowsIdentity.GetCurrent())
        {
            if (id.User != null)
            {
                userSid = id.User.Value;
            }
        }

        string dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = string.Format(
            "{{\"binary\":\"bpe-stage1-blocked.exe\",\"pid\":{0},\"userName\":\"{1}\",\"userSid\":\"{2}\",\"utc\":\"{3}\"}}",
            Process.GetCurrentProcess().Id,
            Environment.UserName.Replace("\\", "\\\\").Replace("\"", "\\\""),
            userSid.Replace("\\", "\\\\").Replace("\"", "\\\""),
            DateTime.UtcNow.ToString("o"));

        File.WriteAllText(markerPath, json);
        Console.WriteLine("MARKER: " + markerPath);
        return 0;
    }
}

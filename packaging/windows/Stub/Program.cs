using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Verdite2.Stub;

/// <summary>
/// Root-of-install launcher for the Windows package.
///
/// A self-contained apphost looks for its managed assembly and hostfxr next to
/// itself, so every DLL has to live in the same folder as the real Verdite2.exe.
/// Putting that folder at the install root is a hundred files beside the thing
/// the player double-clicks. This process is WinExe (no extra console on
/// double-click), attaches to a parent console when there is one so KF2_LOG
/// still reaches a terminal, and waits so the child's AttachConsole can join.
/// </summary>
static class Program
{
    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    static int Main()
    {
        try { AttachConsole(AttachParentProcess); }
        catch { /* No parent console: Explorer, a shortcut, Inno's launch. */ }

        var root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exe = Path.Combine(root, "bin", "Verdite2.exe");
        if (!File.Exists(exe))
        {
            MessageBoxW(IntPtr.Zero,
                "Verdite2 cannot start because bin\\Verdite2.exe is missing.",
                "Verdite2", 0x10);
            return 1;
        }

        var args = Environment.GetCommandLineArgs();
        var childArgs = new StringBuilder();
        for (var i = 1; i < args.Length; i++)
        {
            if (i > 1) childArgs.Append(' ');
            childArgs.Append(Quote(args[i]));
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = childArgs.ToString(),
            UseShellExecute = false,
            WorkingDirectory = root,
        };

        using var p = Process.Start(psi);
        if (p is null) return 1;
        p.WaitForExit();
        return p.ExitCode;
    }

    static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}

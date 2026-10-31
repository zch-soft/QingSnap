using System.Runtime.InteropServices;
using System.Text;

namespace QingSnap.App.Infrastructure;

internal static class ShellLinkInterop
{
    public static void Create(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string iconPath,
        string description)
    {
        object shellLink = new ShellLink();
        try
        {
            var link = (IShellLinkW)shellLink;
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetIconLocation(iconPath, 0);
            link.SetDescription(description);
            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder filePath, int maximumCount, nint findData, uint flags);

        void GetIDList(out nint itemIdList);

        void SetIDList(nint itemIdList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maximumCount);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder workingDirectory, int maximumCount);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string workingDirectory);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumCount);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCommand);

        void SetShowCmd(int showCommand);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maximumCount, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(nint windowHandle, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string filePath);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}

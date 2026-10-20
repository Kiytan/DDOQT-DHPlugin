using System;
using System.Runtime.InteropServices;

namespace QuestTracker;

/// <summary>
/// The standard Windows Save As dialog, called directly through comdlg32.
/// Dungeon Helper 5 plugins run inside the game process without Windows Forms, so SaveFileDialog
/// is not available. Call it from an STA thread other than the render thread, as it blocks until
/// the user closes it.
/// </summary>
internal static class NativeSaveFileDialog
{
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    // Without this the dialog changes the working directory of the whole game process
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER = 0x00080000;

    private const int MaxPathLength = 32768;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileName(ref OpenFileName ofn);

    /// <param name="title">Dialog title</param>
    /// <param name="fileName">File name suggested to the user</param>
    /// <param name="filter">Pairs of description and pattern, each ending in \0</param>
    /// <param name="defaultExtension">Added when the user types a name without an extension</param>
    /// <returns>The chosen path, or null if the user cancelled</returns>
    public static string? Show(string title, string fileName, string filter, string defaultExtension)
    {
        var buffer = Marshal.AllocHGlobal(MaxPathLength * sizeof(char));
        try
        {
            var initial = fileName.ToCharArray();
            Marshal.Copy(initial, 0, buffer, initial.Length);
            Marshal.WriteInt16(buffer, initial.Length * sizeof(char), 0);

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = MaxPathLength,
                lpstrTitle = title,
                lpstrDefExt = defaultExtension,
                Flags = OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST | OFN_EXPLORER
            };

            return GetSaveFileName(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

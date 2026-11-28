using System.Runtime.InteropServices;

namespace MockProof;

/// <summary>Asks Windows for subpixel (ClearType) text smoothing in red, green, blue order, without a change of the stored profile.</summary>
internal static class NativeText
{
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, nint value, uint flags);

    public static void UseSubpixelSmoothing()
    {
        const uint setFontSmoothing = 0x004B, setType = 0x200B, setOrientation = 0x2013;
        const nint clearType = 2, redGreenBlue = 1;
        SystemParametersInfo(setFontSmoothing, 1, 0, 0);
        SystemParametersInfo(setType, 0, clearType, 0);
        SystemParametersInfo(setOrientation, 0, redGreenBlue, 0);
    }
}

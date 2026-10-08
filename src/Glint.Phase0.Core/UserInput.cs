namespace Glint.Phase0.Core;

/// <summary>
/// When the user last pressed a key or moved the mouse, system-wide. Only the
/// time is ever read, never which key: enough to tell a change the user
/// caused from a page changing by itself.
/// </summary>
public static class UserInput
{
    /// Input this recent means the user caused what changed on screen.
    public const long CausedWithinMilliseconds = 5_000;

    /// Milliseconds since the last input; 0 if Windows cannot say, so an
    /// unknown reads as "the user is here" rather than silently as absent.
    public static long IdleMilliseconds()
    {
        var info = new NativeMethods.LastInputInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>()
        };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return 0;
        }

        // Unsigned subtraction stays right across the 49-day tick wrap.
        return unchecked(NativeMethods.GetTickCount() - info.Time);
    }
}

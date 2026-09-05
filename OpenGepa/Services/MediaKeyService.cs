using System.Runtime.InteropServices;

namespace OpenGepa.Services;

/// <summary>Windowsのグローバルメディアキーを送信します。</summary>
public static class MediaKeyService
{
    private const uint KeyUp = 0x0002;

    public static bool TrySend(string presetId)
    {
        if (!TryGetVirtualKey(presetId, out var key)) return false;
        keybd_event(key, 0, 0, UIntPtr.Zero); keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        return true;
    }

    public static bool TryGetVirtualKey(string presetId, out byte key)
    {
        key = presetId switch
        {
            "media-previous" => 0xB1,
            "media-play-pause" => 0xB3,
            "media-next" => 0xB0,
            "media-stop" => 0xB2,
            "media-volume-down" => 0xAE,
            "media-volume-up" => 0xAF,
            "media-volume-mute" => 0xAD,
            _ => 0
        };
        return key != 0;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}

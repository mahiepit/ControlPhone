using System.Buffers.Binary;
using System.Text;

namespace ControlPhone.Scrcpy;

/// <summary>Đóng gói control message theo giao thức scrcpy v5.0 (ControlMessageReader.java). Big-endian.</summary>
public static class ControlMessages
{
    public const byte InjectKeycode = 0, InjectText = 1, InjectTouch = 2, InjectScroll = 3, BackOrScreenOn = 4,
        ExpandNotificationPanel = 5, ExpandSettingsPanel = 6, CollapsePanels = 7, GetClipboard = 8, SetClipboard = 9,
        SetDisplayPower = 10, RotateDevice = 11, StartApp = 16, ResetVideo = 17;

    public const long PointerIdGenericFinger = -2;
    public const byte ActionDown = 0, ActionUp = 1, ActionMove = 2;

    public static byte[] Keycode(byte action, int code, int repeat = 0, int meta = 0)
    {
        var b = new byte[14];
        b[0] = InjectKeycode; b[1] = action;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(2), code);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(6), repeat);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), meta);
        return b;
    }

    public static byte[] Text(string s)
    {
        var body = Encoding.UTF8.GetBytes(s);
        var b = new byte[5 + body.Length];
        b[0] = InjectText;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(1), (uint)body.Length);
        body.CopyTo(b, 5);
        return b;
    }

    public static byte[] Touch(byte action, long pointerId, int x, int y, int w, int h, float pressure)
    {
        var b = new byte[32];
        b[0] = InjectTouch; b[1] = action;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(2), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(14), y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), pressure >= 1 ? (ushort)0xffff : (ushort)Math.Round(pressure * 0x10000));
        // action button (24) + buttons (28) = 0
        return b;
    }

    static short ToI16Fixed(float v)
    {
        var f = Math.Clamp(v / 16f, -1f, 1f);
        return (short)Math.Clamp((int)Math.Round(f * 0x8000), -0x8000, 0x7fff);
    }

    public static byte[] Scroll(int x, int y, int w, int h, float hScroll, float vScroll)
    {
        var b = new byte[21];
        b[0] = InjectScroll;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), x);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(5), y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(9), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(11), (ushort)h);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(13), ToI16Fixed(hScroll));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(15), ToI16Fixed(vScroll));
        return b;
    }

    public static byte[] Empty(byte type) => [type];
    /// <summary>copyKey: 0 = không, 1 = gửi COPY trước rồi lấy clipboard.</summary>
    public static byte[] GetClipboardMsg(byte copyKey) => [GetClipboard, copyKey];

    public static byte[] StartAppMsg(string name)
    {
        var body = Encoding.UTF8.GetBytes(name);
        if (body.Length > 255) body = body[..255];
        return [StartApp, (byte)body.Length, .. body];
    }
    public static byte[] DisplayPower(bool on) => [SetDisplayPower, on ? (byte)1 : (byte)0];

    static long clipboardSeq = 1;
    public static byte[] SetClipboardMsg(string s, bool paste)
    {
        var body = Encoding.UTF8.GetBytes(s);
        var b = new byte[14 + body.Length];
        b[0] = SetClipboard;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(1), Interlocked.Increment(ref clipboardSeq));
        b[9] = paste ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(10), (uint)body.Length);
        body.CopyTo(b, 14);
        return b;
    }
}

/// <summary>Mã phím Android hay dùng.</summary>
public static class AndroidKey
{
    public const int Home = 3, Back = 4, VolumeUp = 24, VolumeDown = 25, Power = 26, Enter = 66, Del = 67,
        Menu = 82, Escape = 111, ForwardDel = 112, AppSwitch = 187, Sleep = 223, Wakeup = 224;
}

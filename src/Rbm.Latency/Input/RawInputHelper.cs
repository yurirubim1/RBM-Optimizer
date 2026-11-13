using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Rbm.Latency.Native;

namespace Rbm.Latency;

/// <summary>Tipos de dispositivo para registro de Raw Input (página de uso HID 0x01, Generic Desktop).</summary>
[Flags]
public enum RawInputDeviceKinds
{
    None = 0,
    Mouse = 1,      // usage 0x02
    Keyboard = 2,   // usage 0x06
    Joystick = 4,   // usage 0x04
    Gamepad = 8,    // usage 0x05
}

public enum RawInputType { Mouse = 0, Keyboard = 1, Hid = 2 }

/// <summary>Dados mínimos extraídos de um WM_INPUT.</summary>
/// <param name="IsMovement">Para mouse: deslocamento ≠ 0 (relatórios só de botão/roda não contam para a taxa).</param>
public readonly record struct RawInputEventInfo(IntPtr Device, RawInputType Type, bool IsMovement, int DeltaX, int DeltaY, ushort ButtonFlags);

/// <summary>Dispositivo listado por GetRawInputDeviceList.</summary>
public sealed record RawInputDeviceEntry(long Handle, RawInputType Type, string? DevicePath);

/// <summary>
/// Auxiliares de Raw Input para a UI (registro/cancelamento e leitura de WM_INPUT). Não requer elevação.
/// Fluxo típico na janela (WPF: HwndSource.AddHook):
/// <code>
/// RawInputHelper.Register(hwnd, RawInputDeviceKinds.Mouse);              // RIDEV_INPUTSINK: recebe mesmo sem foco
/// // no WndProc: if (msg == RawInputHelper.WM_INPUT &amp;&amp; RawInputHelper.TryParse(lParam, out var e) &amp;&amp; e.IsMovement)
/// //     estimator.Add(e.Device, Stopwatch.GetTimestamp());
/// // (não marque handled: DefWindowProc deve processar WM_INPUT)
/// RawInputHelper.Unregister(RawInputDeviceKinds.Mouse);
/// </code>
/// </summary>
public static class RawInputHelper
{
    public const int WM_INPUT = 0x00FF;

    private static readonly int HeaderSize = Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();

    /// <summary>Registra a janela para receber WM_INPUT dos tipos indicados.</summary>
    /// <param name="inputSink">RIDEV_INPUTSINK: recebe entrada mesmo quando a janela não está em primeiro plano (exige hwnd).</param>
    /// <exception cref="Win32Exception">Falha do RegisterRawInputDevices.</exception>
    public static void Register(IntPtr hwnd, RawInputDeviceKinds kinds, bool inputSink = true)
    {
        if (kinds == RawInputDeviceKinds.None) throw new ArgumentException("Nenhum tipo de dispositivo informado.", nameof(kinds));
        if (inputSink && hwnd == IntPtr.Zero) throw new ArgumentException("RIDEV_INPUTSINK exige um HWND válido.", nameof(hwnd));
        var devs = Build(kinds, inputSink ? NativeMethods.RIDEV_INPUTSINK : 0, hwnd);
        if (!NativeMethods.RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices falhou.");
    }

    /// <summary>Cancela o registro (RIDEV_REMOVE; hwndTarget deve ser nulo).</summary>
    public static void Unregister(RawInputDeviceKinds kinds)
    {
        if (kinds == RawInputDeviceKinds.None) return;
        var devs = Build(kinds, NativeMethods.RIDEV_REMOVE, IntPtr.Zero);
        if (!NativeMethods.RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Falha ao cancelar o registro de Raw Input.");
    }

    private static NativeMethods.RAWINPUTDEVICE[] Build(RawInputDeviceKinds kinds, uint flags, IntPtr hwnd)
    {
        var list = new List<NativeMethods.RAWINPUTDEVICE>();
        void Add(RawInputDeviceKinds k, ushort usage)
        {
            if (kinds.HasFlag(k)) list.Add(new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = usage, dwFlags = flags, hwndTarget = hwnd });
        }
        Add(RawInputDeviceKinds.Mouse, 0x02);
        Add(RawInputDeviceKinds.Keyboard, 0x06);
        Add(RawInputDeviceKinds.Joystick, 0x04);
        Add(RawInputDeviceKinds.Gamepad, 0x05);
        return list.ToArray();
    }

    /// <summary>Obtém só o handle do dispositivo de um WM_INPUT (RID_HEADER; mais barato).</summary>
    public static bool TryGetDeviceHandle(IntPtr lParam, out IntPtr device)
    {
        device = IntPtr.Zero;
        uint size = (uint)HeaderSize;
        IntPtr buf = Marshal.AllocHGlobal(HeaderSize);
        try
        {
            uint r = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_HEADER, buf, ref size, (uint)HeaderSize);
            if (r == uint.MaxValue || r == 0) return false;
            device = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buf).hDevice;
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Lê o WM_INPUT completo (RID_INPUT) e extrai tipo, dispositivo e deslocamento do mouse.</summary>
    public static unsafe bool TryParse(IntPtr lParam, out RawInputEventInfo info)
    {
        info = default;
        uint size = 0;
        if (NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, (uint)HeaderSize) != 0 || size < HeaderSize || size > 8192)
            return false;
        byte* buf = stackalloc byte[(int)size];
        uint r = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, (IntPtr)buf, ref size, (uint)HeaderSize);
        if (r == uint.MaxValue || r < HeaderSize) return false;

        var header = *(NativeMethods.RAWINPUTHEADER*)buf;
        var type = header.dwType switch { NativeMethods.RIM_TYPEMOUSE => RawInputType.Mouse, NativeMethods.RIM_TYPEKEYBOARD => RawInputType.Keyboard, _ => RawInputType.Hid };
        if (type == RawInputType.Mouse && r >= HeaderSize + 20)
        {
            // RAWMOUSE (x64): usFlags@0, [pad], usButtonFlags@4, usButtonData@6, ulRawButtons@8, lLastX@12, lLastY@16, ulExtraInformation@20
            byte* m = buf + HeaderSize;
            ushort buttonFlags = *(ushort*)(m + 4);
            int dx = *(int*)(m + 12);
            int dy = *(int*)(m + 16);
            info = new RawInputEventInfo(header.hDevice, type, dx != 0 || dy != 0, dx, dy, buttonFlags);
        }
        else
        {
            info = new RawInputEventInfo(header.hDevice, type, type != RawInputType.Keyboard, 0, 0, 0);
        }
        return true;
    }

    /// <summary>Caminho de interface do dispositivo (ex.: \\?\HID#VID_046D&amp;PID_C08B...) para casar com o Instance ID.</summary>
    public static string? GetDeviceName(IntPtr device)
    {
        uint size = 0;
        NativeMethods.GetRawInputDeviceInfoW(device, NativeMethods.RIDI_DEVICENAME, null, ref size);
        if (size == 0 || size > 4096) return null;
        var buf = new char[size];
        uint r = NativeMethods.GetRawInputDeviceInfoW(device, NativeMethods.RIDI_DEVICENAME, buf, ref size);
        if (r == uint.MaxValue || r == 0) return null;
        return new string(buf, 0, (int)Math.Min(r, size)).TrimEnd('\0');
    }

    /// <summary>Lista os dispositivos de Raw Input atualmente conectados.</summary>
    public static IReadOnlyList<RawInputDeviceEntry> ListDevices()
    {
        uint count = 0;
        uint cb = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();
        if (NativeMethods.GetRawInputDeviceList(null, ref count, cb) != 0 || count == 0) return Array.Empty<RawInputDeviceEntry>();
        var arr = new NativeMethods.RAWINPUTDEVICELIST[count];
        uint got = NativeMethods.GetRawInputDeviceList(arr, ref count, cb);
        if (got == uint.MaxValue) return Array.Empty<RawInputDeviceEntry>();
        return arr.Take((int)got)
            .Select(d => new RawInputDeviceEntry(d.hDevice.ToInt64(),
                d.dwType switch { NativeMethods.RIM_TYPEMOUSE => RawInputType.Mouse, NativeMethods.RIM_TYPEKEYBOARD => RawInputType.Keyboard, _ => RawInputType.Hid },
                GetDeviceName(d.hDevice)))
            .ToList();
    }

    /// <summary>Timestamp QPC atual (mesma base de <see cref="Stopwatch.Frequency"/>).</summary>
    public static long QpcNow() => Stopwatch.GetTimestamp();
}

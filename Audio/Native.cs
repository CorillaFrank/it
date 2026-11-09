using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HamulProto;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    // ---- Estructuras para SendInput (teclado simulado) ----
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion U; }

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint uCode, uint uMapType);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    public static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Título de la ventana que tiene el foco (para saber si es Dota 2)
    public static string ForegroundTitle()
    {
        var sb = new StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString();
    }

    // Pulsa o suelta una tecla usando su código de hardware (scan code), como un teclado real
    public static void SendKey(int vk, bool down)
    {
        ushort scan = (ushort)MapVirtualKey((uint)vk, 0);
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = KEYEVENTF_SCANCODE | (down ? 0u : KEYEVENTF_KEYUP)
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // --- NUEVA FUNCIÓN: reiniciar el servicio de audio ---
    public static void RestartAudioService()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c net stop Audiosrv && net start Audiosrv",
                Verb = "runas", // Pide permisos de administrador
                CreateNoWindow = true,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }
}

// Cambiar el dispositivo predeterminado usa una interfaz COM NO documentada de Windows.
// Funciona en Windows 10/11, pero Microsoft podría cambiarla. Si falla, la app sigue sin ella.
public static class PolicyConfig
{
    public static bool SetDefaultCapture(string deviceId)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            pc.SetDefaultEndpoint(deviceId, 0); // consola
            pc.SetDefaultEndpoint(deviceId, 1); // multimedia
            pc.SetDefaultEndpoint(deviceId, 2); // comunicaciones
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("No pude cambiar el micrófono predeterminado: " + ex.Message);
            return false;
        }
    }
}

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
class PolicyConfigClient { }

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr format);
    [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr format);
    [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
    [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr period);
    [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr mode);
    [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr mode);
    [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string name, int role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool visible);
}
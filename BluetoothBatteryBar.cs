// BluetoothBatteryBar: widget sobre la barra de tareas de Windows que muestra
// los dispositivos Bluetooth conectados y su batería.
// Compilar con build.ps1 (usa el compilador de C# incluido en Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

// Necesario (junto con app.manifest) para que WPF escale bien en pantallas con distinta escala.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace BtBar
{
    class Dev
    {
        public string Mac;
        public string Name;
        public int Battery = -1;      // -1 = el dispositivo no informa de batería
        public bool Connected;
        public bool LE;
        public string Ids = "";       // ids de todos sus nodos, para deducir el tipo
        public bool Charging;
        public string GlyphOverride;  // icono ya decidido (dispositivos Logitech)
        public DateTime Seen;
        public string Label;          // texto en lugar del porcentaje (mandos Xbox: solo dan 4 niveles)
    }

    /// Mandos Xbox (adaptador inalámbrico Xbox o Bluetooth) a través de XInput.
    /// XInput no da porcentaje, solo cuatro niveles: vacía, baja, media y llena.
    static class Xbox
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BATTERY { public byte Type; public byte Level; }
        [DllImport("xinput1_4.dll")]
        static extern uint XInputGetBatteryInformation(uint index, byte devType, out BATTERY info);

        public static List<Dev> Poll()
        {
            List<Dev> list = new List<Dev>();
            for (uint i = 0; i < 4; i++)
            {
                BATTERY b;
                if (XInputGetBatteryInformation(i, 0 /*mando*/, out b) != 0) continue;
                if (b.Type != 2 && b.Type != 3) continue;   // solo mandos a pilas/batería (ni por cable ni virtuales)
                string[] labels = { "Vacía", "Baja", "Media", "Llena" };
                int[] fill = { 5, 25, 60, 100 };
                int lvl = Math.Min((int)b.Level, 3);
                list.Add(new Dev { Mac = "X" + i, Name = "Mando Xbox", Battery = fill[lvl], Label = labels[lvl], Connected = true, GlyphOverride = "" });
            }
            if (list.Count > 1)
                for (int i = 0; i < list.Count; i++) list[i].Name += " " + (i + 1);
            return list;
        }
    }

    /// Batería de dispositivos Logitech conectados por receptor USB (Bolt, Unifying, Lightspeed) o por cable,
    /// leída con el protocolo HID++ 2.0 de Logitech a través de la colección HID "de proveedor".
    static class Logi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid Guid; public uint Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort n1, n2, n3, n4, n5, n6, n7, n8, n9, n10;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(Microsoft.Win32.SafeHandles.SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(ref Guid cls, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid cls, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, uint len, out uint read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, uint len, out uint written, IntPtr ov);
        [DllImport("kernel32.dll")] static extern bool CancelIoEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, IntPtr ov);

        const byte SW = 0x0B;   // identificador de "software" para reconocer nuestras respuestas

        class Channel
        {
            public string Path;
            public Microsoft.Win32.SafeHandles.SafeFileHandle R, W;
            public System.Collections.Concurrent.BlockingCollection<byte[]> Q = new System.Collections.Concurrent.BlockingCollection<byte[]>();
            public volatile bool Dead;
            public Dictionary<byte, string> Names = new Dictionary<byte, string>();
            public Dictionary<byte, byte> Types = new Dictionary<byte, byte>();

            public void Close()
            {
                Dead = true;
                try { CancelIoEx(R, IntPtr.Zero); } catch { }
                try { R.Close(); } catch { }
                try { W.Close(); } catch { }
            }
        }

        static Dictionary<string, Channel> channels = new Dictionary<string, Channel>();
        static HashSet<string> rejected = new HashSet<string>();
        static Dictionary<string, Dev> cache = new Dictionary<string, Dev>();
        public static StringBuilder Log;   // solo para diagnóstico (--dump)

        static void L(string s) { if (Log != null) Log.AppendLine(s); }

        static List<string> FindPaths()
        {
            List<string> paths = new List<string>();
            Guid hid;
            HidD_GetHidGuid(out hid);
            IntPtr set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12 /*PRESENT|DEVICEINTERFACE*/);
            if (set == new IntPtr(-1)) return paths;
            try
            {
                SP_DEVICE_INTERFACE_DATA ifd = new SP_DEVICE_INTERFACE_DATA();
                ifd.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref ifd); i++)
                {
                    uint req;
                    SetupDiGetDeviceInterfaceDetailW(set, ref ifd, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req == 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetailW(set, ref ifd, buf, req, out req, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                            if (p != null && p.ToLowerInvariant().Contains("vid_046d")) paths.Add(p);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        static Channel Open(string path)
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle w = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (w.IsInvalid) return null;
            HIDP_CAPS caps = new HIDP_CAPS();
            IntPtr pp;
            bool ok = false;
            if (HidD_GetPreparsedData(w, out pp))
            {
                ok = HidP_GetCaps(pp, out caps) == 0x00110000
                     && (caps.UsagePage == 0xFF00 || caps.UsagePage == 0xFF43)
                     && caps.InputReportByteLength == 20 && caps.OutputReportByteLength == 20;
                HidD_FreePreparsedData(pp);
            }
            if (!ok) { w.Close(); return null; }
            Microsoft.Win32.SafeHandles.SafeFileHandle r = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (r.IsInvalid) { w.Close(); return null; }

            Channel c = new Channel { Path = path, R = r, W = w };
            Thread t = new Thread(delegate()
            {
                byte[] buf = new byte[64];
                while (!c.Dead)
                {
                    uint n;
                    if (!ReadFile(c.R, buf, (uint)buf.Length, out n, IntPtr.Zero)) { c.Dead = true; break; }
                    if (n == 0) continue;
                    byte[] rep = new byte[n];
                    Array.Copy(buf, rep, (int)n);
                    if (c.Q.Count < 200) c.Q.Add(rep);
                }
            });
            t.IsBackground = true;
            t.Start();
            L("canal abierto: " + path);
            return c;
        }

        // Envía una petición HID++ 2.0 y espera su respuesta. Devuelve null si hay error o no responde.
        static byte[] Request(Channel c, byte dev, byte feat, byte func, params byte[] p)
        {
            if (c.Dead) return null;
            byte[] junk;
            while (c.Q.TryTake(out junk)) { }
            byte[] o = new byte[20];
            o[0] = 0x11; o[1] = dev; o[2] = feat; o[3] = (byte)((func << 4) | SW);
            for (int i = 0; i < p.Length && i < 16; i++) o[4 + i] = p[i];
            uint n;
            if (!WriteFile(c.W, o, 20, out n, IntPtr.Zero)) { c.Dead = true; return null; }
            DateTime end = DateTime.UtcNow.AddMilliseconds(500);
            while (true)
            {
                int ms = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                byte[] r;
                if (ms <= 0 || !c.Q.TryTake(out r, ms)) return null;
                if (r.Length < 7 || r[1] != dev) continue;
                if (r[2] == feat && r[3] == o[3]) return r;
                if ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == feat && r[4] == o[3]) return null;   // respuesta de error
            }
        }

        static byte Feature(Channel c, byte dev, int id)
        {
            byte[] r = Request(c, dev, 0, 0, (byte)(id >> 8), (byte)id);
            return r == null ? (byte)0 : r[4];
        }

        static int FromVoltage(int mv)
        {
            int[] v = { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500 };
            int[] pc = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0 };
            for (int i = 0; i < v.Length; i++) if (mv >= v[i]) return pc[i];
            return 0;
        }

        static Dev Query(Channel c, byte idx)
        {
            // Pedir la función "nombre del dispositivo" sirve además para saber si hay alguien en ese índice.
            byte[] r = Request(c, idx, 0, 0, 0x00, 0x05);
            if (r == null) return null;
            byte nameFeat = r[4];
            L("  índice " + idx + ": responde (nombre=" + nameFeat + ")");

            int batt = -1; bool chg = false;
            byte f = Feature(c, idx, 0x1004);                       // batería unificada
            if (f != 0)
            {
                byte[] s = Request(c, idx, f, 1);
                if (s != null)
                {
                    int soc = s[4], lvl = s[5];
                    if (soc == 0) soc = (lvl & 8) != 0 ? 90 : (lvl & 4) != 0 ? 50 : (lvl & 2) != 0 ? 20 : (lvl & 1) != 0 ? 5 : 0;
                    batt = soc; chg = s[6] >= 1 && s[6] <= 3;
                    L("    0x1004 -> " + BitConverter.ToString(s, 4, 6));
                }
            }
            if (batt < 0 && (f = Feature(c, idx, 0x1000)) != 0)     // estado de batería clásico
            {
                byte[] s = Request(c, idx, f, 0);
                if (s != null) { batt = s[4]; chg = s[6] >= 1 && s[6] <= 4; L("    0x1000 -> " + BitConverter.ToString(s, 4, 6)); }
            }
            if (batt < 0 && (f = Feature(c, idx, 0x1001)) != 0)     // solo voltaje
            {
                byte[] s = Request(c, idx, f, 0);
                if (s != null) { batt = FromVoltage((s[4] << 8) | s[5]); chg = (s[6] & 0x80) != 0; L("    0x1001 -> " + BitConverter.ToString(s, 4, 6)); }
            }
            if (batt < 0 || batt > 100) return null;

            string name; byte type = 255;
            if (!c.Names.TryGetValue(idx, out name) && nameFeat != 0)
            {
                byte[] cnt = Request(c, idx, nameFeat, 0);
                if (cnt != null)
                {
                    int total = cnt[4];
                    StringBuilder sb = new StringBuilder();
                    while (sb.Length < total)
                    {
                        byte[] part = Request(c, idx, nameFeat, 1, (byte)sb.Length);
                        if (part == null) break;
                        int take = Math.Min(16, total - sb.Length);
                        sb.Append(Encoding.ASCII.GetString(part, 4, take));
                    }
                    name = sb.ToString().TrimEnd('\0', ' ');
                    byte[] ty = Request(c, idx, nameFeat, 2);
                    if (ty != null) type = ty[4];
                    if (name.Length > 0) { c.Names[idx] = name; c.Types[idx] = type; }
                }
            }
            else c.Types.TryGetValue(idx, out type);
            if (type == 7) return null;                             // es el propio receptor
            if (string.IsNullOrEmpty(name)) name = "Logitech";
            L("    " + name + " tipo=" + type + " batería=" + batt + " cargando=" + chg);

            string glyph = type == 0 || type == 2 ? ""
                         : type == 3 || type == 4 || type == 5 ? ""
                         : type == 8 ? ""
                         : type == 12 || type == 11 ? ""
                         : Regex.IsMatch(name.ToLowerInvariant(), "key|craft") ? "" : "";
            return new Dev { Mac = "L" + c.Path + "#" + idx, Name = name, Battery = batt, Charging = chg, Connected = true, GlyphOverride = glyph, Seen = DateTime.UtcNow };
        }

        /// Consulta todos los receptores/dispositivos Logitech. Tarda hasta unos segundos: llamar desde un hilo de fondo.
        public static List<Dev> Poll()
        {
            List<string> paths = FindPaths();
            foreach (string gone in new List<string>(channels.Keys))
                if (channels[gone].Dead || !paths.Contains(gone)) { channels[gone].Close(); channels.Remove(gone); }
            foreach (string p in paths)
            {
                if (channels.ContainsKey(p) || rejected.Contains(p)) continue;
                Channel c = null;
                try { c = Open(p); } catch { }
                if (c != null) channels[p] = c; else rejected.Add(p);
            }

            foreach (Channel c in channels.Values)
            {
                L("canal " + c.Path);
                Dev direct = Query(c, 0xFF);                         // dispositivo conectado directamente (cable)
                if (direct != null) { cache[direct.Mac] = direct; continue; }
                for (byte i = 1; i <= 6 && !c.Dead; i++)             // dispositivos emparejados con el receptor
                {
                    Dev d = Query(c, i);
                    if (d != null) cache[d.Mac] = d;
                }
            }

            // Un dispositivo dormido no responde: se mantiene su último dato durante 5 minutos.
            List<Dev> list = new List<Dev>();
            foreach (string k in new List<string>(cache.Keys))
            {
                Dev d = cache[k];
                string path = k.Substring(1, k.LastIndexOf('#') - 1);
                if (!channels.ContainsKey(path) || (DateTime.UtcNow - d.Seen).TotalMinutes > 5) cache.Remove(k);
                else list.Add(d);
            }
            return list;
        }
    }

    static class Bt
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY { public Guid fmtid; public uint pid; }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(ref Guid cls, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
        static extern IntPtr SetupDiGetClassDevsByEnumW(IntPtr cls, string enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buf, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static Guid BluetoothClass = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        static DEVPROPKEY KeyBattery = new DEVPROPKEY { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
        static DEVPROPKEY KeyConnected = new DEVPROPKEY { fmtid = new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), pid = 15 };
        static DEVPROPKEY KeyFriendly = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        static DEVPROPKEY KeyName = new DEVPROPKEY { fmtid = new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), pid = 10 };
        static Regex MacRx = new Regex(@"(?:DEV_|&|_)([0-9A-F]{12})(?:_|\\|$)", RegexOptions.IgnoreCase);

        static byte[] Prop(IntPtr set, ref SP_DEVINFO_DATA d, DEVPROPKEY key)
        {
            byte[] buf = new byte[1024];
            uint type, req;
            if (!SetupDiGetDevicePropertyW(set, ref d, ref key, out type, buf, (uint)buf.Length, out req, 0) || req == 0) return null;
            byte[] r = new byte[req];
            Array.Copy(buf, r, (int)req);
            return r;
        }

        static string Str(byte[] b) { return b == null ? null : Encoding.Unicode.GetString(b).TrimEnd('\0'); }

        /// Todos los dispositivos Bluetooth emparejados, agrupados por dirección MAC.
        public static List<Dev> Enumerate()
        {
            Dictionary<string, Dev> map = new Dictionary<string, Dev>();
            Scan(SetupDiGetClassDevsW(ref BluetoothClass, IntPtr.Zero, IntPtr.Zero, 2 /*DIGCF_PRESENT*/), map);
            // Los auriculares Bluetooth clásico (p. ej. Nothing Ear) publican la batería en su nodo
            // "Hands-Free AG", que Windows pone en la clase System y no en la clase Bluetooth:
            // hay que recorrer también todo el enumerador BTHENUM, sea cual sea la clase.
            Scan(SetupDiGetClassDevsByEnumW(IntPtr.Zero, "BTHENUM", IntPtr.Zero, 6 /*DIGCF_PRESENT|DIGCF_ALLCLASSES*/), map);

            List<Dev> list = new List<Dev>();
            foreach (Dev dev in map.Values)
                if (!string.IsNullOrEmpty(dev.Name)) list.Add(dev);
            list.Sort(delegate(Dev a, Dev b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return list;
        }

        /// Recorre un conjunto de dispositivos y vuelca sus datos en el mapa por MAC. Libera el conjunto.
        static void Scan(IntPtr set, Dictionary<string, Dev> map)
        {
            if (set == new IntPtr(-1)) return;
            try
            {
                SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
                d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    StringBuilder sb = new StringBuilder(512);
                    int req;
                    if (!SetupDiGetDeviceInstanceIdW(set, ref d, sb, sb.Capacity, out req)) continue;
                    string id = sb.ToString().ToUpperInvariant();
                    Match m = MacRx.Match(id);
                    if (!m.Success) continue;
                    string mac = m.Groups[1].Value;
                    Dev dev;
                    if (!map.TryGetValue(mac, out dev)) { dev = new Dev { Mac = mac }; map[mac] = dev; }
                    if ((dev.Ids ?? "").Contains(id + "|")) continue;   // ya visto en la pasada anterior
                    dev.Ids += id + "|";

                    byte[] b = Prop(set, ref d, KeyBattery);
                    if (b != null && b.Length >= 1 && b[0] <= 100 && dev.Battery < 0) dev.Battery = b[0];
                    byte[] c = Prop(set, ref d, KeyConnected);
                    if (c != null && c.Length >= 1 && c[0] != 0) dev.Connected = true;

                    if (id.StartsWith(@"BTHENUM\DEV_") || id.StartsWith(@"BTHLE\DEV_"))
                    {
                        dev.LE = id.StartsWith(@"BTHLE\");
                        string name = Str(Prop(set, ref d, KeyFriendly));
                        if (string.IsNullOrEmpty(name)) name = Str(Prop(set, ref d, KeyName));
                        dev.Name = name;
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        /// Glifo de "Segoe Fluent Icons" según el tipo de dispositivo.
        public static string Glyph(Dev d)
        {
            if (d.GlyphOverride != null) return d.GlyphOverride;
            string n = (d.Name ?? "").ToLowerInvariant();
            if (Regex.IsMatch(n, "key|teclado|tecl")) return "";
            if (Regex.IsMatch(n, "mouse|rat[oó]n|mx master|mx anywhere|trackball")) return "";
            if (Regex.IsMatch(n, "controller|gamepad|mando|xbox|dualsense|dualshock")) return "";
            if (d.Ids.Contains("{0000112F-") || d.Ids.Contains("{00001116-") || Regex.IsMatch(n, "phone|iphone|galaxy|pixel|m[oó]vil")) return "";
            if (d.Ids.Contains("{0000110B-") || d.Ids.Contains("{0000111E-") || d.Ids.Contains("{00001108-")
                || Regex.IsMatch(n, "buds|pods|headphone|headset|auricular|cascos|speaker|altavoz")) return "";
            if (d.Ids.Contains("{0000110C-") || d.Ids.Contains("{0000110E-")) return "";
            return "";
        }
    }

    class Bar : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "BluetoothBatteryBar";

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

        IntPtr hwnd;
        IntPtr taskbar;     // barra de tareas a la que pertenece este widget
        bool primary;
        StackPanel panel;
        Border chrome;

        /// Crea un widget por cada barra de tareas (una por pantalla) y cierra los de las que ya no existen.
        public static void Sync(Dictionary<IntPtr, Bar> bars)
        {
            List<IntPtr> found = new List<IntPtr>();
            IntPtr main = FindWindowW("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) found.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowExW(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) found.Add(h);

            foreach (IntPtr gone in new List<IntPtr>(bars.Keys))
                if (!found.Contains(gone)) { bars[gone].Close(); bars.Remove(gone); }
            foreach (IntPtr tb in found)
                if (!bars.ContainsKey(tb))
                {
                    Bar b = new Bar(tb, tb == main);
                    bars[tb] = b;
                    b.Show();
                }
        }

        public Bar(IntPtr taskbar, bool primary)
        {
            this.taskbar = taskbar;
            this.primary = primary;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = -10000; Top = -10000;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chrome = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),   // casi invisible, pero recibe clics
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 3, 4, 3),
                Child = panel,
                Cursor = Cursors.Hand
            };
            chrome.MouseEnter += delegate { chrome.Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)); };
            chrome.MouseLeave += delegate { chrome.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); };
            chrome.MouseLeftButtonUp += delegate { OpenSettings(); };
            Content = chrome;
            BuildMenu();

            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtrW(hwnd, -20).ToInt64();
                ex |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/;
                SetWindowLongPtrW(hwnd, -20, new IntPtr(ex));
                // Llevar la ventana a su pantalla cuanto antes para que adopte la escala (PPP) de ese monitor.
                RECT tb;
                if (GetWindowRect(this.taskbar, out tb))
                    SetWindowPos(hwnd, new IntPtr(-1), tb.Right - 300, tb.Top, 0, 0, 0x0001 | 0x0010);
            };
            SizeChanged += delegate { Reposition(); };
            Loaded += delegate
            {
                if (Program.LastDevs != null) Render(Program.LastDevs, Program.LastLight);
                Reposition();
            };
        }

        void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem refresh = new MenuItem { Header = "Actualizar ahora" };
            refresh.Click += delegate { Program.RefreshNow(); };
            MenuItem settings = new MenuItem { Header = "Configuración de Bluetooth" };
            settings.Click += delegate { OpenSettings(); };
            MenuItem auto = new MenuItem { Header = "Iniciar con Windows", IsCheckable = true };
            auto.Click += delegate { SetAutoStart(auto.IsChecked); };
            MenuItem exit = new MenuItem { Header = "Salir" };
            exit.Click += delegate { Application.Current.Shutdown(); };
            menu.Items.Add(refresh);
            menu.Items.Add(settings);
            menu.Items.Add(new Separator());
            menu.Items.Add(auto);
            if (Program.IsInstalledCopy)
            {
                MenuItem uninstall = new MenuItem { Header = "Desinstalar…" };
                uninstall.Click += delegate
                {
                    if (MessageBox.Show("¿Desinstalar Bluetooth Battery Bar de este equipo?", "Bluetooth Battery Bar",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    Program.Uninstall();
                    Application.Current.Shutdown();
                };
                menu.Items.Add(uninstall);
            }
            menu.Items.Add(exit);
            menu.Opened += delegate { auto.IsChecked = GetAutoStart(); };
            chrome.ContextMenu = menu;
        }

        static void OpenSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); } catch { }
        }

        static bool GetAutoStart() { return Program.GetAutoStart(); }
        static void SetAutoStart(bool on) { Program.SetAutoStart(on, Process.GetCurrentProcess().MainModule.FileName); }

        public void Render(List<Dev> devs, bool light)
        {
            Brush fg = light ? new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1b)) : Brushes.White;
            Brush track = new SolidColorBrush(light ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255));
            FontFamily icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            FontFamily text = new FontFamily("Segoe UI Variable Text, Segoe UI");
            panel.Children.Clear();

            if (devs.Count == 0)
            {
                // Sin dispositivos: solo un icono de Bluetooth atenuado.
                panel.Children.Add(new TextBlock
                {
                    Text = "", FontFamily = icons, FontSize = 16, Foreground = fg, Opacity = 0.45,
                    Margin = new Thickness(5, 4, 5, 4), VerticalAlignment = VerticalAlignment.Center
                });
                chrome.ToolTip = "Ningún dispositivo Bluetooth conectado";
                return;
            }
            chrome.ToolTip = null;

            foreach (Dev d in devs)
            {
                StackPanel chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 0, 5, 0), Background = Brushes.Transparent };
                chip.Children.Add(new TextBlock
                {
                    Text = Bt.Glyph(d), FontFamily = icons, FontSize = 17, Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
                });

                StackPanel col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                StackPanel line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                if (d.Charging)
                    line.Children.Add(new TextBlock
                    {
                        Text = "", FontFamily = icons, FontSize = 9, Foreground = fg,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 1, 0)
                    });
                line.Children.Add(new TextBlock
                {
                    Text = d.Label != null ? d.Label : d.Battery >= 0 ? d.Battery + "%" : "—", FontFamily = text, FontSize = 12, Foreground = fg
                });
                col.Children.Add(line);
                Grid bar = new Grid { Width = 26, Height = 3, Margin = new Thickness(0, 2, 0, 0) };
                bar.Children.Add(new Border { Background = track, CornerRadius = new CornerRadius(1.5) });
                if (d.Battery >= 0)
                {
                    Color c = d.Battery <= 20 ? Color.FromRgb(0xE8, 0x48, 0x48)
                            : d.Battery <= 40 ? Color.FromRgb(0xF2, 0xA9, 0x1E)
                            : Color.FromRgb(0x3F, 0xC7, 0x6A);
                    bar.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(1.5),
                        HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(3, 26 * d.Battery / 100.0)
                    });
                }
                col.Children.Add(bar);
                chip.Children.Add(col);
                chip.ToolTip = d.Name + (d.Label != null ? " — batería " + d.Label.ToLower() : d.Battery >= 0 ? " — " + d.Battery + " % de batería" : " — no informa de la batería") + (d.Charging ? " (cargando)" : "");
                ToolTipService.SetInitialShowDelay(chip, 200);
                panel.Children.Add(chip);
            }
        }

        // Coloca la ventana sobre la barra de tareas, a la izquierda de la bandeja del sistema.
        public void Reposition()
        {
            if (hwnd == IntPtr.Zero) return;
            RECT tb;
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out tb)) return;

            if (FullscreenAppActive(taskbar))
            {
                if (IsWindowVisible(hwnd)) ShowWindow(hwnd, 0 /*SW_HIDE*/);
                return;
            }
            if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, 4 /*SW_SHOWNOACTIVATE*/);

            uint dpi = GetDpiForWindow(taskbar);
            if (dpi == 0) dpi = 96;
            int rightEdge;
            if (primary)
            {
                // Barra principal: justo a la izquierda de la bandeja del sistema.
                rightEdge = tb.Right - (int)(260 * dpi / 96);
                IntPtr tray = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                RECT tr;
                if (tray != IntPtr.Zero && GetWindowRect(tray, out tr) && tr.Right > tr.Left) rightEdge = tr.Left;
            }
            else
            {
                // Barras secundarias: solo tienen el reloj a la derecha (unos 100 px a escala 100 %).
                rightEdge = tb.Right - (int)(100 * dpi / 96);
            }

            RECT me;
            if (!GetWindowRect(hwnd, out me)) return;
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            int x = rightEdge - w - 4;
            int y = tb.Top + (tb.Bottom - tb.Top - h) / 2;
            SetWindowPos(hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, x, y, 0, 0, 0x0001 /*NOSIZE*/ | 0x0010 /*NOACTIVATE*/);
        }

        // Si hay un juego o vídeo a pantalla completa en el monitor de la barra, el widget se oculta.
        bool FullscreenAppActive(IntPtr taskbar)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == hwnd || fg == taskbar) return false;
            StringBuilder cls = new StringBuilder(64);
            GetClassNameW(fg, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "XamlExplorerHostIslandWindow") return false;
            IntPtr mon = MonitorFromWindow(fg, 2);
            if (mon != MonitorFromWindow(taskbar, 2)) return false;
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            RECT r;
            if (!GetMonitorInfoW(mon, ref mi) || !GetWindowRect(fg, out r)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
    }

    static class Program
    {
        public static List<Dev> LastDevs;
        public static bool LastLight;

        static Dictionary<IntPtr, Bar> bars = new Dictionary<IntPtr, Bar>();
        static Dispatcher ui;
        static string lastSignature;
        static bool demo;
        static Timer poll;

        static bool LightTaskbar()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        public static void RefreshNow()
        {
            lastSignature = null;
            refreshCount = 0;
            ThreadPool.QueueUserWorkItem(delegate { Refresh(); });
        }

        // ---- Instalación: el propio .exe es el instalador ----
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BluetoothBatteryBar";
        const string AppName = "BluetoothBatteryBar";

        static string InstallDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName); } }
        static string InstallExe { get { return System.IO.Path.Combine(InstallDir, AppName + ".exe"); } }
        static string CurrentExe { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        public static bool IsInstalledCopy { get { return string.Equals(CurrentExe, InstallExe, StringComparison.OrdinalIgnoreCase); } }

        // Arranque con Windows por dos vías a la vez: la clave Run del registro y un acceso directo
        // en la carpeta Inicio. Si Windows lanza las dos, la segunda copia se cierra sola.
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        static string StartupLnk { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Bluetooth Battery Bar.lnk"); } }

        public static bool GetAutoStart()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                if (k != null && k.GetValue(AppName) != null) return true;
            return System.IO.File.Exists(StartupLnk);
        }

        public static void SetAutoStart(bool on, string exe)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + exe + "\"");
                else k.DeleteValue(AppName, false);
            }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ApprovedKey))
                {
                    // 02 = habilitado en "Aplicaciones de inicio" de Windows.
                    if (on) k.SetValue(AppName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                    else k.DeleteValue(AppName, false);
                }
            }
            catch { }
            try
            {
                if (!on) { System.IO.File.Delete(StartupLnk); return; }
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartupLnk });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { exe });
                lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { System.IO.Path.GetDirectoryName(exe) });
                lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "Bluetooth Battery Bar" });
                lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch { }
        }

        static void KillOthers()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName(AppName))
                if (p.Id != me) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        }

        // Copia el programa a la carpeta del usuario, lo registra para arrancar con Windows y lo lanza.
        static void Install()
        {
            KillOthers();
            System.IO.Directory.CreateDirectory(InstallDir);
            System.IO.File.Copy(CurrentExe, InstallExe, true);
            SetAutoStart(true, InstallExe);
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Bluetooth Battery Bar");
                k.SetValue("DisplayVersion", "1.0");
                k.SetValue("Publisher", "Frank");
                k.SetValue("DisplayIcon", InstallExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + InstallExe + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            Process.Start(InstallExe);
        }

        public static void Uninstall()
        {
            KillOthers();
            SetAutoStart(false, InstallExe);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            // La carpeta se borra unos segundos después, cuando este proceso ya haya terminado.
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            try { Process.Start(psi); } catch { }
        }

        // Diagnóstico: BluetoothBatteryBar.exe --dump archivo.txt
        static void Dump(string file)
        {
            Logi.Log = new StringBuilder();
            try
            {
                foreach (Dev d in Logi.Poll()) Logi.Log.AppendLine("LOGI: " + d.Name + " " + d.Battery + "% cargando=" + d.Charging);
                foreach (Dev d in Bt.Enumerate()) Logi.Log.AppendLine("BT: " + d.Name + " " + d.Battery + "% conectado=" + d.Connected);
            }
            catch (Exception e) { Logi.Log.AppendLine("ERROR: " + e); }
            System.IO.File.WriteAllText(file, Logi.Log.ToString(), Encoding.UTF8);
        }

        static readonly object refreshLock = new object();
        static List<Dev> logiDevs = new List<Dev>();
        static int refreshCount;

        // Se ejecuta en un hilo de fondo cada 5 s.
        static void Refresh()
        {
            if (!Monitor.TryEnter(refreshLock)) return;
            try { RefreshCore(); }
            finally { Monitor.Exit(refreshLock); }
        }

        static void RefreshCore()
        {
            List<Dev> devs;
            try
            {
                // Los Logitech por USB se consultan una vez por minuto (es más lento que leer el Bluetooth).
                if (!demo && refreshCount++ % 12 == 0)
                {
                    try { logiDevs = Logi.Poll(); } catch { }
                }
                if (demo)
                {
                    devs = new List<Dev>();
                    devs.Add(new Dev { Name = "AZ15", Battery = 80, Connected = true, Ids = "{0000110B-" });
                    devs.Add(new Dev { Name = "MX Keys B", Battery = 55, Connected = true, LE = true });
                    devs.Add(new Dev { Name = "MX Master 3", Battery = 12, Connected = true, LE = true });
                    devs.Add(new Dev { Name = "S26 Ultra de frankar", Battery = -1, Connected = true, Ids = "{0000112F-" });
                }
                else
                {
                    devs = Bt.Enumerate().FindAll(delegate(Dev d) { return d.Connected; });
                    devs.AddRange(logiDevs);
                    try { devs.AddRange(Xbox.Poll()); } catch { }
                }
            }
            catch { return; }

            bool light = LightTaskbar();
            StringBuilder sig = new StringBuilder(light ? "L" : "D");
            foreach (Dev d in devs) sig.Append('|').Append(d.Mac).Append(d.Name).Append(d.Battery).Append(d.Charging);
            string s = sig.ToString();
            if (s == lastSignature) return;
            lastSignature = s;
            ui.BeginInvoke(new Action(delegate
            {
                LastDevs = devs;
                LastLight = light;
                foreach (Bar b in bars.Values) b.Render(devs, light);
            }));
        }

        [STAThread]
        static void Main(string[] args)
        {
            demo = Array.IndexOf(args, "--demo") >= 0;
            int dump = Array.IndexOf(args, "--dump");
            if (dump >= 0 && dump + 1 < args.Length) { Dump(args[dump + 1]); return; }

            if (Array.IndexOf(args, "--uninstall") >= 0)
            {
                Uninstall();
                MessageBox.Show("Bluetooth Battery Bar se ha desinstalado.", "Bluetooth Battery Bar", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (Array.IndexOf(args, "--install") >= 0) { Install(); return; }   // instalación silenciosa
            if (!demo && !IsInstalledCopy)
            {
                // Se ha abierto el .exe desde otra carpeta (descargas, un USB...): ofrecer instalarlo.
                MessageBoxResult r = MessageBox.Show(
                    "¿Instalar Bluetooth Battery Bar en este equipo?\n\n" +
                    "Se copiará a tu carpeta de usuario y arrancará automáticamente con Windows.\n" +
                    "Si eliges «No», se abrirá solo esta vez sin instalar nada.",
                    "Bluetooth Battery Bar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes)
                {
                    try { Install(); }
                    catch (Exception e) { MessageBox.Show("No se pudo instalar:\n" + e.Message, "Bluetooth Battery Bar", MessageBoxButton.OK, MessageBoxImage.Error); }
                    return;
                }
            }
            bool created;
            using (Mutex mutex = new Mutex(true, "BluetoothBatteryBar.SingleInstance" + (demo ? ".Demo" : ""), out created))
            {
                if (!created) return;
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e) { e.Handled = true; };
                ui = Dispatcher.CurrentDispatcher;

                Bar.Sync(bars);
                int tick = 0;
                DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                t.Tick += delegate
                {
                    if (++tick % 7 == 0) Bar.Sync(bars);   // pantallas añadidas/quitadas o Explorer reiniciado
                    foreach (Bar b in bars.Values) b.Reposition();
                };
                t.Start();
                poll = new Timer(delegate { Refresh(); }, null, 0, 5000);
                app.Run();
            }
        }
    }
}

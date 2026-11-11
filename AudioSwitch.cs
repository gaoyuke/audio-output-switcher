// =============================================================================
//  AudioSwitch - 一键在两个声音输出设备之间切换（Windows / .NET Framework 4.x）
//  零第三方依赖：Core Audio COM 接口 + WinForms 托盘 + RegisterHotKey 全局热键
//
//  命令行：
//    AudioSwitch.exe                托盘常驻（首次运行会弹出设置）
//    AudioSwitch.exe /toggle       切换一次后退出
//    AudioSwitch.exe /a | /b       切换到设备 A / B 后退出
//    AudioSwitch.exe /settings     只打开设置窗口
//    AudioSwitch.exe /list         列出活动播放设备
//    AudioSwitch.exe /listout 路径 把设备列表写入文件
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace AudioSwitch
{
    // ------------------------------------------------------------------ 数据结构
    public class Device
    {
        public string Id;
        public string Name;
        public Device() { }
        public Device(string id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }

    public class Config
    {
        public string AId = "";
        public string AName = "";
        public string BId = "";
        public string BName = "";
        public int HotkeyMods = 3;      // MOD_CONTROL | MOD_ALT
        public int HotkeyVk = 79;       // 'O'  -> 默认 Ctrl+Alt+O
        public bool SwitchComm = true;
        public bool ShowBalloon = true;
        public bool AutoStart = false;
        public bool Configured = false;
    }

    public static class Util
    {
        public const int MOD_ALT = 1;
        public const int MOD_CONTROL = 2;
        public const int MOD_SHIFT = 4;
        public const int MOD_WIN = 8;
        public const int MOD_NOREPEAT = 0x4000;

        public static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        public static string HotkeyText(int mods, int vk)
        {
            if (vk == 0 || mods == 0) return "（未设置）";
            StringBuilder sb = new StringBuilder();
            if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl + ");
            if ((mods & MOD_ALT) != 0) sb.Append("Alt + ");
            if ((mods & MOD_SHIFT) != 0) sb.Append("Shift + ");
            if ((mods & MOD_WIN) != 0) sb.Append("Win + ");
            string key;
            try { key = new KeysConverter().ConvertToString((Keys)vk); }
            catch { key = "Key(" + vk + ")"; }
            if (string.IsNullOrEmpty(key)) key = "Key(" + vk + ")";
            sb.Append(key);
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ 配置存储
    public static class ConfigStore
    {
        private static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AudioSwitch");
            }
        }

        public static string ConfigPath { get { return Path.Combine(Dir, "config.xml"); } }

        public static Config Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    XmlSerializer xs = new XmlSerializer(typeof(Config));
                    using (FileStream fs = File.OpenRead(ConfigPath))
                    {
                        Config c = xs.Deserialize(fs) as Config;
                        if (c != null) return c;
                    }
                }
            }
            catch { }
            return new Config();
        }

        public static void Save(Config c)
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                XmlSerializer xs = new XmlSerializer(typeof(Config));
                using (FileStream fs = File.Create(ConfigPath)) xs.Serialize(fs, c);
            }
            catch (Exception ex)
            {
                MessageBox.Show("配置保存失败：\r\n" + ex.Message + "\r\n\r\n路径：" + ConfigPath,
                    "声音输出切换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    public static class AutoStartHelper
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AudioSwitch";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Win32 / COM
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);
        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pvar);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    // 未公开但自 Vista 起一直稳定的接口，Windows 声音设置、EarTrumpet、
    // SoundSwitch、nircmd 均使用它设置默认设备。
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClientClass { }

    public static class Audio
    {
        private const int eRender = 0;
        private const int DEVICE_STATE_ACTIVE = 1;
        private static readonly Guid PKEY_Device =
            new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        public static List<Device> GetDevices()
        {
            List<Device> list = new List<Device>();
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDeviceCollection col;
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out col));
            int count;
            Marshal.ThrowExceptionForHR(col.GetCount(out count));
            for (int i = 0; i < count; i++)
            {
                IMMDevice dev;
                if (col.Item(i, out dev) != 0 || dev == null) continue;
                try
                {
                    string id;
                    dev.GetId(out id);
                    string name = GetFriendlyName(dev);
                    if (string.IsNullOrEmpty(name)) name = "(未命名设备)";
                    list.Add(new Device(id, name));
                }
                finally { try { Marshal.ReleaseComObject(dev); } catch { } }
            }
            try { Marshal.ReleaseComObject(col); } catch { }
            try { Marshal.ReleaseComObject(en); } catch { }
            return list;
        }

        private static string GetFriendlyName(IMMDevice dev)
        {
            IPropertyStore ps;
            if (dev.OpenPropertyStore(0, out ps) != 0 || ps == null) return null;
            try
            {
                string s = ReadProperty(ps, 14);   // PKEY_Device_FriendlyName
                if (string.IsNullOrEmpty(s)) s = ReadProperty(ps, 2);  // PKEY_Device_DeviceDesc
                return s;
            }
            finally { try { Marshal.ReleaseComObject(ps); } catch { } }
        }

        private static string ReadProperty(IPropertyStore ps, int pid)
        {
            PROPERTYKEY key = new PROPERTYKEY();
            key.fmtid = PKEY_Device;
            key.pid = pid;
            PROPVARIANT pv;
            int hr = ps.GetValue(ref key, out pv);
            if (hr != 0) return null;
            string result = null;
            if (pv.vt == 31 && pv.p != IntPtr.Zero)   // VT_LPWSTR
                result = Marshal.PtrToStringUni(pv.p);
            Native.PropVariantClear(ref pv);
            return result;
        }

        public static string GetDefaultId()
        {
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice dev;
            int hr = en.GetDefaultAudioEndpoint(eRender, 0 /* eConsole */, out dev);
            if (hr != 0 || dev == null)
            {
                try { Marshal.ReleaseComObject(en); } catch { }
                return null;
            }
            string id;
            dev.GetId(out id);
            try { Marshal.ReleaseComObject(dev); } catch { }
            try { Marshal.ReleaseComObject(en); } catch { }
            return id;
        }

        public static void SetDefault(string id, bool includeCommunications)
        {
            IPolicyConfig pc = (IPolicyConfig)new PolicyConfigClientClass();
            try
            {
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 0));  // eConsole
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 1));  // eMultimedia
                if (includeCommunications)
                    Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, 2)); // eCommunications
            }
            finally { try { Marshal.ReleaseComObject(pc); } catch { } }
        }
    }

    // ------------------------------------------------------------------ 切换逻辑
    public static class Switcher
    {
        public const int ModeToggle = 0;
        public const int ModeA = 1;
        public const int ModeB = 2;

        public static bool Apply(Config cfg, int mode, out string deviceName, out string message)
        {
            deviceName = null;
            message = "";
            List<Device> devs;
            try { devs = Audio.GetDevices(); }
            catch (Exception ex) { message = "无法枚举音频设备：" + ex.Message; return false; }
            if (devs.Count == 0) { message = "没有找到可用的播放设备。"; return false; }

            string a = Resolve(cfg.AId, cfg.AName, devs);
            string b = Resolve(cfg.BId, cfg.BName, devs);
            if (a == null || b == null || Util.Eq(a, b))
            {
                message = "请先在设置里选择两个不同的输出设备（或设备当前未连接）。";
                return false;
            }

            string cur = null;
            try { cur = Audio.GetDefaultId(); } catch { }
            string target;
            if (mode == ModeA) target = a;
            else if (mode == ModeB) target = b;
            else target = Util.Eq(cur, a) ? b : a;

            if (!IsActive(target, devs))
            {
                string other = Util.Eq(target, a) ? b : a;
                if (mode == ModeToggle && IsActive(other, devs)) target = other;
                else
                {
                    message = "目标设备当前不可用（可能未连接）：" + NameOf(target, cfg, devs);
                    return false;
                }
            }

            try { Audio.SetDefault(target, cfg.SwitchComm); }
            catch (Exception ex) { message = "设置默认设备失败：" + ex.Message; return false; }

            // 若设备 ID 变化过（重新插拔 / 重装驱动），顺便把新 ID 写回配置
            string an = NameOf(a, cfg, devs);
            string bn = NameOf(b, cfg, devs);
            deviceName = NameOf(target, cfg, devs);
            cfg.AId = a; cfg.AName = an;
            cfg.BId = b; cfg.BName = bn;
            message = "已切换到：" + deviceName;
            return true;
        }

        private static bool IsActive(string id, List<Device> devs)
        {
            foreach (Device d in devs) if (Util.Eq(d.Id, id)) return true;
            return false;
        }

        private static string Resolve(string id, string name, List<Device> devs)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (Device d in devs) if (Util.Eq(d.Id, id)) return d.Id;
            if (!string.IsNullOrEmpty(name))
            {
                foreach (Device d in devs) if (Util.Eq(d.Name, name)) return d.Id;
                foreach (Device d in devs)
                    if (d.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return d.Id;
            }
            return null;
        }

        public static string NameOf(string id, Config cfg, List<Device> devs)
        {
            foreach (Device d in devs) if (Util.Eq(d.Id, id)) return d.Name;
            if (Util.Eq(id, cfg.AId)) return cfg.AName;
            if (Util.Eq(id, cfg.BId)) return cfg.BName;
            return Util.Trunc(id, 24);
        }
    }

    // ------------------------------------------------------------------ 托盘图标
    public static class Icons
    {
        public static Icon Make(Color color, bool headset)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush brush = new SolidBrush(color))
                using (Pen pen = new Pen(color, 2.6f))
                {
                    if (headset)
                    {
                        g.DrawArc(pen, 5, 3, 22, 25, 180, 180);
                        g.FillRectangle(brush, 4, 16, 7, 12);
                        g.FillRectangle(brush, 21, 16, 7, 12);
                    }
                    else
                    {
                        GraphicsPath path = new GraphicsPath();
                        path.AddPolygon(new PointF[] {
                            new PointF(3, 13), new PointF(9, 13), new PointF(16, 6),
                            new PointF(16, 26), new PointF(9, 19), new PointF(3, 19) });
                        g.FillPath(brush, path);
                        g.DrawArc(pen, 17, 11, 7, 10, -70, 140);
                        g.DrawArc(pen, 19, 7, 12, 18, -65, 130);
                    }
                }
            }
            IntPtr h = bmp.GetHicon();
            Icon tmp = Icon.FromHandle(h);
            Icon result = (Icon)tmp.Clone();
            tmp.Dispose();
            Native.DestroyIcon(h);
            bmp.Dispose();
            return result;
        }
    }

    // ------------------------------------------------------------------ 全局热键窗口
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        public event EventHandler Pressed;

        public HotkeyWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "AudioSwitch_HotkeySink";
            cp.Parent = new IntPtr(-3);   // HWND_MESSAGE
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1) // WM_HOTKEY
            {
                EventHandler h = Pressed;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // ------------------------------------------------------------------ 设置窗口
    internal sealed class ComboItem
    {
        public string Id;
        public string Name;
        public bool Missing;
        public ComboItem(string id, string name, bool missing) { Id = id; Name = name; Missing = missing; }
        public override string ToString() { return Name + (Missing ? "（当前未连接）" : ""); }
    }

    public sealed class SettingsForm : Form
    {
        private Config cfg;
        private ComboBox cmbA, cmbB;
        private TextBox txtHotkey;
        private Label lblHint, lblInfo;
        private CheckBox chkComm, chkBalloon, chkAuto;
        private int pendingMods, pendingVk;

        public Func<bool> ApplyHotkey;   // 返回 false 表示热键注册失败（被占用）

        public SettingsForm(Config config)
        {
            cfg = config;
            BuildUi();
            pendingMods = cfg.HotkeyMods;
            pendingVk = cfg.HotkeyVk;
            txtHotkey.Text = Util.HotkeyText(pendingMods, pendingVk);
            ReloadDevices();
        }

        private void BuildUi()
        {
            Text = "声音输出切换 · 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(540, 470);
            Font = new Font("Microsoft YaHei UI", 9f);

            int left = 18, w = 504;

            Label l1 = new Label();
            l1.Text = "设备 A（例如：内置扬声器）";
            l1.SetBounds(left, 16, w, 20);
            Controls.Add(l1);

            cmbA = new ComboBox();
            cmbA.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbA.SetBounds(left, 38, w, 26);
            Controls.Add(cmbA);

            Label l2 = new Label();
            l2.Text = "设备 B（例如：耳机）";
            l2.SetBounds(left, 68, w, 20);
            Controls.Add(l2);

            cmbB = new ComboBox();
            cmbB.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbB.SetBounds(left, 90, w, 26);
            Controls.Add(cmbB);

            Button btnRefresh = new Button();
            btnRefresh.Text = "刷新设备列表";
            btnRefresh.SetBounds(left, 122, 110, 27);
            btnRefresh.Click += delegate { ReloadDevices(); };
            Controls.Add(btnRefresh);

            Button btnSwap = new Button();
            btnSwap.Text = "交换 A / B";
            btnSwap.SetBounds(left + 118, 122, 100, 27);
            btnSwap.Click += delegate
            {
                int ia = cmbA.SelectedIndex, ib = cmbB.SelectedIndex;
                cmbA.SelectedIndex = ib;
                cmbB.SelectedIndex = ia;
            };
            Controls.Add(btnSwap);

            GroupBox grp = new GroupBox();
            grp.Text = "全局快捷键";
            grp.SetBounds(left, 160, w, 96);
            Controls.Add(grp);

            txtHotkey = new TextBox();
            txtHotkey.SetBounds(14, 26, 250, 26);
            txtHotkey.ReadOnly = true;
            txtHotkey.TextAlign = HorizontalAlignment.Center;
            txtHotkey.BackColor = Color.White;
            txtHotkey.PreviewKeyDown += delegate(object s, PreviewKeyDownEventArgs e) { e.IsInputKey = true; };
            txtHotkey.KeyDown += txtHotkey_KeyDown;
            grp.Controls.Add(txtHotkey);

            Button btnClear = new Button();
            btnClear.Text = "清除";
            btnClear.SetBounds(272, 25, 70, 27);
            btnClear.Click += delegate
            {
                pendingMods = 0; pendingVk = 0;
                txtHotkey.Text = Util.HotkeyText(0, 0);
                lblHint.Text = "快捷键已清除，点击「保存」生效。";
            };
            grp.Controls.Add(btnClear);

            lblHint = new Label();
            lblHint.Text = "点上面的框后直接按组合键，例如 Ctrl+Alt+O；需含至少一个修饰键。";
            lblHint.SetBounds(14, 58, 470, 28);
            lblHint.ForeColor = Color.Gray;
            grp.Controls.Add(lblHint);

            chkComm = new CheckBox();
            chkComm.Text = "同时切换通信设备（让微信 / 会议软件也跟着切换，推荐）";
            chkComm.SetBounds(left, 266, w, 22);
            chkComm.Checked = cfg.SwitchComm;
            Controls.Add(chkComm);

            chkBalloon = new CheckBox();
            chkBalloon.Text = "切换成功后显示气泡提示";
            chkBalloon.SetBounds(left, 292, w, 22);
            chkBalloon.Checked = cfg.ShowBalloon;
            Controls.Add(chkBalloon);

            chkAuto = new CheckBox();
            chkAuto.Text = "开机自动启动（托盘常驻）";
            chkAuto.SetBounds(left, 318, w, 22);
            chkAuto.Checked = AutoStartHelper.IsEnabled();
            Controls.Add(chkAuto);

            Button btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.SetBounds(left, 352, 90, 30);
            btnSave.Click += btnSave_Click;
            Controls.Add(btnSave);

            Button btnTest = new Button();
            btnTest.Text = "立即测试切换";
            btnTest.SetBounds(left + 98, 352, 110, 30);
            btnTest.Click += btnTest_Click;
            Controls.Add(btnTest);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(left + 216, 352, 90, 30);
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            lblInfo = new Label();
            lblInfo.Text =
                "命令行：/toggle 切换 · /a 切到设备A · /b 切到设备B · /list 列出设备 · /check 自检\r\n" +
                "托盘图标：双击切换，右键可单独切到某个设备。配置：" + ConfigStore.ConfigPath;
            lblInfo.SetBounds(left, 394, w, 60);
            lblInfo.ForeColor = Color.Gray;
            Controls.Add(lblInfo);

            AcceptButton = btnSave;
            CancelButton = btnCancel;
        }

        private void txtHotkey_KeyDown(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            Keys k = e.KeyCode;
            if (k == Keys.Back || k == Keys.Delete)
            {
                pendingMods = 0; pendingVk = 0;
                txtHotkey.Text = Util.HotkeyText(0, 0);
                lblHint.Text = "快捷键已清除，点击「保存」生效。";
                return;
            }
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu ||
                k == Keys.LWin || k == Keys.RWin || k == Keys.Capital) return;

            int mods = 0;
            if (e.Control) mods |= Util.MOD_CONTROL;
            if (e.Alt) mods |= Util.MOD_ALT;
            if (e.Shift) mods |= Util.MOD_SHIFT;
            if ((Native.GetKeyState(0x5B) & 0x8000) != 0 || (Native.GetKeyState(0x5C) & 0x8000) != 0)
                mods |= Util.MOD_WIN;

            if (mods == 0)
            {
                lblHint.Text = "需要至少一个修饰键（Ctrl / Alt / Shift / Win），请重新按。";
                return;
            }
            pendingMods = mods;
            pendingVk = (int)k;
            txtHotkey.Text = Util.HotkeyText(mods, pendingVk);
            lblHint.Text = "已录入，点击「保存」后生效。";
        }

        private void ReloadDevices()
        {
            List<Device> devs;
            try { devs = Audio.GetDevices(); }
            catch (Exception ex)
            {
                MessageBox.Show("枚举播放设备失败：\r\n" + ex.Message, "声音输出切换",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                devs = new List<Device>();
            }

            string oldA = SelectedId(cmbA, cfg.AId);
            string oldB = SelectedId(cmbB, cfg.BId);
            cmbA.Items.Clear();
            cmbB.Items.Clear();
            foreach (Device d in devs)
            {
                cmbA.Items.Add(new ComboItem(d.Id, d.Name, false));
                cmbB.Items.Add(new ComboItem(d.Id, d.Name, false));
            }
            AddMissing(cmbA, cfg.AId, cfg.AName, devs);
            AddMissing(cmbB, cfg.BId, cfg.BName, devs);
            SelectId(cmbA, oldA != null ? oldA : cfg.AId);
            SelectId(cmbB, oldB != null ? oldB : cfg.BId);
            if (cmbA.Items.Count > 0 && cmbA.SelectedIndex < 0) cmbA.SelectedIndex = 0;
            if (cmbB.Items.Count > 1 && cmbB.SelectedIndex < 0) cmbB.SelectedIndex = 1;
            else if (cmbB.Items.Count > 0 && cmbB.SelectedIndex < 0) cmbB.SelectedIndex = 0;
        }

        private static string SelectedId(ComboBox cmb, string fallback)
        {
            ComboItem it = cmb.SelectedItem as ComboItem;
            return it != null ? it.Id : fallback;
        }

        private static void AddMissing(ComboBox cmb, string id, string name, List<Device> devs)
        {
            if (string.IsNullOrEmpty(id)) return;
            foreach (Device d in devs) if (Util.Eq(d.Id, id)) return;
            cmb.Items.Add(new ComboItem(id, string.IsNullOrEmpty(name) ? "(未知设备)" : name, true));
        }

        private static void SelectId(ComboBox cmb, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                ComboItem it = cmb.Items[i] as ComboItem;
                if (it != null && Util.Eq(it.Id, id)) { cmb.SelectedIndex = i; return; }
            }
        }

        private Config BuildConfig()
        {
            Config c = new Config();
            ComboItem ia = cmbA.SelectedItem as ComboItem;
            ComboItem ib = cmbB.SelectedItem as ComboItem;
            c.AId = ia != null ? ia.Id : "";
            c.AName = ia != null ? ia.Name : "";
            c.BId = ib != null ? ib.Id : "";
            c.BName = ib != null ? ib.Name : "";
            c.HotkeyMods = pendingMods;
            c.HotkeyVk = pendingVk;
            c.SwitchComm = chkComm.Checked;
            c.ShowBalloon = chkBalloon.Checked;
            c.AutoStart = chkAuto.Checked;
            c.Configured = true;
            return c;
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            ComboItem ia = cmbA.SelectedItem as ComboItem;
            ComboItem ib = cmbB.SelectedItem as ComboItem;
            if (ia == null || ib == null || Util.Eq(ia.Id, ib.Id))
            {
                MessageBox.Show("请为设备 A 和设备 B 选择两个不同的输出设备。", "声音输出切换",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Config c = BuildConfig();
            cfg.AId = c.AId; cfg.AName = c.AName;
            cfg.BId = c.BId; cfg.BName = c.BName;
            cfg.HotkeyMods = c.HotkeyMods; cfg.HotkeyVk = c.HotkeyVk;
            cfg.SwitchComm = c.SwitchComm; cfg.ShowBalloon = c.ShowBalloon;
            cfg.AutoStart = c.AutoStart; cfg.Configured = true;

            ConfigStore.Save(cfg);
            AutoStartHelper.Set(cfg.AutoStart);

            bool ok = true;
            if (ApplyHotkey != null)
            {
                try { ok = ApplyHotkey(); } catch { ok = false; }
            }
            if (!ok)
            {
                MessageBox.Show(
                    "快捷键 " + Util.HotkeyText(cfg.HotkeyMods, cfg.HotkeyVk) +
                    " 注册失败，可能已被其它程序占用。\r\n请换一个组合键后再保存。",
                    "声音输出切换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Close();
        }

        private void btnTest_Click(object sender, EventArgs e)
        {
            Config c = BuildConfig();
            string name, msg;
            bool ok = Switcher.Apply(c, Switcher.ModeToggle, out name, out msg);
            MessageBox.Show(msg, ok ? "切换成功" : "切换失败",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------ 托盘应用
    internal sealed class TrayApp : ApplicationContext
    {
        private Config cfg;
        private NotifyIcon tray;
        private HotkeyWindow hw;
        private Icon icoA, icoB, icoUnknown;
        private ToolStripMenuItem miCurrent, miToggle, miA, miB, miAuto;
        private SettingsForm settings;

        public TrayApp(Config config)
        {
            cfg = config;
            icoA = Icons.Make(Color.FromArgb(92, 170, 250), false);
            icoB = Icons.Make(Color.FromArgb(105, 215, 140), true);
            icoUnknown = Icons.Make(Color.FromArgb(165, 172, 185), false);

            hw = new HotkeyWindow();
            hw.Pressed += delegate { DoSwitch(Switcher.ModeToggle); };

            ContextMenuStrip menu = new ContextMenuStrip();
            miCurrent = new ToolStripMenuItem("当前设备：未知");
            miCurrent.Enabled = false;
            miToggle = new ToolStripMenuItem("立即切换");
            miToggle.Click += delegate { DoSwitch(Switcher.ModeToggle); };
            miA = new ToolStripMenuItem("切换到设备 A");
            miA.Click += delegate { DoSwitch(Switcher.ModeA); };
            miB = new ToolStripMenuItem("切换到设备 B");
            miB.Click += delegate { DoSwitch(Switcher.ModeB); };
            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置…");
            miSettings.Click += delegate { OpenSettings(); };
            miAuto = new ToolStripMenuItem("开机自动启动");
            miAuto.CheckOnClick = true;
            miAuto.Click += delegate
            {
                AutoStartHelper.Set(miAuto.Checked);
                cfg.AutoStart = miAuto.Checked;
                ConfigStore.Save(cfg);
            };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.AddRange(new ToolStripItem[] {
                miCurrent, new ToolStripSeparator(), miToggle, miA, miB,
                new ToolStripSeparator(), miSettings, miAuto,
                new ToolStripSeparator(), miExit });

            tray = new NotifyIcon();
            tray.Icon = icoUnknown;
            tray.Text = "声音输出切换";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { DoSwitch(Switcher.ModeToggle); };

            if (!RegisterHotkey())
                tray.ShowBalloonTip(4000, "快捷键注册失败",
                    "组合键可能被其它程序占用，请右键托盘图标 → 设置，换一个快捷键。",
                    ToolTipIcon.Warning);

            if (!cfg.Configured)
            {
                GuessDevices();
                tray.ShowBalloonTip(4000, "声音输出切换已启动",
                    "已自动识别两个输出设备，打开设置可修改设备与快捷键。", ToolTipIcon.Info);
                OpenSettings();
            }
            RefreshTray();
        }

        public bool RegisterHotkey()
        {
            Native.UnregisterHotKey(hw.Handle, 1);
            if (cfg.HotkeyVk == 0 || cfg.HotkeyMods == 0) return true;
            return Native.RegisterHotKey(hw.Handle, 1,
                (uint)(cfg.HotkeyMods | Util.MOD_NOREPEAT), (uint)cfg.HotkeyVk);
        }

        private void GuessDevices()
        {
            try
            {
                List<Device> devs = Audio.GetDevices();
                if (devs.Count < 2) return;
                string a = Find(devs, new string[] { "扬声器", "喇叭", "Speakers", "Speaker" });
                string b = Find(devs, new string[] { "耳机", "耳麦", "Headphones", "Headphone", "Headset" });
                if (a == null) a = devs[0].Id;
                if (b == null || Util.Eq(a, b))
                    foreach (Device d in devs) if (!Util.Eq(d.Id, a)) { b = d.Id; break; }
                if (a == null || b == null || Util.Eq(a, b)) return;
                cfg.AId = a; cfg.BId = b;
                cfg.AName = NameFromList(a, devs);
                cfg.BName = NameFromList(b, devs);
                ConfigStore.Save(cfg);
            }
            catch { }
        }

        private static string Find(List<Device> devs, string[] keywords)
        {
            foreach (string kw in keywords)
                foreach (Device d in devs)
                    if (d.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return d.Id;
            return null;
        }

        private static string NameFromList(string id, List<Device> devs)
        {
            foreach (Device d in devs) if (Util.Eq(d.Id, id)) return d.Name;
            return "(未知设备)";
        }

        private void DoSwitch(int mode)
        {
            string name, msg;
            bool ok = Switcher.Apply(cfg, mode, out name, out msg);
            if (ok)
            {
                ConfigStore.Save(cfg);
                RefreshTray();
                if (cfg.ShowBalloon)
                    tray.ShowBalloonTip(1600, "声音输出切换", msg, ToolTipIcon.Info);
            }
            else
            {
                tray.ShowBalloonTip(3500, "切换失败", msg, ToolTipIcon.Warning);
                if (msg.IndexOf("请先在设置") >= 0) OpenSettings();
            }
        }

        private void RefreshTray()
        {
            try
            {
                List<Device> devs = Audio.GetDevices();
                string cur = Audio.GetDefaultId();
                string name = "(未知)";
                foreach (Device d in devs) if (Util.Eq(d.Id, cur)) { name = d.Name; break; }
                miCurrent.Text = "当前：" + Util.Trunc(name, 34);
                string hk = Util.HotkeyText(cfg.HotkeyMods, cfg.HotkeyVk);
                miToggle.Text = "立即切换" + (hk == "（未设置）" ? "" : "   " + hk);
                miA.Text = "切换到 " + Util.Trunc(string.IsNullOrEmpty(cfg.AName) ? "设备 A" : cfg.AName, 30);
                miB.Text = "切换到 " + Util.Trunc(string.IsNullOrEmpty(cfg.BName) ? "设备 B" : cfg.BName, 30);
                miAuto.Checked = AutoStartHelper.IsEnabled();
                if (Util.Eq(cur, cfg.AId)) tray.Icon = icoA;
                else if (Util.Eq(cur, cfg.BId)) tray.Icon = icoB;
                else tray.Icon = icoUnknown;
                tray.Text = Util.Trunc("声音输出：" + name, 60);
            }
            catch { }
        }

        private void OpenSettings()
        {
            if (settings != null && !settings.IsDisposed) { settings.Activate(); return; }
            settings = new SettingsForm(cfg);
            settings.ApplyHotkey = delegate { return RegisterHotkey(); };
            settings.FormClosed += delegate { RefreshTray(); };
            settings.Show();
        }

        private void ExitApp()
        {
            Native.UnregisterHotKey(hw.Handle, 1);
            hw.Dispose();
            tray.Visible = false;
            tray.Dispose();
            icoA.Dispose(); icoB.Dispose(); icoUnknown.Dispose();
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ 入口
    internal static class Program
    {
        private static bool consoleAttached;

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            Config cfg = ConfigStore.Load();

            if (arg == "/list" || arg == "/listout")
            {
                HandleList(args, arg);
                return;
            }
            if (arg == "/toggle" || arg == "/a" || arg == "/b")
            {
                InitConsole();
                int mode = arg == "/a" ? Switcher.ModeA : arg == "/b" ? Switcher.ModeB : Switcher.ModeToggle;
                string name, msg;
                bool ok = Switcher.Apply(cfg, mode, out name, out msg);
                if (ok) ConfigStore.Save(cfg);
                if (consoleAttached) Console.WriteLine(msg);
                else if (!ok)
                {
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "AudioSwitch.log"),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n",
                            Encoding.UTF8);
                    }
                    catch { }
                }
                if (!ok) Environment.ExitCode = 1;
                return;
            }
            if (arg == "/settings")
            {
                SettingsForm f = new SettingsForm(cfg);
                Application.Run(f);
                return;
            }
            if (arg == "/check")
            {
                HandleCheck(cfg);
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\AudioSwitch_SingleInstance_v1", out created))
            {
                if (!created)
                {
                    MessageBox.Show("声音输出切换已经在运行了，请在任务栏托盘区找到它。\r\n" +
                        "（右键托盘图标可打开设置或退出）", "声音输出切换",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new TrayApp(cfg));
                GC.KeepAlive(mutex);
            }
        }

        private static void InitConsole()
        {
            try { consoleAttached = Native.AttachConsole(-1); } catch { consoleAttached = false; }
        }

        private static void HandleCheck(Config cfg)
        {
            InitConsole();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 声音输出切换 · 自检 ===");
            try
            {
                string cur = Audio.GetDefaultId();
                List<Device> devs = Audio.GetDevices();
                sb.AppendLine("活动播放设备：" + devs.Count + " 个");
                foreach (Device d in devs)
                    sb.AppendLine((Util.Eq(d.Id, cur) ? " * " : "   ") + d.Name);
            }
            catch (Exception ex) { sb.AppendLine("设备枚举失败：" + ex.Message); }

            sb.AppendLine("设备 A：" + (string.IsNullOrEmpty(cfg.AName) ? "(未配置)" : cfg.AName));
            sb.AppendLine("设备 B：" + (string.IsNullOrEmpty(cfg.BName) ? "(未配置)" : cfg.BName));
            sb.AppendLine("快捷键：" + Util.HotkeyText(cfg.HotkeyMods, cfg.HotkeyVk));

            HotkeyWindow hw = new HotkeyWindow();
            bool fired = false;
            try
            {
                bool registered = Native.RegisterHotKey(hw.Handle, 1,
                    (uint)(cfg.HotkeyMods | Util.MOD_NOREPEAT), (uint)cfg.HotkeyVk);
                hw.Pressed += delegate { fired = true; };
                sb.AppendLine("全局热键注册：" + (registered
                    ? "成功（组合键未被占用）"
                    : "失败（可能被占用），Win32 错误 " + Marshal.GetLastWin32Error()));
                if (registered)
                {
                    Native.PostMessage(hw.Handle, 0x0312, new IntPtr(1), IntPtr.Zero);
                    DateTime deadline = DateTime.Now.AddSeconds(2);
                    while (!fired && DateTime.Now < deadline)
                    {
                        Application.DoEvents();
                        Thread.Sleep(20);
                    }
                    sb.AppendLine("热键消息处理：" + (fired ? "正常" : "异常（未收到 WM_HOTKEY）"));
                }
            }
            catch (Exception ex) { sb.AppendLine("热键自检异常：" + ex.Message); }
            finally
            {
                Native.UnregisterHotKey(hw.Handle, 1);
                hw.Dispose();
            }
            sb.AppendLine("配置文件：" + ConfigStore.ConfigPath);

            if (consoleAttached) Console.Write(sb.ToString());
            else MessageBox.Show(sb.ToString(), "声音输出切换 · 自检结果",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void HandleList(string[] args, string arg)
        {
            InitConsole();
            StringBuilder sb = new StringBuilder();
            string cur = null;
            try { cur = Audio.GetDefaultId(); } catch { }
            sb.AppendLine("当前活动的播放设备（* = 系统默认）：");
            try
            {
                foreach (Device d in Audio.GetDevices())
                    sb.AppendLine((Util.Eq(d.Id, cur) ? " * " : "   ") + d.Name + "  |  " + d.Id);
            }
            catch (Exception ex) { sb.AppendLine("枚举失败：" + ex.Message); }

            if (arg == "/listout" && args.Length > 1)
            {
                try { File.WriteAllText(args[1], sb.ToString(), Encoding.UTF8); }
                catch (Exception ex) { if (consoleAttached) Console.WriteLine("写文件失败：" + ex.Message); }
            }
            else if (consoleAttached) Console.Write(sb.ToString());
            else MessageBox.Show(sb.ToString(), "播放设备列表", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.Http;
using System.Linq;

namespace CmdTranslator
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TranslatorForm());
        }
    }

    public class TranslatorForm : Form
    {
        const int STD_OUTPUT_HANDLE = -11;
        const int STD_INPUT_HANDLE = -10;

        [StructLayout(LayoutKind.Sequential)] struct COORD { public short X; public short Y; }
        [StructLayout(LayoutKind.Sequential)] struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct CONSOLE_SCREEN_BUFFER_INFO { public COORD dwSize; public COORD dwCursorPosition; public short wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize; }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint dwProcessId);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", CharSet = CharSet.Unicode)]
        static extern bool ReadConsoleOutputCharacter(IntPtr h, [Out] char[] b, uint n, COORD c, out uint r);
        [DllImport("kernel32.dll", EntryPoint = "GetConsoleTitleW", CharSet = CharSet.Unicode)]
        static extern uint GetConsoleTitle(StringBuilder b, uint n);
        [DllImport("kernel32.dll")] static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO i);

        [StructLayout(LayoutKind.Explicit)] public struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent; }
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)] public struct KEY_EVENT_RECORD { [FieldOffset(0)] public int bKeyDown; [FieldOffset(4)] public ushort wRepeatCount; [FieldOffset(6)] public ushort wVirtualKeyCode; [FieldOffset(8)] public ushort wVirtualScanCode; [FieldOffset(10)] public char UnicodeChar; [FieldOffset(12)] public uint dwControlKeyState; }
        [DllImport("kernel32.dll")] static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] b, uint n, out uint w);

        private ComboBox cbProcs; private TextBox txtOutput, txtInput; private System.Windows.Forms.Timer timer;
        private IntPtr consoleHandle; private string lastText = "";
        private Dictionary<string, string> cache = new Dictionary<string, string>();
        private static readonly HttpClient http = new HttpClient();

        public TranslatorForm()
        {
            this.Text = "CLI ¹Ì·¯¸µ ¹ø¿ª±â (°¡½Ã ¿µ¿ª ¿Ïº® µ¿±âÈ­)"; this.Size = new Size(1000, 750);
            SetupUI(); LoadProcs();
            timer = new System.Windows.Forms.Timer { Interval = 1000 }; timer.Tick += Update;
        }

        private void SetupUI()
        {
            Panel top = new Panel { Dock = DockStyle.Top, Height = 40 };
            cbProcs = new ComboBox { Width = 400, Left = 10, Top = 10 };
            Button btnConn = new Button { Text = "¿¬°á ½ÃÀÛ", Left = 420, Top = 9 }; btnConn.Click += Connect;

            // ¡Ú ÀÚµ¿ ÁÙ¹Ù²Þ Ã¼Å©¹Ú½º Ãß°¡
            CheckBox chkWordWrap = new CheckBox { Text = "ÀÚµ¿ ÁÙ¹Ù²Þ", Left = 510, Top = 13, Width = 100, ForeColor = Color.Black };
            chkWordWrap.CheckedChanged += (s, e) =>
            {
                txtOutput.WordWrap = chkWordWrap.Checked;
                txtOutput.ScrollBars = chkWordWrap.Checked ? ScrollBars.Vertical : ScrollBars.Both;
            };

            top.Controls.Add(cbProcs); top.Controls.Add(btnConn); top.Controls.Add(chkWordWrap); this.Controls.Add(top);

            // ¡Ú WordWrap = false ¼³Á¤ ¹× °¡·Î/¼¼·Î ½ºÅ©·Ñ¹Ù(ScrollBars.Both) ±âº» È°¼ºÈ­
            txtOutput = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = new Font("GulimChe", 10f), BackColor = Color.Black, ForeColor = Color.White, WordWrap = false, ScrollBars = ScrollBars.Both };
            this.Controls.Add(txtOutput);

            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            txtInput = new TextBox { Width = 700, Left = 10, Top = 10 };
            Button btnSend = new Button { Text = "Àü¼Û", Left = 720, Top = 9 }; btnSend.Click += Send;
            bottom.Controls.Add(txtInput); bottom.Controls.Add(btnSend); this.Controls.Add(bottom);
        }

        private void LoadProcs()
        {
            cbProcs.Items.Clear();
            foreach (var p in Process.GetProcessesByName("cmd"))
            {
                string title = "¸í·É ÇÁ·ÒÇÁÆ®"; FreeConsole();
                if (AttachConsole((uint)p.Id)) { StringBuilder b = new StringBuilder(256); GetConsoleTitle(b, 256); title = b.ToString(); FreeConsole(); }
                cbProcs.Items.Add(new { Text = $"PID:{p.Id} - {title}", Value = (uint)p.Id });
            }
            cbProcs.DisplayMember = "Text";
            if (cbProcs.Items.Count > 0) cbProcs.SelectedIndex = 0;
        }

        private void Connect(object s, EventArgs e)
        {
            if (cbProcs.SelectedItem == null) return;
            FreeConsole();
            if (AttachConsole((uint)((dynamic)cbProcs.SelectedItem).Value)) { consoleHandle = GetStdHandle(STD_OUTPUT_HANDLE); timer.Start(); }
        }

        private async void Send(object s, EventArgs e)
        {
            string cmd = txtInput.Text;
            if (string.IsNullOrWhiteSpace(cmd)) return;
            if (Regex.IsMatch(cmd, @"[°¡-ÆR]")) cmd = await Translate(cmd, "ko", "en");

            IntPtr hIn = GetStdHandle(STD_INPUT_HANDLE);
            List<INPUT_RECORD> recs = new List<INPUT_RECORD>();
            foreach (char c in cmd + "\r") { recs.Add(CreateKey(c, 1)); recs.Add(CreateKey(c, 0)); }
            WriteConsoleInput(hIn, recs.ToArray(), (uint)recs.Count, out _);
            txtInput.Clear();
        }

        private INPUT_RECORD CreateKey(char c, int down) => new INPUT_RECORD { EventType = 1, KeyEvent = new KEY_EVENT_RECORD { bKeyDown = down, wRepeatCount = 1, UnicodeChar = c } };

        private async void Update(object s, EventArgs e)
        {
            string cur = ReadConsoleGrid(consoleHandle);
            if (cur == lastText) return;
            lastText = cur;

            string[] lines = cur.Split('\n');
            StringBuilder outSb = new StringBuilder();

            // ¡Ú ÆÐµù(¿©¹é) ¹®Á¦ ¿Ïº® ÇØ°á ¡Ú
            // ±ÛÀÚ°¡ ÃµÀå¿¡ µü ºÙ¾î¼­ Àß·Á º¸ÀÌÁö ¾Êµµ·Ï, ¸Ç À§¿¡ ºó ÁÙÀ» ÇÏ³ª Ãß°¡ÇØ¼­ ÅØ½ºÆ®¸¦ ¾Æ·¡·Î ³»¸³´Ï´Ù.
            // ´õ ³»¸®°í ½ÍÀ¸½Ã¸é outSb.AppendLine(); À» ÇÑ ¹ø ´õ ½áÁÖ½Ã¸é µË´Ï´Ù!
            outSb.AppendLine();
            outSb.AppendLine();
            outSb.AppendLine();
            outSb.AppendLine();

            foreach (var line in lines)
            {
                // ¾ç¿·À¸·Îµµ ³Ê¹« µü ºÙÁö ¾Ê°Ô ¾Õ¿¡ ¶ç¾î¾²±â(°ø¹é)¸¦ »ìÂ¦ Ãß°¡ÇØ¼­ µé¿©¾²±â ÇØÁÝ´Ï´Ù.
                string processed = await ProcessLine(line);
                outSb.AppendLine("  " + processed);
            }

            txtOutput.Text = outSb.ToString();
        }
        private async Task<string> ProcessLine(string line)
        {
            string cleanLine = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(cleanLine)) return cleanLine;
            if (Regex.IsMatch(cleanLine, @"^[a-zA-Z]:\\")) return cleanLine;

            if (!Regex.IsMatch(cleanLine, @"[a-zA-Z]")) return cleanLine;

            if (cache.ContainsKey(cleanLine)) return cache[cleanLine];

            string trans;
            if (cleanLine.Contains("|") || cleanLine.Contains("¦¢")) trans = await TranslateTable(cleanLine);
            else trans = await Translate(cleanLine, "en", "ko");

            cache[cleanLine] = trans;
            return trans;
        }

        private string ReadConsoleGrid(IntPtr handle)
        {
            if (!GetConsoleScreenBufferInfo(handle, out var csbi)) return "";
            StringBuilder sb = new StringBuilder();

            // =========================================================
            // ¡å ¿©±â¼­ºÎÅÍ ¼öÄ¡¸¦ Á÷Á¢ Á¶Á¤ÇÏ½Ã¸é µË´Ï´Ù! ¡å

            // 1. À§·Î ¸î ÁÙ±îÁö ³Ë³ËÇÏ°Ô ÀÐ¾î¿ÃÁö ¼³Á¤ÇÕ´Ï´Ù. (±âº»°ª 100)
            // À­ºÎºÐÀÌ Àß¸°´Ù¸é ÀÌ ¼ýÀÚ¸¦ 150, 200, 300 µîÀ¸·Î È® ´Ã·ÁÁÖ¼¼¿ä!
            int readLines = 500;

            // 2. È­¸éÀÇ ¸Ç ¾Æ·§ºÎºÐ(Bottom)°ú Ä¿¼­ À§Ä¡ Áß ´õ ¾Æ·¡ÂÊÀ» ±âÁØÁ¡(³¡Á¡)À¸·Î ¾ÈÀüÇÏ°Ô Àâ½À´Ï´Ù.
            short endY = Math.Max(csbi.srWindow.Bottom, csbi.dwCursorPosition.Y);

            // 3. ³¡Á¡À¸·ÎºÎÅÍ readLines ¸¸Å­ À§·Î ÂÞ¿í ²ø¾î¿Ã·Á¼­ ½ÃÀÛÁ¡(startY)À» Àâ½À´Ï´Ù.
            short startY = (short)Math.Max(0, endY - readLines);

            // ¡ã ¿©±â±îÁö ¡ã
            // =========================================================

            for (short y = startY; y <= endY; y++)
            {
                char[] buf = new char[csbi.dwSize.X];
                ReadConsoleOutputCharacter(handle, buf, (uint)csbi.dwSize.X, new COORD { X = 0, Y = y }, out uint charsRead);

                string l = new string(buf, 0, (int)charsRead).TrimEnd();
                sb.AppendLine(l); // ºó ÁÙµµ ¿øº» ·¹ÀÌ¾Æ¿ô À¯Áö¸¦ À§ÇØ Æ÷ÇÔ
            }

            return sb.ToString().TrimEnd(); // ¸¶Áö¸· ºÒÇÊ¿äÇÑ ÁÙ¹Ù²Þ¸¸ Á¦°Å
        }

        private async Task<string> TranslateTable(string row)
        {
            char sep = row.Contains("¦¢") ? '¦¢' : '|';
            string[] cells = row.Split(sep);

            for (int i = 0; i < cells.Length; i++)
            {
                string cellText = cells[i];
                string trimmed = cellText.Trim();

                if (trimmed.Length > 0 && Regex.IsMatch(trimmed, @"[a-zA-Z]"))
                {
                    int originalDisplayWidth = GetDisplayWidth(cellText);
                    string translated = await Translate(trimmed, "en", "ko");
                    int translatedDisplayWidth = GetDisplayWidth(translated);

                    int leftSpaces = cellText.Length - cellText.TrimStart().Length;
                    int rightSpaces = originalDisplayWidth - translatedDisplayWidth - leftSpaces;

                    if (leftSpaces < 0) leftSpaces = 1;
                    if (rightSpaces < 0) rightSpaces = 1;

                    cells[i] = new string(' ', leftSpaces) + translated + new string(' ', rightSpaces);
                }
            }
            return string.Join(sep.ToString(), cells);
        }

        private int GetDisplayWidth(string s)
        {
            return s.Sum(c => (c >= 0x1100 && c <= 0x115F || c >= 0x2E80 && c <= 0xA4CF || c >= 0xAC00 && c <= 0xD7A3 || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFF60) ? 2 : 1);
        }

        private async Task<string> Translate(string text, string sl, string tl)
        {
            try
            {
                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";
                string res = await http.GetStringAsync(url);
                string tr = Regex.Match(res, "\"(.*?)\"").Groups[1].Value;
                return tr.Replace("\\n", "\r\n").Replace("\\\"", "\"").Replace("\\u003c", "<").Replace("\\u003e", ">");
            }
            catch { return text; }
        }
    }
}
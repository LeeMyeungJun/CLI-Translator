using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", CharSet = CharSet.Unicode)] static extern bool WriteConsoleInput(IntPtr h, INPUT_RECORD[] b, uint n, out uint w);

        private ComboBox cbProcs; private TextBox txtOutput, txtInput; private System.Windows.Forms.Timer timer;
        private IntPtr consoleHandle; private string lastText = "";
        private Dictionary<string, string> cache = new Dictionary<string, string>();
        private static readonly HttpClient http = new HttpClient();
        private static readonly SemaphoreSlim translateThrottle = new SemaphoreSlim(2); // ponytail: 동시 요청 수를 제한해 구글 봇 차단(302) 유발 가능성을 낮춤

        public TranslatorForm()
        {
            this.Text = "CLI 미러링 번역기 (가시 영역 완벽 동기화)"; this.Size = new Size(1000, 750);
            SetupUI(); LoadProcs();
            timer = new System.Windows.Forms.Timer { Interval = 3000 }; timer.Tick += Update; // ponytail: 2.5초->3초, 요청 빈도를 더 낮춰 구글 봇 차단 유발을 줄임
        }

        private void SetupUI()
        {
            Panel top = new Panel { Dock = DockStyle.Top, Height = 40 };
            cbProcs = new ComboBox { Width = 400, Left = 10, Top = 10 };
            Button btnConn = new Button { Text = "연결 시작", Left = 420, Top = 9 }; btnConn.Click += Connect;

            // ★ 자동 줄바꿈 체크박스 추가
            CheckBox chkWordWrap = new CheckBox { Text = "자동 줄바꿈", Left = 510, Top = 13, Width = 100, ForeColor = Color.Black };
            chkWordWrap.CheckedChanged += (s, e) =>
            {
                txtOutput.WordWrap = chkWordWrap.Checked;
                txtOutput.ScrollBars = chkWordWrap.Checked ? ScrollBars.Vertical : ScrollBars.Both;
            };

            top.Controls.Add(cbProcs); top.Controls.Add(btnConn); top.Controls.Add(chkWordWrap); this.Controls.Add(top);

            // ★ WordWrap = false 설정 및 가로/세로 스크롤바(ScrollBars.Both) 기본 활성화
            txtOutput = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Font = new Font("GulimChe", 10f), BackColor = Color.Black, ForeColor = Color.White, WordWrap = false, ScrollBars = ScrollBars.Both };
            this.Controls.Add(txtOutput);

            Panel bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            txtInput = new TextBox { Width = 700, Left = 10, Top = 10 };
            Button btnSend = new Button { Text = "전송", Left = 720, Top = 9 }; btnSend.Click += Send;
            bottom.Controls.Add(txtInput); bottom.Controls.Add(btnSend); this.Controls.Add(bottom);
            this.AcceptButton = btnSend;
        }

        private void LoadProcs()
        {
            cbProcs.Items.Clear();
            foreach (var p in Process.GetProcessesByName("cmd"))
            {
                string title = "명령 프롬프트"; FreeConsole();
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
            if (Regex.IsMatch(cmd, @"[가-힣]")) cmd = await Translate(cmd, "ko", "en");

            IntPtr hIn = GetStdHandle(STD_INPUT_HANDLE);
            List<INPUT_RECORD> recs = new List<INPUT_RECORD>();
            foreach (char c in cmd + "\r") { recs.Add(CreateKey(c, 1)); recs.Add(CreateKey(c, 0)); }
            WriteConsoleInput(hIn, recs.ToArray(), (uint)recs.Count, out _);
            txtInput.Clear();
        }

        private INPUT_RECORD CreateKey(char c, int down) => new INPUT_RECORD { EventType = 1, KeyEvent = new KEY_EVENT_RECORD { bKeyDown = down, wRepeatCount = 1, UnicodeChar = c } };

        private bool isUpdating = false;
        private DateTime pauseUntil = DateTime.MinValue; // ponytail: 차단 의심 시 잠깐 쉬는 용도, 고정 10초. 더 정교한 백오프 필요하면 그때 추가

        private async void Update(object s, EventArgs e)
        {
            if (isUpdating) return; // ponytail: 이전 갱신이 아직 번역 중이면 겹쳐 돌리지 않음
            if (DateTime.UtcNow < pauseUntil) return; // ponytail: 최근 번역 실패(차단 의심) 시 요청을 잠깐 멈춰 차단 악화를 막음
            isUpdating = true;
            try
            {
                string cur = ReadConsoleGrid(consoleHandle);
                if (cur == lastText) return;
                lastText = cur;

                string[] lines = cur.Split('\n');
                // 줄마다 순차 대기하던 것을 병렬 실행으로 변경 (N줄 * 왕복시간 -> 1회 왕복시간)
                string[] processed = await Task.WhenAll(lines.Select(ProcessLine));

                StringBuilder outSb = new StringBuilder();

                // ★ 패딩(여백) 문제 완벽 해결 ★
                // 글자가 천장에 딱 붙어서 잘려 보이지 않도록, 맨 위에 빈 줄을 하나 추가해서 텍스트를 아래로 내립니다.
                // 더 내리고 싶으시면 outSb.AppendLine(); 을 한 번 더 써주시면 됩니다!
                outSb.AppendLine();
                outSb.AppendLine();
                outSb.AppendLine();
                outSb.AppendLine();

                foreach (var p in processed)
                    outSb.AppendLine("  " + p); // 양옆으로도 너무 딱 붙지 않게 들여쓰기

                txtOutput.Text = outSb.ToString();
            }
            finally { isUpdating = false; }
        }
        private async Task<string> ProcessLine(string line)
        {
            string cleanLine = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(cleanLine)) return cleanLine;
            if (Regex.IsMatch(cleanLine, @"^[a-zA-Z]:\\")) return cleanLine;

            if (!Regex.IsMatch(cleanLine, @"[a-zA-Z]")) return cleanLine;

            if (cache.ContainsKey(cleanLine)) return cache[cleanLine];

            string trans;
            if (cleanLine.Contains("|") || cleanLine.Contains("│")) trans = await TranslateTable(cleanLine);
            else trans = await Translate(cleanLine, "en", "ko");

            cache[cleanLine] = trans;
            return trans;
        }

        private string ReadConsoleGrid(IntPtr handle)
        {
            if (!GetConsoleScreenBufferInfo(handle, out var csbi)) return "";
            StringBuilder sb = new StringBuilder();

            // =========================================================
            // ▼ 여기서부터 수치를 직접 조정하시면 됩니다! ▼

            // 1. 위로 몇 줄까지 넉넉하게 읽어올지 설정합니다. (기본값 100)
            // 윗부분이 잘린다면 이 숫자를 150, 200, 300 등으로 확 늘려주세요!
            int readLines = 500;

            // 2. 화면의 맨 아랫부분(Bottom)과 커서 위치 중 더 아래쪽을 기준점(끝점)으로 안전하게 잡습니다.
            short endY = Math.Max(csbi.srWindow.Bottom, csbi.dwCursorPosition.Y);

            // 3. 끝점으로부터 readLines 만큼 위로 쭈욱 끌어올려서 시작점(startY)을 잡습니다.
            short startY = (short)Math.Max(0, endY - readLines);

            // ▲ 여기까지 ▲
            // =========================================================

            for (short y = startY; y <= endY; y++)
            {
                char[] buf = new char[csbi.dwSize.X];
                ReadConsoleOutputCharacter(handle, buf, (uint)csbi.dwSize.X, new COORD { X = 0, Y = y }, out uint charsRead);

                string l = new string(buf, 0, (int)charsRead).TrimEnd();
                sb.AppendLine(l); // 빈 줄도 원본 레이아웃 유지를 위해 포함
            }

            return sb.ToString().TrimEnd(); // 마지막 불필요한 줄바꿈만 제거
        }

        private async Task<string> TranslateTable(string row)
        {
            char sep = row.Contains("│") ? '│' : '|';
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
            await translateThrottle.WaitAsync();
            try
            {
                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";
                string res = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(res);
                var sb = new StringBuilder();
                foreach (var segment in doc.RootElement[0].EnumerateArray())
                    sb.Append(segment[0].GetString());
                return sb.ToString();
            }
            catch { pauseUntil = DateTime.UtcNow.AddSeconds(10); return text; }
            finally { translateThrottle.Release(); }
        }
    }
}
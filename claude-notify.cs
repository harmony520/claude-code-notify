// claude-notify -- native toast popup for Claude Code hooks (Windows).
//
// A lightweight, dependency-free notification popup that appears when Claude
// Code needs your approval or finishes a task. Clicking it brings the terminal
// window running Claude Code back to the foreground.
//
// Build:
//   csc /nologo /optimize+ /target:winexe /codepage:65001
//       /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
//       /r:System.Management.dll
//       /out:claude-notify.exe claude-notify.cs
//
// Usage:
//   claude-notify.exe --scenario confirm   (amber, waiting for approval)
//   claude-notify.exe --scenario done      (green, task finished)
//
// Optional overrides:
//   --title "..."      custom title text
//   --message "..."    custom body text
//   --sound <name>     Reminder | Default | <absolute path to .wav>
//
// The Claude session PID is read from the CLAUDE_PID environment variable
// (inherited by the hook process) to help route the focus click to the right
// window when several Claude sessions are open.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClaudeNotify {

static class Native {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder sb,int n);
    [DllImport("user32.dll")] public static extern int  GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h,bool alt);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int  DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int val,int sz);
}

static class Sounds {
    // Resolve a bundled Windows sound by filename under %SystemRoot%\Media so we
    // never hardcode a "C:\" — Windows can live on any drive letter.
    public static string Media(string file) {
        string root = Environment.GetEnvironmentVariable("SystemRoot");
        if(string.IsNullOrEmpty(root)) root = @"C:\Windows";
        return Path.Combine(root, "Media", file);
    }
}

static class Program {
    [STAThread]
    static void Main(string[] args) {
        string scenario="done", title=null, message=null, sound=null;
        uint sessionPid=0;
        bool bg=false;
        for(int i=0;i<args.Length;i++) switch(args[i]) {
            case "--bg":          bg=true; break;
            case "--scenario":    if(i+1<args.Length) scenario=args[++i]; break;
            case "--title":       if(i+1<args.Length) title   =args[++i]; break;
            case "--message":     if(i+1<args.Length) message =args[++i]; break;
            case "--sound":       if(i+1<args.Length) sound   =args[++i]; break;
            case "--session-pid": if(i+1<args.Length) uint.TryParse(args[++i],out sessionPid); break;
        }
        // Claude Code does NOT run hooks through cmd.exe, so a `%CLAUDE_PID%`
        // token on the command line arrives unexpanded and TryParse leaves
        // sessionPid at 0. The hook process is a child of the Claude session,
        // so it inherits CLAUDE_PID in its environment -- read it from there.
        if(sessionPid==0) {
            try{ uint.TryParse(Environment.GetEnvironmentVariable("CLAUDE_PID"),out sessionPid); }catch{}
        }
        // The Notification/Stop hook expects a fast return; the click handler must
        // stay alive to catch the click. Re-launch ourselves detached with --bg so
        // the caller returns instantly while the detached copy owns the popup.
        if(!bg) {
            var sb=new StringBuilder("--bg");
            for(int i=0;i<args.Length;i++){sb.Append(" \"");sb.Append(args[i].Replace("\"","\\\""));sb.Append('"');}
            var psi=new ProcessStartInfo(Application.ExecutablePath,sb.ToString());
            psi.UseShellExecute=false;
            // Redirect the child's standard streams. With UseShellExecute=false the
            // child otherwise INHERITS our stdout/stderr handles, and since it lives
            // on for the popup's lifetime it keeps the hook's output pipe open long
            // after we exit -- Claude Code reads that pipe until EOF, so every hook
            // fire stalled for seconds. Redirecting hands the child fresh pipes we
            // never read, so our own handles close the moment we return.
            psi.RedirectStandardOutput=true;
            psi.RedirectStandardError=true;
            psi.RedirectStandardInput=true;
            psi.CreateNoWindow=true;
            try{Process.Start(psi);}catch{}
            return;
        }
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ToastForm(scenario,title,message,sound,sessionPid));
    }
}

class ToastForm : Form {
    const string Needle = "Claude Code";
    SoundPlayer player;
    Timer fadeIn, fadeOut, life;
    Color accent;
    uint sessionPid;

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get {
        var cp=base.CreateParams;
        // TOOLWINDOW keeps it out of the taskbar; TOPMOST keeps it above other
        // windows. We deliberately do NOT set WS_EX_NOACTIVATE (0x08000000):
        // that flag stops the window ever activating, which swallows all clicks
        // on the buttons/labels.
        cp.ExStyle |= 0x00000080|0x00000008; // TOOLWINDOW|TOPMOST
        return cp;
    }}

    public ToastForm(string scenario, string title, string message, string sound, uint sessPid) {
        sessionPid = sessPid;
        bool confirm = string.Equals(scenario,"confirm",StringComparison.OrdinalIgnoreCase);

        // --- scenario config (Chinese defaults, written as \uXXXX so the source
        //     compiles identically regardless of the compiler's active codepage) ---
        string wav, btnText;
        int lifeMs;
        if(confirm) {
            accent  = Color.FromArgb(245,158,11);   // amber
            wav     = Sounds.Media("Alarm05.wav");
            // \u7b49\u5f85\u786e\u8ba4 = 等待确认
            // \u9700\u8981\u4f60\u6388\u6743\u540e\u624d\u80fd\u7ee7\u7eed = 需要你授权后才能继续
            // \u53bb\u786e\u8ba4 = 去确认
            if(title==null)   title   = "\u7b49\u5f85\u786e\u8ba4";
            if(message==null) message = "\u9700\u8981\u4f60\u6388\u6743\u540e\u624d\u80fd\u7ee7\u7eed";
            btnText = "\u53bb\u786e\u8ba4";
            lifeMs  = 20000;
        } else {
            accent  = Color.FromArgb(52,199,89);    // green
            wav     = Sounds.Media("Ring01.wav");
            // \u4efb\u52a1\u5b8c\u6210 = 任务完成
            // Claude \u5df2\u5b8c\u6210\u5f53\u524d\u4efb\u52a1 = Claude 已完成当前任务
            // \u56de\u5230\u7ec8\u7aef = 回到终端
            if(title==null)   title   = "\u4efb\u52a1\u5b8c\u6210";
            if(message==null) message = "Claude \u5df2\u5b8c\u6210\u5f53\u524d\u4efb\u52a1";
            btnText = "\u56de\u5230\u7ec8\u7aef";
            lifeMs  = 12000;
        }
        if(string.Equals(sound,"Reminder",StringComparison.OrdinalIgnoreCase)) wav=Sounds.Media("Alarm05.wav");
        else if(string.Equals(sound,"Default",StringComparison.OrdinalIgnoreCase)) wav=Sounds.Media("Ring01.wav");
        else if(sound!=null && File.Exists(sound)) wav=sound;

        // --- DPI scale ---
        float s; using(var g=CreateGraphics()) s=g.DpiX/96f;
        int W=Sc(420,s), H=Sc(148,s);

        // --- form ---
        Text=""; FormBorderStyle=FormBorderStyle.None;
        StartPosition=FormStartPosition.Manual; TopMost=true;
        ShowInTaskbar=false; BackColor=Color.FromArgb(28,28,32);
        Opacity=0; Cursor=Cursors.Hand; Size=new Size(W,H);
        var wa=Screen.PrimaryScreen.WorkingArea;
        Location=new Point(wa.X+(wa.Width-W)/2, wa.Y+(wa.Height-H)/2);

        // --- left accent bar ---
        var bar=new Panel{BackColor=accent,Location=new Point(0,0),Size=new Size(Sc(4,s),H)};
        Controls.Add(bar);

        // --- icon circle ---
        var icon=new Panel{Location=new Point(Sc(20,s),Sc(26,s)),Size=new Size(Sc(46,s),Sc(46,s)),BackColor=BackColor};
        Color ac=accent; string gl=confirm?"!":"\u2713";
        icon.Paint+=(o,e)=>{
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var b=new SolidBrush(Color.FromArgb(40,ac))) e.Graphics.FillEllipse(b,0,0,icon.Width-1,icon.Height-1);
            using(var p2=new Pen(ac,Sc(2,s))) e.Graphics.DrawEllipse(p2,1,1,icon.Width-3,icon.Height-3);
            using(var f=new Font("Segoe UI",confirm?14f:13f,FontStyle.Bold))
            using(var wb=new SolidBrush(ac)){
                var sf=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center};
                e.Graphics.DrawString(gl,f,wb,new RectangleF(0,confirm?-1f:0f,icon.Width,icon.Height),sf);
            }
        };
        Controls.Add(icon);

        // --- title ---
        var lblT=new Label{Text=title,Font=new Font("Segoe UI",11f,FontStyle.Bold),
            ForeColor=Color.White,BackColor=BackColor,AutoSize=false,
            Location=new Point(Sc(80,s),Sc(22,s)),Size=new Size(W-Sc(80+36,s),Sc(24,s))};
        Controls.Add(lblT);

        // --- message ---
        var lblM=new Label{Text=message,Font=new Font("Segoe UI",9.5f),
            ForeColor=Color.FromArgb(180,184,192),BackColor=BackColor,AutoSize=false,
            Location=new Point(Sc(80,s),Sc(50,s)),Size=new Size(W-Sc(80+20,s),Sc(40,s))};
        Controls.Add(lblM);

        // --- close x ---
        var x=new Label{Text="\u00d7",Font=new Font("Segoe UI",11f),
            ForeColor=Color.FromArgb(90,94,102),BackColor=BackColor,
            TextAlign=ContentAlignment.MiddleCenter,Cursor=Cursors.Hand,
            Location=new Point(W-Sc(30,s),Sc(6,s)),Size=new Size(Sc(22,s),Sc(22,s))};
        x.MouseEnter+=(o,e)=>x.ForeColor=Color.White;
        x.MouseLeave+=(o,e)=>x.ForeColor=Color.FromArgb(90,94,102);
        x.Click+=(o,e)=>FadeOut();
        Controls.Add(x);

        // --- action button ---
        var btn=new Button{Text=btnText,Font=new Font("Segoe UI",9.5f,FontStyle.Bold),
            FlatStyle=FlatStyle.Flat,BackColor=accent,ForeColor=Color.FromArgb(20,20,20),
            Cursor=Cursors.Hand,Size=new Size(Sc(108,s),Sc(30,s))};
        btn.FlatAppearance.BorderSize=0;
        btn.Location=new Point(W-btn.Width-Sc(16,s),H-btn.Height-Sc(14,s));
        Color hov=Blend(accent,Color.White,0.18f);
        btn.MouseEnter+=(o,e)=>btn.BackColor=hov;
        btn.MouseLeave+=(o,e)=>btn.BackColor=ac;
        Controls.Add(btn);

        // --- separator line above button ---
        var sep=new Panel{BackColor=Color.FromArgb(48,50,56),
            Location=new Point(Sc(4,s),H-btn.Height-Sc(22,s)),
            Size=new Size(W-Sc(4,s),1)};
        Controls.Add(sep);

        // --- click wiring ---
        EventHandler go=(o,e)=>{FocusClaude(sessionPid);FadeOut();};
        btn.Click+=go; Click+=go; icon.Click+=go; lblT.Click+=go; lblM.Click+=go;

        // --- timers ---
        fadeIn=new Timer{Interval=12};
        fadeIn.Tick+=(o,e)=>{Opacity=Math.Min(1.0,Opacity+0.16);if(Opacity>=1)fadeIn.Stop();};
        fadeOut=new Timer{Interval=12};
        fadeOut.Tick+=(o,e)=>{Opacity-=0.14;if(Opacity<=0.04){fadeOut.Stop();Close();}};
        life=new Timer{Interval=lifeMs};
        life.Tick+=(o,e)=>{life.Stop();FadeOut();};

        // --- sound (async, never blocks popup) ---
        try{if(File.Exists(wav)){player=new SoundPlayer(wav);player.Play();}}catch{}
    }

    static int Sc(int v,float s){return(int)Math.Round(v*s);}
    static Color Blend(Color a,Color b,float t){
        return Color.FromArgb((int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t));
    }
    void FadeOut(){life.Stop();fadeIn.Stop();if(!fadeOut.Enabled)fadeOut.Start();}

    protected override void OnHandleCreated(EventArgs e){
        base.OnHandleCreated(e);
        int r=2; Native.DwmSetWindowAttribute(Handle,33,ref r,4);          // rounded corners
        int c=(accent.B<<16)|(accent.G<<8)|accent.R;
        Native.DwmSetWindowAttribute(Handle,34,ref c,4);                   // accent border
    }
    protected override void OnShown(EventArgs e){
        base.OnShown(e);
        Native.ShowWindow(Handle,5);   // SW_SHOW -- override SW_HIDE from a hidden parent
        fadeIn.Start(); life.Start();
    }

    static void FocusClaude(uint sessionPid){
        IntPtr h=FindClaudeWindow(sessionPid);
        if(h==IntPtr.Zero)return;
        if(Native.IsIconic(h))Native.ShowWindow(h,9);
        Native.SwitchToThisWindow(h,true);
    }
    // Find the terminal window running Claude Code. When sessionPid (CLAUDE_PID)
    // is known we first try process-tree matching: pick the window whose owning
    // process is an ancestor/descendant of the session, so with several Claude
    // windows open the click routes to the *right* one. This works when each
    // session runs in its own terminal process (conhost, VS Code terminal, or WT
    // in per-window-process mode). It can't disambiguate under stock Windows
    // Terminal -- its ConPTY model shares one WindowsTerminal.exe across all
    // tabs/windows and the middle OpenConsole host exits, breaking the chain -- so
    // we fall back to a plain title match, which is exact for the single-window case.
    static IntPtr FindClaudeWindow(uint sessionPid){
        IntPtr found=IntPtr.Zero;
        uint myPid=(uint)Process.GetCurrentProcess().Id;

        // Build process tree: PID -> parent PID. ONE WMI query for the whole
        // snapshot -- doing a query per process (Process.GetProcesses() can be
        // hundreds) freezes the UI thread for seconds on click.
        var tree = new System.Collections.Generic.Dictionary<uint,uint>();
        if(sessionPid!=0) {
            try {
                using(var moc = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId,ParentProcessId FROM Win32_Process")) {
                    foreach(System.Management.ManagementObject mo in moc.Get()) {
                        try {
                            uint cpid = (uint)mo["ProcessId"];
                            uint ppid = (uint)mo["ParentProcessId"];
                            tree[cpid] = ppid;
                        } catch {}
                    }
                }
            } catch {}
        }

        // Helper: does the process tree of targetPid contain sessionPid?
        Func<uint,bool> TreeContains = (targetPid) => {
            if(sessionPid==0) return false;
            var seen = new System.Collections.Generic.HashSet<uint>();
            uint cur = targetPid;
            for(int i=0; i<50 && cur!=0; i++) {
                if(cur==sessionPid) return true;
                if(seen.Contains(cur)) break;
                seen.Add(cur);
                if(!tree.ContainsKey(cur)) break;
                cur = tree[cur];
            }
            return false;
        };

        // Pass 1: window whose process tree contains sessionPid AND title matches.
        if(sessionPid!=0) {
            Native.EnumWindows((h,lp)=>{
                if(!Native.IsWindowVisible(h))return true;
                uint pid; Native.GetWindowThreadProcessId(h,out pid);
                if(pid==myPid)return true;
                if(!TreeContains(pid))return true;
                int len=Native.GetWindowTextLength(h); if(len<=0)return true;
                var sb=new StringBuilder(len+1); Native.GetWindowText(h,sb,sb.Capacity);
                if(sb.ToString().IndexOf(Needle,StringComparison.OrdinalIgnoreCase)>=0){
                    found=h; return false;
                }
                return true;
            },IntPtr.Zero);
            if(found!=IntPtr.Zero) return found;
        }

        // Pass 2 (fallback): any window with a matching title.
        Native.EnumWindows((h,lp)=>{
            if(!Native.IsWindowVisible(h))return true;
            uint pid; Native.GetWindowThreadProcessId(h,out pid);
            if(pid==myPid)return true;
            int len=Native.GetWindowTextLength(h); if(len<=0)return true;
            var sb=new StringBuilder(len+1); Native.GetWindowText(h,sb,sb.Capacity);
            if(sb.ToString().IndexOf(Needle,StringComparison.OrdinalIgnoreCase)>=0){found=h;return false;}
            return true;
        },IntPtr.Zero);
        return found;
    }
}

} // namespace

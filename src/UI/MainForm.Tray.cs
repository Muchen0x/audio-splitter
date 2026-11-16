using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    partial class MainForm
    {
        // ==================== 托盘与单实例激活 ====================

        void InitTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (s, e) => RestoreFromTray());
            menu.Items.Add("退出", null, (s, e) => { exiting = true; Close(); });

            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon != null ? this.Icon : SystemIcons.Application;
            trayIcon.Text = "Audio Splitter - 就绪";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
        }

        void HideToTray()
        {
            ShowInTaskbar = false;
            Hide();
            if (!trayTipShown)
            {
                trayTipShown = true;
                try { trayIcon.ShowBalloonTip(3000, "Audio Splitter", "已最小化到托盘，双击图标恢复窗口", ToolTipIcon.Info); }
                catch { }
            }
        }

        void RestoreFromTray()
        {
            if (!ShowInTaskbar) ShowInTaskbar = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

            IntPtr h = Handle;
            ShowWindow(h, SW_RESTORE);
            Activate();
            if (GetForegroundWindow() == h) return;

            // 前台锁定兜底: 正常路径由第二个实例 AllowSetForegroundWindow(ASFW_ANY) 转授许可,
            // 但从托盘菜单/任务栏点进来时没有第二实例, 只能靠把自己的输入队列临时挂到当前前台
            // 线程上 —— 共享输入队列后本线程就"继承"了前台资格, SetForegroundWindow 才会生效。
            uint fgPid;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != myThread)
                    attached = AttachThreadInput(myThread, fgThread, true);
                BringWindowToTop(h);
                SetForegroundWindow(h);
                SetFocus(h);
            }
            catch { }
            finally
            {
                if (attached) { try { AttachThreadInput(myThread, fgThread, false); } catch { } }
            }

            if (GetForegroundWindow() != h)
            {
                // 最后兜底: 前台许可被彻底拒绝 (组策略/别的进程持续抢前台) 时短暂置顶再放开
                TopMost = true;
                TopMost = false;
                Activate();
            }
        }

        void RegisterActivateHandler()
        {
            try
            {
                activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
                // executeOnlyOnce=false: 每次第二个实例拉起都要能唤起主窗口 (true 只响应第一次)
                ThreadPool.RegisterWaitForSingleObject(activateEvent, delegate(object state, bool timedOut)
                {
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(new Action(delegate { RestoreFromTray(); }));
                    }
                    catch { }
                }, null, -1, false);
            }
            catch { }
        }
    }
}
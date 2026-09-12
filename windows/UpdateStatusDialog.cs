using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CodexUsage;

internal static class UpdateStatusDialog
{
    internal static void Show(Func<bool, JsonObject> read, Func<Task> local, Func<Task> quota)
    {
        var dialog = new Window { Title = "数据更新状态与重试", Width = 540, Height = 390, MinWidth = 380, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Theme.Background };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var retryLocal = new Button { Content = "重试本地更新", Margin = new Thickness(0, 0, 8, 0) };
        var retryQuota = new Button { Content = "重试账号额度", Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "关闭", IsCancel = true };
        buttons.Children.Add(retryLocal); buttons.Children.Add(retryQuota); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Theme.Background, BorderThickness = new Thickness(0), Foreground = Theme.Foreground };
        layout.Children.Add(text); dialog.Content = layout;
        bool pendingLocal = false, pendingQuota = false;
        void Render()
        {
            var state = read(false); var updates = state.O("updates"); var l = updates.O("local"); var q = updates.O("quota");
            string Describe(JsonObject source) => source.S("status") + "\n上次快照：" + J.Date(source.N("updatedAt"), "yyyy-MM-dd HH:mm:ss")
                + (source.S("error").Length > 0 ? "\n" + source.S("error") : "");
            string next = "本地日志\n" + Describe(l) + "\n数据目录：" + state.S("home") + "\n\n账号额度\n" + Describe(q)
                + "\n账号额度约每 60 秒读取，不受本地自动更新开关影响。\n若读取失败，请确认 Codex 已登录后重试。";
            if (text.Text != next) text.Text = next;
            retryLocal.IsEnabled = !pendingLocal && !l.B("busy"); retryQuota.IsEnabled = !pendingQuota && !q.B("busy") && q.B("enabled", true);
        }
        retryLocal.Click += async (_, _) => { pendingLocal = true; Render(); try { await local(); } finally { pendingLocal = false; Render(); } };
        retryQuota.Click += async (_, _) => { pendingQuota = true; Render(); try { await quota(); } finally { pendingQuota = false; Render(); } };
        close.Click += (_, _) => dialog.Close();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => Render();
        dialog.Closed += (_, _) => timer.Stop(); Render(); timer.Start(); dialog.ShowDialog();
    }
}

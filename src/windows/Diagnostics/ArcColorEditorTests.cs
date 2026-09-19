using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsage;

internal static class ArcColorEditorTests
{
    internal static async Task<JsonObject> RunAsync(string? frames = null)
    {
        var checks = new JsonArray();
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException("Arc editor: " + description); checks.Add(description); }
        var editor = new ArcColorEditor(new(), false, .68) { ShowActivated = false };
        CapsuleArcStyle? preview = null; int saves = 0, clears = 0;
        editor.Preview = value => { preview = value; if (value is null) clears++; };
        editor.Save = _ => { saves++; return Task.FromResult<string?>("synthetic write failure"); };
        try
        {
            editor.Show(); editor.UpdateLayout(); await Task.Delay(30);
            var nodes = Descendants(editor).ToArray();
            var fields = nodes.OfType<TextBox>().ToArray(); var mode = nodes.OfType<ComboBox>().Single();
            var wheel = nodes.OfType<ArcColorWheel>().Single();
            var apply = nodes.OfType<Button>().Single(x => x.Content as string == "应用");
            var reset = nodes.OfType<Button>().Single(x => x.Content as string == "重置默认颜色");
            fields[0].Text = "#broken"; await editor.ApplyDraft();
            Check(!apply.IsEnabled && saves == 0 && preview is null, "invalid HEX blocks apply without changing preview or persisted state");
            fields[0].Text = "0000FF";
            Check(preview?.LowHex == "#0000FF" && preview.HighHex is not null && apply.IsEnabled, "valid HEX materializes both gradient endpoints and previews immediately");
            var gradient = editor.Draft; mode.SelectedIndex = 0; fields[0].Text = "#AABBCC"; mode.SelectedIndex = 1;
            Check(editor.Draft.LowHex == gradient.LowHex && editor.Draft.SolidHex == "#AABBCC", "mode switching preserves independent solid and gradient drafts");
            fields[0].Text = "#123"; editor.UpdateTheme(true);
            Check(fields[0].Text == "#123" && !apply.IsEnabled, "theme refresh retains invalid text instead of silently replacing an active edit");
            fields[0].Text = "#123456";
            var peer = UIElementAutomationPeer.CreatePeerForElement(wheel)!; var range = (IRangeValueProvider)peer.GetPattern(PatternInterface.RangeValue)!;
            Check(peer.GetAutomationControlType() == AutomationControlType.Slider, "color wheel is discoverable as an accessible slider");
            range.SetValue(180); Check(Math.Abs(range.Value - 180) < .001 && editor.Draft != gradient, "UIA hue adjustment updates the same color draft and preview as pointer input");
            await editor.ApplyDraft();
            Check(saves == 1 && editor.IsVisible && preview is not null && nodes.OfType<TextBlock>().Any(x => x.Text == "synthetic write failure"), "save failure retains visible draft and feedback for retry");
            var pending = new TaskCompletionSource<string?>(); editor.Save = _ => { saves++; return pending.Task; };
            Task saving = editor.ApplyDraft(); editor.Close();
            Check(editor.IsVisible && !reset.IsEnabled && !apply.IsEnabled && range.IsReadOnly, "pending save freezes all editing and keeps the window alive until the write completes");
            pending.SetResult("synthetic retry failure"); await saving;
            Check(reset.IsEnabled && apply.IsEnabled && !range.IsReadOnly, "failed pending save restores edit controls for recovery");
            if (frames is not null)
            {
                Directory.CreateDirectory(frames);
                foreach (bool light in new[] { false, true })
                {
                    editor.UpdateTheme(light); editor.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(editor);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(frames, "arc-editor-" + (light ? "light" : "dark") + ".png")); encoder.Save(file);
                }
            }
            editor.Save = _ => { saves++; return Task.FromResult<string?>(null); }; await editor.ApplyDraft();
            Check(!editor.IsVisible && clears == 1 && preview is null && saves == 3, "successful retry closes once and clears transient preview after confirmed save");
        }
        finally { editor.Close(); }
        var cancelled = new ArcColorEditor(new(), false, .5); int cancelledSaves = 0; CapsuleArcStyle? cancelledPreview = null;
        cancelled.Preview = value => cancelledPreview = value; cancelled.Save = _ => { cancelledSaves++; return Task.FromResult<string?>(null); };
        try
        {
            cancelled.ShowActivated = false; cancelled.Show(); cancelled.UpdateLayout(); Descendants(cancelled).OfType<TextBox>().First().Text = "#FF0000";
            Check(cancelledPreview is not null, "cancel fixture first establishes a live color preview"); cancelled.Close();
            Check(cancelledPreview is null && cancelledSaves == 0, "closing without applying withdraws the preview without invoking storage");
        }
        finally { cancelled.Close(); }
        return J.Obj(("success", true), ("checks", checks));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject node)
            foreach (var descendant in Descendants(node)) yield return descendant;
    }
}

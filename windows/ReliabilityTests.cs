using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexUsage;

internal static class ReliabilityTests
{
    internal static void Run()
    {
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Reliability: " + name); }
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexUsage-reliability-" + Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test directory");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json"); byte[] damaged = [123, 34, 255, 35, 10]; File.WriteAllBytes(path, damaged);
            var settings = new Settings(path); Check(settings.Error.Length > 0, "damaged settings are locked");
            bool refused = false; try { settings.Update(J.Obj(("refresh", 30))); } catch (InvalidDataException) { refused = true; }
            Check(refused && File.ReadAllBytes(path).SequenceEqual(damaged), "ordinary save cannot overwrite damaged settings");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false; try { settings.Recover(); } catch (IOException) { failed = true; }
                Check(failed && settings.Error.Length > 0, "failed replacement retains recovery lock");
                Check(File.ReadAllBytes(path).SequenceEqual(damaged), "failed recovery preserves exact original");
            }
            string backup = settings.Recover();
            Check(File.ReadAllBytes(backup).SequenceEqual(damaged), "successful recovery preserves exact original bytes");
            Check(settings.Error.Length == 0 && J.Read(path).Count == 0, "recovered defaults are persisted");
            settings.Update(J.Obj(("refresh", 30))); Check(new Settings(path).Refresh == 30, "saving works after recovery");
            bool healthyRefused = false; try { settings.Recover(); } catch (InvalidOperationException) { healthyRefused = true; }
            Check(healthyRefused && new Settings(path).Refresh == 30, "healthy configuration is not reset");
            File.WriteAllBytes(path, damaged); var externallyRepaired = new Settings(path);
            J.Write(path, J.Obj(("refresh", 60), ("floating", J.Obj(("theme", "light")))));
            Check(externallyRepaired.Recover() == "" && externallyRepaired.Refresh == 60 && externallyRepaired.Floating.S("theme") == "light", "external repair is adopted without erasing valid preferences");
        }
        finally { Directory.Delete(directory, true); }
        Task.Run(async () =>
        {
            int local = 0, quota = 0;
            await RefreshOperations.Run(false, true, () => { local++; return Task.CompletedTask; }, () => { quota++; return Task.CompletedTask; });
            Check(local == 1 && quota == 0, "automatic local scan does not change quota schedule");
            await RefreshOperations.Run(true, false, () => { local++; return Task.CompletedTask; }, () => { quota++; return Task.CompletedTask; });
            Check(local == 2 && quota == 0, "explicit no-quota isolation is honored");
            bool failed = false;
            try { await RefreshOperations.Run(true, true, () => throw new IOException("local scan failed"), () => { quota++; return Task.CompletedTask; }); }
            catch (IOException) { failed = true; }
            Check(failed && quota == 1, "local synchronous failure cannot prevent manual quota retry");
            var localPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var quotaPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var both = RefreshOperations.Run(true, true, () => { local++; return localPending.Task; }, () => { quota++; return quotaPending.Task; });
            Check(local == 3 && quota == 2 && !both.IsCompleted, "manual refresh starts both sources concurrently");
            localPending.SetResult(); Check(!both.IsCompleted, "local completion is not reported as combined completion");
            quotaPending.SetException(new IOException("account unavailable"));
            failed = false; try { await both; } catch (IOException) { failed = true; }
            Check(failed, "account failure is not converted into success");
            await CheckDisconnectedClose(Check);
        }).GetAwaiter().GetResult();
    }
    private static async Task CheckDisconnectedClose(Action<bool, string> check)
    {
        string pipe = "CodexUsage-reliability-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool didClose = false;
        var server = Ipc.Listen(pipe, async request =>
        {
            accepted.SetResult(); await finishSave.Task.WaitAsync(stop.Token); return J.Obj(("closing", true));
        }, stop.Token, _ => didClose = true, request => { if (request.S("action") == "close") disconnected.SetResult(); });
        try
        {
            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(stop.Token);
                byte[] bytes = Encoding.UTF8.GetBytes("{\"action\":\"close\"}");
                await client.WriteAsync(BitConverter.GetBytes(bytes.Length), stop.Token);
                await client.WriteAsync(bytes, stop.Token); await client.FlushAsync(stop.Token);
                await accepted.Task.WaitAsync(stop.Token);
                // The caller disconnects while the window is still preserving its draft.
            }
            finishSave.SetResult(); await disconnected.Task.WaitAsync(stop.Token);
            check(!didClose, "failed close reply invokes rollback instead of completing closure");
        }
        finally { stop.Cancel(); await server; }
    }
}

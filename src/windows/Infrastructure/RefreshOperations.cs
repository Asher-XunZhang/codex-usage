using System;
using System.Threading.Tasks;

namespace CodexUsage;

internal static class RefreshOperations
{
    // Each source starts even if the other fails synchronously. Completion never means
    // the other source succeeded; the host publishes independent source states.
    internal static Task Run(bool manual, bool quotaEnabled, Func<Task> local, Func<Task> quota)
        => manual && quotaEnabled ? Task.WhenAll(Invoke(local), Invoke(quota)) : Invoke(local);
    private static async Task Invoke(Func<Task> operation) => await operation();
}

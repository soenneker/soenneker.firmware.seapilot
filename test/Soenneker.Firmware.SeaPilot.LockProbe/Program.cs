using System;
using System.Threading.Tasks;
using Soenneker.Locks.Files;

namespace Soenneker.Firmware.SeaPilot.LockProbe;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        using FileLockLease lease = await new FileLock(args[0]).Acquire();
        Console.WriteLine("LOCKED");
        await Console.In.ReadLineAsync();
    }
}

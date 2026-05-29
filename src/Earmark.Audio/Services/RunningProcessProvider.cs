using System.Diagnostics;
using System.Runtime.Versioning;

using Earmark.Audio.Interop;
using Earmark.Core.Audio;
using Earmark.Core.Models;

using Microsoft.Extensions.Logging;

namespace Earmark.Audio.Services;

/// <summary>
/// Polls the running-process set on a short interval and raises start/stop events by diffing
/// successive scans. Windows has no per-user, non-elevated push notification for arbitrary process
/// start/stop (the WMI trace classes need <c>System.Management</c> and are flaky without admin), so
/// a 2s diff is the pragmatic event source: fast enough to pin a freshly launched app before it
/// makes its first sound, cheap enough to leave running. Paths are resolved once per pid and cached.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunningProcessProvider : IRunningProcessProvider, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<RunningProcessProvider> _logger;
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private Dictionary<uint, RunningProcess> _known = new();
    private int _scanning;
    private bool _disposed;

    public RunningProcessProvider(ILogger<RunningProcessProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _timer = new Timer(_ => Scan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Prime off-thread: construction can run on the UI thread during DI, and enumerating the
        // whole process table plus resolving paths is too heavy to block it. The first scan diffs
        // against an empty set, so every running process counts as "started" - that's intentional:
        // it drives the consumer's first reconcile once the set is ready (the debounce there
        // collapses the burst into one pass). The poll timer is armed only after the prime, so
        // scans never overlap.
        _ = Task.Run(() =>
        {
            try
            {
                Scan();
                _logger.LogInformation("RunningProcessProvider primed: {Count} processes, poll = {Interval}",
                    _known.Count, PollInterval);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Initial process scan failed");
            }
            finally
            {
                _timer.Change(PollInterval, PollInterval);
            }
        });
    }

    public event EventHandler<RunningProcessEvent>? ProcessStarted;
    public event EventHandler<RunningProcessEvent>? ProcessStopped;

    public IReadOnlyList<RunningProcess> GetRunningProcesses()
    {
        lock (_gate)
        {
            return _known.Values.ToList();
        }
    }

    private void Scan()
    {
        // Overlap guard: a slow scan must not stack on the next timer tick.
        if (Interlocked.Exchange(ref _scanning, 1) == 1)
        {
            return;
        }

        try
        {
            Dictionary<uint, RunningProcess> previous;
            lock (_gate)
            {
                previous = _known;
            }

            Process[] procs;
            try
            {
                procs = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Process enumeration failed; keeping previous set");
                return;
            }

            var current = new Dictionary<uint, RunningProcess>(previous.Count);
            List<RunningProcess>? started = null;

            foreach (var p in procs)
            {
                try
                {
                    var pid = (uint)p.Id;
                    if (pid == 0)
                    {
                        continue;
                    }

                    var name = p.ProcessName;

                    // Reuse the cached entry when the pid maps to the same process. The name check
                    // catches the rare case where Windows recycled the pid onto a different process
                    // between scans, so a path re-resolve happens for the new owner.
                    if (previous.TryGetValue(pid, out var existing) &&
                        string.Equals(existing.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        current[pid] = existing;
                        continue;
                    }

                    var rp = new RunningProcess(pid, name, ProcessPath.TryGet(pid));
                    current[pid] = rp;
                    (started ??= new List<RunningProcess>()).Add(rp);
                }
                catch
                {
                    // Process exited mid-enumeration or query was denied - skip it.
                }
                finally
                {
                    p.Dispose();
                }
            }

            List<RunningProcess>? stopped = null;
            foreach (var kv in previous)
            {
                if (!current.ContainsKey(kv.Key))
                {
                    (stopped ??= new List<RunningProcess>()).Add(kv.Value);
                }
            }

            lock (_gate)
            {
                _known = current;
            }

            if (started is not null)
            {
                foreach (var rp in started)
                {
                    ProcessStarted?.Invoke(this, new RunningProcessEvent(rp));
                }
            }

            if (stopped is not null)
            {
                foreach (var rp in stopped)
                {
                    ProcessStopped?.Invoke(this, new RunningProcessEvent(rp));
                }
            }
        }
        catch (Exception ex)
        {
            // This runs on a Timer (ThreadPool) thread - an unhandled exception here, including one
            // thrown by a subscriber's handler, would tear down the whole process. Swallow + log.
            _logger.LogWarning(ex, "Process scan failed");
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}

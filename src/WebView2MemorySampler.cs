using System;
using System.Diagnostics;
using System.Threading;

namespace Nexus.Overlay;

/// <summary>
/// Samples the working set of the WebView2 child processes
/// (msedgewebview2.exe) and reports to the service log via
/// <c>/diagnostics/client-mem</c> (source=host), plus a local line in
/// nexus-overlay.log. The renderer's own probe (nexus-web) can only read its JS
/// heap via performance.memory; a renderer's total working set can far exceed
/// its JS heap, so this host number is what tells a JS-heap leak apart from a
/// GPU/canvas one.
///
/// Sparse by design, matching the renderer probe: a baseline line, then a line
/// only when the largest child or the total crosses a 100 MB step, plus an
/// hourly heartbeat. A steady-state host is near-silent.
///
/// `total` sums every msedgewebview2.exe on the box, which can include another
/// WebView2 app; `largest` (the heaviest single child) is the renderer we care
/// about and is unaffected by that.
/// </summary>
internal sealed class WebView2MemorySampler : IDisposable
{
    private const int SampleMs = 60_000;
    private const long HeartbeatMs = 60 * 60_000;
    private const int StepMb = 100;

    private readonly NexusApi _api;
    private readonly Timer _timer;
    private int _lastTotalMb = -1;
    private int _lastLargestMb = -1;
    private long _lastEmitTicks;

    public WebView2MemorySampler(NexusApi api)
    {
        _api = api;
        _timer = new Timer(_ => Tick(), null, dueTime: 0, period: SampleMs);
    }

    public void Dispose() => _timer.Dispose();

    private void Tick()
    {
        try
        {
            var (totalMb, largestMb, largestPid, count) = Sample();
            if (count == 0) return;

            var now = Environment.TickCount64;
            var baselineOrHeartbeat = _lastTotalMb < 0 || (now - _lastEmitTicks) >= HeartbeatMs;
            var stepped = Math.Abs(totalMb - _lastTotalMb) >= StepMb
                          || Math.Abs(largestMb - _lastLargestMb) >= StepMb;
            if (!baselineOrHeartbeat && !stepped) return;

            _lastTotalMb = totalMb;
            _lastLargestMb = largestMb;
            _lastEmitTicks = now;

            _api.PostClientMem(new ClientMemSample
            {
                Source = "host",
                TotalWsMB = totalMb,
                LargestWsMB = largestMb,
                LargestPid = largestPid,
                Children = count,
            });
            // Also land it locally; the POST can be dropped if the service is
            // mid-restart, and nexus-overlay.log is collected alongside. Same tag
            // + shape the service writes for the POSTed copy, so `[client-mem]`
            // greps both logs uniformly.
            Log.Info($"[client-mem] host total={totalMb}MB largest={largestMb}MB(pid {largestPid}) children={count}");
        }
        catch
        {
            /* sampling never destabilizes the host */
        }
    }

    private static (int totalMb, int largestMb, int largestPid, int count) Sample()
    {
        long total = 0;
        int largest = 0, largestPid = 0, count = 0;
        var procs = Process.GetProcessesByName("msedgewebview2");
        foreach (var p in procs)
        {
            try
            {
                var mb = (int)(p.WorkingSet64 / (1024 * 1024));
                total += mb;
                count++;
                if (mb > largest) { largest = mb; largestPid = p.Id; }
            }
            catch
            {
                /* process exited between enumerate and read */
            }
            finally
            {
                p.Dispose();
            }
        }
        return ((int)total, largest, largestPid, count);
    }
}

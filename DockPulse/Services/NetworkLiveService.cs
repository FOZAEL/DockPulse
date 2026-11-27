using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading;

namespace DockPulse.Services;

public readonly record struct NetworkLiveSample(long DownBps, long UpBps);

public sealed record NetworkLiveStats(
    IReadOnlyList<NetworkLiveSample> Samples,
    NetworkLiveSample Current,
    long PeakDownBps,
    long PeakUpBps,
    long AvgDownBps,
    long AvgUpBps,
    bool NoTraffic);

/// <summary>
/// Samples live network download/upload speed (bits per second) every 2 seconds across active non-loopback,
/// non-tunnel interfaces, keeping the last 30 samples (60 seconds). Only samples while leased via <see cref="Touch"/>.
/// </summary>
public sealed partial class NetworkLiveService : IDisposable
{
    public const int MaxSamples = 30;
    private const int SampleIntervalMs = 2000;
    private const long LeaseTimeoutMs = 10_000;

    private const double Kbps = 1000.0;
    private const double Mbps = 1_000_000.0;
    private const double Gbps = 1_000_000_000.0;

    private readonly object _lock = new();
    private readonly List<NetworkLiveSample> _samples = new(MaxSamples);
    private readonly Timer _timer;

    private long _lastTouchTicks;
    private bool _running;
    private bool _hasPrev;
    private long _prevRx;
    private long _prevTx;
    private long _prevTimestamp;
    private bool _disposed;

    public NetworkLiveService()
    {
        _timer = new Timer(OnTimerTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action? Sampled;

    /// <summary>Extends the active sampling lease for another 10 seconds and starts sampling if idle.</summary>
    public void Touch()
    {
        long nowTicks = Environment.TickCount64;
        bool needBaseline = false;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _lastTouchTicks = nowTicks;
            if (!_running)
            {
                _running = true;
                _hasPrev = false;
                needBaseline = true;
                _timer.Change(SampleIntervalMs, SampleIntervalMs);
            }
        }

        if (needBaseline)
        {
            // Capture an initial byte-counter baseline outside the timer so the first 2s tick produces a real speed
            var (rx, tx) = ReadTotalBytes();
            long ts = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                if (_running && !_hasPrev && !_disposed)
                {
                    _prevRx = rx;
                    _prevTx = tx;
                    _prevTimestamp = ts;
                    _hasPrev = true;
                }
            }
        }
    }

    /// <summary>Takes an immediate sample for the manual Refresh button.</summary>
    public void SampleNow()
    {
        Touch();
        TakeSample(forceEmitWhenNoBaseline: true);
    }

    public NetworkLiveStats GetStats()
    {
        NetworkLiveSample[] copy;
        lock (_lock)
        {
            copy = [.. _samples];
        }

        var current = copy.Length > 0 ? copy[^1] : default;
        long peakDown = 0;
        long peakUp = 0;
        long sumDown = 0;
        long sumUp = 0;

        for (int i = 0; i < copy.Length; i++)
        {
            var s = copy[i];
            if (s.DownBps > peakDown)
            {
                peakDown = s.DownBps;
            }

            if (s.UpBps > peakUp)
            {
                peakUp = s.UpBps;
            }

            sumDown += s.DownBps;
            sumUp += s.UpBps;
        }

        int n = Math.Max(1, copy.Length);
        long avgDown = (long)Math.Round((double)sumDown / n);
        long avgUp = (long)Math.Round((double)sumUp / n);
        bool noTraffic = peakDown == 0 && peakUp == 0;

        return new NetworkLiveStats(copy, current, peakDown, peakUp, avgDown, avgUp, noTraffic);
    }

    private void OnTimerTick(object? state)
    {
        long nowTicks = Environment.TickCount64;
        lock (_lock)
        {
            if (_disposed || !_running)
            {
                return;
            }

            if (nowTicks - _lastTouchTicks > LeaseTimeoutMs)
            {
                _running = false;
                _hasPrev = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
        }

        TakeSample(forceEmitWhenNoBaseline: false);
    }

    private void TakeSample(bool forceEmitWhenNoBaseline)
    {
        var (rx, tx) = ReadTotalBytes();
        long ts = Stopwatch.GetTimestamp();
        bool emit = false;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (!_hasPrev)
            {
                _prevRx = rx;
                _prevTx = tx;
                _prevTimestamp = ts;
                _hasPrev = true;
                emit = forceEmitWhenNoBaseline;
            }
            else
            {
                double elapsedSec = (double)(ts - _prevTimestamp) / Stopwatch.Frequency;
                if (elapsedSec >= 0.05)
                {
                    long rxDelta = rx - _prevRx;
                    long txDelta = tx - _prevTx;
                    if (rxDelta < 0)
                    {
                        rxDelta = 0;
                    }

                    if (txDelta < 0)
                    {
                        txDelta = 0;
                    }

                    long downBps = (long)Math.Round((rxDelta * 8.0) / elapsedSec);
                    long upBps = (long)Math.Round((txDelta * 8.0) / elapsedSec);

                    _prevRx = rx;
                    _prevTx = tx;
                    _prevTimestamp = ts;

                    _samples.Add(new NetworkLiveSample(downBps, upBps));
                    if (_samples.Count > MaxSamples)
                    {
                        _samples.RemoveAt(0);
                    }

                    emit = true;
                }
                else
                {
                    emit = forceEmitWhenNoBaseline;
                }
            }
        }

        if (emit)
        {
            try
            {
                Sampled?.Invoke();
            }
            catch
            {
                // Ignore subscriber errors
            }
        }
    }

    private static (long Received, long Sent) ReadTotalBytes()
    {
        long totalRx = 0;
        long totalTx = 0;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                try
                {
                    var stats = ni.GetIPStatistics();
                    totalRx += stats.BytesReceived;
                    totalTx += stats.BytesSent;
                }
                catch
                {
                    // Skip transient or unreadable interfaces
                }
            }
        }
        catch
        {
            // Ignore interface enumeration failures
        }

        return (totalRx, totalTx);
    }

    public static string FormatBps(long bps)
    {
        if (bps <= 0)
        {
            return "0 bps";
        }

        if (bps >= Gbps)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bps / Gbps:0.00} Gbps");
        }

        if (bps >= Mbps || Math.Round(bps / Kbps) >= 1000.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bps / Mbps:0.0} Mbps");
        }

        if (bps >= Kbps)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bps / Kbps):0} Kbps");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bps} bps");
    }

    public static string FormatShortBpsAxis(long bps)
    {
        if (bps <= 0)
        {
            return "0";
        }

        string[] units = ["bps", "Kbps", "Mbps", "Gbps"];
        double v = bps;
        int i = 0;
        while (v >= 1000.0 && i < units.Length - 1)
        {
            v /= 1000.0;
            i++;
        }

        string fmt = v >= 100.0 ? "0" : (Math.Abs(v - Math.Round(v)) < 0.05 ? "0" : "0.0");
        return $"{v.ToString(fmt, CultureInfo.InvariantCulture)} {units[i]}";
    }

    public static long ComputeNiceBpsCeiling(long maxBps)
    {
        if (maxBps <= 0)
        {
            return 10_000_000L; // 10 Mbps
        }

        double unit = 1.0;
        if (maxBps >= Gbps)
        {
            unit = Gbps;
        }
        else if (maxBps >= Mbps)
        {
            unit = Mbps;
        }
        else if (maxBps >= Kbps)
        {
            unit = Kbps;
        }

        double inUnits = maxBps / unit;
        double mag = Math.Pow(10.0, Math.Floor(Math.Log10(Math.Max(inUnits, 0.1))));
        double n = inUnits / mag;
        double nice = n switch
        {
            <= 1.0 => 1.0,
            <= 1.5 => 1.5,
            <= 2.0 => 2.0,
            <= 2.5 => 2.5,
            <= 4.0 => 4.0,
            <= 5.0 => 5.0,
            <= 8.0 => 8.0,
            _ => 10.0,
        };

        return (long)Math.Ceiling(nice * mag * unit);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _running = false;
        }

        _timer.Dispose();
    }
}

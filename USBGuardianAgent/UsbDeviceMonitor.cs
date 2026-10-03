using System.Management;
using System.Threading.Channels;

namespace USBGuardianAgent;

public sealed class UsbDeviceMonitor : IDisposable
{
    private const string DeviceChangeQuery =
        "SELECT * FROM Win32_DeviceChangeEvent " +
        "WHERE EventType = 2 OR EventType = 3";

    private readonly ILogger<UsbDeviceMonitor> _logger;
    private readonly object _gate = new();
    private readonly Channel<bool> _signals =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private ManagementEventWatcher? _watcher;
    private DateTime _nextStartAttemptUtc;
    private bool _disposed;

    public UsbDeviceMonitor(ILogger<UsbDeviceMonitor> logger)
    {
        _logger = logger;
    }

    public void RequestScan() => _signals.Writer.TryWrite(true);

    public void EnsureStarted()
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (_gate)
        {
            if (_disposed || _watcher != null ||
                DateTime.UtcNow < _nextStartAttemptUtc)
                return;

            ManagementEventWatcher? candidate = null;
            try
            {
                candidate = new ManagementEventWatcher(
                    new WqlEventQuery(DeviceChangeQuery));
                candidate.EventArrived += OnDeviceChanged;
                candidate.Stopped += OnWatcherStopped;
                _watcher = candidate;
                candidate.Start();
                if (!ReferenceEquals(_watcher, candidate)) return;
                _nextStartAttemptUtc = DateTime.MinValue;
                _logger.LogInformation(
                    "WMI device-change monitoring started; periodic fallback remains active.");
            }
            catch (Exception ex)
            {
                if (candidate != null)
                {
                    candidate.EventArrived -= OnDeviceChanged;
                    candidate.Stopped -= OnWatcherStopped;
                    candidate.Dispose();
                }
                if (ReferenceEquals(_watcher, candidate)) _watcher = null;
                _nextStartAttemptUtc = DateTime.UtcNow.AddMinutes(1);
                _logger.LogWarning(ex,
                    "WMI device-change monitoring could not start. " +
                    "Using the periodic enforcement fallback.");
            }
        }
    }

    public async Task WaitForNextScanAsync(
        TimeSpan fallbackInterval,
        CancellationToken cancellationToken)
    {
        EnsureStarted();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(fallbackInterval);

        bool eventTriggered = false;
        try
        {
            await _signals.Reader.ReadAsync(timeout.Token);
            eventTriggered = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The fallback interval elapsed.
        }

        while (_signals.Reader.TryRead(out _)) eventTriggered = true;

        if (eventTriggered)
        {
            // Give Plug and Play a short window to expose the final WMI instance.
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private void OnDeviceChanged(object sender, EventArrivedEventArgs args)
    {
        _signals.Writer.TryWrite(true);
    }

    private void OnWatcherStopped(object sender, StoppedEventArgs args)
    {
        bool shouldLog;
        lock (_gate)
        {
            shouldLog = !_disposed && ReferenceEquals(_watcher, sender);
            if (shouldLog)
            {
                _watcher = null;
                _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            }
        }

        if (!shouldLog) return;
        if (sender is ManagementEventWatcher stoppedWatcher)
        {
            stoppedWatcher.EventArrived -= OnDeviceChanged;
            stoppedWatcher.Stopped -= OnWatcherStopped;
            stoppedWatcher.Dispose();
        }
        _logger.LogWarning(
            "WMI device-change monitoring stopped with status {Status}; it will be restarted.",
            args.Status);
        _signals.Writer.TryWrite(true);
    }

    public void Dispose()
    {
        ManagementEventWatcher? watcher;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher == null) return;

        watcher.EventArrived -= OnDeviceChanged;
        watcher.Stopped -= OnWatcherStopped;
        try { watcher.Stop(); } catch { }
        watcher.Dispose();
    }
}

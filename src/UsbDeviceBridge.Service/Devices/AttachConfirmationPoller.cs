using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Service.Devices;

/// <summary>
/// Polls usbipd state after an attach or detach command to confirm the device
/// reached the expected state within a timeout.
/// </summary>
internal sealed class AttachConfirmationPoller(UsbIpdClient usbIpdClient, ILogger logger)
{
    /// <summary>
    /// Polls until the device at <paramref name="busId"/> is in the
    /// <see cref="DeviceState.Attached"/> state, or the timeout elapses.
    /// </summary>
    public async Task<(bool Confirmed, string Message)> WaitForAttachedStateAsync(
        string busId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<UsbIpdStateDevice> devices;
            try
            {
                devices = await usbIpdClient.GetDevicesAsync(cancellationToken);
            }
            catch (UsbIpdException ex)
            {
                logger.LogWarning(ex, "Attach verification failed while reading usbipd state.");
                return (false, "Attach started, but state verification failed.");
            }

            var device = devices.FirstOrDefault(
                d => string.Equals(d.BusId, busId, StringComparison.OrdinalIgnoreCase)
            );
            if (device is null)
                return (false, $"Device '{busId}' disappeared during attach.");

            var state = UsbIpdStateParser.Classify(device);
            if (state == DeviceState.Attached)
                return (true, string.Empty);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return (false, "Attach command completed, but device did not reach the attached state in time.");
    }

    /// <summary>
    /// Polls until the device at <paramref name="busId"/> has left the
    /// <see cref="DeviceState.Attached"/> state, or the timeout elapses.
    /// </summary>
    public async Task<(bool Confirmed, string Message)> WaitForDetachedStateAsync(
        string busId,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<UsbIpdStateDevice> devices;
            try
            {
                devices = await usbIpdClient.GetDevicesAsync(cancellationToken);
            }
            catch (UsbIpdException ex)
            {
                logger.LogWarning(ex, "Detach verification failed while reading usbipd state.");
                return (false, "Detach started, but state verification failed.");
            }

            var device = devices.FirstOrDefault(
                d => string.Equals(d.BusId, busId, StringComparison.OrdinalIgnoreCase)
            );

            if (device is null)
                return (true, string.Empty);

            var state = UsbIpdStateParser.Classify(device);
            if (state != DeviceState.Attached)
                return (true, string.Empty);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return (false, "Detach command completed, but device did not leave the attached state in time.");
    }
}

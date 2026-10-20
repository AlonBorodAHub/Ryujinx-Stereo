using System.Threading;

namespace Ryujinx.Input.HLE;

public static class StereoAdjustmentFeedback
{
    public sealed record Update(double DepthPercent, double Convergence, bool Reset);
    private static Update _pending;
    public static void Publish(double depthPercent, double convergence, bool reset = false) =>
        Interlocked.Exchange(ref _pending, new Update(depthPercent, convergence, reset));
    public static Update Take() => Interlocked.Exchange(ref _pending, null);
}

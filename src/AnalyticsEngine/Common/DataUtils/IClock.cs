using System;

namespace DataUtils
{
    /// <summary>
    /// Port for reading the current time, so time-dependent import rules - watermark advance, cadence
    /// windows, de-duplication look-back - can be asserted deterministically instead of by waiting for
    /// real wall-clock time or contorting the inputs. See issue #368.
    ///
    /// The established shape in this solution is to pass the instant in as a value
    /// (<c>ImportCadenceGate.ShouldRun(..., DateTime nowUtc)</c>). This port is for the callers that
    /// need to obtain that instant, not a replacement for that pattern: pure rules should keep taking
    /// the time as a parameter rather than depending on <see cref="IClock"/>.
    /// </summary>
    public interface IClock
    {
        /// <summary>The current UTC time.</summary>
        DateTime UtcNow { get; }
    }

    /// <summary>
    /// Production <see cref="IClock"/>, reading <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        /// <summary>Shared instance - the clock is stateless, so there is no reason to allocate one per caller.</summary>
        public static readonly SystemClock Instance = new SystemClock();

        public DateTime UtcNow => DateTime.UtcNow;
    }
}

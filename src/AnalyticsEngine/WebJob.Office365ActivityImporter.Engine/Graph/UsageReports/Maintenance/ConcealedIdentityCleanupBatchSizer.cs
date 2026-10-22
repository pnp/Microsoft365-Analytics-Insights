using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Maintenance
{
    /// <summary>
    /// Chooses how many users each cleanup batch takes: as many as the batches so far show should stay well inside the cap on
    /// how long one batch - one transaction, holding its locks - is planned to take, and fewer when the cost per user is high
    /// enough that a batch inside the softer target costs little throughput. The cap is a planning threshold, not a wall-clock
    /// guarantee: a database that slows down can still push the odd batch past it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cap comes first.</b> A batch's time only grows with its users, and at most in proportion - the fixed part does
    /// not grow - so, at the speed a batch of <c>s</c> users ran at, taking <c>t</c>, <c>s x ceiling / t</c> users take no
    /// longer than the ceiling. No size is chosen beyond what a recent successful batch proves this way, with the ceiling at
    /// <see cref="CeilingShareOfCap"/> of the cap (<see cref="ConcealedIdentityUserCleanup.MaxBatchDuration"/>), so a batch
    /// can run up to two-thirds slower than the one that proved it and still finish inside the cap. Blocking, I/O or log
    /// throttling beyond that can still push one past it; the paragraphs below say what happens then.
    /// </para>
    /// <para>
    /// <b>Within that, time per user decides.</b> From recent batches of well-spread sizes (the largest at least twice the
    /// smallest; closer sizes only fit noise) it fits <c>time = floor + perUser x users</c>. When the largest batch that keeps
    /// to the target (<see cref="ConcealedIdentityUserCleanup.TargetBatchDuration"/>) is within
    /// <see cref="ThroughputTolerance"/> of the best time per user it may take, it takes that; otherwise - the fixed cost
    /// dominates - it takes the largest batch the proofs allow. With no usable fit it keeps the last size a fit chose, or,
    /// before the first one, doubles while that is provably within the ceiling and halves when it is not; where the last fit
    /// found the fixed cost dominating, it keeps growing as far as the batches prove safe.
    /// </para>
    /// <para>
    /// <b>A failure halves the next batch; only a size-dependent one bounds it, and only for a while.</b> A batch that timed
    /// out, or that ran past the cap where its size is to blame, was too big for this database: every later batch stays at or
    /// below half its size, however fast smaller batches are. That limit doubles back once for each
    /// <see cref="LimitDecayInterval"/> without another such failure, so a real cliff in the cost is paid for at most once a
    /// day, and a slow spell taken for one is never permanent: the failure is not fitted as a cost - a timeout's time is only a
    /// lower bound, and a slow batch's is the database's - and each time the limit doubles back, the sizing in force before it
    /// was set is taken up again, whatever the limited batches fitted. A deadlock, a lock timeout or a reference that
    /// appeared mid-batch is not about size or cost, so it is a detour: the next batch is half the size, and the ceiling
    /// doubles back each time a batch at it commits within the planned share of the cap - or no slower than the batches
    /// before the failure - until the size the failure interrupted is taken up again. Neither the failure nor the batches
    /// held down by it are remembered: they would push the proofs of that size out of the window, and add spread the sizing
    /// never chose to the fit.
    /// </para>
    /// <para>
    /// <b>A batch past the cap on familiar ground is the database's, not its size's.</b> When a batch at most
    /// <see cref="FamiliarSizeMargin"/> times the size of one that recently committed inside the cap runs past it, the database
    /// was slow - a busy moment, I/O or log throttling, a checkpoint, a lock wait just short of the lock timeout - and a
    /// smaller batch would not have been the cure: it is a detour too. Only a batch past the cap on new ground - bigger than
    /// anything that recently fitted, which is where a cliff in the cost is found - or the
    /// <see cref="OverrunsBeforeTheSizeIsBlamed"/>th in a row at a size, is booked as too big.
    /// </para>
    /// </remarks>
    internal sealed class ConcealedIdentityCleanupBatchSizer
    {
        /// <summary>How much worse than the best time per user a batch size may be, to keep a batch under the target.</summary>
        internal const double ThroughputTolerance = 0.15;

        /// <summary>The share of the cap that sizes are planned to: room for a batch to run slower than the one that proved it.</summary>
        internal const double CeilingShareOfCap = 0.6;

        /// <summary>
        /// How much bigger than a batch that recently committed inside the cap a batch may be and still be on familiar ground:
        /// more than the largest step the proofs take where the fixed cost dominates, less than the doubling that finds a cliff.
        /// </summary>
        internal const double FamiliarSizeMargin = 1.5;

        /// <summary>
        /// Batches in a row, of at least the same size and on familiar ground, that must run past the cap before the size is
        /// blamed after all. A slow moment rarely hits three in a row; a cliff or a cost that has grown does, every time.
        /// </summary>
        internal const int OverrunsBeforeTheSizeIsBlamed = 3;

        /// <summary>How long the lasting size limit holds without another size-dependent failure before it doubles back.</summary>
        internal static readonly TimeSpan LimitDecayInterval = TimeSpan.FromHours(24);

        /// <summary>Batches the fit and the proofs use: recent enough to follow a change in load.</summary>
        private const int Window = 8;

        private readonly int _min;
        private readonly int _max;
        private readonly double _targetSeconds;
        private readonly double _capSeconds;
        private readonly double _ceilingSeconds;
        private readonly IClock _clock;
        private readonly List<Batch> _recent = new List<Batch>();
        private int? _lastFitted;
        private bool _lastFittedTookLargest;

        // At or below half of the latest batch that timed out or ran past the cap for its size; doubles back each day.
        private int _sizeLimit;
        private DateTime _sizeLimitSinceUtc;

        // The sizing in force when a limit was first set, taken up again each time it doubles back.
        private int? _lastFittedBeforeLimit;
        private bool _lastFittedTookLargestBeforeLimit;

        // Batches of at least _overrunSize users that ran past the cap on familiar ground since one last fitted inside it.
        private int _overrunSize;
        private int _overruns;

        // A detour after transient failures: the size they interrupted, and how many times in a row they halved it.
        private int _interruptedSize;
        private int _detourHalvings;

        // A batch that committed inside the cap.
        private struct Batch
        {
            public int Users;
            public double Seconds;
        }

        public ConcealedIdentityCleanupBatchSizer(int initial, int min, int max, TimeSpan target, TimeSpan cap, IClock clock = null)
        {
            if (min < 1 || max < min) throw new ArgumentOutOfRangeException(nameof(max));
            _min = min;
            _max = max;
            _targetSeconds = target.TotalSeconds;
            _capSeconds = cap.TotalSeconds;
            _ceilingSeconds = cap.TotalSeconds * CeilingShareOfCap;
            _clock = clock ?? SystemClock.Instance;
            _sizeLimit = max;
            Next = Clamp(initial);
        }

        /// <summary>Users for the next batch.</summary>
        public int Next { get; private set; }

        /// <summary>The most users any batch may take for now, after the size-dependent failures seen so far.</summary>
        internal int SizeLimit => _sizeLimit;

        /// <summary>Records a batch that committed: <paramref name="users"/> users in <paramref name="took"/>.</summary>
        public void Observe(int users, TimeSpan took)
        {
            if (users <= 0) return;
            DecaySizeLimit();
            var seconds = Math.Max(0, took.TotalSeconds);
            var heldDown = _detourHalvings > 0 && users >= DetourCeiling;
            if (seconds > _capSeconds)
            {
                // A batch held down by a detour is smaller than one the database has just run, so past the cap it is the
                // database again: the detour goes on, halving.
                if (!heldDown && OverranBecauseOfItsSize(users))
                {
                    ObserveFailure(users, took, sizeDependent: true);
                    return;
                }

                // The database was slow: a detour, as after a deadlock.
                ObserveFailure(users, took, sizeDependent: false);
                return;
            }

            if (_overruns > 0 && users >= _overrunSize) _overruns = 0;

            if (heldDown)
            {
                // Held down after a transient failure. Each batch at the ceiling that shows the database is no slower than
                // before doubles it back; at the end of the detour the interrupted size, chosen from the same - unchanged -
                // window, is resumed.
                if (seconds <= Math.Max(_ceilingSeconds, SlowestRemembered()))
                {
                    _detourHalvings--;
                }
                Next = Clamp(Math.Min(DetourCeiling, Math.Min(_interruptedSize, _sizeLimit)));
                return;
            }

            Remember(new Batch { Users = users, Seconds = seconds });

            var proven = ProvenSize(users);
            int next;
            if (TryFit(out var floor, out var perUser))
            {
                next = Math.Min(proven, Choose(floor, perUser, proven));
                _lastFittedTookLargest = next >= proven;
                _lastFitted = next;
            }
            else if (_lastFitted.HasValue)
            {
                // Recent sizes too close together to fit. Where the fit last said the fixed cost dominates, keep growing as far
                // as the batches prove safe; otherwise keep the size it chose.
                next = _lastFittedTookLargest ? proven : Math.Min(proven, _lastFitted.Value);
            }
            else
            {
                next = users * 2 <= proven ? users * 2 : users / 2;
            }

            Next = Clamp(Math.Min(next, Math.Min(DetourCeiling, _sizeLimit)));
        }

        /// <summary>
        /// Records a batch that failed - it rolled back - after <paramref name="took"/>. The next batch is half its size.
        /// <paramref name="sizeDependent"/> - a command timeout - also keeps later batches at or below that half, until the
        /// limit doubles back a day later; otherwise - a deadlock, a lock timeout, a reference that appeared mid-batch - the
        /// half is a detour, and the sizing resumes where it was once batches at it show the database is no slower than before.
        /// </summary>
        public void ObserveFailure(int users, TimeSpan took, bool sizeDependent)
        {
            if (users <= 0) return;
            DecaySizeLimit();

            if (sizeDependent)
            {
                if (_sizeLimit >= _max)
                {
                    _lastFittedBeforeLimit = _lastFitted;
                    _lastFittedTookLargestBeforeLimit = _lastFittedTookLargest;
                }
                _sizeLimit = Math.Min(_sizeLimit, Clamp(users / 2));
                _sizeLimitSinceUtc = _clock.UtcNow;
                _overruns = 0;
                _detourHalvings = 0;
                Next = _sizeLimit;
                return;
            }

            if (_detourHalvings == 0)
            {
                _interruptedSize = users;
            }
            if (DetourCeiling > _min)
            {
                _detourHalvings++;
            }
            Next = Clamp(Math.Min(DetourCeiling, _sizeLimit));
        }

        // The interrupted size, halved once for each transient failure in a row; no ceiling outside a detour.
        private int DetourCeiling => _detourHalvings == 0 ? _max : Math.Max(_min, _interruptedSize >> Math.Min(_detourHalvings, 30));

        private double SlowestRemembered() => _recent.Select(b => b.Seconds).DefaultIfEmpty(0).Max();

        /// <summary>
        /// Whether a batch that committed past the cap did so because of its size: it was on new ground - more than
        /// <see cref="FamiliarSizeMargin"/> times every batch that recently committed inside the cap - or it is the
        /// <see cref="OverrunsBeforeTheSizeIsBlamed"/>th batch in a row of at least its size to run past the cap.
        /// </summary>
        private bool OverranBecauseOfItsSize(int users)
        {
            var largestFitted = _recent.Select(b => b.Users).DefaultIfEmpty(0).Max();
            if (users > largestFitted * FamiliarSizeMargin) return true;
            _overrunSize = _overruns == 0 ? users : Math.Min(_overrunSize, users);
            return ++_overruns >= OverrunsBeforeTheSizeIsBlamed;
        }

        private void DecaySizeLimit()
        {
            var now = _clock.UtcNow;
            var doubled = false;
            while (_sizeLimit < _max && now - _sizeLimitSinceUtc >= LimitDecayInterval)
            {
                _sizeLimit = Math.Min(_max, _sizeLimit * 2);
                _sizeLimitSinceUtc += LimitDecayInterval;
                doubled = true;
            }

            if (doubled)
            {
                // Before any fit, the sizing was growing as far as the batches proved safe.
                _lastFitted = _lastFittedBeforeLimit ?? _max;
                _lastFittedTookLargest = !_lastFittedBeforeLimit.HasValue || _lastFittedTookLargestBeforeLimit;
            }
        }

        /// <summary>The batch size for a fitted floor and per-user cost (both in seconds), at most <paramref name="largest"/>.</summary>
        internal int Choose(double floor, double perUser, int largest)
        {
            var withinTarget = floor >= _targetSeconds
                ? 0
                : (int)Math.Min(_max, Math.Floor((_targetSeconds - floor) / perUser));

            // The smallest batch whose time per user is within the tolerance of the largest allowed batch's.
            var bestPerUser = floor / Math.Max(1, largest) + perUser;
            var floorShareAllowed = (1 + ThroughputTolerance) * bestPerUser - perUser;
            var efficient = floor <= 0 || floorShareAllowed <= 0 ? _min : (int)Math.Ceiling(floor / floorShareAllowed);

            return withinTarget >= efficient ? withinTarget : largest;
        }

        /// <summary>
        /// The most users a recent successful batch proves will take no longer than the ceiling; <paramref name="users"/> -
        /// no growth - when none of them does. A batch between the ceiling and the cap is not a reason to shrink: only a
        /// failure or a batch past the cap is.
        /// </summary>
        private int ProvenSize(int users)
        {
            var proven = 0;
            foreach (var batch in _recent)
            {
                if (batch.Seconds > _ceilingSeconds) continue;
                var size = batch.Seconds <= 0.001 ? _max : (int)Math.Min(_max, Math.Floor(batch.Users * _ceilingSeconds / batch.Seconds));
                proven = Math.Max(proven, size);
            }
            return proven > 0 ? proven : users;
        }

        private bool TryFit(out double floor, out double perUser)
        {
            floor = 0;
            perUser = 0;

            var points = _recent;
            if (points.Count < 2) return false;
            var smallest = points.Min(b => b.Users);
            var largest = points.Max(b => b.Users);
            if (largest < 2 * smallest) return false;

            // Least squares: time = floor + perUser x users.
            var meanUsers = points.Average(b => (double)b.Users);
            var meanSeconds = points.Average(b => b.Seconds);
            double sxx = 0, sxy = 0;
            foreach (var b in points)
            {
                sxx += (b.Users - meanUsers) * (b.Users - meanUsers);
                sxy += (b.Users - meanUsers) * (b.Seconds - meanSeconds);
            }

            // Noise can make a bigger batch look cheaper: treat that as "users cost almost nothing" - the proven size still
            // bounds the batch, so this never plans one past the ceiling.
            perUser = Math.Max(1e-6, sxy / sxx);
            floor = Math.Max(0, meanSeconds - perUser * meanUsers);
            return true;
        }

        private void Remember(Batch batch)
        {
            _recent.Add(batch);
            if (_recent.Count > Window) _recent.RemoveAt(0);
        }

        private int Clamp(int size) => Math.Max(_min, Math.Min(_max, size));
    }
}

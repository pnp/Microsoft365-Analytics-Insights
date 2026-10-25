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
    /// <para>
    /// <b>A fixed cost per batch that is itself near the cap is nobody's size.</b> The delete of the users has to check the
    /// foreign-key children that have no index leading with the user column, and that costs the same whether a batch takes
    /// one user or a thousand. Where it alone takes longer than the ceiling, no size brings a batch inside it: a batch that
    /// committed under the cap is never halved for it, and one past the cap is not blamed on its size. Instead, once recent
    /// batches of clearly different sizes (at least <see cref="FixedCostSpread"/> times apart, three or more of them, every
    /// one slower than the ceiling) fit a line whose intercept is at or above the ceiling, the fixed cost is irreducible and
    /// the batch is the largest the fit predicts to finish under <see cref="FixedCostShareOfCap"/> of the cap - or, where
    /// even one user takes longer than that, under <see cref="FixedCostShareOfTimeout"/> of the command timeout - because
    /// the bigger the batch, the less each user pays of it. A limit set on the way, by a batch past the cap that turned out
    /// to be about the fixed cost, is lifted again. Each step grows by at most <see cref="MaxGrowthPerFixedCostStep"/> times
    /// the largest recent batch, so a noisy fit cannot jump straight to a size nothing has shown. Timeouts are different:
    /// the time of a timed-out batch is only a bound, so after <see cref="MaxHalvingsWithoutACommit"/> halvings in a row with
    /// not one batch committing between them - smaller batches timed out too, so size is not what is wrong - the size is held.
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

        /// <summary>
        /// How far apart (largest over smallest) the sizes of the batches must be for their times to say whether a fixed cost
        /// per batch is what they are made of. Closer than the <c>2</c> the ordinary fit asks, because here every batch is
        /// already slow, and the answer - a slope near zero - is the same however the times are read.
        /// </summary>
        internal const double FixedCostSpread = 1.5;

        /// <summary>Batches the fixed-cost fit needs: two points always fit a line, noise or not.</summary>
        private const int FixedCostMinimumBatches = 3;

        /// <summary>The share of the cap a batch is planned to when its fixed cost alone is above the ceiling.</summary>
        internal const double FixedCostShareOfCap = 0.9;

        /// <summary>The share of the command timeout a batch is planned to when even one user takes longer than the cap.</summary>
        internal const double FixedCostShareOfTimeout = 0.8;

        /// <summary>The most a fixed-cost step grows the batch: times the largest recent batch.</summary>
        internal const int MaxGrowthPerFixedCostStep = 4;

        /// <summary>
        /// How many times in a row a batch may time out and be halved, with no batch committing in between, before the size is
        /// held: when a third of the size still times out, the size is not what is wrong.
        /// </summary>
        internal const int MaxHalvingsWithoutACommit = 3;

        /// <summary>How long the lasting size limit holds without another size-dependent failure before it doubles back.</summary>
        internal static readonly TimeSpan LimitDecayInterval = TimeSpan.FromHours(24);

        /// <summary>Batches the fit and the proofs use: recent enough to follow a change in load.</summary>
        private const int Window = 8;

        private readonly int _min;
        private readonly int _max;
        private readonly double _targetSeconds;
        private readonly double _capSeconds;
        private readonly double _ceilingSeconds;
        private readonly double _timeoutSeconds;
        private readonly IClock _clock;
        private readonly List<Batch> _recent = new List<Batch>();

        // Every batch that committed, whatever it took - including those past the cap, which _recent leaves out.
        private readonly List<Batch> _committed = new List<Batch>();

        // The fixed cost per batch and the cost per user once it was found irreducible; cleared when a batch comes in under the ceiling.
        private double? _fixedCostSeconds;
        private double _fixedCostPerUser;

        // Timeouts since a batch last committed, and whether the lasting limit was set by a commit past the cap rather than a timeout.
        private int _timeoutsSinceCommit;
        private bool _limitSetByOverrun;
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

        public ConcealedIdentityCleanupBatchSizer(int initial, int min, int max, TimeSpan target, TimeSpan cap, IClock clock = null, TimeSpan? commandTimeout = null)
        {
            if (min < 1 || max < min) throw new ArgumentOutOfRangeException(nameof(max));
            _min = min;
            _max = max;
            _targetSeconds = target.TotalSeconds;
            _capSeconds = cap.TotalSeconds;
            _ceilingSeconds = cap.TotalSeconds * CeilingShareOfCap;
            _timeoutSeconds = (commandTimeout ?? TimeSpan.FromSeconds(ConcealedIdentityUserCleanup.BatchCommandTimeoutSeconds)).TotalSeconds;
            _clock = clock ?? SystemClock.Instance;
            _sizeLimit = max;
            Next = Clamp(initial);
        }

        /// <summary>Users for the next batch.</summary>
        public int Next { get; private set; }

        /// <summary>The most users any batch may take for now, after the size-dependent failures seen so far.</summary>
        internal int SizeLimit => _sizeLimit;

        /// <summary>
        /// The seconds every batch costs however few users it takes, once recent batches showed that it is above the ceiling and
        /// no batch size can bring a batch inside it; null otherwise. For the run's log line.
        /// </summary>
        internal double? FixedCostSeconds => _fixedCostSeconds;

        /// <summary>Records a batch that committed: <paramref name="users"/> users in <paramref name="took"/>.</summary>
        public void Observe(int users, TimeSpan took)
        {
            if (users <= 0) return;
            DecaySizeLimit();
            _timeoutsSinceCommit = 0;
            var seconds = Math.Max(0, took.TotalSeconds);
            var heldDown = _detourHalvings > 0 && users >= DetourCeiling;

            if (!heldDown)
            {
                RememberCommitted(new Batch { Users = users, Seconds = seconds });
                if (TryFitFixedCost(out var fixedCost, out var fixedCostPerUser))
                {
                    _fixedCostSeconds = fixedCost;
                    _fixedCostPerUser = fixedCostPerUser;
                    ChooseForFixedCost(users);
                    return;
                }

                if (_fixedCostSeconds.HasValue)
                {
                    // The sizes in the window are too close to fit again, but a batch as slow as the fixed cost predicts keeps it.
                    if (IsConsistentWithFixedCost(users, seconds))
                    {
                        ChooseForFixedCost(users);
                        return;
                    }
                    _fixedCostSeconds = null;
                }
            }

            if (seconds > _capSeconds)
            {
                // A batch held down by a detour is smaller than one the database has just run, so past the cap it is the
                // database again: the detour goes on, halving.
                if (!heldDown && OverranBecauseOfItsSize(users))
                {
                    ObserveFailureCore(users, sizeDependent: true, fromCommit: true);
                    return;
                }

                // The database was slow: a detour, as after a deadlock.
                ObserveFailureCore(users, sizeDependent: false, fromCommit: true);
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

            var provenSize = ProvenSize();
            var hasProof = provenSize > 0;
            var proven = hasProof ? provenSize : users;
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
            else if (proven >= users * 2)
            {
                next = users * 2;
            }
            else if (seconds <= _ceilingSeconds)
            {
                // Under the ceiling: grow as far as this batch proves, never shrink a batch that committed inside it.
                next = proven;
            }
            else
            {
                // Under the cap but over the ceiling, and nothing proves a size: one batch cannot say whether the users or a
                // fixed cost made it slow. Take a modest step down towards the ceiling - never a halving - and let the next
                // batches, a fifth smaller each, show whether the time follows (the users) or does not (a fixed cost).
                next = Math.Max(hasProof ? proven : 0, (int)(users * Math.Min(0.8, _ceilingSeconds / seconds)));
            }

            Next = Clamp(Math.Min(next, Math.Min(DetourCeiling, _sizeLimit)));
        }

        /// <summary>
        /// Records a batch that failed - it rolled back - after <paramref name="took"/>. The next batch is half its size.
        /// <paramref name="sizeDependent"/> - a command timeout - also keeps later batches at or below that half, until the
        /// limit doubles back a day later; otherwise - a deadlock, a lock timeout, a reference that appeared mid-batch - the
        /// half is a detour, and the sizing resumes where it was once batches at it show the database is no slower than before.
        /// A size-dependent failure that follows <see cref="MaxHalvingsWithoutACommit"/> others with no batch committing between
        /// them does not halve again.
        /// </summary>
        public void ObserveFailure(int users, TimeSpan took, bool sizeDependent)
            => ObserveFailureCore(users, sizeDependent, fromCommit: false);

        // fromCommit: the batch committed, but ran past the cap - not a failure, so it counts for no timeout.
        private void ObserveFailureCore(int users, bool sizeDependent, bool fromCommit)
        {
            if (users <= 0) return;
            DecaySizeLimit();

            if (sizeDependent)
            {
                _fixedCostSeconds = null;
                if (!fromCommit && ++_timeoutsSinceCommit > MaxHalvingsWithoutACommit)
                {
                    // Smaller batches timed out too, so the size is not what is wrong, and halving again would only make
                    // every later batch smaller for nothing. Hold the size until one commits.
                    _overruns = 0;
                    _detourHalvings = 0;
                    Next = Clamp(Math.Min(users, _sizeLimit));
                    return;
                }

                if (_sizeLimit >= _max)
                {
                    _lastFittedBeforeLimit = _lastFitted;
                    _lastFittedTookLargestBeforeLimit = _lastFittedTookLargest;
                }
                _sizeLimit = Math.Min(_sizeLimit, Clamp(users / 2));
                _sizeLimitSinceUtc = _clock.UtcNow;
                _limitSetByOverrun = fromCommit;
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
        /// The most users a recent successful batch proves will take no longer than the ceiling; 0 when none of them does. A
        /// batch between the ceiling and the cap is not a reason to shrink: only a failure or a batch past the cap is.
        /// </summary>
        private int ProvenSize()
        {
            var proven = 0;
            foreach (var batch in _recent)
            {
                if (batch.Seconds > _ceilingSeconds) continue;
                var size = batch.Seconds <= 0.001 ? _max : (int)Math.Min(_max, Math.Floor(batch.Users * _ceilingSeconds / batch.Seconds));
                proven = Math.Max(proven, size);
            }
            return proven;
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

            FitLine(points, out floor, out perUser);
            return true;
        }

        // Least squares: time = floor + perUser x users.
        private static void FitLine(List<Batch> points, out double floor, out double perUser)
        {
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
        }

        /// <summary>
        /// Whether recent batches of clearly different sizes, every one slower than the ceiling, fit a line whose intercept is
        /// at or above it: the time does not follow the users, so no size brings a batch inside the ceiling.
        /// </summary>
        private bool TryFitFixedCost(out double floor, out double perUser)
        {
            floor = 0;
            perUser = 0;

            var points = _committed;
            if (points.Count < FixedCostMinimumBatches) return false;
            if (points.Any(b => b.Seconds <= _ceilingSeconds)) return false;
            if (points.Max(b => b.Users) < FixedCostSpread * points.Min(b => b.Users)) return false;

            FitLine(points, out floor, out perUser);
            return floor >= _ceilingSeconds;
        }

        // A batch as slow as the known fixed cost says it should be: not much slower, and it committed. Not also short of the
        // timeout's margin: that is what FixedCostBatchSize plans to, not a sign that a batch's time is not the fixed cost's.
        // With that bound, a fixed cost near or past the margin put batches a little over it - noise, or the fixed cost itself -
        // and once the window had lost the spread to fit again, each was blamed on its size and halved, and the half was held,
        // again and again down to one user per batch. A batch creeping towards the timeout is still caught: by a refit whenever
        // the window has the spread, by running more than 30% slower than predicted, and by the timeout, which is size-dependent.
        private bool IsConsistentWithFixedCost(int users, double seconds)
        {
            var predicted = _fixedCostSeconds.Value + _fixedCostPerUser * users;
            return seconds > _ceilingSeconds && seconds <= Math.Min(_timeoutSeconds, predicted * 1.3);
        }

        private void ChooseForFixedCost(int users)
        {
            // Halving did not make a batch any quicker, so a limit that a batch past the cap set on the way was not about size.
            if (_limitSetByOverrun)
            {
                _sizeLimit = _max;
                _limitSetByOverrun = false;
                _lastFittedBeforeLimit = null;
            }
            _overruns = 0;

            var size = FixedCostBatchSize(users);
            _lastFitted = size;
            _lastFittedTookLargest = true;
            Next = Clamp(Math.Min(size, Math.Min(DetourCeiling, _sizeLimit)));
        }

        // The largest batch the fixed cost and the cost per user predict to finish inside the cap or, when even one user takes
        // longer than that, the command timeout; at most a few times the largest batch seen. The current size when none fits.
        private int FixedCostBatchSize(int users)
        {
            var floor = _fixedCostSeconds.Value;
            var perUser = Math.Max(1e-6, _fixedCostPerUser);

            int SizeFor(double budgetSeconds) => floor >= budgetSeconds ? 0 : (int)Math.Min(_max, Math.Floor((budgetSeconds - floor) / perUser));

            var size = SizeFor(_capSeconds * FixedCostShareOfCap);
            if (size == 0) size = SizeFor(_timeoutSeconds * FixedCostShareOfTimeout);
            if (size == 0) return users;

            var largestSeen = _committed.Max(b => b.Users);
            return (int)Math.Min(size, (long)largestSeen * MaxGrowthPerFixedCostStep);
        }

        private void RememberCommitted(Batch batch)
        {
            _committed.Add(batch);
            if (_committed.Count > Window) _committed.RemoveAt(0);
        }

        private void Remember(Batch batch)
        {
            _recent.Add(batch);
            if (_recent.Count > Window) _recent.RemoveAt(0);
        }

        private int Clamp(int size) => Math.Max(_min, Math.Min(_max, size));
    }
}

using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// Three values for one modelled input: what an administrator considers a cautious, a central and an
    /// optimistic figure. All three or none, in that order, so a range is never half a range.
    /// </summary>
    public sealed class CopilotInvestmentRange
    {
        [JsonProperty("conservative")]
        public double? Conservative { get; set; }

        [JsonProperty("base")]
        public double? Base { get; set; }

        [JsonProperty("upside")]
        public double? Upside { get; set; }

        [JsonIgnore]
        public bool IsEmpty => !Conservative.HasValue && !Base.HasValue && !Upside.HasValue;

        [JsonIgnore]
        public bool IsComplete => Conservative.HasValue && Base.HasValue && Upside.HasValue;

        public double? Value(string scenario)
        {
            switch (scenario)
            {
                case CopilotInvestmentScenarios.Conservative: return Conservative;
                case CopilotInvestmentScenarios.Base: return Base;
                case CopilotInvestmentScenarios.Upside: return Upside;
                default: return null;
            }
        }

        public CopilotInvestmentRange Clone() => new CopilotInvestmentRange { Conservative = Conservative, Base = Base, Upside = Upside };
    }

    /// <summary>The three modelled scenarios, as the stable keys every surface uses.</summary>
    public static class CopilotInvestmentScenarios
    {
        public const string Conservative = "conservative";
        public const string Base = "base";
        public const string Upside = "upside";
        public static readonly string[] All = { Conservative, Base, Upside };
    }

    /// <summary>
    /// The financial and productivity assumptions an administrator sets for the Copilot investment report.
    /// </summary>
    /// <remarks>
    /// <para><b>No built-in values.</b> Every field starts empty. The product does not know what this tenant pays
    /// for a seat or a credit, what an hour of its people's time is worth, or how many minutes Copilot gives back,
    /// and a default would be published as if it were the tenant's own figure. A missing input makes the
    /// figures that need it unavailable, and the report says which input is missing.</para>
    /// <para><b>Modelled, never measured.</b> Money and hours computed from these are scenarios over observed
    /// activity. Nothing here is an invoice, a contract price or a measured saving.</para>
    /// </remarks>
    public sealed class CopilotInvestmentAssumptions
    {
        public const double MaxSeatPrice = 10000;
        public const double MaxCreditPrice = 100;
        public const double MaxHourlyCost = 10000;
        public const double MaxMinutesPerActiveDay = 480;
        public const int MaxNoteLength = 500;

        /// <summary>ISO 4217 currency code every price and hourly cost is in, e.g. <c>USD</c>.</summary>
        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>What one Microsoft 365 Copilot seat costs per user per month.</summary>
        [JsonProperty("seatPricePerMonth")]
        public double? SeatPricePerMonth { get; set; }

        /// <summary>What one Copilot credit costs, applied to the preview credit balances.</summary>
        [JsonProperty("creditPrice")]
        public double? CreditPrice { get; set; }

        /// <summary>What one billed Copilot Studio credit costs, applied to the daily Copilot Studio ledger.</summary>
        [JsonProperty("studioCreditPrice")]
        public double? StudioCreditPrice { get; set; }

        /// <summary>The fully loaded cost of one hour of an employee's time.</summary>
        [JsonProperty("hourlyCost")]
        public double? HourlyCost { get; set; }

        /// <summary>Minutes of work Copilot assists on a day a person actually uses it.</summary>
        [JsonProperty("minutesPerActiveDay")]
        public CopilotInvestmentRange MinutesPerActiveDay { get; set; } = new CopilotInvestmentRange();

        /// <summary>The share (0 to 1) of assisted time that turns into business value rather than slack.</summary>
        [JsonProperty("realisation")]
        public CopilotInvestmentRange Realisation { get; set; } = new CopilotInvestmentRange();

        /// <summary>Where the figures came from, in the administrator's words - shown verbatim, never translated.</summary>
        [JsonProperty("note")]
        public string Note { get; set; }

        public CopilotInvestmentAssumptions Clone() => new CopilotInvestmentAssumptions
        {
            Currency = Currency,
            SeatPricePerMonth = SeatPricePerMonth,
            CreditPrice = CreditPrice,
            StudioCreditPrice = StudioCreditPrice,
            HourlyCost = HourlyCost,
            MinutesPerActiveDay = (MinutesPerActiveDay ?? new CopilotInvestmentRange()).Clone(),
            Realisation = (Realisation ?? new CopilotInvestmentRange()).Clone(),
            Note = Note,
        };

        /// <summary>The fields in a stable order, for change history and the methodology export.</summary>
        public IEnumerable<KeyValuePair<string, string>> Fields()
        {
            var minutes = MinutesPerActiveDay ?? new CopilotInvestmentRange();
            var realisation = Realisation ?? new CopilotInvestmentRange();
            yield return Field(CopilotInvestmentFields.Currency, Currency);
            yield return Field(CopilotInvestmentFields.SeatPricePerMonth, Format(SeatPricePerMonth));
            yield return Field(CopilotInvestmentFields.CreditPrice, Format(CreditPrice));
            yield return Field(CopilotInvestmentFields.StudioCreditPrice, Format(StudioCreditPrice));
            yield return Field(CopilotInvestmentFields.HourlyCost, Format(HourlyCost));
            yield return Field(CopilotInvestmentFields.MinutesConservative, Format(minutes.Conservative));
            yield return Field(CopilotInvestmentFields.MinutesBase, Format(minutes.Base));
            yield return Field(CopilotInvestmentFields.MinutesUpside, Format(minutes.Upside));
            yield return Field(CopilotInvestmentFields.RealisationConservative, Format(realisation.Conservative));
            yield return Field(CopilotInvestmentFields.RealisationBase, Format(realisation.Base));
            yield return Field(CopilotInvestmentFields.RealisationUpside, Format(realisation.Upside));
            yield return Field(CopilotInvestmentFields.Note, Note);
        }

        private static KeyValuePair<string, string> Field(string name, string value) => new KeyValuePair<string, string>(name, value);

        internal static string Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Normalises text fields (trims, upper-cases the currency) without changing any number.</summary>
        public CopilotInvestmentAssumptions Normalised()
        {
            var copy = Clone();
            copy.Currency = string.IsNullOrWhiteSpace(copy.Currency) ? null : copy.Currency.Trim().ToUpperInvariant();
            copy.Note = string.IsNullOrWhiteSpace(copy.Note) ? null : copy.Note.Trim();
            return copy;
        }

        /// <summary>Every rule the values break, as stable codes the portal translates. Empty when valid.</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (Currency != null && !Regex.IsMatch(Currency, "^[A-Z]{3}$"))
                errors.Add(CopilotInvestmentErrorCodes.CurrencyInvalid);
            CheckPositive(SeatPricePerMonth, MaxSeatPrice, CopilotInvestmentErrorCodes.SeatPriceOutOfRange, errors);
            CheckPositive(CreditPrice, MaxCreditPrice, CopilotInvestmentErrorCodes.CreditPriceOutOfRange, errors);
            CheckPositive(StudioCreditPrice, MaxCreditPrice, CopilotInvestmentErrorCodes.StudioCreditPriceOutOfRange, errors);
            CheckPositive(HourlyCost, MaxHourlyCost, CopilotInvestmentErrorCodes.HourlyCostOutOfRange, errors);
            if ((SeatPricePerMonth.HasValue || CreditPrice.HasValue || StudioCreditPrice.HasValue || HourlyCost.HasValue) && Currency == null)
                errors.Add(CopilotInvestmentErrorCodes.CurrencyRequired);
            CheckRange(MinutesPerActiveDay, 0, MaxMinutesPerActiveDay, CopilotInvestmentErrorCodes.MinutesOutOfRange,
                CopilotInvestmentErrorCodes.MinutesIncomplete, CopilotInvestmentErrorCodes.MinutesOrder, errors);
            CheckRange(Realisation, 0, 1, CopilotInvestmentErrorCodes.RealisationOutOfRange,
                CopilotInvestmentErrorCodes.RealisationIncomplete, CopilotInvestmentErrorCodes.RealisationOrder, errors);
            if (Note != null && Note.Length > MaxNoteLength) errors.Add(CopilotInvestmentErrorCodes.NoteTooLong);
            return errors;
        }

        private static void CheckPositive(double? value, double max, string code, List<string> errors)
        {
            if (!value.HasValue) return;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value <= 0 || value.Value > max) errors.Add(code);
        }

        private static void CheckRange(CopilotInvestmentRange range, double min, double max, string outOfRange,
            string incomplete, string order, List<string> errors)
        {
            if (range == null || range.IsEmpty) return;
            if (!range.IsComplete)
            {
                errors.Add(incomplete);
                return;
            }
            var values = new[] { range.Conservative.Value, range.Base.Value, range.Upside.Value };
            if (values.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max))
            {
                errors.Add(outOfRange);
                return;
            }
            if (range.Conservative.Value > range.Base.Value || range.Base.Value > range.Upside.Value) errors.Add(order);
        }
    }

    /// <summary>Stable field names, used by the change history, the provenance list and the missing-input list.</summary>
    public static class CopilotInvestmentFields
    {
        public const string Currency = "currency";
        public const string SeatPricePerMonth = "seatPricePerMonth";
        public const string CreditPrice = "creditPrice";
        public const string StudioCreditPrice = "studioCreditPrice";
        public const string HourlyCost = "hourlyCost";
        public const string MinutesConservative = "minutesPerActiveDay.conservative";
        public const string MinutesBase = "minutesPerActiveDay.base";
        public const string MinutesUpside = "minutesPerActiveDay.upside";
        public const string RealisationConservative = "realisation.conservative";
        public const string RealisationBase = "realisation.base";
        public const string RealisationUpside = "realisation.upside";
        public const string Note = "note";

        /// <summary>Every modelled input, in the order the portal lists them.</summary>
        public static readonly string[] Inputs =
        {
            Currency, SeatPricePerMonth, CreditPrice, StudioCreditPrice, HourlyCost,
            MinutesConservative, MinutesBase, MinutesUpside, RealisationConservative, RealisationBase, RealisationUpside,
        };

        /// <summary>The inputs a modelled return on the seat investment needs.</summary>
        public static readonly string[] RequiredForValue =
        {
            Currency, SeatPricePerMonth, HourlyCost, MinutesConservative, MinutesBase, MinutesUpside,
            RealisationConservative, RealisationBase, RealisationUpside,
        };
    }

    /// <summary>Stable codes for a refused save or an invalid scenario. The portal translates every one.</summary>
    public static class CopilotInvestmentErrorCodes
    {
        public const string InvalidRequest = "invalidRequest";
        public const string VersionConflict = "versionConflict";
        public const string StorageNotConfigured = "storageNotConfigured";
        public const string StateUnavailable = "stateUnavailable";
        public const string CurrencyInvalid = "currencyInvalid";
        public const string CurrencyRequired = "currencyRequired";
        public const string SeatPriceOutOfRange = "seatPriceOutOfRange";
        public const string CreditPriceOutOfRange = "creditPriceOutOfRange";
        public const string StudioCreditPriceOutOfRange = "studioCreditPriceOutOfRange";
        public const string HourlyCostOutOfRange = "hourlyCostOutOfRange";
        public const string MinutesOutOfRange = "minutesOutOfRange";
        public const string MinutesIncomplete = "minutesIncomplete";
        public const string MinutesOrder = "minutesOrder";
        public const string RealisationOutOfRange = "realisationOutOfRange";
        public const string RealisationIncomplete = "realisationIncomplete";
        public const string RealisationOrder = "realisationOrder";
        public const string NoteTooLong = "noteTooLong";
        public const string ScenarioInvalid = "scenarioInvalid";

        /// <summary>Every code a save can be refused with, for the portal's translation test.</summary>
        public static readonly string[] All =
        {
            InvalidRequest, VersionConflict, StorageNotConfigured, StateUnavailable, CurrencyInvalid, CurrencyRequired,
            SeatPriceOutOfRange, CreditPriceOutOfRange, StudioCreditPriceOutOfRange, HourlyCostOutOfRange,
            MinutesOutOfRange, MinutesIncomplete, MinutesOrder, RealisationOutOfRange, RealisationIncomplete,
            RealisationOrder, NoteTooLong, ScenarioInvalid,
        };
    }

    public sealed class CopilotInvestmentFieldChange
    {
        [JsonProperty("field")]
        public string Field { get; set; }

        [JsonProperty("oldValue")]
        public string OldValue { get; set; }

        [JsonProperty("newValue")]
        public string NewValue { get; set; }
    }

    public sealed class CopilotInvestmentAssumptionsChange
    {
        [JsonProperty("version")]
        public long Version { get; set; }

        [JsonProperty("changedBy")]
        public string ChangedBy { get; set; }

        [JsonProperty("changedUtc")]
        public DateTime ChangedUtc { get; set; }

        [JsonProperty("changes")]
        public List<CopilotInvestmentFieldChange> Changes { get; set; } = new List<CopilotInvestmentFieldChange>();
    }

    /// <summary>The stored row: the assumptions in force, who set them and the last changes.</summary>
    public sealed class CopilotInvestmentAssumptionsDocument
    {
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("version")]
        public long Version { get; set; }

        [JsonProperty("assumptions")]
        public CopilotInvestmentAssumptions Assumptions { get; set; } = new CopilotInvestmentAssumptions();

        [JsonProperty("updatedBy")]
        public string UpdatedBy { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime? UpdatedUtc { get; set; }

        [JsonProperty("history")]
        public List<CopilotInvestmentAssumptionsChange> History { get; set; } = new List<CopilotInvestmentAssumptionsChange>();
    }

    /// <summary>The assumptions could not be read or written. Never answered with invented values.</summary>
    public sealed class CopilotInvestmentAssumptionsUnavailableException : Exception
    {
        public CopilotInvestmentAssumptionsUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>A save refused for a reason the administrator can act on. <see cref="Code"/> is stable.</summary>
    public sealed class CopilotInvestmentAssumptionsRejectedException : Exception
    {
        public CopilotInvestmentAssumptionsRejectedException(string code, IReadOnlyList<string> validationErrors = null) : base(code)
        {
            Code = code;
            ValidationErrors = validationErrors ?? new List<string>();
        }

        public string Code { get; }
        public IReadOnlyList<string> ValidationErrors { get; }
    }

    /// <summary>
    /// Keeps the assumptions in the <see cref="StatePartitions.CopilotInvestmentAssumptions"/> partition of the
    /// AnalyticsState table: one row, read with one point lookup, written conditionally on the ETag it was read
    /// with so two administrators saving against the same version cannot overwrite each other.
    /// </summary>
    /// <remarks>
    /// A store that is not durable (no Storage connection string) answers reads with an empty document - there
    /// is nothing to read, so every modelled figure says which input is missing - and refuses saves, because a
    /// save that vanished on the next restart would publish figures nobody can reproduce.
    /// </remarks>
    public sealed class CopilotInvestmentAssumptionsStore
    {
        public const string DocumentKey = "Current";
        public const int MaxHistory = 50;
        public static readonly TimeSpan DefaultStorageTimeout = TimeSpan.FromSeconds(10);
        private const int MaxNameLength = 256;

        private readonly IKeyValueStore _values;
        private readonly IConditionalKeyValueStore _conditional;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _storageTimeout;

        public CopilotInvestmentAssumptionsStore(IKeyValueStore values, bool isDurable, Func<DateTime> utcNow = null, TimeSpan? storageTimeout = null)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            _conditional = values as IConditionalKeyValueStore;
            if (isDurable && _conditional == null)
                throw new ArgumentException("A durable assumptions store needs conditional writes.", nameof(values));
            IsDurable = isDurable;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _storageTimeout = storageTimeout ?? DefaultStorageTimeout;
            if (_storageTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(storageTimeout));
        }

        public bool IsDurable { get; }

        public Task<CopilotInvestmentAssumptionsDocument> GetAsync(CancellationToken cancellationToken = default) =>
            WithDeadlineAsync(async token => (await ReadAsync(token).ConfigureAwait(false)).Document, cancellationToken);

        public Task<CopilotInvestmentAssumptionsDocument> SaveAsync(
            CopilotInvestmentAssumptions assumptions, long expectedVersion, string changedBy, CancellationToken cancellationToken = default)
        {
            if (assumptions == null) throw new CopilotInvestmentAssumptionsRejectedException(CopilotInvestmentErrorCodes.InvalidRequest);
            var normalised = assumptions.Normalised();
            var errors = normalised.Validate();
            if (errors.Count > 0) throw new CopilotInvestmentAssumptionsRejectedException(errors[0], errors);
            return WithDeadlineAsync(token => WriteAsync(normalised, expectedVersion, changedBy, token), cancellationToken);
        }

        private async Task<(CopilotInvestmentAssumptionsDocument Document, string VersionToken)> ReadAsync(CancellationToken cancellationToken)
        {
            string json, token = null;
            try
            {
                if (_conditional != null)
                {
                    var read = await _conditional.GetVersionedAsync(DocumentKey, cancellationToken).ConfigureAwait(false);
                    json = read.Value;
                    token = read.VersionToken;
                }
                else
                {
                    json = await _values.GetStringAsync(DocumentKey, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new CopilotInvestmentAssumptionsUnavailableException(
                    $"Couldn't read the Copilot investment assumptions from {_values.Description}.", ex);
            }

            if (json == null) return (new CopilotInvestmentAssumptionsDocument(), token);

            CopilotInvestmentAssumptionsDocument document;
            try
            {
                document = JsonConvert.DeserializeObject<CopilotInvestmentAssumptionsDocument>(json,
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
            }
            catch (JsonException ex)
            {
                throw new CopilotInvestmentAssumptionsUnavailableException("The stored Copilot investment assumptions are not readable JSON.", ex);
            }

            // Reported, not replaced: quietly dropping an unreadable document would publish figures with
            // inputs nobody chose.
            if (document?.Assumptions == null || document.SchemaVersion < 1
                || document.SchemaVersion > CopilotInvestmentAssumptionsDocument.CurrentSchemaVersion || document.Version < 0)
                throw new CopilotInvestmentAssumptionsUnavailableException("The stored Copilot investment assumptions have an unsupported shape.");
            document.Assumptions.MinutesPerActiveDay = document.Assumptions.MinutesPerActiveDay ?? new CopilotInvestmentRange();
            document.Assumptions.Realisation = document.Assumptions.Realisation ?? new CopilotInvestmentRange();
            var errors = document.Assumptions.Validate();
            if (errors.Count > 0)
                throw new CopilotInvestmentAssumptionsUnavailableException("The stored Copilot investment assumptions are invalid: " + string.Join(", ", errors));
            document.History = document.History ?? new List<CopilotInvestmentAssumptionsChange>();
            return (document, token);
        }

        private async Task<CopilotInvestmentAssumptionsDocument> WriteAsync(
            CopilotInvestmentAssumptions assumptions, long expectedVersion, string changedBy, CancellationToken cancellationToken)
        {
            if (!IsDurable) throw new CopilotInvestmentAssumptionsRejectedException(CopilotInvestmentErrorCodes.StorageNotConfigured);

            var (current, versionToken) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Version != expectedVersion)
                throw new CopilotInvestmentAssumptionsRejectedException(CopilotInvestmentErrorCodes.VersionConflict);

            var changes = current.Assumptions.Fields().Zip(assumptions.Fields(), (was, proposed) => new { was, proposed })
                .Where(p => !string.Equals(p.was.Value, p.proposed.Value, StringComparison.Ordinal))
                .Select(p => new CopilotInvestmentFieldChange { Field = p.proposed.Key, OldValue = p.was.Value, NewValue = p.proposed.Value })
                .ToList();
            if (changes.Count == 0) return current;

            var who = string.IsNullOrWhiteSpace(changedBy) ? null : changedBy.Trim();
            if (who != null && who.Length > MaxNameLength) who = who.Substring(0, MaxNameLength);
            var now = _utcNow();
            var next = new CopilotInvestmentAssumptionsDocument
            {
                Version = current.Version + 1,
                Assumptions = assumptions,
                UpdatedBy = who,
                UpdatedUtc = now,
                History = new[]
                {
                    new CopilotInvestmentAssumptionsChange { Version = current.Version + 1, ChangedBy = who, ChangedUtc = now, Changes = changes },
                }.Concat(current.History).Take(MaxHistory).ToList(),
            };

            bool written;
            try
            {
                written = await _conditional.TrySetStringAsync(DocumentKey, JsonConvert.SerializeObject(next), versionToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new CopilotInvestmentAssumptionsUnavailableException(
                    $"Couldn't save the Copilot investment assumptions to {_values.Description}.", ex);
            }

            if (!written) throw new CopilotInvestmentAssumptionsRejectedException(CopilotInvestmentErrorCodes.VersionConflict);
            return next;
        }

        private async Task<T> WithDeadlineAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller))
            {
                deadline.CancelAfter(_storageTimeout);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (deadline.Token.Register(() => cancelled.TrySetResult(true)))
                {
                    try
                    {
                        var task = operation(deadline.Token);
                        if (!task.IsCompleted && await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                        {
                            _ = task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            deadline.Token.ThrowIfCancellationRequested();
                        }
                        var result = await task.ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        return result;
                    }
                    catch (Exception ex) when (caller.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("The assumptions request was cancelled.", ex, caller);
                    }
                    catch (CopilotInvestmentAssumptionsRejectedException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (deadline.IsCancellationRequested)
                    {
                        throw new CopilotInvestmentAssumptionsUnavailableException("The assumptions storage deadline expired.",
                            new TimeoutException("The assumptions storage operation timed out.", ex));
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new CopilotInvestmentAssumptionsUnavailableException("The assumptions storage operation was cancelled.", ex);
                    }
                }
            }
        }
    }

    /// <summary>
    /// A reader's own what-if figures, sent with a request and never stored. Each supplied field replaces the
    /// tenant default for that request only; the response says which figures came from where.
    /// </summary>
    public sealed class CopilotInvestmentScenario
    {
        public string Currency { get; set; }
        public double? SeatPricePerMonth { get; set; }
        public double? CreditPrice { get; set; }
        public double? StudioCreditPrice { get; set; }
        public double? HourlyCost { get; set; }
        public double? MinutesConservative { get; set; }
        public double? MinutesBase { get; set; }
        public double? MinutesUpside { get; set; }
        public double? RealisationConservative { get; set; }
        public double? RealisationBase { get; set; }
        public double? RealisationUpside { get; set; }

        public bool Any =>
            Currency != null || SeatPricePerMonth.HasValue || CreditPrice.HasValue || StudioCreditPrice.HasValue || HourlyCost.HasValue
            || MinutesConservative.HasValue || MinutesBase.HasValue || MinutesUpside.HasValue
            || RealisationConservative.HasValue || RealisationBase.HasValue || RealisationUpside.HasValue;

        /// <summary>A short, stable identity for result caches.</summary>
        public string Key => !Any ? "none" : string.Join("|", new[]
        {
            Currency ?? "-", F(SeatPricePerMonth), F(CreditPrice), F(StudioCreditPrice), F(HourlyCost),
            F(MinutesConservative), F(MinutesBase), F(MinutesUpside),
            F(RealisationConservative), F(RealisationBase), F(RealisationUpside),
        });

        private static string F(double? value) => CopilotInvestmentAssumptions.Format(value) ?? "-";

        /// <summary>
        /// The tenant defaults with this scenario applied, and which fields each came from. Throws
        /// <see cref="CopilotInvestmentAssumptionsRejectedException"/> with <see cref="CopilotInvestmentErrorCodes.ScenarioInvalid"/>
        /// when the combination breaks a rule, so a hand-edited link cannot publish an impossible model.
        /// </summary>
        public CopilotInvestmentAssumptions ApplyTo(CopilotInvestmentAssumptions tenant, IDictionary<string, string> provenance)
        {
            var result = (tenant ?? new CopilotInvestmentAssumptions()).Clone();
            var minutes = result.MinutesPerActiveDay;
            var realisation = result.Realisation;
            Set(CopilotInvestmentFields.Currency, Currency == null ? null : Currency.Trim().ToUpperInvariant(), v => result.Currency = v, result.Currency, provenance);
            Set(CopilotInvestmentFields.SeatPricePerMonth, SeatPricePerMonth, v => result.SeatPricePerMonth = v, result.SeatPricePerMonth, provenance);
            Set(CopilotInvestmentFields.CreditPrice, CreditPrice, v => result.CreditPrice = v, result.CreditPrice, provenance);
            Set(CopilotInvestmentFields.StudioCreditPrice, StudioCreditPrice, v => result.StudioCreditPrice = v, result.StudioCreditPrice, provenance);
            Set(CopilotInvestmentFields.HourlyCost, HourlyCost, v => result.HourlyCost = v, result.HourlyCost, provenance);
            Set(CopilotInvestmentFields.MinutesConservative, MinutesConservative, v => minutes.Conservative = v, minutes.Conservative, provenance);
            Set(CopilotInvestmentFields.MinutesBase, MinutesBase, v => minutes.Base = v, minutes.Base, provenance);
            Set(CopilotInvestmentFields.MinutesUpside, MinutesUpside, v => minutes.Upside = v, minutes.Upside, provenance);
            Set(CopilotInvestmentFields.RealisationConservative, RealisationConservative, v => realisation.Conservative = v, realisation.Conservative, provenance);
            Set(CopilotInvestmentFields.RealisationBase, RealisationBase, v => realisation.Base = v, realisation.Base, provenance);
            Set(CopilotInvestmentFields.RealisationUpside, RealisationUpside, v => realisation.Upside = v, realisation.Upside, provenance);
            var errors = result.Validate();
            if (errors.Count > 0) throw new CopilotInvestmentAssumptionsRejectedException(CopilotInvestmentErrorCodes.ScenarioInvalid, errors);
            return result;
        }

        private static void Set<T>(string field, T scenario, Action<T> assign, T tenant, IDictionary<string, string> provenance)
        {
            if (scenario != null)
            {
                assign(scenario);
                if (provenance != null) provenance[field] = CopilotInvestmentProvenance.Scenario;
            }
            else if (provenance != null)
            {
                provenance[field] = tenant == null ? CopilotInvestmentProvenance.Missing : CopilotInvestmentProvenance.Tenant;
            }
        }
    }

    /// <summary>Where one modelled input came from.</summary>
    public static class CopilotInvestmentProvenance
    {
        public const string Tenant = "tenant";
        public const string Scenario = "scenario";
        public const string Missing = "missing";
    }
}

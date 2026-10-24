using System;
using System.Collections.Generic;

namespace DataUtils
{
    /// <summary>
    /// A command line that refuses what it does not understand. Hosts used to scan their arguments for the switches
    /// they knew and ignore the rest, so a mistyped debug switch silently ran the normal endless import.
    /// </summary>
    public sealed class StrictArguments
    {
        private readonly Dictionary<string, string> _given = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Why the arguments were refused; null when they are acceptable.</summary>
        public string Error { get; private set; }

        public bool IsValid => Error == null;

        public bool Has(string name) => _given.ContainsKey(name);

        /// <summary>The value given to an option that takes one; null when the option was not given.</summary>
        public string ValueOf(string name) => _given.TryGetValue(name, out var value) ? value : null;

        /// <param name="args">The process arguments.</param>
        /// <param name="flags">Options that stand alone.</param>
        /// <param name="valueOptions">Options that are followed by exactly one value.</param>
        public static StrictArguments Parse(string[] args, IEnumerable<string> flags, IEnumerable<string> valueOptions)
        {
            var result = new StrictArguments();
            var flagSet = new HashSet<string>(flags ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var valueSet = new HashSet<string>(valueOptions ?? new string[0], StringComparer.OrdinalIgnoreCase);
            args = args ?? new string[0];

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i] ?? string.Empty;
                if (!flagSet.Contains(arg) && !valueSet.Contains(arg))
                {
                    result.Error = $"Unrecognised argument '{arg}'.";
                    return result;
                }
                if (result._given.ContainsKey(arg))
                {
                    result.Error = $"'{arg}' was given more than once.";
                    return result;
                }

                if (flagSet.Contains(arg))
                {
                    result._given[arg] = null;
                    continue;
                }

                var value = i + 1 < args.Length ? args[i + 1] : null;
                if (string.IsNullOrWhiteSpace(value) || (flagSet.Contains(value) || valueSet.Contains(value)))
                {
                    result.Error = $"'{arg}' needs a value.";
                    return result;
                }
                result._given[arg] = value;
                i++;
            }

            return result;
        }
    }
}

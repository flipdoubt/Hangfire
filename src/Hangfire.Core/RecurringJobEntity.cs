// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cronos;
using Hangfire.Annotations;
using Hangfire.Common;
using Hangfire.Storage;

namespace Hangfire
{
    internal class RecurringJobEntity
    {
        private readonly IList<Exception> _errors = new List<Exception>();
        private readonly IDictionary<string, string> _recurringJob;
        private readonly DateTime _now;

        public RecurringJobEntity(
            [NotNull] string recurringJobId,
            [NotNull] IDictionary<string, string> recurringJob,
            [NotNull] ITimeZoneResolver timeZoneResolver,
            DateTime now)
        {
            if (timeZoneResolver == null) throw new ArgumentNullException(nameof(timeZoneResolver));

            _recurringJob = recurringJob ?? throw new ArgumentNullException(nameof(recurringJob));
            _now = now;

            RecurringJobId = recurringJobId ?? throw new ArgumentNullException(nameof(recurringJobId));

            if (recurringJob.ContainsKey("Queue") && !String.IsNullOrWhiteSpace(recurringJob["Queue"]))
            {
                Queue = recurringJob["Queue"];
            }

            try
            {
                TimeZone = recurringJob.ContainsKey("TimeZoneId") && !String.IsNullOrWhiteSpace(recurringJob["TimeZoneId"])
                    ? timeZoneResolver.GetTimeZoneById(recurringJob["TimeZoneId"])
                    : TimeZoneInfo.Utc;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                _errors.Add(ex);
            }

            if (recurringJob.ContainsKey("Cron") && !String.IsNullOrWhiteSpace(recurringJob["Cron"]))
            {
                Cron = recurringJob["Cron"];
            }

            try
            {
                if (!recurringJob.ContainsKey("Job") || String.IsNullOrWhiteSpace(recurringJob["Job"]))
                {
                    throw new InvalidOperationException("The 'Job' field has a null or empty value");
                }

                Job = InvocationData.DeserializePayload(recurringJob["Job"]).DeserializeJob();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                _errors.Add(ex);
            }

            if (recurringJob.ContainsKey("LastJobId") && !String.IsNullOrWhiteSpace(recurringJob["LastJobId"]))
            {
                LastJobId = recurringJob["LastJobId"];
            }

            if (recurringJob.ContainsKey("LastExecution") && !String.IsNullOrWhiteSpace(recurringJob["LastExecution"]))
            {
                LastExecution = JobHelper.DeserializeDateTime(recurringJob["LastExecution"]);
            }

            if (recurringJob.ContainsKey("NextExecution") && !String.IsNullOrWhiteSpace(recurringJob["NextExecution"]))
            {
                NextExecution = JobHelper.DeserializeDateTime(recurringJob["NextExecution"]);
            }

            if (recurringJob.ContainsKey("CreatedAt") && !String.IsNullOrWhiteSpace(recurringJob["CreatedAt"]))
            {
                CreatedAt = JobHelper.DeserializeDateTime(recurringJob["CreatedAt"]);
            }
            else
            {
                CreatedAt = now;
            }

            if (recurringJob.TryGetValue("Misfire", out var misfireStr))
            {
                MisfireHandling = (MisfireHandlingMode)Enum.Parse(typeof(MisfireHandlingMode), misfireStr);
                if (!Enum.IsDefined(typeof(MisfireHandlingMode), MisfireHandling))
                {
                    throw new NotSupportedException(String.Format("Misfire option '{0}' is not supported.", (int)MisfireHandling));
                }
            }
            else
            {
                MisfireHandling = MisfireHandlingMode.Relaxed;
            }

            if (recurringJob.ContainsKey("V") && !String.IsNullOrWhiteSpace(recurringJob["V"]))
            {
                Version = int.Parse(recurringJob["V"], CultureInfo.InvariantCulture);
            }

            if (recurringJob.TryGetValue("RetryAttempt", out var attemptString) &&
                int.TryParse(attemptString, out var retryAttempt))
            {
                RetryAttempt = retryAttempt;
            }
        }

        public string RecurringJobId { get; }

        public string Queue { get; set; }
        public string Cron { get; set; }
        public TimeZoneInfo TimeZone { get; set; }
        public Job Job { get; set; }
        public MisfireHandlingMode MisfireHandling { get; set; }

        public DateTime CreatedAt { get; }
        public DateTime? NextExecution { get; private set; }

        public DateTime? LastExecution { get; set; }
        public string LastJobId { get; set; }
        public int? Version { get; set; }
        public int RetryAttempt { get; set; }

        public Exception[] Errors => _errors.ToArray();

        public IEnumerable<DateTime> TrySchedule(DateTime now, TimeSpan precision, out Exception error)
        {
            if (_errors.Count > 0)
            {
                error = _errors.Count == 1 ? _errors[0] : new AggregateException(_errors);
                return Enumerable.Empty<DateTime>();
            }

            var result = new List<DateTime>();
            DateTime? nextExecution = null;

            while (TryGetNextExecution(nextExecution, out nextExecution, out error))
            {
                if (nextExecution == null || nextExecution > now) break;
                if (nextExecution == now)
                {
                    result.Add(nextExecution.Value);
                }
                else
                {
                    switch (MisfireHandling)
                    {
                        case MisfireHandlingMode.Relaxed:
                            nextExecution = now;
                            result.Add(nextExecution.Value);
                            break;
                        case MisfireHandlingMode.Strict:
                            result.Add(nextExecution.Value);
                            break;
                        case MisfireHandlingMode.Ignorable:
                            if (now.Add(precision.Negate()) <= nextExecution && nextExecution <= now)
                            {
                                result.Add(nextExecution.Value);
                            }

                            break;
                    }
                }
            }

            NextExecution = nextExecution;
            return result;
        }

        public bool IsChanged(out IReadOnlyDictionary<string, string> changedFields, out DateTime? nextExecution)
        {
            changedFields = GetChangedFields(out nextExecution);
            return changedFields.Count > 0 || nextExecution != NextExecution;
        }

        public void ScheduleRetry(TimeSpan delay, string error, out IReadOnlyDictionary<string, string> changedFields, out DateTime? nextExecution)
        {
            RetryAttempt++;
            nextExecution = _now.Add(delay);

            var result = new Dictionary<string, string>
            {
                { "RetryAttempt", RetryAttempt.ToString(CultureInfo.InvariantCulture) },
                { "Error", error ?? String.Empty }
            };
            
            if (!_recurringJob.ContainsKey("V"))
            {
                result.Add("V", "2");
            }

            changedFields = result;
        }

        public void Disable(string error, out IReadOnlyDictionary<string, string> changedFields, out DateTime? nextExecution)
        {
            nextExecution = null;

            var result = new Dictionary<string, string>
            {
                { "NextExecution", String.Empty },
                { "Error", error ?? String.Empty }
            };

            if (!_recurringJob.ContainsKey("V"))
            {
                result.Add("V", "2");
            }

            changedFields = result;
        }

        private IReadOnlyDictionary<string, string> GetChangedFields(out DateTime? nextExecution)
        {
            var result = new Dictionary<string, string>();

            if ((_recurringJob.ContainsKey("Queue") ? _recurringJob["Queue"] : null) != Queue)
            {
                result.Add("Queue", Queue);
            }

            if ((_recurringJob.ContainsKey("Cron") ? _recurringJob["Cron"] : null) != Cron)
            {
                result.Add("Cron", Cron);
            }

            if ((_recurringJob.ContainsKey("TimeZoneId") ? _recurringJob["TimeZoneId"] : null) != TimeZone.Id)
            {
                result.Add("TimeZoneId", TimeZone.Id);
            }

            var serializedJob = InvocationData.SerializeJob(Job).SerializePayload();

            if ((_recurringJob.ContainsKey("Job") ? _recurringJob["Job"] : null) != serializedJob)
            {
                result.Add("Job", serializedJob);
            }

            var serializedCreatedAt = JobHelper.SerializeDateTime(CreatedAt);

            if ((_recurringJob.ContainsKey("CreatedAt") ? _recurringJob["CreatedAt"] : null) != serializedCreatedAt)
            {
                result.Add("CreatedAt", serializedCreatedAt);
            }

            var serializedLastExecution = LastExecution.HasValue ? JobHelper.SerializeDateTime(LastExecution.Value) : null;

            if ((_recurringJob.ContainsKey("LastExecution") ? _recurringJob["LastExecution"] : null) !=
                serializedLastExecution)
            {
                result.Add("LastExecution", serializedLastExecution ?? String.Empty);
            }

            var timeZoneChanged = !TimeZone.Id.Equals(_recurringJob.ContainsKey("TimeZoneId")
                ? _recurringJob["TimeZoneId"]
                : TimeZoneInfo.Utc.Id);

            var serializedNextExecution = NextExecution.HasValue ? JobHelper.SerializeDateTime(NextExecution.Value) : null;
            if (serializedNextExecution != null &&
                (_recurringJob.ContainsKey("NextExecution") ? _recurringJob["NextExecution"] : null) !=
                serializedNextExecution)
            {
                result.Add("NextExecution", serializedNextExecution ?? String.Empty);
                nextExecution = NextExecution;
            }
            else
            {
                TryGetNextExecution(result.ContainsKey("Cron") || timeZoneChanged ? _now.AddSeconds(-1) : (DateTime?)null, out nextExecution, out _);
                serializedNextExecution = nextExecution.HasValue ? JobHelper.SerializeDateTime(nextExecution.Value) : null;

                if ((_recurringJob.ContainsKey("NextExecution") ? _recurringJob["NextExecution"] : null) !=
                    serializedNextExecution)
                {
                    result.Add("NextExecution", serializedNextExecution ?? String.Empty);
                }
            }

            if ((_recurringJob.ContainsKey("LastJobId") ? _recurringJob["LastJobId"] : null) != LastJobId)
            {
                result.Add("LastJobId", LastJobId ?? String.Empty);
            }

            var misfireHandlingValue = MisfireHandling.ToString("D");
            if ((!_recurringJob.ContainsKey("Misfire") && MisfireHandling != MisfireHandlingMode.Relaxed) ||
                (_recurringJob.ContainsKey("Misfire") && _recurringJob["Misfire"] != misfireHandlingValue))
            {
                result.Add("Misfire", misfireHandlingValue);
            }

            if (!_recurringJob.ContainsKey("V"))
            {
                result.Add("V", "2");
            }

            if (_recurringJob.ContainsKey("Error") && !String.IsNullOrEmpty(_recurringJob["Error"]))
            {
                result.Add("Error", String.Empty);
            }

            if (_recurringJob.ContainsKey("RetryAttempt") && _recurringJob["RetryAttempt"] != "0")
            {
                result.Add("RetryAttempt", "0");
            }

            return result;
        }

        public override string ToString()
        {
            return String.Join(";", _recurringJob.Select(x => $"{x.Key}:{x.Value}"));
        }

        public static CronExpression ParseCronExpression([NotNull] string cronExpression)
        {
            if (cronExpression == null) throw new ArgumentNullException(nameof(cronExpression));

            var parts = cronExpression.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var format = CronFormat.Standard;

            if (parts.Length == 6)
            {
                format |= CronFormat.IncludeSeconds;
            }
            else if (parts.Length != 5)
            {
                throw new CronFormatException(
                    $"Wrong number of parts in the `{cronExpression}` cron expression, you can only use 5 or 6 (with seconds) part-based expressions.");
            }

            return CronExpression.Parse(cronExpression, format);
        }

        private bool TryGetNextExecution(DateTime? from, out DateTime? nextExecution, out Exception exception)
        {
            try
            {
                nextExecution = ParseCronExpression(Cron).GetNextOccurrence(
                    from ?? (LastExecution ?? CreatedAt.AddSeconds(-1)),
                    TimeZone,
                    inclusive: false);

                exception = null;
                return true;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                exception = ex;
                nextExecution = null;
                return false;
            }
        }
    }
}
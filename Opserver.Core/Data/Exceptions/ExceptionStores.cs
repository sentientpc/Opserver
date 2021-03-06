﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Profiling;
using StackExchange.Exceptional;

namespace StackExchange.Opserver.Data.Exceptions
{
    public static class ExceptionStores
    {
        private static readonly object _updateLock = new object();

        public static HashSet<string> KnownApplications { get; } =
            GetConfiguredApplicationGroups().SelectMany(g => g.Applications.Select(a => a.Name)).ToHashSet();

        public static List<Application> Applications { get; private set; } = new List<Application>();
        private static ApplicationGroup CatchAll { get; set; }
        public static List<ApplicationGroup> ApplicationGroups { get; private set; } = GetConfiguredApplicationGroups();

        public static int TotalExceptionCount => Applications?.Sum(a => a.ExceptionCount) ?? 0;
        public static int TotalRecentExceptionCount => Applications?.Sum(a => a.RecentExceptionCount) ?? 0;

        public static MonitorStatus MonitorStatus
        {
            get
            {
                var settings = Current.Settings.Exceptions;
                int total = TotalExceptionCount,
                    recent = TotalRecentExceptionCount;

                if (ExceptionsModule.Stores.Any(s => s.MonitorStatus == MonitorStatus.Critical))
                    return MonitorStatus.Critical;

                if (settings.CriticalCount.HasValue && total > settings.CriticalCount)
                    return MonitorStatus.Critical;
                if (settings.CriticalRecentCount.HasValue && recent > settings.CriticalRecentCount)
                    return MonitorStatus.Critical;

                if (settings.WarningCount.HasValue && total > settings.WarningCount)
                    return MonitorStatus.Warning;
                if (settings.WarningRecentCount.HasValue && recent > settings.WarningRecentCount)
                    return MonitorStatus.Warning;

                return MonitorStatus.Good;
            }
        }

        private static List<ApplicationGroup> GetConfiguredApplicationGroups()
        {
            var settings = Current.Settings.Exceptions;
            if (settings.Groups?.Any() ?? false)
            {
                var configured = settings.Groups
                    .Select(g => new ApplicationGroup
                    {
                        Name = g.Name,
                        Applications = g.Applications.OrderBy(a => a).Select(a => new Application {Name = a}).ToList()
                    }).ToList();
                // The user could have configured an "Other", don't duplicate it
                if (configured.All(g => g.Name != "Other"))
                {
                    configured.Add(new ApplicationGroup {Name = "Other"});
                }
                CatchAll = configured.First(g => g.Name == "Other");
                return configured;
            }
            // One big bucket if nothing is configured
            CatchAll = new ApplicationGroup {Name = "All"};
            if (settings.Applications?.Any() ?? false)
            {
                CatchAll.Applications = settings.Applications.OrderBy(a => a).Select(a => new Application {Name = a}).ToList();
            }
            return new List<ApplicationGroup>
            {
                CatchAll
            };
        }

        public static IEnumerable<string> GetAppNames(string group, string app)
        {
            if (app.HasValue())
            {
                yield return app;
                yield break;
            }

            if (group.IsNullOrEmpty()) yield break;

            var apps = ApplicationGroups?.FirstOrDefault(g => g.Name == group)?.Applications;
            if (apps != null)
            {
                foreach (var a in apps)
                {
                    yield return a.Name;
                }
            }
        }

        public static async Task<Error> GetError(string app, Guid guid)
        {
            var stores = GetStores(app);
            var result = await Task.WhenAll(stores.Select(s => s.GetErrorAsync(guid))).ConfigureAwait(false);
            return Array.Find(result, e => e != null);
        }

        public static async Task<T> ActionAsync<T>(string appName, Func<ExceptionStore, Task<T>> action)
        {
            var result = default(T);
            List<Task> toPoll = new List<Task>();
            foreach (var s in GetApps(appName).Select(a => a.Store).Distinct())
            {
                if (s == null)
                {
                    continue;
                }
                var aResult = await action(s).ConfigureAwait(false);
                if (typeof(T) == typeof(bool) && Convert.ToBoolean(aResult))
                {
                    result = aResult;
                }
                toPoll.Add(s.Applications.PollAsync(!s.Applications.IsPolling));
                toPoll.Add(s.ErrorSummary.PollAsync(!s.ErrorSummary.IsPolling));
            }
            await Task.WhenAll(toPoll).ConfigureAwait(false);
            return result;
        }

        public static IEnumerable<Application> GetApps(string appName)
        {
            if (Applications == null) yield break;
            foreach (var a in Applications)
            {
                if (a.Name == appName || appName == null)
                    yield return a;
            }
        }

        private static IEnumerable<ExceptionStore> GetStores(string appName)
        {
            var stores = GetApps(appName).Where(a => a.Store != null).Select(a => a.Store).Distinct();
            // if we have no error stores hooked up, return all stores to search
            return stores.Any() ? stores : ExceptionsModule.Stores;
        }

        private static IEnumerable<Error> GetSorted(IEnumerable<Error> source, ExceptionSorts? sort = ExceptionSorts.TimeDesc)
        {
            switch (sort)
            {
                case ExceptionSorts.AppAsc:
                    return source.OrderBy(e => e.ApplicationName);
                case ExceptionSorts.AppDesc:
                    return source.OrderByDescending(e => e.ApplicationName);
                case ExceptionSorts.TypeAsc:
                    return source.OrderBy(e => e.Type.Split(StringSplits.Period_Plus).Last());
                case ExceptionSorts.TypeDesc:
                    return source.OrderByDescending(e => e.Type.Split(StringSplits.Period_Plus).Last());
                case ExceptionSorts.MessageAsc:
                    return source.OrderBy(e => e.Message);
                case ExceptionSorts.MessageDesc:
                    return source.OrderByDescending(e => e.Message);
                case ExceptionSorts.UrlAsc:
                    return source.OrderBy(e => e.UrlPath);
                case ExceptionSorts.UrlDesc:
                    return source.OrderByDescending(e => e.UrlPath);
                case ExceptionSorts.IPAddressAsc:
                    return source.OrderBy(e => e.IPAddress);
                case ExceptionSorts.IPAddressDesc:
                    return source.OrderByDescending(e => e.IPAddress);
                case ExceptionSorts.HostAsc:
                    return source.OrderBy(e => e.Host);
                case ExceptionSorts.HostDesc:
                    return source.OrderByDescending(e => e.Host);
                case ExceptionSorts.MachineNameAsc:
                    return source.OrderBy(e => e.MachineName);
                case ExceptionSorts.MachineNameDesc:
                    return source.OrderByDescending(e => e.MachineName);
                case ExceptionSorts.CountAsc:
                    return source.OrderBy(e => e.DuplicateCount.GetValueOrDefault(1));
                case ExceptionSorts.CountDesc:
                    return source.OrderByDescending(e => e.DuplicateCount.GetValueOrDefault(1));
                case ExceptionSorts.TimeAsc:
                    return source.OrderBy(e => e.CreationDate);
                //case ExceptionSorts.TimeDesc:
                default:
                    return source.OrderByDescending(e => e.CreationDate);
            }
        }

        public static async Task<List<Error>> GetAllErrors(string group = null, string app = null, int? maxPerApp = null, ExceptionSorts sort = ExceptionSorts.TimeDesc)
        {
            using (MiniProfiler.Current.Step("GetAllErrors() - All Stores" + (group.HasValue() ? " (group:" + group + ")" : "") + (app.HasValue() ? " (app:" + app + ")" : "")))
            {
                maxPerApp = maxPerApp ?? Current.Settings.Exceptions.PerAppCacheCount;
                var stores = ExceptionsModule.Stores;
                IEnumerable <Error> allErrors;
                if (stores.Count == 1)
                {
                    allErrors = await stores[0].GetErrorSummary(maxPerApp.Value, group, app).ConfigureAwait(false);
                }
                else
                {
                    var tasks = stores.Select(s => s.GetErrorSummary(maxPerApp.Value, group, app));
                    var taskResults = await Task.WhenAll(tasks).ConfigureAwait(false);
                    var combined = new List<Error>();
                    foreach (var r in taskResults)
                    {
                        combined.AddRange(r);
                    }
                    allErrors = combined;
                }
                return GetSorted(allErrors, sort).ToList();
            }
        }

        public static async Task<List<Error>> GetSimilarErrorsAsync(Error error, bool byTime = false, int max = 200, ExceptionSorts sort = ExceptionSorts.TimeDesc)
        {
            if (error == null) return new List<Error>();
            var errorFetches = GetStores(error.ApplicationName).Select(s => byTime ? s.GetSimilarErrorsInTimeAsync(error, max) : s.GetSimilarErrorsAsync(error, max));
            var similarErrors = (await Task.WhenAll(errorFetches).ConfigureAwait(false)).SelectMany(e => e);
            return GetSorted(similarErrors, sort).ToList();
        }

        public static async Task<List<Error>> FindErrorsAsync(string searchText, string group = null, string app = null, int max = 200, bool includeDeleted = false, ExceptionSorts sort = ExceptionSorts.TimeDesc)
        {
            if (searchText.IsNullOrEmpty()) return new List<Error>();
            var stores = app.HasValue() ? GetStores(app) : ExceptionsModule.Stores; // Apps are across stores, group doesn't matter here
            var errorFetches = stores.Select(s => s.FindErrorsAsync(searchText, max, includeDeleted, GetAppNames(group, app)));
            var results = (await Task.WhenAll(errorFetches).ConfigureAwait(false)).SelectMany(e => e);
            return GetSorted(results, sort).ToList();
        }

        internal static List<ApplicationGroup> UpdateApplicationGroups()
        {
            using (MiniProfiler.Current.Step("UpdateApplicationGroups() - All Stores"))
            {
                lock (_updateLock) // In the case of multiple stores, it's off to the races.
                {
                    var result = ApplicationGroups;
                    var apps = ExceptionsModule.Stores.SelectMany(s => s.Applications?.Data ?? Enumerable.Empty<Application>()).ToList();
                    // Loop through all configured groups and hook up applications returned from the queries
                    foreach (var g in result)
                    {
                        for (var i = 0; i < g.Applications.Count; i++)
                        {
                            var a = g.Applications[i];
                            foreach (var app in apps)
                            {
                                if (app.Name == a.Name)
                                {
                                    g.Applications[i] = app;
                                    break;
                                }
                            }
                        }
                    }
                    // Clear all dynamic apps from the CatchAll group
                    CatchAll.Applications.RemoveAll(a => !KnownApplications.Contains(a.Name));
                    // Check for the any dyanmic/unconfigured apps
                    foreach (var app in apps.OrderBy(a => a.Name))
                    {
                        if (!KnownApplications.Contains(app.Name))
                        {
                            // This dynamic app needs a home!
                            CatchAll.Applications.Add(app);
                        }
                    }
                    // Cleanout those with no errors right now
                    var foundNames = apps.Select(a => a.Name).ToHashSet();
                    foreach (var a in result.SelectMany(g => g.Applications))
                    {
                        if (!foundNames.Contains(a.Name))
                        {
                            a.ClearCounts();
                        }
                    }

                    Applications = apps;
                    ApplicationGroups = result;
                    return result;
                }
            }
        }
    }
}
﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Fx.Portability.ObjectModel;
using Microsoft.Fx.Portability.Reporting.ObjectModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Fx.Portability.Reports
{
    /// <summary>
    /// Please do not use this class directly, it is for Razor so that the engine can compute the HTML pages properly.
    /// </summary>
    public sealed class RazorHtmlObject
    {
        private readonly ITargetMapper _targetMapper;

        public ReportingResult ReportingResult { get; private set; }

        public IEnumerable<MissingTypeInfo> MissingTypes { get; private set; }

        public IOrderedEnumerable<AssemblyUsageInfo> OrderedAssembliesByIdentity { get; private set; }

        public ITargetMapper TargetMapper { get; private set; }

        public IEnumerable<string> TargetHeaders { get; private set; }

        public IEnumerable<KeyValuePair<string, ICollection<string>>> OrderedUnresolvedAssemblies { get; private set; }

        public IOrderedEnumerable<KeyValuePair<AssemblyInfo, IDictionary<BreakingChange, IEnumerable<MemberInfo>>>> OrderedBreakingChangesByAssembly { get; private set; }

        public IDictionary<BreakingChange, IEnumerable<MemberInfo>> BreakingChangesSummary { get; private set; }

        public IOrderedEnumerable<AssemblyInfo> OrderedBreakingChangeSkippedAssemblies { get; private set; }

        public RazorHtmlObject(AnalyzeResponse response, ITargetMapper targetMapper)
        {
            _targetMapper = targetMapper;
            RequestFlags = response.ReportingResult.RequestFlags;
            ReportingResult = response.ReportingResult;
            TargetMapper = _targetMapper;
            OrderedUnresolvedAssemblies = response.ReportingResult.GetUnresolvedAssemblies().OrderBy(asm => asm.Key);
            OrderedAssembliesByIdentity = response.ReportingResult.GetAssemblyUsageInfo().OrderBy(a => a.SourceAssembly.AssemblyIdentity);
            MissingTypes = response.ReportingResult.GetMissingTypes();
            TargetHeaders = _targetMapper.GetTargetNames(response.ReportingResult.Targets, true);
            OrderedBreakingChangesByAssembly = GetGroupedBreakingChanges(response.BreakingChanges, response.ReportingResult.GetAssemblyUsageInfo().Select(a => a.SourceAssembly));
            BreakingChangesSummary = GetBreakingChangesSummary(OrderedBreakingChangesByAssembly);
            OrderedBreakingChangeSkippedAssemblies = response.BreakingChangeSkippedAssemblies.OrderBy(a => a.AssemblyIdentity);
        }

        private IDictionary<BreakingChange, IEnumerable<MemberInfo>> GetBreakingChangesSummary(IEnumerable<KeyValuePair<AssemblyInfo, IDictionary<BreakingChange, IEnumerable<MemberInfo>>>> orderedBreakingChangesByAssembly)
        {
            return orderedBreakingChangesByAssembly
                .SelectMany(o => o.Value)
                .GroupBy(o => o.Key)
                .Select(o => new KeyValuePair<BreakingChange, IEnumerable<MemberInfo>>(o.Key, new SortedSet<MemberInfo>(o.SelectMany(j => j.Value))))
                .ToDictionary(o => o.Key, o => o.Value);
        }

        private IOrderedEnumerable<KeyValuePair<AssemblyInfo, IDictionary<BreakingChange, IEnumerable<MemberInfo>>>> GetGroupedBreakingChanges(IList<BreakingChangeDependency> breakingChanges, IEnumerable<AssemblyInfo> assembliesToInclude)
        {
            Dictionary<AssemblyInfo, IDictionary<BreakingChange, IEnumerable<MemberInfo>>> ret = new Dictionary<AssemblyInfo, IDictionary<BreakingChange, IEnumerable<MemberInfo>>>();
            foreach (BreakingChangeDependency b in breakingChanges)
            {
                // Add breaking changes, grouped by assembly, each with a collection of MemberInfos that trigger the break
                if (!ret.ContainsKey(b.DependantAssembly))
                {
                    ret.Add(b.DependantAssembly, new Dictionary<BreakingChange, IEnumerable<MemberInfo>>());
                }
                if (!ret[b.DependantAssembly].ContainsKey(b.Break))
                {
                    ret[b.DependantAssembly].Add(b.Break, new List<MemberInfo>());
                }
                if (!ret[b.DependantAssembly][b.Break].Contains(b.Member))
                {
                    (ret[b.DependantAssembly][b.Break] as IList<MemberInfo>).Add(b.Member);
                }
            }

            // If a full list of assemblies was passed, include empty entries for the remaining assemblies
            // so that our compat summary can be complete
            if (assembliesToInclude != null)
            {
                foreach (var a in assembliesToInclude)
                {
                    if (!ret.ContainsKey(a))
                    {
                        ret.Add(a, new Dictionary<BreakingChange, IEnumerable<MemberInfo>>());
                    }
                }
            }

            // Order meaningfully (by issue - from high to low pri - and then by assembly name)
            return ret
                .OrderByDescending(x => x.Value.Where(b => b.Key.ImpactScope == BreakingChangeImpact.Major && !b.Key.IsRetargeting).Count())
                .ThenByDescending(x => x.Value.Where(b => b.Key.ImpactScope == BreakingChangeImpact.Minor && !b.Key.IsRetargeting).Count())
                .ThenByDescending(x => x.Value.Where(b => b.Key.ImpactScope == BreakingChangeImpact.Edge && !b.Key.IsRetargeting).Count())
                .ThenByDescending(x => x.Value.Where(b => b.Key.ImpactScope == BreakingChangeImpact.Major && b.Key.IsRetargeting).Count())
                .ThenByDescending(x => x.Value.Where(b => b.Key.ImpactScope == BreakingChangeImpact.Minor && b.Key.IsRetargeting).Count())
                .ThenByDescending(x => x.Value.Where(b => b.Key.ImpactScope == BreakingChangeImpact.Edge && b.Key.IsRetargeting).Count())
                .ThenBy(x => x.Key);
        }

        public string RemoveTypeOrMemberPrefix(string assemblyName)
        {
            string[] split = assemblyName.Split(new string[] { ":" }, 2, StringSplitOptions.RemoveEmptyEntries);
            return split.Length == 2 ? split[1] : split[0];
        }

        public IList<string> GetUnresolvedAssemblies(AssemblyUsageInfo assembly)
        {
            string assemblyName = assembly.SourceAssembly.AssemblyIdentity;
            var unresolvedAssemblyIdentities = from pair in OrderedUnresolvedAssemblies
                                               where pair.Value.Contains(assemblyName)
                                               select pair.Key;
            return unresolvedAssemblyIdentities.ToList();
        }

        public IReadOnlyList<MissingTypeInfo> MatchingMissingTypes(AssemblyUsageInfo assembly)
        {
            var matching = MissingTypes.Where(t => t.UsedIn.Contains(assembly.SourceAssembly)).OrderBy(type => type.TypeName).ToList();
            return matching;
        }

        public IEnumerable<TargetSupportedIn> GetMatchingTargetsAndSupportedVersions(IEnumerable<Version> usedVersions)
        {
            var zipped = usedVersions.Zip(ReportingResult.Targets, (a, b) => { return new TargetSupportedIn(b, a); });
            return zipped;
        }

        public AnalyzeRequestFlags RequestFlags { get; set; }
    }
}

﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Fx.Portability.Analyzer;
using Microsoft.Fx.Portability.ObjectModel;
using Microsoft.Fx.Portability.Reporting;
using Microsoft.Fx.Portability.Reporting.ObjectModel;
using Microsoft.Fx.Portability.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.Fx.Portability
{
    public class ApiPortClient
    {
        private readonly IApiPortService _apiPortService;
        private readonly IProgressReporter _progressReport;
        private readonly ITargetMapper _targetMapper;
        private readonly IDependencyFinder _dependencyFinder;
        private readonly IReportGenerator _reportGenerator;
        private readonly IEnumerable<IgnoreAssemblyInfo> _assembliesToIgnore;

        public ITargetMapper TargetMapper { get { return _targetMapper; } }

        public ApiPortClient(IApiPortService apiPortService, IProgressReporter progressReport, ITargetMapper targetMapper, IDependencyFinder dependencyFinder, IReportGenerator reportGenerator, IEnumerable<IgnoreAssemblyInfo> assembliesToIgnore)
        {
            _apiPortService = apiPortService;
            _progressReport = progressReport;
            _targetMapper = targetMapper;
            _dependencyFinder = dependencyFinder;
            _reportGenerator = reportGenerator;
            _assembliesToIgnore = assembliesToIgnore;
        }

        public async Task<ReportingResult> AnalyzeAssemblies(IApiPortOptions options)
        {
            var dependencyInfo = _dependencyFinder.FindDependencies(options.InputAssemblies, _progressReport);

            if (dependencyInfo.UserAssemblies.Any())
            {
                AnalyzeRequest request = GenerateRequest(options, dependencyInfo);

                return await GetResultFromServiceAsync(request, dependencyInfo);
            }
            else
            {
                _progressReport.ReportIssue(LocalizedStrings.NoFilesAvailableToUpload);

                return null;
            }
        }

        public async Task<IEnumerable<byte[]>> GetAnalysisReportAsync(IApiPortOptions options, IEnumerable<string> outputFormats)
        {
            var dependencyInfo = _dependencyFinder.FindDependencies(options.InputAssemblies, _progressReport);

            if (dependencyInfo.UserAssemblies.Any())
            {
                AnalyzeRequest request = GenerateRequest(options, dependencyInfo);

                var tasks = outputFormats
                    .Select(f => GetResultFromServiceAsync(request, f))
                    .ToList();

                await Task.WhenAll(tasks);

                return tasks.Select(t => t.Result).ToList();
            }
            else
            {
                _progressReport.ReportIssue(LocalizedStrings.NoFilesAvailableToUpload);

                return Enumerable.Empty<byte[]>();
            }
        }

        public async Task<string> GetExtensionForFormat(string format)
        {
            var outputFormats = await _apiPortService.GetResultFormatsAsync();
            var outputFormat = outputFormats.Response.FirstOrDefault(f => string.Equals(format, f.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (outputFormat == null)
            {
                throw new UnknownReportFormatException(format);
            }

            return outputFormat.FileExtension;
        }

        public async Task<IEnumerable<string>> ListResultFormatsAsync()
        {
            using (var progressTask = _progressReport.StartTask(LocalizedStrings.RetrievingOutputFormats))
            {
                try
                {
                    var outputFormats = await _apiPortService.GetResultFormatsAsync();

                    CheckEndpointStatus(outputFormats.Headers.Status);

                    return outputFormats.Response.Select(r => r.DisplayName).ToList();
                }
                catch (Exception)
                {
                    progressTask.Abort();
                    throw;
                }
            }
        }

        public AnalyzeRequest GenerateRequest(IApiPortOptions options, IDependencyInfo dependencyInfo)
        {
            return new AnalyzeRequest
            {
                Targets = options.Targets.SelectMany(_targetMapper.GetNames).ToList(),
                Dependencies = dependencyInfo.Dependencies,
                AssembliesToIgnore = _assembliesToIgnore,                 // We pass along assemblies to ignore instead of filtering them from Dependencies at this point
                                                                          // because breaking change analysis and portability analysis will likely want to filter dependencies
                                                                          // in different ways for ignored assemblies. 
                                                                          // For breaking changes, we should show breaking changes for
                                                                          // an assembly if it is un-ignored on any of the user-specified targets and we should hide breaking changes
                                                                          // for an assembly if it ignored on all user-specified targets. 
                                                                          // For portability analysis, on the other hand, we will want to show portability for precisely those targets
                                                                          // that a user specifies that are not on the ignore list. In this case, some of the assembly's dependency
                                                                          // information will be needed.
                UnresolvedAssemblies = dependencyInfo.UnresolvedAssemblies.Keys.ToList(),
                UnresolvedAssembliesDictionary = dependencyInfo.UnresolvedAssemblies,
                UserAssemblies = dependencyInfo.UserAssemblies.ToList(),
                AssembliesWithErrors = dependencyInfo.AssembliesWithErrors.ToList(),
                ApplicationName = options.Description,
                Version = AnalyzeRequest.CurrentVersion,
                RequestFlags = options.RequestFlags,
                BreakingChangesToSuppress = options.BreakingChangeSuppressions
            };
        }

        /// <summary>
        /// Gets the Portability of an application as a ReportingResult.
        /// </summary>
        /// <returns>Set of APIs/assemblies that are not portable/missing.</returns>
        private async Task<ReportingResult> GetResultFromServiceAsync(AnalyzeRequest request, IDependencyInfo dependencyInfo)
        {
            var fullResponse = await RetrieveResultAsync(request);

            CheckEndpointStatus(fullResponse.Headers.Status);

            using (var progressTask = _progressReport.StartTask(LocalizedStrings.ComputingReport))
            {
                try
                {
                    var response = fullResponse.Response;

                    return _reportGenerator.ComputeReport(
                        response.Targets,
                        response.SubmissionId,
                        request.RequestFlags,
                        dependencyInfo?.Dependencies,
                        response.MissingDependencies,
                        dependencyInfo?.UnresolvedAssemblies,
                        response.UnresolvedUserAssemblies,
                        dependencyInfo?.AssembliesWithErrors
                    );
                }
                catch (Exception)
                {
                    progressTask.Abort();
                    throw;
                }
            }
        }

        private async Task<ServiceResponse<AnalyzeResponse>> RetrieveResultAsync(AnalyzeRequest request)
        {
            using (var progressTask = _progressReport.StartTask(LocalizedStrings.SendingDataToService))
            {
                try
                {
                    return await _apiPortService.SendAnalysisAsync(request);
                }
                catch (Exception)
                {
                    progressTask.Abort();
                    throw;
                }
            }
        }

        /// <summary>
        /// Gets the Portability report for the request.
        /// </summary>
        /// <returns>An array of bytes corresponding to the report.</returns>
        private async Task<byte[]> GetResultFromServiceAsync(AnalyzeRequest request, string format)
        {
            using (var progressTask = _progressReport.StartTask(LocalizedStrings.SendingDataToService))
            {
                try
                {
                    var response = await _apiPortService.SendAnalysisAsync(request, format);

                    CheckEndpointStatus(response.Headers.Status);

                    return response.Response;
                }
                catch (Exception)
                {
                    progressTask.Abort();
                    throw;
                }
            }
        }

        /// <summary>
        /// Verifies that the service is alive.  If the service is not alive, then an issue is logged 
        /// that will be reported back to the user.
        /// </summary>
        private void CheckEndpointStatus(EndpointStatus status)
        {
            if (status == EndpointStatus.Deprecated)
            {
                _progressReport.ReportIssue(LocalizedStrings.ServerEndpointDeprecated);
            }
        }

        public async Task<IEnumerable<AvailableTarget>> ListTargets()
        {
            using (var progressTask = _progressReport.StartTask(LocalizedStrings.RetrievingTargets))
            {
                try
                {
                    var targets = await _apiPortService.GetTargetsAsync();

                    CheckEndpointStatus(targets.Headers.Status);

                    return targets.Response;
                }
                catch (Exception)
                {
                    progressTask.Abort();
                    throw;
                }
            }
        }
    }
}

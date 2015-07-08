﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Fx.Portability;
using Microsoft.Fx.Portability.Analysis;
using Microsoft.Fx.Portability.Analyzer;
using Microsoft.Fx.Portability.ObjectModel;
using Microsoft.Fx.Portability.Reporting;
using Microsoft.Practices.Unity;
using Microsoft.Practices.Unity.Configuration;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;

namespace ApiPort
{
    internal static class DependencyBuilder
    {
        public static IUnityContainer Build(ICommandLineOptions options, ProductInformation productInformation)
        {
            var container = new UnityContainer();

            var targetMapper = new TargetMapper();
            targetMapper.LoadFromConfig();

            var ignoreAssemblyList = new FileIgnoreAssemblyInfoList(options.RequestFlags.HasFlag(AnalyzeRequestFlags.NoDefaultIgnoreFile), options.IgnoredAssemblyFiles);

            container.RegisterInstance(options);
            container.RegisterInstance<ITargetMapper>(targetMapper);
            container.RegisterInstance<IEnumerable<IgnoreAssemblyInfo>>(ignoreAssemblyList);

            // For debug purposes, the FileOutputApiPortService helps as it serializes the request to json and opens it with the
            // default json handler. To use this service, uncomment the the next line and comment the one after that.
            //container.RegisterType<IApiPortService, FileOutputApiPortService>(new ContainerControlledLifetimeManager());
            container.RegisterInstance<IApiPortService>(new ApiPortService(options.ServiceEndpoint, productInformation));

            container.RegisterType<IDependencyFinder, ReflectionMetadataDependencyFinder>(new ContainerControlledLifetimeManager());
            container.RegisterType<IReportGenerator, ReportGenerator>(new ContainerControlledLifetimeManager());
            container.RegisterType<ApiPortService>(new ContainerControlledLifetimeManager());
            container.RegisterType<IFileSystem, WindowsFileSystem>(new ContainerControlledLifetimeManager());
            container.RegisterType<IFileWriter, ReportFileWriter>(new ContainerControlledLifetimeManager());
            container.RegisterType<IRequestAnalyzer, RequestAnalyzer>(new ContainerControlledLifetimeManager());
            container.RegisterType<IAnalysisEngine, AnalysisEngine>(new ContainerControlledLifetimeManager());
            container.RegisterType<ICollection<IReportWriter>>(new ContainerControlledLifetimeManager(), new InjectionFactory(WriterCollection));

            // Register the default output format name
            container.RegisterInstance("DefaultOutputFormat", "Excel");

            if (Console.IsOutputRedirected)
            {
                container.RegisterInstance<IProgressReporter>(new TextWriterProgressReporter(Console.Out));
            }
            else
            {
                container.RegisterType<IProgressReporter, ConsoleProgressReporter>(new ContainerControlledLifetimeManager());
            }

            // Load any customizations via Unity
            var fileMap = new ExeConfigurationFileMap
            {
                ExeConfigFilename = Path.Combine(GetApplicationDirectory(), "unity.config")
            };

            var configuration = ConfigurationManager.OpenMappedExeConfiguration(fileMap, ConfigurationUserLevel.None);
            var unitySection = (UnityConfigurationSection)configuration.GetSection("unity");

            return unitySection == null ? container : container.LoadConfiguration(unitySection);
        }

        private static string GetApplicationDirectory()
        {
            return Path.GetDirectoryName(typeof(DependencyBuilder).Assembly.Location);
        }

        private static object WriterCollection(IUnityContainer container)
        {
            return container.ResolveAll<IReportWriter>().ToList();
        }
    }
}

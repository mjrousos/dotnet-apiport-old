﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Fx.Portability.ObjectModel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Fx.Portability.Analyzer.Resources;

namespace Microsoft.Fx.Portability.Analyzer
{
    internal class ReflectionMetadataDependencyInfo : IDependencyInfo
    {
        private readonly ConcurrentDictionary<string, ICollection<string>> _unresolvedAssemblies = new ConcurrentDictionary<string, ICollection<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _assembliesWithError = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<AssemblyInfo> _userAssemblies = new HashSet<AssemblyInfo>();
        private readonly ConcurrentDictionary<MemberInfo, ICollection<AssemblyInfo>> _cachedDependencies = new ConcurrentDictionary<MemberInfo, ICollection<AssemblyInfo>>();
        private readonly IEnumerable<string> _inputAssemblies;

        private ReflectionMetadataDependencyInfo(IEnumerable<string> inputAssemblies)
        {
            _inputAssemblies = inputAssemblies;
        }

        public static ReflectionMetadataDependencyInfo ComputeDependencies(IEnumerable<string> inputAssemblies, IProgressReporter progressReport)
        {
            var engine = new ReflectionMetadataDependencyInfo(inputAssemblies);

            engine.FindDependencies(progressReport);

            return engine;
        }

        public IDictionary<MemberInfo, ICollection<AssemblyInfo>> Dependencies
        {
            get { return _cachedDependencies; }
        }

        public IEnumerable<string> AssembliesWithErrors
        {
            get { return _assembliesWithError; }
        }

        public IDictionary<string, ICollection<string>> UnresolvedAssemblies
        {
            get { return _unresolvedAssemblies; }
        }

        public IEnumerable<AssemblyInfo> UserAssemblies
        {
            get { return _userAssemblies; }
        }

        private void FindDependencies(IProgressReporter progressReport)
        {
            _inputAssemblies.AsParallel().ForAll(filename =>
            {
                try
                {
                    foreach (var dependencies in GetDependencies(filename))
                    {
                        var m = new MemberInfo
                        {
                            MemberDocId = dependencies.MemberDocId,
                            TypeDocId = dependencies.TypeDocId,
                            DefinedInAssemblyIdentity = dependencies.DefinedInAssemblyIdentity
                        };

                        if (m.DefinedInAssemblyIdentity == null && !dependencies.IsPrimitive)
                        {
                            throw new InvalidOperationException("All non-primitive types should be defined in an assembly");
                        }

                        // Add this memberinfo
                        var newassembly = new HashSet<AssemblyInfo> { dependencies.CallingAssembly };

                        var assemblies = _cachedDependencies.AddOrUpdate(m, newassembly, (key, existingSet) =>
                        {
                            lock (existingSet)
                            {
                                existingSet.Add(dependencies.CallingAssembly);
                            }
                            return existingSet;
                        });
                    }
                }
                catch (InvalidPEAssemblyException)
                {
                    // This often indicates a non-PE file
                    _assembliesWithError.Add(filename);
                }
                catch (BadImageFormatException)
                {
                    // This often indicates a PE file with invalid contents (either because the assembly is protected or corrupted)
                    _assembliesWithError.Add(filename);
                }
            });

            // Clear out unresolved dependencies that were resolved during processing
            ICollection<string> collection;
            foreach (var assembly in _userAssemblies)
            {
                _unresolvedAssemblies.TryRemove(assembly.AssemblyIdentity, out collection);
            }
        }

        private IEnumerable<MemberDependency> GetDependencies(string assemblyLocation)
        {
            try
            {
                using (var stream = File.OpenRead(assemblyLocation))
                using (var peFile = new PEReader(stream))
                {
                    var metadataReader = GetMetadataReader(peFile);

                    AddReferencedAssemblies(metadataReader);

                    var helper = new DependencyFinderEngineHelper(metadataReader, assemblyLocation);
                    helper.ComputeData();

                    // Remember this assembly as a user assembly.
                    _userAssemblies.Add(helper.CallingAssembly);

                    return helper.MemberDependency;
                }
            }
            catch (Exception exc)
            {
                // InvalidPEAssemblyExceptions may be expected and indicative of a non-PE file
                if (exc is InvalidPEAssemblyException) throw;
                
                // Other exceptions are unexpected, though, and wil benefit from
                // more details on the scenario that hit them
                throw new PortabilityAnalyzerException(string.Format(LocalizedStrings.MetadataParsingExceptionMessage, assemblyLocation), exc);
            }
        }

        /// <summary>
        /// Add all assemblies that were referenced to the referenced assembly dictionary.  By default, 
        /// we add every referenced assembly and will remove the ones that are actually referenced when 
        /// all submitted assemblies are processed.
        /// </summary>
        /// <param name="metadataReader"></param>
        private void AddReferencedAssemblies(MetadataReader metadataReader)
        {
            var assemblyReferences = metadataReader.AssemblyReferences
                                        .Select(metadataReader.GetAssemblyReference)
                                        .Select(metadataReader.FormatAssemblyInfo);

            var assemblyName = metadataReader.FormatAssemblyInfo();

            foreach (var reference in assemblyReferences)
            {
                _unresolvedAssemblies.AddOrUpdate(
                    reference,
                    new HashSet<string>(StringComparer.Ordinal) { assemblyName },
                    (key, existing) =>
                    {
                        lock (existing)
                        {
                            existing.Add(assemblyName);
                        }

                        return existing;
                    });
            }
        }

        /// <summary>
        /// Attempt to get a MetadataReader.  Call this method instead of directly on PEReader so that
        /// exceptions thrown by it are caught and propagated as a known InvalidPEAssemblyException
        /// </summary>
        /// <param name="peReader"></param>
        /// <returns></returns>
        private MetadataReader GetMetadataReader(PEReader peReader)
        {
            try
            {
                return peReader.GetMetadataReader();
            }
            catch (Exception e)
            {
                throw new InvalidPEAssemblyException(e);
            }
        }

        private class InvalidPEAssemblyException : Exception
        {
            public InvalidPEAssemblyException(Exception inner)
                : base("Not a valid assembly", inner)
            { }
        }
    }
}

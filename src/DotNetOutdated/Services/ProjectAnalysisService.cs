﻿using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using DotNetOutdated.Models;
using NuGet.ProjectModel;

namespace DotNetOutdated.Services
{
    internal class ProjectAnalysisService : IProjectAnalysisService
    {
        private readonly IDependencyGraphService _dependencyGraphService;
        private readonly IDotNetRestoreService _dotNetRestoreService;
        private readonly IFileSystem _fileSystem;

        public ProjectAnalysisService(IDependencyGraphService dependencyGraphService, IDotNetRestoreService dotNetRestoreService, IFileSystem fileSystem)
        {
            _dependencyGraphService = dependencyGraphService;
            _dotNetRestoreService = dotNetRestoreService;
            _fileSystem = fileSystem;
        }
        
        public List<Project> AnalyzeProject(string projectPath, bool includeTransitiveDependencies, int transitiveDepth)
        {
            var dependencyGraph = _dependencyGraphService.GenerateDependencyGraph(projectPath);
            if (dependencyGraph == null)
                return null;

            var projects = new List<Project>();
            foreach (var packageSpec in dependencyGraph.Projects.Where(p => p.RestoreMetadata.ProjectStyle == ProjectStyle.PackageReference))
            {
                // Restore the packages
                _dotNetRestoreService.Restore(packageSpec.FilePath);
                
                // Load the lock file
                string lockFilePath = _fileSystem.Path.Combine(packageSpec.RestoreMetadata.OutputPath, "project.assets.json");
                var lockFile = LockFileUtilities.GetLockFile(lockFilePath, NuGet.Common.NullLogger.Instance);
                
                // Create a project
                var project = new Project
                {
                    Name = packageSpec.Name,
                    Sources = packageSpec.RestoreMetadata.Sources.Select(s => s.SourceUri).ToList(),
                    FilePath = packageSpec.FilePath
                };
                projects.Add(project);

                // Get the target frameworks with their dependencies 
                foreach (var targetFrameworkInformation in packageSpec.TargetFrameworks)
                {
                    var targetFramework = new TargetFramework
                    {
                        Name = targetFrameworkInformation.FrameworkName,
                    };
                    project.TargetFrameworks.Add(targetFramework);

                    var target = lockFile.Targets.FirstOrDefault(t => t.TargetFramework.Equals(targetFrameworkInformation.FrameworkName));

                    if (target != null)
                    {
                        foreach (var projectDependency in targetFrameworkInformation.Dependencies)
                        {
                           var projectLibrary = target.Libraries.FirstOrDefault(library => string.Equals(library.Name, projectDependency.Name, StringComparison.OrdinalIgnoreCase));

                            var dependency = new Dependency
                            {
                                Name = projectDependency.Name,
                                VersionRange = projectDependency.LibraryRange.VersionRange,
                                ResolvedVersion = projectLibrary?.Version,
                                IsAutoReferenced = projectDependency.AutoReferenced,
                                IsTransitive = false,

                                // We have no sure way to determine if this is a development dependency. For now, we simply look whether the
                                // package contains build assets or alternatively if it contains no runtime and compile time assemblies
                                IsDevelopmentDependency = projectLibrary != null && (projectLibrary.Build.Any() 
                                                                                     || (!projectLibrary.CompileTimeAssemblies.Any() && !projectLibrary.RuntimeAssemblies.Any()))
                            };
                            targetFramework.Dependencies.Add(dependency);

                            // Process transitive dependencies for the library
                            if (includeTransitiveDependencies)
                                AddDependencies(targetFramework, projectLibrary, target, 1, transitiveDepth);
                        }
                    }
                }
            }

            return projects;
        }

        private void AddDependencies(TargetFramework targetFramework, LockFileTargetLibrary parentLibrary, LockFileTarget target, int level, int transitiveDepth)
        {
            if (parentLibrary?.Dependencies != null)
            {
                foreach (var packageDependency in parentLibrary.Dependencies)
                {
                    var childLibrary = target.Libraries.FirstOrDefault(library => library.Name == packageDependency.Id);

                    // Only add library and process child dependencies if we have not come across this dependency before
                    if (!targetFramework.Dependencies.Any(dependency => dependency.Name == packageDependency.Id))
                    {
                        var childDependency = new Dependency
                        {
                            Name = packageDependency.Id,
                            VersionRange = packageDependency.VersionRange,
                            ResolvedVersion = childLibrary?.Version,
                            IsTransitive = true
                        };
                        targetFramework.Dependencies.Add(childDependency);

                        // Process the dependency for this project depency
                        if (level < transitiveDepth)
                            AddDependencies(targetFramework, childLibrary, target, level + 1, transitiveDepth);
                    }
                }
            }
        }
    }
}
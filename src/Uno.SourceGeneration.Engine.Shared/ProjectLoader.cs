// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
//
// Portions of this file are based on Roslyn's MSBuild projects loader.
//
using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Xml;
using Uno.SourceGeneratorTasks.Helpers;
using Microsoft.Build.Execution;
using MSB = Microsoft.Build;
using MSBE = Microsoft.Build.Execution;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using System.Linq;
using System.Collections.Concurrent;
using static Microsoft.Extensions.Logging.LoggerExtensions;
using Uno.SourceGeneratorTasks;

namespace Uno.SourceGeneration.Host
{
	public class ProjectLoader
	{
		private static readonly Microsoft.Extensions.Logging.ILogger _log = typeof(ProjectLoader).Log();

		private static ConcurrentDictionary<(string projectFile, string configuration, string targetFramework), ProjectDetails> _allProjects
			= new ConcurrentDictionary<(string projectFile, string configuration, string targetFramework), ProjectDetails>();

		public static ProjectDetails LoadProjectDetails(BuildEnvironment environment, Dictionary<string, string> globalProperties)
		{
			var key = (environment.ProjectFile, environment.Configuration, environment.TargetFramework);

			if (_allProjects.TryGetValue(key, out var details))
			{
				if (!details.HasChanged())
				{
					if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
					{
						_log.LogDebug($"Using cached project file details for [{environment.ProjectFile}]");
					}

					return details;
				}
				else
				{
					if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
					{
						_log.LogDebug($"Reloading project file details [{environment.ProjectFile}] as one of its imports has been modified.");
					}
				}
			}

			if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
			{
				_log.LogDebug($"Loading project file [{environment.ProjectFile}]");
			}

			details = new ProjectDetails();

			if (Environment.GetEnvironmentVariable("Platform") is string envPlatform && !string.IsNullOrEmpty(envPlatform))
			{
				throw new InvalidOperationException(
					$"Your system has the Platform environment variable set to [{envPlatform}], which " +
					"is known to break some msbuild projects. Visit https://github.com/nventive/Uno.SourceGeneration/issues/48 for more details."
				);
			}

			// Platform is intentionally kept as not defined, to avoid having 
			// dependent projects being loaded with a platform they don't support.
			// properties["Platform"] = _platform;

			var xmlReader = XmlReader.Create(environment.ProjectFile);
			details.Collection = new Microsoft.Build.Evaluation.ProjectCollection();

			// Change this logger details to troubleshoot project loading details.
			// collection.RegisterLogger(new Microsoft.Build.Logging.ConsoleLogger() { Verbosity = LoggerVerbosity.Diagnostic });

#if HAS_BINLOG
			Microsoft.Build.Logging.BinaryLogger binaryLogger = null;

			if (
				environment.BinLogOutputPath != null
				&& environment.BinLogEnabled
			)
			{
				var binLogPath = Path.Combine(
					environment.BinLogOutputPath,
					$"SourceGenerator-{environment.TargetFramework}-{Path.GetFileNameWithoutExtension(environment.ProjectFile)}-{Guid.NewGuid()}.binlog"
				);

				details.Collection.RegisterLogger(
					binaryLogger = new Microsoft.Build.Logging.BinaryLogger()
					{
						Verbosity = LoggerVerbosity.Diagnostic,
						CollectProjectImports = Microsoft.Build.Logging.BinaryLogger.ProjectImportsCollectionMode.Embed,
						Parameters = $"logfile={binLogPath}"
					}
				);

				_log.LogInformation("Using BinaryLogger: " + binLogPath);
			}
#endif

			details.Collection.OnlyLogCriticalEvents = false;
			var xml = Microsoft.Build.Construction.ProjectRootElement.Create(xmlReader, details.Collection);

			// When constructing a project from an XmlReader, MSBuild cannot determine the project file path.  Setting the
			// path explicitly is necessary so that the reserved properties like $(MSBuildProjectDirectory) will work.
			xml.FullPath = Path.GetFullPath(environment.ProjectFile);

			var loadedProject = new Microsoft.Build.Evaluation.Project(
				xml,
				globalProperties,
				toolsVersion: null,
				projectCollection: details.Collection
			);

			var buildTargets = new BuildTargets(loadedProject, "Compile");

			// don't execute anything after CoreCompile target, since we've
			// already done everything we need to compute compiler inputs by then.
			buildTargets.RemoveAfter("CoreCompile", includeTargetInRemoval: true);

			details.Configuration = environment.Configuration;
			details.LoadedProject = loadedProject;

			// create a project instance to be executed by build engine.
			// The executed project will hold the final model of the project after execution via msbuild.
			details.ExecutedProject = loadedProject.CreateProjectInstance();

			var hostServices = new Microsoft.Build.Execution.HostServices();

			// connect the host "callback" object with the host services, so we get called back with the exact inputs to the compiler task.
			hostServices.RegisterHostObject(loadedProject.FullPath, "CoreCompile", "Csc", null);

			var buildParameters = new Microsoft.Build.Execution.BuildParameters(loadedProject.ProjectCollection);

			// This allows for the loggers to 
			buildParameters.Loggers = details.Collection.Loggers;

			var buildRequestData = new Microsoft.Build.Execution.BuildRequestData(details.ExecutedProject, buildTargets.Targets, hostServices);

			var result = BuildAsync(buildParameters, buildRequestData);

			if (result.Exception == null)
			{
				ValidateOutputPath(details.ExecutedProject);

				var projectFilePath = Path.GetFullPath(Path.GetDirectoryName(environment.ProjectFile));

				details.References = details.ExecutedProject.GetItems("ReferencePath").Select(r => r.EvaluatedInclude).ToArray();

				if (!details.References.Any())
				{
					if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error))
					{
						_log.LogError($"Project has no references.");
					}

					LogFailedTargets(environment.ProjectFile, result);
					details.Generators = new (Type, Func<SourceGenerator>)[0];
					return details;
				}
				// else
				// {
				//     if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
				//     {
				//         _log.LogDebug($"Project references: {string.Join("; ", details.References)}");
				//     }
				// }

				details.IntermediatePath = Path.Combine(projectFilePath, details.ExecutedProject.GetPropertyValue("IntermediateOutputPath"));

				// This is the legacy way of looking for source generators, based on the defunct "Roslyn 2.0 Source Generator" proposal.
				// This causes issues with the VS IDE, where roslyn tries to load our source generators to 
				// find analyzers, but does not find any and reports it in the errors window.
				//
				// The SourceGenerator ItemGroup should now be used instead.
				var analyzerFiles = details.ExecutedProject
					.GetItems("Analyzer")
					.Select(r => Path.Combine(projectFilePath, r.EvaluatedInclude))
					.Select(Path.GetFullPath);

				var sourceGeneratorFiles = details.ExecutedProject
					.GetItems("SourceGenerator")
					.Select(r => Path.Combine(projectFilePath, r.EvaluatedInclude))
					.Select(Path.GetFullPath);

				var sourceGeneratorAdditionalDependencies = details.ExecutedProject
					.GetItems("SourceGeneratorAdditionalDependency")
					.Select(e => Path.GetFullPath(e.EvaluatedInclude));

				foreach (var dependency in sourceGeneratorAdditionalDependencies)
				{
					if (File.Exists(dependency))
					{
						var asm = Assembly.LoadFile(dependency);
					}
				}

				details.Generators = LoadAnalyzers(analyzerFiles.Concat(sourceGeneratorFiles).Distinct());

				if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
				{
					var allGenerators = details.Generators.Select(g => g.generatorType.FullName).JoinBy("; ");

					_log.LogDebug($"Found {details.Generators.Length} Source Generators. ({allGenerators}");
				}
			}
			else
			{
				if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error))
				{
					_log.LogError($"Project analysis failed ({result.Exception}");
				}

				LogFailedTargets(environment.ProjectFile, result);

				details.Generators = new (Type, Func<SourceGenerator>)[0];
			}

			_allProjects.TryAdd(key, details);

			details.BuildImportsMap();

#if HAS_BINLOG
			binaryLogger?.Shutdown();
#endif

			return details;
		}

		private static void LogFailedTargets(string projectFile, BuildResult result)
		{
			if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
			{
				var failedTargetsEnum = result
					.ResultsByTarget
					.Where(p => p.Value.ResultCode == TargetResultCode.Failure)

					// CoreCompile will most likely fail, particularly if it depends 
					// on generated code to compile.
					.Where(p => p.Key != "CoreCompile")
					.Select(p => p.Value);

				if (failedTargetsEnum.Any())
				{
					var failedTargets = string.Join("; ", failedTargetsEnum);

					_log.LogError(
						$"Failed to analyze project file [{projectFile}], the targets [{failedTargets}] failed to execute." +
						"This may be due to an invalid path, such as $(SolutionDir) being used in the csproj; try using relative paths instead." +
						"This may also be related to a missing default configuration directive. Refer to the Uno.SourceGenerator Readme.md file for more details."
					);
				}
			}
		}

		private static void ValidateOutputPath(ProjectInstance project)
		{
			// Ensure that the loaded has an OutputPath defined. This checks for 
			// projects that may have an invalid default configuration|platform, which
			// needs to be fixed first.
			if ((!project.GetProperty("OutputPath")?.EvaluatedValue.HasValue()) ?? false)
			{
				var evaluatedConfig = project.GetProperty("Configuration")?.EvaluatedValue;
				var evaluatedPlatform = project.GetProperty("Platform")?.EvaluatedValue;

				throw new Exception(
					$"The current project does not define an OutputPath property for [{evaluatedConfig}|{evaluatedPlatform}]. " +
					$"Validate that the fallback platform at the top of [{project.FullPath}] matches one of the " +
					$"sections defining an OutputPath property with the [{evaluatedConfig}|{evaluatedPlatform}] condition."
				);
			}
		}

		private static (Type, Func<SourceGenerator>)[] LoadAnalyzers(IEnumerable<string> enumerable)
		{
			var generators = new List<(Type, Func<SourceGenerator>)>();

			foreach (var analyzerAsm in enumerable.Where(ContainsGenerators))
			{
				var assemblyDirectory = Path.GetFullPath(Path.GetDirectoryName(analyzerAsm));

				try
				{
					Assembly LocalResolve(object s, ResolveEventArgs e)
					{
						var assemblyName = new AssemblyName(e.Name).Name;

						return assemblyName == "Uno.SourceGeneration"
							? typeof(SourceGenerator).Assembly
							: Assembly.LoadFile(Path.Combine(assemblyDirectory, assemblyName + ".dll"));
					}

					try
					{
						AppDomain.CurrentDomain.AssemblyResolve += LocalResolve;

						// Use LoadFrom and not LoadFile, in order for the AppDomain shadowing
						// to function properly.
						var asm = Assembly.LoadFrom(analyzerAsm);

						var q = from type in asm.GetTypes()
								where !type.IsAbstract
								where type.GetBaseTypes().Any(c => c.FullName == typeof(SourceGenerator).FullName)
								select type;

						generators.AddRange(
							q.Select(t =>
								(t, new Func<SourceGenerator>(() =>(SourceGenerator)Activator.CreateInstance(t)))
							)
						);

					}
					finally
					{
						AppDomain.CurrentDomain.AssemblyResolve -= LocalResolve;
					}
				}
				catch (Exception e)
				{
					var typeLoad = e as ReflectionTypeLoadException;

					if (typeLoad != null)
					{
						_log.LogError($"{typeLoad.LoaderExceptions.Select(inner => inner.ToString()).JoinBy(",")}");
					}
					else
					{
						_log.LogWarning($"Unable to load {analyzerAsm} ({e.Message}, {e.InnerException}");
					}
				}
			}

			return generators.ToArray();
		}

		private static bool ContainsGenerators(string assembly)
		{
			try
			{
				_log.LogInformation($"Checking [{assembly}]");
				var definition = Mono.Cecil.AssemblyDefinition.ReadAssembly(assembly);

				return definition.MainModule.Types.Any(t => t.BaseType?.FullName == typeof(SourceGenerator).FullName);
			}
			catch (Exception e)
			{
				_log.LogError($"Failed to read [{assembly}], {e.Message}", e);
				return false;
			}
		}

		private static MSBE.BuildResult BuildAsync(MSBE.BuildParameters parameters, MSBE.BuildRequestData requestData)
		{
			var buildManager = MSBE.BuildManager.DefaultBuildManager;

			var taskSource = new TaskCompletionSource<MSB.Execution.BuildResult>();

			buildManager.BeginBuild(parameters);

			// enable cancellation of build
			CancellationTokenRegistration registration = default(CancellationTokenRegistration);

			// execute build async
			try
			{
				buildManager.PendBuildRequest(requestData).ExecuteAsync(sub =>
				{
					// when finished
					try
					{
						var result = sub.BuildResult;
						buildManager.EndBuild();
						registration.Dispose();
						taskSource.TrySetResult(result);
					}
					catch (Exception e)
					{
						taskSource.TrySetException(e);
					}
				}, null);
			}
			catch (Exception e)
			{
				taskSource.SetException(e);
			}

			return taskSource.Task.Result;
		}
	}
}

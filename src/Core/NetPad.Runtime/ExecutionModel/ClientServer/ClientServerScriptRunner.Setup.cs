using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using NetPad.Common;
using NetPad.Compilation;
using NetPad.Configuration;
using NetPad.Data;
using NetPad.DotNet;
using NetPad.DotNet.References;
using NetPad.ExecutionModel.External;
using NetPad.IO;
using NetPad.Packages;
using NetPad.Presentation;
using SourceCodeCollection = NetPad.DotNet.CodeAnalysis.SourceCodeCollection;

namespace NetPad.ExecutionModel.ClientServer;

public partial class ClientServerScriptRunner
{
    private readonly ICodeParser _codeParser;
    private readonly ICodeCompiler _codeCompiler;
    private readonly IPackageProvider _packageProvider;
    private readonly Settings _settings;

    /// <summary>
    /// Contains post-setup info needed to run the script.
    /// </summary>
    /// <param name="ScriptHostDepsDir">The full path to the directory that will be used to deploy script-host
    /// dependencies as well as dependencies needed by both the script-host process and the script itself.</param>
    /// <param name="ScriptDir">The full path to the directory that will be used to deploy the script
    /// assembly and any dependencies that are only needed by the script.</param>
    /// <param name="ScriptAssemblyFilePath">The full path to the compiled script assembly.</param>
    /// <param name="InPlaceDependencyDirectories">Full paths to directories where dependencies where not copied to
    /// one of the deployment directories and instead should be loaded from their original locations (in-place).</param>
    /// <param name="UserProgramStartLineNumber">The line number that user code starts on.</param>
    private record SetupInfo(
        DirectoryPath ScriptHostDepsDir,
        DirectoryPath ScriptDir,
        FilePath ScriptAssemblyFilePath,
        string[] InPlaceDependencyDirectories,
        int UserProgramStartLineNumber);

    /// <summary>
    /// Sets up the environment the script will run in. It creates a folder structure similar to the following and
    /// deploys dependencies needed to run the script-host process and the script itself to these folders:
    /// <code>
    /// /script-root            # A temp directory created for each script when the script is run
    ///     /script-host        # Contains all dependency assets (assemblies, binaries...etc) that are specific
    ///                           to the script-host process or are shared between script-host and the script assembly.
    ///     /script-run-1       # A new dir is created for each script run. It contains the script assembly and all
    ///                           dependency assets that only the script assembly needs to run
    ///     /script-run-2
    ///     /script-run-3
    ///     /...
    /// </code>
    /// <para>
    /// Some dependencies are copied (deployed) to this folder structure where they will be loaded from, while other
    /// dependencies will not be copied, and instead will be loaded from their original locations (in-place).
    /// </para>
    /// </summary>
    private async Task<SetupInfo?> SetupRunEnvironmentAsync(RunOptions runOptions, CancellationToken cancellationToken)
    {
        _ = _appStatusMessagePublisher.PublishAsync(_script.Id, "Gathering dependencies...");

        var dependencies = new List<Dependency>();
        var additionalCode = new SourceCodeCollection();

        // Add script references
        dependencies.AddRange(_script.Config.References
            .Select(x => new Dependency(x, NeededBy.Script, LoadStrategy.LoadInPlace)));

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // Add data connection resources
        if (_script.DataConnection != null)
        {
            var dcResources = await GetDataConnectionResourcesAsync(_script.DataConnection);

            if (dcResources.Code.Count > 0)
            {
                additionalCode.AddRange(dcResources.Code);
            }

            if (dcResources.References.Count > 0)
            {
                dependencies.AddRange(dcResources.References
                    .Select(x => new Dependency(x, NeededBy.Shared, LoadStrategy.DeployAndLoad)));
            }

            if (dcResources.Assembly != null)
            {
                dependencies.Add(
                    new Dependency(new AssemblyImageReference(dcResources.Assembly), NeededBy.Shared,
                        LoadStrategy.DeployAndLoad));
            }
        }

        // Add assembly files needed to support running script
        dependencies.AddRange(_userVisibleAssemblies
            .Select(assemblyPath => new Dependency(
                new AssemblyFileReference(assemblyPath),
                NeededBy.Shared,
                LoadStrategy.DeployAndLoad))
        );

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        Task.WaitAll(dependencies
            .Select(d => d.LoadAssetsAsync(_script.Config.TargetFrameworkVersion, _packageProvider))
            .ToArray()
        );

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var compileAssemblyImageDeps = dependencies
            .Select(d =>
                d.NeededBy != NeededBy.ScriptHost && d.Reference is AssemblyImageReference air
                    ? air.AssemblyImage
                    : null!)
            .Where(x => x != null!)
            .ToArray();

        var compileAssemblyFileDeps = dependencies
            .Where(x => x.NeededBy != NeededBy.ScriptHost)
            .SelectMany(x => x.Assets)
            .DistinctBy(x => x.Path)
            .Where(x => x.IsManagedAssembly)
            .Select(x => new
            {
                x.Path,
                AssemblyName = AssemblyName.GetAssemblyName(x.Path)
            })
            // Choose the highest version of duplicate assemblies
            .GroupBy(a => a.AssemblyName.Name)
            .Select(grp => grp.OrderBy(x => x.AssemblyName.Version).Last())
            .Select(x => x.Path)
            .ToHashSet();

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // Parse Code & Compile
        _ = _appStatusMessagePublisher.PublishAsync(_script.Id, "Compiling...");
        var (parsingResult, compilationResult) = ParseAndCompile.Do(
            runOptions.SpecificCodeToRun ?? _script.Code,
            _script,
            _codeParser,
            _codeCompiler,
            compileAssemblyImageDeps,
            compileAssemblyFileDeps,
            additionalCode);

        if (!compilationResult.Success)
        {
            var errors = compilationResult
                .Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => CorrectDiagnosticErrorLineNumber(d, parsingResult.UserProgramStartLineNumber));

            await _output.WriteAsync(new ErrorScriptOutput("Compilation failed:\n" + errors.JoinToString("\n")));

            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        _ = _appStatusMessagePublisher.PublishAsync(_script.Id, "Preparing...");
        var scriptHostDepsDir = await DeployScriptHostDependenciesAsync(dependencies);

        var (scriptDir, scriptAssemblyFilePath) = await DeployScriptDependenciesAsync(
            compilationResult.AssemblyBytes,
            scriptHostDepsDir,
            dependencies);

        return new SetupInfo(
            scriptHostDepsDir,
            scriptDir,
            scriptAssemblyFilePath,
            dependencies.Where(x => x.LoadStrategy == LoadStrategy.LoadInPlace)
                .SelectMany(x => x.Assets.Select(a => Path.GetDirectoryName(a.Path)))
                .Where(x => !string.IsNullOrEmpty(x))
                .ToHashSet()
                .ToArray()!,
            parsingResult.UserProgramStartLineNumber
        );
    }

    private async Task<(SourceCodeCollection Code, IReadOnlyList<Reference> References, AssemblyImage? Assembly)>
        GetDataConnectionResourcesAsync(DataConnection dataConnection)
    {
        var code = new SourceCodeCollection();
        var references = new List<Reference>();

        var targetFrameworkVersion = _script.Config.TargetFrameworkVersion;

        var connectionResources =
            await _dataConnectionResourcesCache.GetResourcesAsync(dataConnection, targetFrameworkVersion);

        var applicationCode = connectionResources.SourceCode?.ApplicationCode;
        if (applicationCode?.Count > 0)
        {
            code.AddRange(applicationCode);
        }

        var requiredReferences = connectionResources.RequiredReferences;
        if (requiredReferences?.Length > 0)
        {
            references.AddRange(requiredReferences);
        }

        return (code, references, connectionResources.Assembly);
    }

    private async Task<DirectoryPath> DeployScriptHostDependenciesAsync(IList<Dependency> dependencies)
    {
        var scriptHostDepsDirPath = _scriptHostRootDirectory.Combine("script-host");
        var scriptHostDepsDir = scriptHostDepsDirPath.GetInfo();

        scriptHostDepsDir.Create();

        var scriptHostDeps = dependencies
            .Where(x => x.NeededBy is NeededBy.ScriptHost or NeededBy.Shared);

        await DeployAsync(scriptHostDepsDir, scriptHostDeps);
        return scriptHostDepsDirPath;
    }

    private async Task<(DirectoryPath, FilePath)> DeployScriptDependenciesAsync(
        byte[] scriptAssembly,
        DirectoryPath scriptHostDepsDir,
        IList<Dependency> dependencies)
    {
        var scriptDirPath = Path.Combine(
            _scriptHostRootDirectory.Path,
            "script",
            DateTime.Now.ToString("yyyy-MM-dd_hh-mm-ss-fff"));

        var scriptDir = new DirectoryInfo(scriptDirPath);
        scriptDir.Create();

        var scriptDeps = dependencies.Where(x => x.NeededBy is NeededBy.Script).ToArray();

        await DeployAsync(scriptDir, scriptDeps);

        // Write compiled assembly to dir
        var fileSafeScriptName = StringUtil
                                     .RemoveInvalidFileNameCharacters(_script.Name, "_")
                                     .Replace(" ", "_")
                                 // Arbitrary suffix so we don't match an assembly/asset with the same name.
                                 // Example: Assume user names script "Microsoft.Extensions.DependencyInjection"
                                 // If user also has a reference to "Microsoft.Extensions.DependencyInjection.dll"
                                 // then code further below will not copy the "Microsoft.Extensions.DependencyInjection.dll"
                                 // to the output directory, resulting in the referenced assembly not being found.
                                 + "__";

        FilePath scriptAssemblyFilePath = Path.Combine(scriptDir.FullName, $"{fileSafeScriptName}.dll");

        await File.WriteAllBytesAsync(scriptAssemblyFilePath.Path, scriptAssembly);

        // A runtimeconfig.json file tells .NET how to run the assembly
        var probingPaths = new[]
            {
                scriptDir.FullName,
                scriptHostDepsDir.Path,
            }
            .Concat(scriptDeps.SelectMany(x => x.Assets.Select(a => Path.GetDirectoryName(a.Path)!)))
            .Distinct()
            .Where(x => !string.IsNullOrEmpty(x))
            .ToArray();

        await File.WriteAllTextAsync(
            Path.Combine(scriptDir.FullName, $"{fileSafeScriptName}.runtimeconfig.json"),
            GenerateRuntimeConfigFileContents(probingPaths)
        );

        // The scriptconfig.json is custom and passes some options to the running script
        await File.WriteAllTextAsync(
            Path.Combine(scriptDir.FullName, "scriptconfig.json"),
            $@"{{
    ""output"": {{
        ""maxDepth"": {_settings.Results.MaxSerializationDepth},
        ""maxCollectionSerializeLength"": {_settings.Results.MaxCollectionSerializeLength}
    }}
}}");

        return (scriptDirPath, scriptAssemblyFilePath);
    }

    private async Task DeployAsync(DirectoryInfo destination, IEnumerable<Dependency> dependencies)
    {
        foreach (var dependency in dependencies)
        {
            var reference = dependency.Reference;

            if (reference is AssemblyImageReference air)
            {
                var assemblyImage = air.AssemblyImage;
                var fileName = assemblyImage.ConstructAssemblyFileName();
                var destFilePath = Path.Combine(destination.FullName, fileName);

                // Checking file exists means that the first assembly in the list of paths will win.
                // Later assemblies with the same file name will not be copied to the output directory.
                if (!File.Exists(destFilePath))
                {
                    await File.WriteAllBytesAsync(
                        destFilePath,
                        assemblyImage.Image);
                }
            }

            if (dependency.LoadStrategy == LoadStrategy.LoadInPlace)
            {
                continue;
            }

            foreach (var asset in dependency.Assets)
            {
                var destFilePath = Path.Combine(destination.FullName, Path.GetFileName(asset.Path));
                if (!File.Exists(destFilePath))
                {
                    File.Copy(asset.Path, destFilePath, true);
                }
            }
        }
    }

    private string GenerateRuntimeConfigFileContents(string[] probingPaths)
    {
        var tfm = _script.Config.TargetFrameworkVersion.GetTargetFrameworkMoniker();
        var frameworkName = _script.Config.UseAspNet ? "Microsoft.AspNetCore.App" : "Microsoft.NETCore.App";
        int majorVersion = _script.Config.TargetFrameworkVersion.GetMajorVersion();

        var runtimeVersion = _dotNetInfo.GetDotNetRuntimeVersionsOrThrow()
            .Where(v => v.Version.Major == majorVersion &&
                        v.FrameworkName.Equals(frameworkName, StringComparison.OrdinalIgnoreCase))
            .MaxBy(v => v.Version)?
            .Version;

        if (runtimeVersion == null)
        {
            throw new Exception(
                $"Could not find a {tfm} runtime with the name {frameworkName} and version {majorVersion}");
        }

        var probingPathsJson = JsonSerializer.Serialize(probingPaths);

        return $$"""
                 {
                     "runtimeOptions": {
                         "tfm": "{{tfm}}",
                         "rollForward": "Minor",
                         "framework": {
                             "name": "{{frameworkName}}",
                             "version": "{{runtimeVersion}}"
                         },
                         "additionalProbingPaths": {{probingPathsJson}}
                     }
                 }
                 """;
    }

    /// <summary>
    /// Corrects line numbers in compilation errors relative to the line number where user code starts.
    /// </summary>
    private static string CorrectDiagnosticErrorLineNumber(Diagnostic diagnostic, int userProgramStartLineNumber)
    {
        var err = diagnostic.ToString();

        if (!err.StartsWith('('))
        {
            return err;
        }

        var errParts = err.Split(':');
        var span = errParts.First().Trim(['(', ')']);
        var spanParts = span.Split(',');
        var lineNumberStr = spanParts[0];

        return int.TryParse(lineNumberStr, out int lineNumber)
            ? $"({lineNumber - userProgramStartLineNumber},{spanParts[1]}):{errParts.Skip(1).JoinToString(":")}"
            : err;
    }

    /// <summary>
    /// The component that a dependency is needed by.
    /// </summary>
    enum NeededBy
    {
        Script,
        ScriptHost,
        Shared,
    }

    /// <summary>
    /// How a dependency is deployed and loaded.
    /// </summary>
    enum LoadStrategy
    {
        /// <summary>
        /// Dependency will be copied to output directory and loaded from there.
        /// </summary>
        DeployAndLoad,

        /// <summary>
        /// Dependency will not be copied, and will be loaded from its original location (in-place).
        /// </summary>
        LoadInPlace
    }

    /// <summary>
    /// A dependency needed by the run environment.
    /// </summary>
    /// <param name="Reference">A reference dependency.</param>
    /// <param name="NeededBy">Which components need this dependency to run.</param>
    /// <param name="LoadStrategy">How will this dependency be deployed.</param>
    record Dependency(Reference Reference, NeededBy NeededBy, LoadStrategy LoadStrategy)
    {
        public ReferenceAsset[] Assets { get; private set; } = [];

        public async Task LoadAssetsAsync(
            DotNetFrameworkVersion dotNetFrameworkVersion,
            IPackageProvider packageProvider)
        {
            Assets = (await Reference.GetAssetsAsync(dotNetFrameworkVersion, packageProvider)).ToArray();
        }
    }
}

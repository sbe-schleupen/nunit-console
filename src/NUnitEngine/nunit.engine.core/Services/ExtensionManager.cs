﻿// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TestCentric.Metadata;
using NUnit.Engine.Extensibility;
using NUnit.Engine.Internal;
using NUnit.Engine.Internal.FileSystemAccess;
using NUnit.Engine.Internal.FileSystemAccess.Default;
using System.Linq;

namespace NUnit.Engine.Services
{
    public class ExtensionManager
    {
        static readonly Version CURRENT_ENGINE_VERSION = Assembly.GetExecutingAssembly().GetName().Version;

        static readonly Logger log = InternalTrace.GetLogger(typeof(ExtensionManager));

        private readonly IFileSystem _fileSystem;
        private readonly IDirectoryFinder _directoryFinder;

        // List of all ExtensionPoints discovered
        private readonly List<ExtensionPoint> _extensionPoints = new List<ExtensionPoint>();

        // Index to ExtensionPoints based on the Path
        private readonly Dictionary<string, ExtensionPoint> _extensionPointIndex = new Dictionary<string, ExtensionPoint>();

        // List of ExtensionNodes for all extensions discovered.
        private List<ExtensionNode> _extensions = new List<ExtensionNode>();

        private bool _extensionsAreLoaded;

        // AssemblyTracker is a List of candidate ExtensionAssemblies, with built-in indexing
        // by file path and assembly name, eliminating the need to update indices separately.
        private readonly ExtensionAssemblyTracker _assemblies = new ExtensionAssemblyTracker();
    
        // List of all extensionDirectories specified on command-line or in environment,
        // used to ignore duplicate calls to FindExtensionAssemblies.
        private readonly List<string> _extensionDirectories = new List<string>();

        public ExtensionManager()
            : this(new FileSystem())
        {
        }

        internal ExtensionManager(IFileSystem fileSystem)
            : this(fileSystem, new DirectoryFinder(fileSystem))
        {
        }

        internal ExtensionManager(IFileSystem fileSystem, IDirectoryFinder directoryFinder)
        {
            _fileSystem = fileSystem;
            _directoryFinder = directoryFinder;
        }

        #region Public Properties and Methods

        /// <summary>
        /// Gets an enumeration of all ExtensionPoints in the engine.
        /// </summary>
        public IEnumerable<IExtensionPoint> ExtensionPoints
        {
            get { return _extensionPoints.ToArray(); }
        }

        /// <summary>
        /// Gets an enumeration of all installed Extensions.
        /// </summary>
        public IEnumerable<IExtensionNode> Extensions
        {
            get
            {
                LoadExtensions();

                return _extensions.ToArray();
            }
        }

        /// <summary>
        /// Find the extension points in a loaded assembly.
        /// </summary>
        public virtual void FindExtensionPoints(params Assembly[] targetAssemblies)
        {
            foreach (var assembly in targetAssemblies)
            {
                log.Info("FindExtensionPoints scanning {0} assembly", assembly.GetName().Name);

                foreach (ExtensionPointAttribute attr in assembly.GetCustomAttributes(typeof(ExtensionPointAttribute), false))
                {
                    if (_extensionPointIndex.ContainsKey(attr.Path))
                    {
                        string msg = string.Format(
                            "The Path {0} is already in use for another extension point.",
                            attr.Path);
                        throw new NUnitEngineException(msg);
                    }

                    var ep = new ExtensionPoint(attr.Path, attr.Type)
                    {
                        Description = attr.Description,
                    };

                    _extensionPoints.Add(ep);
                    _extensionPointIndex.Add(ep.Path, ep);

                    log.Info("  Found ExtensionPoint: Path={0}, Type={1}", ep.Path, ep.TypeName);
                }

                foreach (Type type in assembly.GetExportedTypes())
                {
                    foreach (TypeExtensionPointAttribute attr in type.GetCustomAttributes(typeof(TypeExtensionPointAttribute), false))
                    {
                        string path = attr.Path ?? "/NUnit/Engine/TypeExtensions/" + type.Name;

                        if (_extensionPointIndex.ContainsKey(path))
                        {
                            string msg = string.Format(
                                "The Path {0} is already in use for another extension point.",
                                attr.Path);
                            throw new NUnitEngineException(msg);
                        }

                        var ep = new ExtensionPoint(path, type)
                        {
                            Description = attr.Description,
                        };

                        _extensionPoints.Add(ep);
                        _extensionPointIndex.Add(path, ep);

                        log.Info("  Found ExtensionPoint: Path={0}, Type={1}", ep.Path, ep.TypeName);
                    }
                }
            }
        }

        /// <summary>
        /// Find extension assemblies starting from a given base directory,
        /// and using the contained '.addins' files to direct the search.
        /// </summary>
        /// <param name="initialDirectory">Path to the initial directory.</param>
        public void FindExtensionAssemblies(string startDir)
        {
            // Ignore a call for a directory we have already used
            if (!_extensionDirectories.Contains(startDir))
            {
                _extensionDirectories.Add(startDir);

                log.Info($"FindExtensionAssemblies examining extension directory {startDir}");

                ProcessDirectory(_fileSystem.GetDirectory(startDir), false);
            }
        }

        /// <summary>
        /// Find ExtensionAssemblies for a host assembly using
        /// a built-in algorithm that searches in certain known locations.
        /// </summary>
        /// <param name="hostAssembly">An assembly that supports NUnit extensions.</param>
        public void FindExtensionAssemblies(Assembly hostAssembly)
        {
            log.Info($"FindExtensionAssemblies called for host {hostAssembly.FullName}");

            bool isChocolateyPackage = System.IO.File.Exists(Path.Combine(Path.GetDirectoryName(hostAssembly.Location)!, "VERIFICATION.txt"));
            string[] extensionPatterns = isChocolateyPackage
                ? new[] { "nunit-extension-*/**/tools/", "nunit-extension-*/**/tools/*/" }
                : new[] { "NUnit.Extension.*/**/tools/", "NUnit.Extension.*/**/tools/*/" };


            IDirectory startDir = _fileSystem.GetDirectory(AssemblyHelper.GetDirectoryName(hostAssembly));

            while (startDir != null)
            {
                foreach (var pattern in extensionPatterns)
                    foreach (var dir in _directoryFinder.GetDirectories(startDir, pattern))
                        ProcessDirectory(dir, true);

                startDir = startDir.Parent;
            }
        }

        /// <summary>
        /// Get an ExtensionPoint based on its unique identifying path.
        /// </summary>
        public IExtensionPoint GetExtensionPoint(string path)
        {
            return _extensionPointIndex.TryGetValue(path, out ExtensionPoint ep) ? ep : null;
        }

        /// <summary>
        /// Get extension objects for all nodes of a given type
        /// </summary>
        public IEnumerable<T> GetExtensions<T>()
        {
            foreach (var node in GetExtensionNodes<T>())
                yield return (T)node.ExtensionObject;
        }

        /// <summary>
        /// Get all ExtensionNodes for a path
        /// </summary>
        public IEnumerable<IExtensionNode> GetExtensionNodes(string path)
        {
            LoadExtensions();

            var ep = GetExtensionPoint(path);
            if (ep != null)
                foreach (var node in ep.Extensions)
                    yield return node;
        }

        /// <summary>
        /// Get the first or only ExtensionNode for a given ExtensionPoint
        /// </summary>
        /// <param name="path">The identifying path for an ExtensionPoint</param>
        /// <returns></returns>
        public IExtensionNode GetExtensionNode(string path)
        {
            LoadExtensions();

            // TODO: Remove need for the cast
            var ep = GetExtensionPoint(path) as ExtensionPoint;

            return ep != null && ep.Extensions.Count > 0 ? ep.Extensions[0] : null;
        }

        /// <summary>
        /// Get all extension nodes of a given Type.
        /// </summary>
        /// <param name="includeDisabled">If true, disabled nodes are included</param>
        public IEnumerable<ExtensionNode> GetExtensionNodes<T>(bool includeDisabled = false)
        {
            LoadExtensions();

            var ep = GetExtensionPoint(typeof(T));
            if (ep == null)
                log.Debug("There is no extension point of type {typeof(T).Name}");
            else
                foreach (var node in ep.Extensions)
                    if (includeDisabled || node.Enabled)
                        yield return node;
        }

        /// <summary>
        /// Enable or disable an extension
        /// </summary>
        public void EnableExtension(string typeName, bool enabled)
        {
            LoadExtensions();

            foreach (var node in _extensions)
                if (node.TypeName == typeName)
                    node.Enabled = enabled;
        }

        /// <summary>
        /// We can only load extensions after all candidate assemblies are identified.
        /// This method may be called by the user after all "Find" calls are complete.
        /// If the user fails to call it and subsequently tries to examine extensions
        /// using other ExtensionManager properties or methods, it will be called 
        /// but calls not going through ExtensionManager may fail.
        /// </summary>
        public void LoadExtensions()
        {
            if (!_extensionsAreLoaded)
            {
                _extensionsAreLoaded = true;

                foreach (var candidate in _assemblies)
                    FindExtensionsInAssembly(candidate);
            }
        }

        /// <summary>
        /// Get an ExtensionPoint based on the required Type for extensions.
        /// </summary>
        public ExtensionPoint GetExtensionPoint(Type type)
        {
            foreach (var ep in _extensionPoints)
                if (ep.TypeName == type.FullName)
                    return ep;

            return null;
        }

        /// <summary>
        /// Get an ExtensionPoint based on a Cecil TypeReference.
        /// </summary>
        public ExtensionPoint GetExtensionPoint(TypeReference type)
        {
            foreach (var ep in _extensionPoints)
                if (ep.TypeName == type.FullName)
                    return ep;

            return null;
        }

        #endregion

        /// <summary>
        /// Deduce the extension point based on the Type of an extension.
        /// Returns null if no extension point can be found that would
        /// be satisfied by the provided Type.
        /// </summary>
        private ExtensionPoint DeduceExtensionPointFromType(TypeReference typeRef)
        {
            var ep = GetExtensionPoint(typeRef);
            if (ep != null)
                return ep;

            TypeDefinition typeDef = typeRef.Resolve();

            foreach (InterfaceImplementation iface in typeDef.Interfaces)
            {
                ep = DeduceExtensionPointFromType(iface.InterfaceType);
                if (ep != null)
                    return ep;
            }

            TypeReference baseType = typeDef.BaseType;
            return baseType != null && baseType.FullName != "System.Object"
                ? DeduceExtensionPointFromType(baseType)
                : null;
        }

        /// <summary>
        /// Scans a directory for candidate addin assemblies. Note that assemblies in
        /// the directory are only scanned if no file of type .addins is found. If such
        /// a file is found, then those assemblies it references are scanned.
        /// </summary>
        private void ProcessDirectory(IDirectory startDir, bool fromWildCard)
        {
            var directoryName = startDir.FullName;
            if (WasVisited(directoryName, fromWildCard))
            {
                log.Warning($"Skipping directory '{directoryName}' because it was already visited.");
                return;
            }

            log.Info($"Scanning directory '{directoryName}' for extensions.");
            Visit(directoryName, fromWildCard);

            if (ProcessAddinsFiles(startDir, fromWildCard) == 0)
                foreach (var file in startDir.GetFiles("*.dll"))
                    ProcessCandidateAssembly(file.FullName, true);
        }

        /// <summary>
        /// Process all .addins files found in a directory
        /// </summary>
        private int ProcessAddinsFiles(IDirectory startDir, bool fromWildCard)
        {
            var addinsFiles = startDir.GetFiles("*.addins");
            var addinsFileCount = 0;

            foreach (var file in addinsFiles)
            {
                ProcessAddinsFile(file, fromWildCard);
                addinsFileCount++;
            }

            return addinsFileCount;
        }

        /// <summary>
        /// Process a .addins type file. The file contains one entry per
        /// line. Each entry may be a directory to scan, an assembly
        /// path or a wildcard pattern used to find assemblies. Blank
        /// lines and comments started by # are ignored.
        /// </summary>
        private void ProcessAddinsFile(IFile addinsFile, bool fromWildCard)
        {
            log.Info("Processing file " + addinsFile.FullName);

            foreach (var entry in AddinsFile.Read(addinsFile).Where(e => e.Text != string.Empty))
            {
                bool isWild = fromWildCard || entry.IsPattern;
                IDirectory baseDir = addinsFile.Parent;
                string entryDir = entry.DirectoryName;
                string entryFile = entry.FileName;

                log.Debug($"Processing entry {entry.Text}");
                if (entry.IsDirectory)
                {
                    if (entry.IsFullyQualified)
                    {
                        baseDir = _fileSystem.GetDirectory(entry.Text);
                        foreach (var dir in _directoryFinder.GetDirectories(_fileSystem.GetDirectory(entryDir), ""))
                            ProcessDirectory(dir, isWild);
                    }
                    else
                        foreach (var dir in _directoryFinder.GetDirectories(baseDir, entry.Text))
                            ProcessDirectory(dir, isWild);
                }
                else
                {
                    if (entry.IsFullyQualified)
                    {
                        foreach (var file in _directoryFinder.GetFiles(_fileSystem.GetDirectory(entryDir), entryFile))
                            ProcessCandidateAssembly(file.FullName, isWild);
                    }
                    else
                        foreach (var file in _directoryFinder.GetFiles(baseDir, entry.Text))
                            ProcessCandidateAssembly(file.FullName, isWild);
                }
            }
        }

        private void ProcessCandidateAssembly(string filePath, bool fromWildCard)
        {
            log.Debug($"Processing candidate assembly {filePath}");

            // Did we already process this file?
            if (_assemblies.ContainsPath(filePath))
            {
                log.Debug("  Skipping assembly already processed");
                return;
            }

            try
            {
                var candidateAssembly = new ExtensionAssembly(filePath, fromWildCard);

                // We never add assemblies unless the host can load them
                if (!CanLoadTargetFramework(Assembly.GetEntryAssembly(), candidateAssembly))
                {
                    log.Debug("  Unable to load this assembly");
                    return;
                }

                _assemblies.AddOrUpdate(candidateAssembly);
            }
            catch (BadImageFormatException e)
            {
                if (!fromWildCard)
                    throw new NUnitEngineException($"Specified extension {filePath} could not be read", e);
            }
            catch (NUnitEngineException)
            {
                if (!fromWildCard)
                    throw;
            }
        }

        // Dictionary containing all directory paths already visited.
        private readonly Dictionary<string, object> _visited = new Dictionary<string, object>();

        private bool WasVisited(string path, bool fromWildcard)
        {
            return _visited.ContainsKey($"path={path}_visited={fromWildcard}");
        }

        private void Visit(string path, bool fromWildcard)
        {
            _visited.Add($"path={path}_visited={fromWildcard}", null);
        }

        /// <summary>
        /// Scan a single assembly for extensions marked by ExtensionAttribute.
        /// For each extension, create an ExtensionNode and link it to the
        /// correct ExtensionPoint. Public for testing.
        /// </summary>
        internal void FindExtensionsInAssembly(ExtensionAssembly extensionAssembly)
        {
            log.Info($"Scanning {extensionAssembly.FilePath} for Extensions");

            if (!CanLoadTargetFramework(Assembly.GetEntryAssembly(), extensionAssembly))
            {
                log.Info($"{extensionAssembly.FilePath} cannot be loaded on this runtime");
                return;
            }

            IRuntimeFramework assemblyTargetFramework = null;
#if NETFRAMEWORK
            var currentFramework = RuntimeFramework.CurrentFramework;
            assemblyTargetFramework = extensionAssembly.TargetFramework;
            if (!currentFramework.CanLoad(assemblyTargetFramework))
            {
                if (!extensionAssembly.FromWildCard)
                {
                    throw new NUnitEngineException($"Extension {extensionAssembly.FilePath} targets {assemblyTargetFramework.DisplayName}, which is not available.");
                }
                else
                {
                    log.Info($"Assembly {extensionAssembly.FilePath} targets {assemblyTargetFramework.DisplayName}, which is not available. Assembly found via wildcard.");
                    return;
                }
            }
#endif

            foreach (var extensionType in extensionAssembly.Assembly.MainModule.GetTypes())
            {
                CustomAttribute extensionAttr = extensionType.GetAttribute("NUnit.Engine.Extensibility.ExtensionAttribute");

                if (extensionAttr == null)
                    continue;

                // TODO: This is a remnant of older code. In principle, this should be generalized
                // to something like "HostVersion". However, this can safely remain until
                // we separate ExtensionManager into its own assembly.
                string versionArg = extensionAttr.GetNamedArgument("EngineVersion") as string;
                if (versionArg != null)
                {
                    if (new Version(versionArg) > CURRENT_ENGINE_VERSION)
                    {
                        log.Warning($"  Ignoring {extensionType.Name}. It requires version {versionArg}.");
                        continue;
                    }
                }

                var node = new ExtensionNode(extensionAssembly.FilePath, extensionAssembly.AssemblyVersion, extensionType.FullName, assemblyTargetFramework)
                {
                    Path = extensionAttr.GetNamedArgument("Path") as string,
                    Description = extensionAttr.GetNamedArgument("Description") as string
                };

                object enabledArg = extensionAttr.GetNamedArgument("Enabled");
                node.Enabled = enabledArg == null || (bool)enabledArg;

                log.Info("  Found ExtensionAttribute on Type " + extensionType.Name);

                foreach (var attr in extensionType.GetAttributes("NUnit.Engine.Extensibility.ExtensionPropertyAttribute"))
                {
                    string name = attr.ConstructorArguments[0].Value as string;
                    string value = attr.ConstructorArguments[1].Value as string;

                    if (name != null && value != null)
                    {
                        node.AddProperty(name, value);
                        log.Info("        ExtensionProperty {0} = {1}", name, value);
                    }
                }

                _extensions.Add(node);

                ExtensionPoint ep;
                if (node.Path == null)
                {
                    ep = DeduceExtensionPointFromType(extensionType);
                    if (ep == null)
                    {
                        string msg = string.Format(
                            "Unable to deduce ExtensionPoint for Type {0}. Specify Path on ExtensionAttribute to resolve.",
                            extensionType.FullName);
                        throw new NUnitEngineException(msg);
                    }

                    node.Path = ep.Path;
                }
                else
                {
                    // TODO: Remove need for the cast
                    ep = GetExtensionPoint(node.Path) as ExtensionPoint;
                    if (ep == null)
                    {
                        string msg = string.Format(
                            "Unable to locate ExtensionPoint for Type {0}. The Path {1} cannot be found.",
                            extensionType.FullName,
                            node.Path);
                        throw new NUnitEngineException(msg);
                    }
                }

                ep.Install(node);
            }
        }

        private const string NETFRAMEWORK = ".NETFramework";
        private const string NETCOREAPP = ".NETCoreApp";
        private const string NETSTANDARD = ".NETStandard";

        /// <summary>
        /// Checks that the target framework of the current runner can load the extension assembly. For example, .NET Core
        /// cannot load .NET Framework assemblies and vice-versa.
        /// </summary>
        /// <param name="runnerAsm">The executing runner</param>
        /// <param name="extensionAsm">The extension we are attempting to load</param>
        internal bool CanLoadTargetFramework(Assembly runnerAsm, ExtensionAssembly extensionAsm)
        {
            if (runnerAsm == null)
                return true;

            var runnerFrameworkName = GetTargetRuntime(runnerAsm.Location);
            var extensionFrameworkName = GetTargetRuntime(extensionAsm.FilePath);

            switch (runnerFrameworkName.Identifier)
            {
                case NETSTANDARD:
                    throw new Exception($"{runnerAsm.FullName} test runner must target .NET Core or .NET Framework, not .NET Standard");

                case NETCOREAPP:
                    switch (extensionFrameworkName.Identifier)
                    {
                        case NETSTANDARD:
                        case NETCOREAPP:
                            return true;
                        case NETFRAMEWORK:
                        default:
                            log.Info($".NET Core runners require .NET Core or .NET Standard extension for {extensionAsm.FilePath}");
                            return false;
                    }
                case NETFRAMEWORK:
                default:
                    switch (extensionFrameworkName.Identifier)
                    {
                        case NETFRAMEWORK:
                            return runnerFrameworkName.Version.Major == 4 || extensionFrameworkName.Version.Major < 4;
                        // For .NET Framework calling .NET Standard, we only support if framework is 4.7.2 or higher
                        case NETSTANDARD:
                            return extensionFrameworkName.Version >= new Version(4, 7, 2);
                        case NETCOREAPP:
                        default:
                            log.Info($".NET Framework runners cannot load .NET Core extension {extensionAsm.FilePath}");
                            return false;
                    }
            }

        }

        private System.Runtime.Versioning.FrameworkName GetTargetRuntime(string filePath)
        {
            var assemblyDef = AssemblyDefinition.ReadAssembly(filePath);
            var frameworkName = assemblyDef.GetFrameworkName();
            if (string.IsNullOrEmpty(frameworkName))
            {
                var runtimeVersion = assemblyDef.GetRuntimeVersion();
                frameworkName = $".NETFramework,Version=v{runtimeVersion.ToString(3)}";
            }
            return new System.Runtime.Versioning.FrameworkName(frameworkName);
        }

        public void Dispose()
        {
            // Make sure all assemblies release the underlying file streams. 
            foreach (var candidate in _assemblies)
            {
                candidate.Dispose();
            }
        }
    }
}

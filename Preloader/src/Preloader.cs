using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using InjectionLibrary.Attributes;
using InjectionLibrary.Exceptions;
using InjectionLibrary.Utils;
using Mono;
using Mono.Cecil;

namespace InjectionLibrary;

internal static class Preloader
{
    public const string GUID = MyPluginInfo.PLUGIN_GUID;
    public const string NAME = MyPluginInfo.PLUGIN_NAME;
    public const string VERSION = MyPluginInfo.PLUGIN_VERSION;
		
    internal static readonly BepInPlugin Plugin = new BepInPlugin(GUID, NAME, VERSION);

    private static ReaderParameters ReaderParameters;
    
    private static readonly string RequiresInjectionAttributeName = typeof(RequiresInjectionsAttribute).FullName;
    private static readonly string InjectInterfaceAttributeName = typeof(InjectInterfaceAttribute).FullName;
    private static readonly string HandleErrorsAttributeName = typeof(HandleErrorsAttribute).FullName;

    internal static ManualLogSource Log { get; } = Logger.CreateLogSource(nameof(InjectionLibrary));

    private static readonly string MainDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    
    private static readonly Dictionary<string, Dictionary<string, List<(TypeDefinition @interface, ErrorHandlingStrategy strategy)>>> Interfaces = [];

    private static readonly LinkedList<AssemblyDefinition> LoadedAssemblies = [];

    private static bool _inError;
    
    //Required by BepInEx!
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once InconsistentNaming
    public static IEnumerable<string> TargetDLLs => EnumerateTargetDlLs();

    private static string _targetAssembly = null;
        
    //Required by BepInEx!
    // ReSharper disable once UnusedMember.Global
    public static void Patch(ref AssemblyDefinition assembly)
    {
        if (_inError)
            return;
        
        try
        {
            if (Interfaces.TryGetValue(_targetAssembly, out var dict))
            {
                Log.LogInfo($"Patching {_targetAssembly}");
                foreach (var module in assembly.Modules)
                {
                    //fix BepInEx default loader ( no idea why they do not use their own Resolver when loading patched assemblies )
                    module.assembly_resolver = Disposable.NotOwned(TypeLoader.ReaderParameters.AssemblyResolver);
                    module.metadata_resolver = new MetadataResolver(TypeLoader.ReaderParameters.AssemblyResolver);
                    
                    foreach (var type in module.Types)
                    {
                        if (!dict.TryGetValue(type.Name, out var list))
                            continue;

                        foreach (var (@interface, strategy) in list)
                        {
                            type.ImplementInterface(@interface, strategy);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _inError = true;
            if (ex is not TerminationException)
                Log.LogFatal($"Exception while patching {assembly.Name.Name}:\n{ex}");
        }

        if (!PluginConfig.Enabled.Value) 
            return;
            
        var outputAssembly = $"{PluginConfig.OutputPath.Value}/{assembly.Name.Name}{PluginConfig.OutputExtension.Value}";
        Log.LogWarning($"Saving modified Assembly to {outputAssembly}");
        assembly.Write(outputAssembly);
    }
        
    //Required by BepInEx!
    // ReSharper disable once UnusedMember.Global
    public static void Initialize()
    {
        Log.LogInfo("Preloader Started");
        PluginConfig.Init();
        
        var resolver = new DefaultAssemblyResolver();

        ReaderParameters = new ReaderParameters
        {
            ReadWrite = false,
            ReadSymbols = false,
            ReadingMode = ReadingMode.Deferred,
            AssemblyResolver = resolver
        };

        resolver.ResolveFailure += delegate(object _, AssemblyNameReference reference)
        {
            if (!Utility.TryParseAssemblyName(reference.FullName, out var assemblyName))
            {
                Log.LogWarning($"Could not parse assembly name: {reference.FullName}");
                return null;
            }

            foreach (var item in new[]
                     {
                         Paths.BepInExAssemblyDirectory,
                         Paths.PluginPath,
                         Paths.PatcherPluginPath,
                     }.Concat(Paths.DllSearchPaths))
            {
                if (Utility.TryResolveDllAssembly(assemblyName, item, ReaderParameters, out var assembly))
                {
                    return assembly;
                }
            }

            Log.LogWarning($"Could not find assembly: {assemblyName}");
            return null;
        };
    }
    
    private static bool PublicKeyTokenEquals(byte[] a, byte[] b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;

        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;

        return true;
    }
    

    //Required by BepInEx!
    // ReSharper disable once UnusedMember.Global
    public static void Finish()
    {
        foreach (var assembly in LoadedAssemblies)
        {
            try
            {
                assembly.Dispose();
            }
            catch (Exception)
            {
                //ignored
            }
        }
        
        LoadedAssemblies.Clear();
        
        if (_inError)
        {
            Log.LogWarning("""

                           //////////////////////////////////////////////////////////////////
                           \\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\
                           An Error Occurred while injecting code, 
                           the game is most likely in a broken state and will probably crash!
                           //////////////////////////////////////////////////////////////////
                           \\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\\
                           """);
        }
        Log.LogInfo("Preloader Finished");
    }

    private static IEnumerable<string> EnumerateTargetDlLs()
    {
        //loop over all assemblies!
        var assemblyPaths =
            Directory.GetFiles(Path.GetFullPath(Paths.PluginPath), "*.dll", SearchOption.AllDirectories);
        foreach (var path in assemblyPaths)
        {
            try
            {
                //try to load the assembly
                var assemblyDefinition = AssemblyDefinition.ReadAssembly(path, ReaderParameters);
                
                //check if it is tagged with RequiresInjectionAttribute
                if (!assemblyDefinition.HasCustomAttributes)
                {
                    assemblyDefinition.Dispose();
                    continue;
                }
                
                var attribute = assemblyDefinition.CustomAttributes.FirstOrDefault(x =>
                    x.AttributeType.FullName == RequiresInjectionAttributeName);
                if (attribute == null)
                {
                    assemblyDefinition.Dispose();
                    continue;
                }

                Log.LogDebug($"Assembly {assemblyDefinition.Name.Name} Wants to inject something");

                LoadedAssemblies.AddLast(assemblyDefinition);

                var assemblyStrategy = ErrorHandlingStrategy.Terminate;
                var strategyAttribute =
                    assemblyDefinition.CustomAttributes.FirstOrDefault(at =>
                        at.AttributeType.FullName == HandleErrorsAttributeName);

                if (strategyAttribute != null)
                    assemblyStrategy = strategyAttribute.GetAttributeInstance<HandleErrorsAttribute>().Strategy;

                //search for interfaces tagged with InjectInterfaceAttribute
                foreach (var type in assemblyDefinition.MainModule.Types)
                {
                    if (!type.HasCustomAttributes)
                        continue;

                    var attributes = type.CustomAttributes
                        .Where(at => at.AttributeType.FullName == InjectInterfaceAttributeName).ToArray();
                    if (!attributes.Any())
                        continue;

                    Log.LogDebug($"Found {type.FullName}");

                    foreach (var customAttribute in attributes)
                    {
                        var instance = customAttribute.GetAttributeInstance<InjectInterfaceAttribute>();

                        if (!Interfaces.TryGetValue(instance.AssemblyName, out var dict))
                        {
                            dict = [];
                            Interfaces[instance.AssemblyName] = dict;
                        }

                        if (!dict.TryGetValue(instance.TypeName, out var list))
                        {
                            list = [];
                            dict[instance.TypeName] = list;
                        }

                        list.Add((type, assemblyStrategy));
                    }
                }
            }
            catch (BadImageFormatException ex)
            {
                Log.LogWarning("Skipping " + Path.GetRelativePath(Paths.BepInExRootPath, path) + " because it's not a valid .NET assembly. Full error: " +
                               ex.Message);
            } 
            catch (Exception ex)
            {
                _inError = true;
                Log.LogFatal($"Exception parsing {Path.GetRelativePath(Paths.BepInExRootPath, path)}:\n{ex}");
                break;
            }
        }

        Log.LogInfo($"Found {LoadedAssemblies.Count} assemblies that require Injections");

        foreach (var assembly in Interfaces.Keys)
        {
            if (_inError)
                break;
            
            _targetAssembly = assembly;
            yield return assembly;
        }
        _targetAssembly = null;
    }

    private static class PluginConfig
    {
        public static void Init()
        {
            var config = new ConfigFile(Utility.CombinePaths(MainDir, $"{NAME}.Development.cfg"), true, Plugin);
            //Initialize Configs
            Enabled = config.Bind("DevelOptions", "Enabled", false, "Enable development dll output");
            OutputPath = config.Bind("DevelOptions", "OutputPath", MainDir, "Folder where to write the modified dlls");
            OutputExtension = config.Bind("DevelOptions", "OutputExtension", ".pdll", "Extension to use for the modified dlls\n( Do not use .dll if outputting inside the BepInEx folders )");

            //remove unused options
            PropertyInfo orphanedEntriesProp = config.GetType()
                .GetProperty("OrphanedEntries", BindingFlags.NonPublic | BindingFlags.Instance);

            var orphanedEntries = (Dictionary<ConfigDefinition, string>)orphanedEntriesProp!.GetValue(config, null);

            orphanedEntries.Clear(); // Clear orphaned entries (Unbinded/Abandoned entries)
            config.Save(); // Save the config file
        }

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> OutputPath;
        internal static ConfigEntry<string> OutputExtension;
    }

}
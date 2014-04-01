// --------------------------------------------------------------------------------------
// Common utilities for environment, debugging and working with project files
// --------------------------------------------------------------------------------------

namespace MonoDevelop.FSharp

open System
open System.IO
open System.Diagnostics
open System.Reflection
open System.Globalization
open Microsoft.FSharp.Reflection
open MonoDevelop.Projects
open MonoDevelop.Ide.Gui
open MonoDevelop.Ide
open MonoDevelop.Core.Assemblies
open MonoDevelop.Core
open FSharp.CompilerBinding

// --------------------------------------------------------------------------------------
// Common utilities for working with files & extracting information from 
// MonoDevelop objects (e.g. references, project items etc.)
// --------------------------------------------------------------------------------------

module CompilerArguments = 

  /// Wraps the given string between double quotes
  let wrapFile (s:string) = if s.StartsWith "\"" then s else "\"" + s + "\""  
  
  /// Is the specified extension supported F# file?
  let supportedExtension ext = 
    [".fsscript"; ".fs"; ".fsx"; ".fsi"] |> List.exists (fun sup ->
        String.Compare(ext, sup, true) = 0)

  // Translate the target framework to an enum used by FSharp.CompilerBinding
  let getTargetFramework (targetFramework:TargetFrameworkMoniker) = 
      if targetFramework = TargetFrameworkMoniker.NET_3_5 then FSharpTargetFramework.NET_3_5
      elif targetFramework = TargetFrameworkMoniker.NET_3_0 then FSharpTargetFramework.NET_3_0
      elif targetFramework = TargetFrameworkMoniker.NET_2_0 then FSharpTargetFramework.NET_2_0
      elif targetFramework = TargetFrameworkMoniker.NET_4_0 then FSharpTargetFramework.NET_4_0
      elif targetFramework = TargetFrameworkMoniker.NET_4_5 then FSharpTargetFramework.NET_4_5
      else FSharpTargetFramework.NET_4_5
  
  module Project =
      let getReferences (project: DotNetProject) configSelector =
        //let projDir = Path.GetDirectoryName(project.FileName.ToString())
        //The original hint path can be extracted here rather than the incorrect reference that the monodevelop project system returns
        [ for pr in project.References do
            if pr.IsValid then yield pr.Reference
            else
                if pr.ExtendedProperties.Contains "_OriginalMSBuildReferenceHintPath" then
                    yield pr.ExtendedProperties.["_OriginalMSBuildReferenceHintPath"] :?> string
        ]

//TODO: Not sure if this is still needed                
//        [ for ref in project.GetReferencedAssemblies(configSelector) do
//            if not (Path.IsPathRooted ref) then
//                yield Path.GetFullPath (Path.Combine(projDir, ref))
//            else
//                yield Path.GetFullPath ref ]
                  
      let isPortable (project: DotNetProject) =
        not (String.IsNullOrEmpty project.TargetFramework.Id.Profile)
        
      let getPortableReferences (project: DotNetProject) configSelector = 
        let fdir = 
            project.TargetRuntime.GetReferenceFrameworkDirectories() 
            |> Seq.map (fun fp -> fp.ToString() ) 
            |> Seq.toArray
   
        // create a new target framework  moniker, the default one is incorrect for portable unless the project type is PortableDotnetProject
        // which has the default moniker profile of ".NETPortable" rather than ".NETFramework".  
        // We cant use a PortableDotnetProject as this requires adding a guid flavour, which breaks compatiability with VS until 
        // the MD project system is refined to support project the way VS does.
       
        let frameworkMoniker = TargetFrameworkMoniker (".NETPortable", project.TargetFramework.Id.Version, project.TargetFramework.Id.Profile)
        let assemblyDirectoryName = frameworkMoniker.GetAssemblyDirectoryName()
        // TODO: figure out the correct path [1] happens to be right one here
        let portablePath = Path.Combine(fdir.[1], assemblyDirectoryName)
        
        let portableReferences = System.IO.Directory.EnumerateFiles(portablePath) |> List.ofSeq
                
        let projectReferences = getReferences project configSelector |> List.map wrapFile
                
        projectReferences |> Seq.append portableReferences
        |> set 
        |> Set.map ((+) "-r:")
        |> Set.toList
       
  /// Generates references for the current project & configuration as a 
  /// list of strings of the form [ "-r:<full-path>"; ... ]
  let private generateReferences (project: DotNetProject, langVersion, targetFramework, configSelector, shouldWrap) = 
   if Project.isPortable project then
        Project.getPortableReferences project configSelector 
   else
       let wrapf = if shouldWrap then wrapFile else id
       
       [ // Should we wrap references in "..."
        
        // The unversioned reference text "FSharp.Core" is used in Visual Studio .fsproj files.  This can sometimes be 
        // incorrectly resolved so we just skip this simple reference form and rely on the default directory search below.
        let projectReferences =
            Project.getReferences project configSelector
            |> Seq.filter (fun (ref: string) -> not (ref.EndsWith("FSharp.Core")))
            |> set
             
        // If 'mscorlib.dll' and 'FSharp.Core.dll' is not in the set of references, we need to resolve it and add it. 
        // We look in the directories returned by getDefaultDirectories(langVersion, targetFramework).
        for assumedFile in ["mscorlib"; "FSharp.Core"] do 
          let coreRef =
            projectReferences |> Seq.tryFind (fun fn -> fn.EndsWith(assumedFile + ".dll", true, CultureInfo.InvariantCulture) 
                                                        || fn.EndsWith(assumedFile, true, CultureInfo.InvariantCulture))
        
          match coreRef with
          | None ->
              //fall back to using default directories for F# Core
              let dirs = FSharpEnvironment.getDefaultDirectories(langVersion, targetFramework) 
              match FSharpEnvironment.resolveAssembly dirs assumedFile with
              | Some fn -> yield "-r:" + wrapf(fn)
              | None -> Debug.WriteLine(sprintf "Resolution: Assembly resolution failed when trying to find default reference for '%s'!" assumedFile)
    
          | Some r -> 
            Debug.WriteLine(sprintf "Resolution: Found '%s' reference '%s'" assumedFile r)
          
        for file in projectReferences do 
          yield "-r:" + wrapf(file) ]


  /// Generates command line options for the compiler specified by the 
  /// F# compiler options (debugging, tail-calls etc.), custom command line
  /// parameters and assemblies referenced by the project ("-r" options)
  let generateCompilerOptions (project:DotNetProject, fsconfig:FSharpCompilerParameters, reqLangVersion, targetFramework, configSelector, shouldWrap) =
    let dashr = generateReferences (project, reqLangVersion, targetFramework, configSelector, shouldWrap) |> Array.ofSeq
    let defines = fsconfig.DefineConstants.Split([| ';'; ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
    [  yield "--noframework"
       for symbol in defines do yield "--define:" + symbol
       yield if fsconfig.DebugSymbols then  "--debug+" else  "--debug-"
       yield if fsconfig.Optimize then "--optimize+" else "--optimize-"
       yield if fsconfig.GenerateTailCalls then "--tailcalls+" else "--tailcalls-"
       // TODO: This currently ignores escaping using "..."
       for arg in fsconfig.OtherFlags.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) do
         yield arg 
       yield! dashr ] 
  

  /// Get source files of the current project (returns files that have 
  /// build action set to 'Compile', but not e.g. scripts or resources)
  let getSourceFiles (items:ProjectItemCollection) = 
    [ for file in items.GetAll<ProjectFile>() do
        if file.BuildAction = "Compile" && file.Subtype <> Subtype.Directory then 
          yield file.Name.ToString() ]

    
  /// Generate inputs for the compiler (excluding source code!); returns list of items 
  /// containing resources (prefixed with the --resource parameter)
  let generateOtherItems (items:ProjectItemCollection) = 
    [ for file in items.GetAll<ProjectFile>() do
        match file.BuildAction with
        | _ when file.Subtype = Subtype.Directory -> ()
        | "EmbeddedResource" -> 
            let fileName = file.Name.ToString()
            let logicalResourceName = file.ProjectVirtualPath.ToString().Replace("\\",".").Replace("/",".")
            yield "--resource:" + wrapFile fileName + "," + wrapFile logicalResourceName
        | "None" | "Content" | "Compile" -> ()
        | s -> ()] // failwith("Items of type '" + s + "' not supported") ]

  let private getToolPath (pathsToSearch:seq<string>) (extensions:seq<string>) (toolName:string) =
    let filesToSearch = Seq.map (fun x -> toolName + x) extensions

    let tryFindPathAndFile (filesToSearch:seq<string>) (path:string) =
      try
        let candidateFiles = Directory.GetFiles(path)

        let fileIfExists candidateFile =
          Seq.tryFind (fun x -> Path.Combine(path,x) = candidateFile) filesToSearch
        match Seq.tryPick fileIfExists candidateFiles with
          | Some x -> Some(path,x)
          | None -> None

      with
        | e -> None

    Seq.tryPick (tryFindPathAndFile filesToSearch) pathsToSearch


  /// Get full path to tool
  let getEnvironmentToolPath (runtime:TargetRuntime) (framework:TargetFramework) (extensions:seq<string>) (toolName:string) =
    let pathsToSearch = runtime.GetToolsPaths(framework)
    getToolPath pathsToSearch extensions toolName

  let getDefaultTargetFramework (runtime:TargetRuntime) =
    let newest_net_framework_folder (best:TargetFramework,best_version:int[]) (candidate_framework:TargetFramework) =
      if runtime.IsInstalled(candidate_framework) && candidate_framework.Id.Identifier = TargetFrameworkMoniker.ID_NET_FRAMEWORK then
        let version = candidate_framework.Id.Version
        let parsed_version_s = (if version.[0] = 'v' then version.[1..] else version).Split('.')
        let parsed_version =
          try
            Array.map (fun x -> int x) parsed_version_s
          with
            | _ -> [| 0 |]
        let mutable level = 0
        let mutable cont = true
        let min_level = min parsed_version.Length best_version.Length
        let mutable new_best = false
        while cont && level < min_level do
          if parsed_version.[level] > best_version.[level] then
            new_best <- true
            cont <- false
          elif best_version.[level] > parsed_version.[level] then
            cont <- false
          else
            cont <- true
          level <- level + 1
        if new_best then
          (candidate_framework, parsed_version)
        else
          (best,best_version)
      else
        (best,best_version)
    let candidate_frameworks = MonoDevelop.Core.Runtime.SystemAssemblyService.GetTargetFrameworks()
    let first = Seq.head candidate_frameworks
    let best_info = Seq.fold newest_net_framework_folder (first,[| 0 |]) candidate_frameworks
    fst best_info

  let private getShellToolPath (extensions:seq<string>) (toolName:string)  =
    let pathVariable = Environment.GetEnvironmentVariable("PATH")
    let searchPaths = pathVariable.Split [| IO.Path.PathSeparator  |]
    getToolPath searchPaths extensions toolName

  let getDefaultInteractive() =

    let runtime = IdeApp.Preferences.DefaultTargetRuntime
    let framework = getDefaultTargetFramework runtime

    match getEnvironmentToolPath runtime framework [|""; ".exe"; ".bat" |] "fsharpi" with
    | Some(dir,file)-> Some(Path.Combine(dir,file))
    | None->
    match getShellToolPath [| ""; ".exe"; ".bat" |] "fsharpi" with
    | Some(dir,file)-> Some(Path.Combine(dir,file))
    | None->
    match getEnvironmentToolPath runtime framework [|""; ".exe"; ".bat" |] "fsi" with
    | Some(dir,file)-> Some(Path.Combine(dir,file))
    | None->
    match getShellToolPath [| ""; ".exe"; ".bat" |] "fsi" with
    | Some(dir,file)-> Some(Path.Combine(dir,file))
    | None-> 
    match FSharpEnvironment.BinFolderOfDefaultFSharpCompiler None with
    | Some(dir) when FSharpEnvironment.safeExists(Path.Combine(dir, "fsi.exe")) ->  
        Some(Path.Combine(dir,"fsi.exe"))
    | _ -> None

  let getCompilerFromEnvironment (runtime:TargetRuntime) (framework:TargetFramework) =
    match getEnvironmentToolPath runtime framework [| ""; ".exe"; ".bat" |] "fsharpc" with
    | Some(dir,file) -> Some(Path.Combine(dir,file))
    | None ->
    match getEnvironmentToolPath runtime framework [| ""; ".exe"; ".bat" |] "fsc" with
    | Some(dir,file) -> Some(Path.Combine(dir,file))
    | None -> None
        
  // Only used when xbuild support is not enabled. When xbuild is enabled, the .targets 
  // file finds FSharp.Build.dll which finds the F# compiler.
  let getDefaultFSharpCompiler() =
  
    let runtime = IdeApp.Preferences.DefaultTargetRuntime
    let framework = getDefaultTargetFramework runtime

    match getCompilerFromEnvironment runtime framework with
    | Some(result)-> Some(result)
    | None->
    match getShellToolPath [| ""; ".exe"; ".bat" |] "fsharpc" with
    | Some(dir,file) -> Some(Path.Combine(dir,file))
    | None ->
    match getShellToolPath [| ""; ".exe"; ".bat" |] "fsc" with
    | Some(dir,file) -> Some(Path.Combine(dir,file))
    | None -> 
    match FSharpEnvironment.BinFolderOfDefaultFSharpCompiler None with
    | Some(dir) when FSharpEnvironment.safeExists(Path.Combine(dir, "fsc.exe")) ->  
        Some(Path.Combine(dir,"fsc.exe"))
    | _ -> None

  let getArgumentsFromProject (proj:DotNetProject, config) =
        let projConfig = proj.GetConfiguration(config) :?> DotNetProjectConfiguration
        let fsconfig = projConfig.CompilationParameters :?> FSharpCompilerParameters
        generateCompilerOptions (proj, fsconfig, None, getTargetFramework projConfig.TargetFramework.Id, config, false) |> Array.ofList


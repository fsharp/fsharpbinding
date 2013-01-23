// --------------------------------------------------------------------------------------
// (c) Robin Neatherway
// --------------------------------------------------------------------------------------
namespace FSharp.InteractiveAutocomplete

open System
open Microsoft.Build.BuildEngine
open Microsoft.Build.Framework
open Microsoft.Build.Tasks
open Microsoft.Build.Utilities

module ProjectParser =

  type ProjectResolver =
    {
      project:  Project
      rar:      ResolveAssemblyReference
      loadtime: DateTime
    }

  let private mkrar () =
    let x = { new IBuildEngine with
                member be.BuildProjectFile(projectFileName, targetNames, globalProperties, argetOutputs) = true
                member be.LogCustomEvent(e) = ()
                member be.LogErrorEvent(e) = ()
                member be.LogMessageEvent(e) = ()
                member be.LogWarningEvent(e) = ()
                member be.ColumnNumberOfTaskNode with get() = 1
                member be.ContinueOnError with get() = true
                member be.LineNumberOfTaskNode with get() = 1
                member be.ProjectFileOfTaskNode with get() = "" }
    let rar = new ResolveAssemblyReference ()
    do rar.BuildEngine <- x
    do rar.AllowedRelatedFileExtensions <- [| ".pdb"; ".xml"; ".optdata" |]
    do rar.FindRelatedFiles <- true
    do rar.SearchPaths <- [|"{CandidateAssemblyFiles}"
                            "{HintPathFromItem}"
                            "{TargetFrameworkDirectory}"
                            "{AssemblyFolders}"
                            "{GAC}"
                            "{RawFileName}"
                           |]
    do rar.AllowedAssemblyExtensions <- [| ".exe"; ".dll" |]
    rar

  let load (uri: string) : Option<ProjectResolver> =
    let p = new Project()
    try
      p.Load(uri)
      Some { project = p; rar =  mkrar (); loadtime = DateTime.Now }
    with :? InvalidProjectFileException as e ->
      None

  let getFileName (p: ProjectResolver) : string = p.project.FullFileName

  let getLoadTime (p: ProjectResolver) : DateTime = p.loadtime

  let getDirectory (p: ProjectResolver) : string =
    IO.Path.GetDirectoryName p.project.FullFileName

  let getFiles (p: ProjectResolver) : string array =
    let fs  = p.project.GetEvaluatedItemsByName("Compile")
    let dir = getDirectory p
    [| for f in fs do yield IO.Path.Combine(dir, f.FinalItemSpec) |]

  let getReferences (p: ProjectResolver) : string array =
    let convert (bi: BuildItem) : ITaskItem =
      let ti = new TaskItem(bi.FinalItemSpec)
      if bi.HasMetadata("HintPath") then
        ti.SetMetadata("HintPath", bi.GetEvaluatedMetadata("HintPath"))
      ti :> ITaskItem

    let pwd = Environment.CurrentDirectory
    do Environment.CurrentDirectory <- getDirectory p
    let refs = p.project.GetEvaluatedItemsByName "Reference"
    p.rar.Assemblies <- [| for r in refs do yield convert r |]
    p.rar.TargetProcessorArchitecture <- p.project.GetEvaluatedProperty "PlatformTarget"
    // TODO: Execute may fail
    ignore <| p.rar.Execute ()
    do Environment.CurrentDirectory <- pwd
    //Array.append [|"-r:/Library/Frameworks/Mono.framework/Versions/3.0.0/lib/mono/4.0/mscorlib.dll"|]
    //             [| for f in p.rar.ResolvedFiles do yield "-r:" + f.ItemSpec |]
    [| for f in p.rar.ResolvedFiles do yield "-r:" + f.ItemSpec |]

  let getOptions (p: ProjectResolver) : string array =
    let getprop s = p.project.GetEvaluatedProperty s
    // TODO: Robustify - convert.ToBoolean may fail
    let optimize     = getprop "Optimize" |> Convert.ToBoolean
    let tailcalls    = getprop "Tailcalls" |> Convert.ToBoolean
    let debugsymbols = getprop "DebugSymbols" |> Convert.ToBoolean
    let defines = (getprop "DefineConstants").Split([|';';',';' '|],
                                                    StringSplitOptions.RemoveEmptyEntries)
    let otherflags = (getprop "OtherFlags")
    let otherflags = if otherflags = null
                     then [||]
                     else otherflags.Split([|' '|],
                                           StringSplitOptions.RemoveEmptyEntries)

    [|
      yield "--noframework"
      for symbol in defines do yield "--define:" + symbol
      yield if debugsymbols then  "--debug+" else  "--debug-"
      yield if optimize then "--optimize+" else "--optimize-"
      yield if tailcalls then "--tailcalls+" else "--tailcalls-"
      yield! otherflags
      yield! (getReferences p)
     |]

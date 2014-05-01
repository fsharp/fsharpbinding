namespace MonoDevelop.FSharp

open System
open System.IO
open MonoDevelop.Core
open MonoDevelop.Ide.Gui.Components
open MonoDevelop.Projects
open MonoDevelop.Ide
open MonoDevelop.Ide.Gui
open MonoDevelop.Ide.Gui.Pads.ProjectPad

open System.Collections.Generic
open System.Linq
open System.Xml
open System.Xml.Linq
open Linq2Xml

/// The command handler type for nodes in F# projects in the solution explorer.
type FSharpProjectNodeCommandHandler() =
  inherit NodeCommandHandler()

  /// Reload project causing the node tree up refresh with new ordering
  let reloadProject (currentNode: ITreeNavigator) =
    use monitor = IdeApp.Workbench.ProgressMonitors.GetProjectLoadProgressMonitor(true)
    monitor.BeginTask("Reloading Project", 1)
    let file = currentNode.DataItem :?> ProjectFile
    file.Project.ParentFolder.ReloadItem(monitor, file.Project) |> ignore
    monitor.Step (1)
    monitor.EndTask()

  let moveNodes (currentNode: ITreeNavigator) (movingNode:ProjectFile) position =
    let moveToNode = currentNode.DataItem :?> ProjectFile
    let projectFile = movingNode.Project.FileName.ToString()

    ///partially apply the default namespace of msbuild to xs
    let xd = xs "http://schemas.microsoft.com/developer/msbuild/2003"

    // If the "Compile" element contains a "Link" element then it is a linked file,
    // so use that value for comparison when finding the node.
    let nodeName (node:XElement) = 
       let link = node.Descendants(xd "Link") |> firstOrNone
       match link with
       | Some l -> l.Value
       | None   -> node |> attributeValue "Include"

    //open project file
    use file = IO.File.Open(projectFile, FileMode.Open)
    let xdoc = XElement.Load(file)
    file.Close()

    //get all the compile nodes from the project file
    let compileNodes = xdoc |> descendants (xd "Compile")

    let findByIncludeFile name seq = 
        seq |> where (fun elem -> nodeName elem = name )
            |> firstOrNone
    
    let getFullName (pf:ProjectFile) = pf.ProjectVirtualPath.ToString().Replace("/", "\\")

    let movingElement = compileNodes |> findByIncludeFile (getFullName movingNode)
    let moveToElement = compileNodes |> findByIncludeFile (getFullName moveToNode)

    let addFunction (moveTo:XElement) (position:DropPosition) =
        match position with
        | DropPosition.Before -> moveTo.AddBeforeSelf : obj -> unit
        | DropPosition.After -> moveTo.AddAfterSelf : obj -> unit
        | _ -> ignore

    match (movingElement, moveToElement, position) with
    | Some(moving), Some(moveTo), (DropPosition.Before | DropPosition.After) ->
        moving.Remove()
        //if the moving node contains a DependentUpon node as a child remove the DependentUpon nodes
        moving.Descendants( xd "DependentUpon") |> Seq.iter (fun node -> node.Remove())
        //get the add function using the position
        let add = addFunction moveTo position
        add(moving)
        xdoc.Save(projectFile)
        reloadProject currentNode
    | _ -> ()//If we cant find both nodes or the position isnt before or after we dont continue

  /// Implement drag and drop of nodes in F# projects in the solution explorer.
  override x.OnNodeDrop(dataObject, dragOperation, position) =
    match dataObject, dragOperation with
    | :? ProjectFile as movingNode, DragOperation.Move ->
        //Move as long as this is a drag op and the moving node is a project file
        moveNodes x.CurrentNode movingNode position
    | _ -> //otherwise use the base behaviour
           base.OnNodeDrop(dataObject, dragOperation, position) 
        
  /// Implement drag and drop of nodes in F# projects in the solution explorer.
  override x.CanDragNode() = DragOperation.Move

  /// Implement drag and drop of nodes in F# projects in the solution explorer.
  override x.CanDropNode(dataObject, dragOperation) = true

  /// Implement drag and drop of nodes in F# projects in the solution explorer.
  override x.CanDropNode(dataObject, dragOperation, position) =
      //currently we are going to only support dropping project files from the same parent project
      match (dataObject, x.CurrentNode.DataItem) with
      | (:? ProjectFile as drag), (:? ProjectFile as drop) -> 
         drag.Project = drop.Project && drop.ProjectVirtualPath.ParentDirectory = drag.ProjectVirtualPath.ParentDirectory
      | _ -> false


/// MD/XS extension for the F# project nodes in the solution explorer.
type FSharpProjectFileNodeExtension() =
  inherit NodeBuilderExtension()

  /// Check if an item in the project model is recognized by this extension.
  let (|SupportedProjectFile|SupportedProjectFolder|NotSupported|) (item:obj) =
    match item with
    | :? ProjectFile as projfile when projfile.Project <> null-> SupportedProjectFile(projfile)
    | :? ProjectFolder as projfolder when projfolder.Project <> null-> SupportedProjectFolder(projfolder)
    | _ -> NotSupported

  override x.CanBuildNode(dataType:Type) =
    // Extend any file or folder belonging to a F# project
    typedefof<ProjectFile>.IsAssignableFrom(dataType) || typedefof<ProjectFolder>.IsAssignableFrom (dataType)

  override x.CompareObjects(thisNode:ITreeNavigator, otherNode:ITreeNavigator) : int =
    match (otherNode.DataItem, thisNode.DataItem) with
    | SupportedProjectFile(file2), SupportedProjectFile(file1) when file1.Project = file2.Project-> 
      if file1.Project :? DotNetProject && (file1.Project :?> DotNetProject).LanguageName = "F#" then
            file1.Project.Files.IndexOf(file1).CompareTo(file2.Project.Files.IndexOf(file2))
      else NodeBuilder.DefaultSort
    | SupportedProjectFolder(folder1), SupportedProjectFolder(folder2) when folder1.Project = folder2.Project->
        let folders = folder1.Project.Files |> Seq.filter (fun file -> file.Subtype = Subtype.Directory) 

        let folder1Index = folders |> Seq.tryFindIndex(fun pf -> pf.FilePath = folder1.Path)
        let folder2Index = folders |> Seq.tryFindIndex(fun pf -> pf.FilePath = folder2.Path)

        match folder1Index, folder2Index with
        | Some(i1), Some(i2) -> i2.CompareTo(i1)
        | _ -> NodeBuilder.DefaultSort
    | _ -> NodeBuilder.DefaultSort

  override x.CommandHandlerType = typeof<FSharpProjectNodeCommandHandler>



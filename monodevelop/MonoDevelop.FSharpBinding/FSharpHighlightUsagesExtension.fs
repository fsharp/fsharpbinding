namespace MonoDevelop.FSharp

open System
open Mono.TextEditor
open MonoDevelop.Core
open MonoDevelop.Ide
open MonoDevelop.Ide.FindInFiles
open MonoDevelop.Projects
open ICSharpCode.NRefactory.Semantics
open ICSharpCode.NRefactory.TypeSystem
open ICSharpCode.NRefactory.TypeSystem.Implementation
open Microsoft.FSharp.Compiler.SourceCodeServices

/// MD/XS extension for highlighting the usages of a symbol within the current buffer.
type HighlightUsagesExtension() as this =
    inherit MonoDevelop.SourceEditor.AbstractUsagesExtension<ResolveResult>()
            
    override x.TryResolve(resolveResult) =
        true

    override x.GetReferences(_, token) =
        try
            let line, col, lineStr = MonoDevelop.getLineInfoFromOffset(this.Editor.Caret.Offset, this.Editor.Document)
            let currentFile = this.FileName.FullPath.ToString()
            let source = this.Editor.Text
            let projectContent = this.Document.ProjectContent

            let projectFilename, files, args, framework = MonoDevelop.getCheckerArgs(this.Document.Project, currentFile)

            let symbolReferences =
                Async.RunSynchronously(async{return! MDLanguageService.Instance.GetUsesOfSymbolAtLocationInFile(projectFilename, currentFile, source, files, line, col, lineStr, args, framework)},
                                       cancellationToken = token)

            match symbolReferences with
            | Some(fsSymbolName, references) -> 
                seq{for symbolUse in references do
                      yield NRefactory.createMemberReference(projectContent, symbolUse, currentFile, source, fsSymbolName) }
            | _ -> Seq.empty
                            
        with
        | :? OperationCanceledException -> Seq.empty
        | exn -> LoggingService.LogError("Unhandled Exception in F# HighlightingUsagesExtension", exn)
                 Seq.empty       

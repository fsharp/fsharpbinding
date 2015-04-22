﻿// --------------------------------------------------------------------------------------
// Provides tool tips with F# hints for MonoDevelop
// (this file implements MonoDevelop interfaces and calls 'LanguageService')
// --------------------------------------------------------------------------------------
namespace MonoDevelop.FSharp

open System
open System.IO
open FSharp.CompilerBinding
open Mono.TextEditor
open MonoDevelop.Core
open MonoDevelop.Ide
open MonoDevelop.SourceEditor
open MonoDevelop.Ide.CodeCompletion
open Microsoft.FSharp.Compiler.SourceCodeServices
open MonoDevelop.FSharp.Symbols
open ExtCore.Control
open Symbols

/// Resolves locations to tooltip items, and orchestrates their display.
/// We resolve language items to an NRefactory symbol.
type FSharpTooltipProvider() = 
    inherit Mono.TextEditor.TooltipProvider()

    // Keep the last result and tooltip window cached
    let mutable lastResult = None : TooltipItem option
    static let mutable lastWindow = None : TooltipInformationWindow option

    //keep the last enterNotofy handler so we can remove the handler as a new TipWindow is created
    let mutable enterNotify = None : IDisposable option

    let killTooltipWindow() =
       lastWindow |> Option.iter (fun w -> w.Destroy())
       enterNotify |> Option.iter (fun en -> en.Dispose ())

    override x.GetItem (editor, offset) =
      try
        let activeDoc = IdeApp.Workbench.ActiveDocument
        if activeDoc = null then null else

        let fileName = activeDoc.FileName.FullPath.ToString()

        let supported = MDLanguageService.SupportedFileName (fileName)
        if supported <> true then null else

        let extEditor = editor :?> ExtensibleTextEditor

        let docText = editor.Text
        if docText = null || offset >= docText.Length || offset < 0 then null else

        let projFile, files, args = MonoDevelop.getCheckerArgs(extEditor.Project, fileName)

        let line, col, lineStr = MonoDevelop.getLineInfoFromOffset(offset, editor.Document)

        let getTooltipFromLanguageService (parseAndCheckResults: ParseAndCheckResults) =
            async {
               // Get tool-tip from the language service
               let! tip = parseAndCheckResults.GetToolTip(line, col, lineStr)
               match tip with
               | None -> return NoToolTipText
               | Some (FSharpToolTipText(elems),_) when elems |> List.forall (function FSharpToolTipElement.None -> true | _ -> false) -> return NoToolTipData
               | Some(tiptext,(col1,col2)) -> 
                   LoggingService.LogInfo "TooltipProvider: Got data"
                   //check to see if the last result is the same tooltipitem, if so return the previous tooltipitem
                   match lastResult with
                   | Some(tooltipItem) when
                       tooltipItem.Item :? FSharpToolTipText && 
                       tooltipItem.Item :?> FSharpToolTipText = tiptext && 
                       tooltipItem.ItemSegment = TextSegment(editor.LocationToOffset (line, col1 + 1), col2 - col1) ->
                           return Tooltip tooltipItem
                   //If theres no match or previous cached result generate a new tooltipitem
                   | Some(_)
                   | None -> let line = editor.Document.OffsetToLineNumber offset
                             let segment = TextSegment(editor.LocationToOffset (line, col1 + 1), col2 - col1)
                             let tooltipItem = TooltipItem (tiptext, segment)
                             lastResult <- Some(tooltipItem)
                             return Tooltip tooltipItem }

        let result =
            //operate on available results no async gettypeparse results is available quick enough
            let parseAndCheckResults = MDLanguageService.Instance.GetTypedParseResultIfAvailable (projFile, fileName, docText, files, args, AllowStaleResults.MatchingSource)
            Async.RunSynchronously (
                    async {
                        try
                            LoggingService.LogInfo "TooltipProvider: Getting tool tip"
                            let! symbol = parseAndCheckResults.GetSymbol(line, col, lineStr)
                            //Hack: Because FCS does not always contain XmlDocSigs for tooltips we have to have to currently use the old tooltips
                            // to extract the signature, this is only limited in that it deals with only a single tooltip in a group/list
                            // This should be fine as there are issues with generic tooltip xmldocs anyway
                            // e.g. generics such as Dictionary<'a,'b>.Contains currently dont work.
                            let! tip = parseAndCheckResults.GetToolTip(line, col, lineStr)
                            //we create the backupSig as lazily as possible we could put the async call in here but I was worried about GC retension.
                            let backupSig = 
                                lazy
                                    match tip with
                                    | Some (FSharpToolTipText (first :: _remainder), (_startCol,_endCol)) ->
                                        match first with
                                        | FSharpToolTipElement.Single (_name, FSharpXmlDoc.XmlDocFileSignature(key, file)) -> Some (file, key)
                                        | FSharpToolTipElement.Group ((_name, FSharpXmlDoc.XmlDocFileSignature (key, file)) :: _remainder)  -> Some (file, key)
                                        | FSharpToolTipElement.CompositionError error ->
                                            LoggingService.LogError (sprintf "TooltipProvider: Composition error: %s" error)
                                            None
                                        | _ -> None
                                    | _ -> None
                            
                            // As the new tooltips are unfinished we match ToolTip here to use the new tooltips and anything else to run through the old tooltip system
                            // In the section above we return EmptyTip for any tooltips symbols that have not yet ben finished
                            match symbol with
                            | Some s -> 
                                 let tt = SymbolTooltips.getTooltipFromSymbolUse s backupSig
                                 match tt with
                                 | ToolTip(signature, summary) ->
                                     //get the TextSegment the the symbols range occupies
                                     let textSeg = Symbols.getTextSegment extEditor.Document symbol.Value col lineStr
                        
                                     //check to see if the last result is the same tooltipitem, if so return the previous tooltipitem
                                     match lastResult with
                                     | Some(tooltipItem) when
                                         tooltipItem.Item :? (string * XmlDoc) &&
                                         tooltipItem.Item :?> (string * XmlDoc) = (signature, summary) &&
                                         tooltipItem.ItemSegment = textSeg ->
                                             return Tooltip tooltipItem
                                     //If theres no match or previous cached result generate a new tooltipitem
                                     | Some(_)
                                     | None -> let tooltipItem = TooltipItem((signature, summary), textSeg)
                                               lastResult <- Some(tooltipItem)
                                               return Tooltip tooltipItem
                                 | EmptyTip -> return! getTooltipFromLanguageService parseAndCheckResults
                            | None -> return! getTooltipFromLanguageService parseAndCheckResults
                        with
                        | :? TimeoutException -> return ParseAndCheckNotFound
                        | ex ->
                            LoggingService.LogError ("TooltipProvider: unexpected exception", ex)
                            return ParseAndCheckNotFound},
                    ServiceSettings.blockingTimeout)

        match result with
        | ParseAndCheckNotFound -> LoggingService.LogWarning "TooltipProvider: ParseAndCheckResults not found"; null
        | NoToolTipText -> LoggingService.LogWarning "TooltipProvider: TootipText not returned"; null
        | NoToolTipData -> LoggingService.LogWarning "TooltipProvider: No data found"; null
        | Tooltip t -> t
       
      with exn ->
          LoggingService.LogError ("TooltipProvider: Error retrieving tooltip", exn)
          null

    override x.CreateTooltipWindow (_editor, _offset, _modifierState, item) = 
        let doc = IdeApp.Workbench.ActiveDocument
        if (doc = null) then null else
        //At the moment as the new tooltips are unfinished we have two types here
        // ToolTipText for the old tooltips and (string * XmlDoc) for the new tooltips
        match item.Item with 
        | :? FSharpToolTipText as titem ->
            let tooltip = TooltipFormatting.formatTip(titem)
            let (signature, comment) = 
                match tooltip with
                | [signature,comment] -> signature,comment
                //With multiple tips just take the head.  
                //This shouldnt happen anyway as we split them in the resolver provider
                | multiple -> multiple |> List.head
            //dont show a tooltip if there is no content
            if String.IsNullOrEmpty(signature) then null 
            else            
                let result = new TooltipInformationWindow(ShowArrow = true)
                let toolTipInfo = new TooltipInformation(SignatureMarkup = signature)
                if not (String.IsNullOrEmpty(comment)) then toolTipInfo.SummaryMarkup <- comment
                result.AddOverload(toolTipInfo)
                result.RepositionWindow ()                  
                result :> _

        | :? (string * XmlDoc) as tip -> 
            let signature, xmldoc = tip
            let result = new TooltipInformationWindow(ShowArrow = true)
            let toolTipInfo = new TooltipInformation(SignatureMarkup = signature)
            match xmldoc with
            | Full(summary) -> toolTipInfo.SummaryMarkup <- summary
            | Lookup(key, potentialFilename) ->
                let summary = 
                    maybe {let! filename = potentialFilename
                           let! markup = TooltipXmlDoc.findDocForEntity(filename, key)
                           let summary = TooltipsXml.getTooltipSummary Styles.simpleMarkup markup
                           return summary}
                summary |> Option.iter (fun summary -> toolTipInfo.SummaryMarkup <- summary)
            | EmptyDoc -> ()
            result.AddOverload(toolTipInfo)
            result.RepositionWindow ()                  
            result :> _

        | _ -> LoggingService.LogError "TooltipProvider: Type mismatch, not a FSharpLocalResolveResult"
               null
    
    override x.ShowTooltipWindow (editor, offset, modifierState, _mouseX, _mouseY, item) =
        match (lastResult, lastWindow) with
        | Some(lastRes), Some(lastWin) when item.Item = lastRes.Item && lastWin.IsRealized ->
            lastWin :> _                   
        | _ -> killTooltipWindow()
               match x.CreateTooltipWindow (editor, offset, modifierState, item) with
               | :? TooltipInformationWindow as tipWindow ->
                   let positionWidget = editor.TextArea
                   let region = item.ItemSegment.GetRegion(editor.Document)
                   let p1, p2 = editor.LocationToPoint(region.Begin), editor.LocationToPoint(region.End)
                   let caret = Gdk.Rectangle (int p1.X - positionWidget.Allocation.X, 
                                              int p2.Y - positionWidget.Allocation.Y, 
                                              int (p2.X - p1.X), 
                                              int editor.LineHeight)
                   //For debug this is usful for visualising the tooltip location
                   // editor.SetSelection(item.ItemSegment.Offset, item.ItemSegment.EndOffset)
               
                   tipWindow.ShowPopup(positionWidget, caret, MonoDevelop.Components.PopupPosition.Top)
                   enterNotify <- Some (tipWindow.EnterNotifyEvent.Subscribe(fun _ -> editor.HideTooltip (false)))
                   //cache last window shown
                   lastWindow <- Some(tipWindow)
                   lastResult <- Some(item)
                   tipWindow :> _
               | _ -> LoggingService.LogError "TooltipProvider: Type mismatch, not a TooltipInformationWindow"
                      null
            
    interface IDisposable with
        member x.Dispose() = killTooltipWindow()
